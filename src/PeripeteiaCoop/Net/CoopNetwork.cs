using System;
using System.Collections.Generic;
using System.Globalization;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Minimal host/client session:
/// - host listens on a UDP port;
/// - client connects and both exchange poses at 30 Hz;
/// - each side renders the other player as an interpolated cloned Marie model
///   with a lightweight procedural walk cycle;
/// - a client reconnects automatically if the host restarts.
///
/// This is deliberately not authoritative yet; it is the smallest useful
/// network milestone and is fully testable with two local game instances.
/// </summary>
internal sealed class CoopNetwork : IDisposable
{
    private enum PacketType : byte
    {
        Hello = 1,
        Welcome = 2,
        Pose = 3,
        DoorState = 4,
        ItemState = 5,
        WeaponState = 6,
        WeaponEvent = 7,
        Damage = 8,
        ItemEvent = 9,
        EnemyState = 10,
        EnemyDeath = 11,
        StoryEvent = 12,
        PlayerDeath = 13,
        SaveLoad = 14,
        SceneChange = 15,
        CorpsePose = 16,
        NpcDamage = 17,
    }

    [Flags]
    private enum PoseFlags : byte
    {
        None = 0,
        Crouch = 1 << 0,
        Sprint = 1 << 1,
        Grounded = 1 << 2,
        InAir = 1 << 3,
        Jumping = 1 << 4,
        Shimmy = 1 << 5,
    }

    private const string ConnectionKey = "peripeteia-coop";
    private const int ProtocolVersion = 20;
    private const float PoseInterval = 1.0f / 30.0f;
    private const float StatsInterval = 5.0f;

    // Carrying a teammate's corpse: how far the player can reach, and how far in
    // front of the view the body is held.
    private const float DragReach = 3.2f;

    // The distance in front of the view the carried body is held at. Recorded at
    // the moment the body is grabbed so the carry does not depend on the crosshair
    // still touching the corpse every frame.
    private const float DefaultDragHold = 1.8f;

    private readonly CoopSettings _settings;
    private readonly EventBasedNetListener _listener;
    private readonly NetManager _net;
    private readonly Dictionary<int, RemoteAvatar> _avatars = new();
    private readonly Dictionary<NetPeer, int> _peerIds = new();

    // Reusable buffers for the animator parameters in an EnemyState packet.
    // Sized larger than EnemySync.MaxAnimatorParameters so a newer sender can
    // never overflow them.
    private readonly int[] _animHashes = new int[32];
    private readonly byte[] _animTypes = new byte[32];
    private readonly float[] _animValues = new float[32];

    private int _nextPeerId = 1;
    private int _localId = -1;
    private float _nextPoseTime;
    private float _nextStatsTime;
    private float _nextLeanLog;
    private float _nextVisibilityLog;
    private int _poseSent;
    private int _poseReceived;
    private Vector3 _autopilotOrigin;
    private bool _autopilotOriginSet;
    private int _autopilotPlayerId;
    private float _autopilotGroundedSince;
    private float _nextAutopilotTime;
    private float _nextHuntLog;
    private float _nextShootTime;
    private PlayerMovement? _localPlayer;
    private ObjectHighlight? _localHighlight;

    // The corpse currently being carried. Once a body is grabbed the carry is
    // latched: it lasts until the interact key is released (or the teammate gets
    // back up), even if the crosshair slides off the body while it is being
    // dragged. Without the latch the body was dropped and re-grabbed every other
    // frame, which looked like the carry instantly letting go.
    private RemoteAvatar? _corpseDragTarget;
    private float _corpseDragHold = DefaultDragHold;
    private bool _disposed;
    private bool _reconnectPending;
    private float _nextReconnectTime;
    private string _statusLine = "connecting...";

    public string StatusText
    {
        get
        {
            NetPeer? host = _net.FirstPeer;
            int ping = host != null ? host.Ping : 0;
            return $"Peripeteia Coop | {_settings.Role} | peers={_net.ConnectedPeersCount} | " +
                   $"id={_localId} | ping={ping} ms | {_statusLine}";
        }
    }

    public int ConnectedPeersCount => _net.ConnectedPeersCount;

    /// <summary>Co-op id of this instance (host = 0).</summary>
    public int LocalId => _localId;

    /// <summary>The local player, once the game has spawned it.</summary>
    public PlayerMovement? LocalPlayer => _localPlayer;

    /// <summary>Root transform of a remote player, or null when it is not spawned.</summary>
    public Transform? GetAvatarRoot(int id)
    {
        if (_avatars.TryGetValue(id, out RemoteAvatar? avatar) &&
            avatar != null &&
            avatar.Root != null)
        {
            return avatar.Root.transform;
        }

        return null;
    }

    /// <summary>Ids of every remote player currently spawned on this machine.</summary>
    public IEnumerable<int> RemotePlayerIds()
    {
        foreach (int id in _avatars.Keys)
        {
            yield return id;
        }
    }

    /// <summary>The remote avatar for an id, or null when it is not spawned.</summary>
    public RemoteAvatar? GetAvatar(int id)
    {
        return _avatars.TryGetValue(id, out RemoteAvatar? avatar) ? avatar : null;
    }

    public CoopNetwork(CoopSettings settings)
    {
        _settings = settings;

        _listener = new EventBasedNetListener();
        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            UpdateTime = 15,
            // Two Unity instances can stall for several seconds while loading
            // the main menu. The default 5 s timeout made the first handshake
            // intermittently die with PeerNotFound.
            DisconnectTimeout = 20000,
            // LiteNetLib gives up after 10 attempts / ~5 s by default. If the
            // host process is still loading its scene, it accepts the request
            // only after the client has already reported ConnectionFailed.
            // Keep retrying long enough for both instances to reach the menu.
            MaxConnectAttempts = 120,
            ReconnectDelay = 500,
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public void Start()
    {
        if (_settings.Role == CoopRole.Host)
        {
            _localId = 0;

            if (!_net.Start(_settings.Port))
            {
                CoopPlugin.LogSource.LogError($"[net] Failed to start host on UDP port {_settings.Port}.");
                return;
            }

            _statusLine = "listening";
            CoopPlugin.LogSource.LogInfo(
                $"[net] Host listening on UDP {_settings.Port} as '{_settings.PlayerName}' " +
                $"(protocol={ProtocolVersion}, mod={PluginInfo.Version}, game='{Application.version}')."
            );
            return;
        }

        if (_settings.Role == CoopRole.Client)
        {
            _net.Start();
            _statusLine = "connecting...";
            CoopPlugin.LogSource.LogInfo(
                $"[net] Client connecting to {_settings.Address}:{_settings.Port} as '{_settings.PlayerName}' " +
                $"(protocol={ProtocolVersion}, mod={PluginInfo.Version}, game='{Application.version}')."
            );
            _net.Connect(_settings.Address, _settings.Port, ConnectionKey);
        }
    }

    public void Tick(PlayerMovement? localPlayer)
    {
        if (_disposed)
        {
            return;
        }

        _localPlayer = localPlayer;
        _net.PollEvents();
        DoorSync.Tick();
        ItemSync.Tick(localPlayer != null ? localPlayer.transform : null);
        WeaponSync.Tick(localPlayer);
        CombatSync.Tick(localPlayer);
        EnemySync.Tick(localPlayer);
        AiTargeting.Tick(localPlayer);
        PlayerStateSync.Tick(localPlayer);
        PlayerStateSync.FlushPending();
        StorySync.Tick(localPlayer);

        string localScene = localPlayer != null ? localPlayer.gameObject.scene.name : string.Empty;
        bool localReady = IsLocalPlayerReady(localPlayer);

        if (_settings.Role == CoopRole.Client &&
            _reconnectPending &&
            _net.ConnectedPeersCount == 0 &&
            Time.unscaledTime >= _nextReconnectTime)
        {
            _reconnectPending = false;
            _statusLine = "reconnecting...";
            CoopPlugin.LogSource.LogInfo(
                $"[net] Reconnecting to {_settings.Address}:{_settings.Port}..."
            );
            _net.Connect(_settings.Address, _settings.Port, ConnectionKey);
        }

        if (localPlayer != null)
        {
            foreach (RemoteAvatar avatar in _avatars.Values)
            {
                avatar.TryRefresh(localPlayer.transform);
            }
        }

        foreach (RemoteAvatar avatar in _avatars.Values)
        {
            bool visible = localReady &&
                           avatar.RemoteReady &&
                           avatar.HasPose &&
                           avatar.HasLanded &&
                           string.Equals(avatar.RemoteScene, localScene, StringComparison.Ordinal) &&
                           string.Equals(avatar.PoseScene, localScene, StringComparison.Ordinal);
            avatar.SetVisible(visible);

            if (CoopPlugin.ProbeEnabled && Time.unscaledTime >= _nextVisibilityLog)
            {
                _nextVisibilityLog = Time.unscaledTime + 2.0f;
                string active = avatar.Root != null && avatar.Root.activeSelf ? "True" : "False";
                string rootPos = avatar.Root != null ? avatar.Root.transform.position.ToString("F1") : "null";
                CoopPlugin.LogSource.LogInfo(
                    $"[vis] id={avatar.Id} visible={visible} active={active} root={rootPos} " +
                    $"localReady={localReady} remoteReady={avatar.RemoteReady} " +
                    $"pose={avatar.HasPose} landed={avatar.HasLanded} " +
                    $"remoteScene='{avatar.RemoteScene}' poseScene='{avatar.PoseScene}' localScene='{localScene}'"
                );
            }
        }

        ApplyAutoPilot(localPlayer);

        if (localPlayer != null)
        {
            SendLocalPose(localPlayer);
        }

        float deltaTime = Time.unscaledDeltaTime;
        UpdateCorpseDrag(localPlayer);
        foreach (RemoteAvatar avatar in _avatars.Values)
        {
            avatar.Update(deltaTime);
        }

        if (Time.unscaledTime >= _nextStatsTime)
        {
            _nextStatsTime = Time.unscaledTime + StatsInterval;

            string poses = string.Empty;
            foreach (RemoteAvatar avatar in _avatars.Values)
            {
                poses += string.Format(
                    CultureInfo.InvariantCulture,
                    " {0}@({1:F1},{2:F1},{3:F1})",
                    avatar.Id,
                    avatar.TargetPosition.x,
                    avatar.TargetPosition.y,
                    avatar.TargetPosition.z
                );
            }

            CoopPlugin.LogSource.LogInfo(
                $"[net] role={_settings.Role} peers={_net.ConnectedPeersCount} avatars={_avatars.Count} " +
                $"sent={_poseSent} received={_poseReceived} poses:{poses}"
            );

            if (_settings.Role != CoopRole.Offline)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[sync] enemies mirrored={EnemySync.MirrorsSent} deaths={EnemySync.DeathsSent}/{EnemySync.DeathsApplied} " +
                    $"aiTargets={AiTargeting.RegisteredCount} story={StorySync.EventsSent}/{StorySync.EventsApplied} " +
                    $"storyEffects={StorySync.EffectsApplied} playerDeaths={PlayerStateSync.DeathsSent}/{PlayerStateSync.DeathsApplied} " +
                    $"saveLoads={SaveLoadSync.LoadsRequested}/{SaveLoadSync.LoadsApplied} " +
                    $"scenes={SceneSync.ChangesSent}/{SceneSync.ChangesApplied}"
                );
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _net.Stop();

        foreach (RemoteAvatar avatar in _avatars.Values)
        {
            avatar.Destroy();
        }

        _avatars.Clear();
        _peerIds.Clear();
    }

    private void SendLocalPose(PlayerMovement localPlayer)
    {
        if (Time.unscaledTime < _nextPoseTime || _net.ConnectedPeersCount == 0)
        {
            return;
        }

        _nextPoseTime = Time.unscaledTime + PoseInterval;

        Transform transform = localPlayer.transform;
        Vector3 position = transform.position;
        float yaw = transform.eulerAngles.y;
        string sceneName = localPlayer.gameObject.scene.name;
        bool ready = IsLocalPlayerReady(localPlayer);

        Vector2 animatorDirection = localPlayer.AnimatorDirectionNormalized;
        float speed = localPlayer.animatorMagnitude;
        float crouchTimer = localPlayer.crouchTimer;
        IKControl? localIk = localPlayer.GetComponentInChildren<IKControl>();
        ObjectHighlight? localHighlight = localPlayer.GetComponentInChildren<ObjectHighlight>();
        if (localHighlight == null)
        {
            // ObjectHighlight may live on the camera pivot/frob helper rather
            // than under the Player root.
            localHighlight = UnityEngine.Object.FindObjectOfType<ObjectHighlight>();
        }

        // IKControl.LeanTwist is the value the game uses for the first-person
        // body lean. On some builds it is only populated after the IK pass, so
        // fall back to PlayerMovement.leanAmount while it is still zero.
        float lean = localIk != null && Mathf.Abs(localIk.LeanTwist) > 0.01f
            ? localIk.LeanTwist
            : localPlayer.leanAmount * 20.0f;
        lean = SanitizeLean(lean);
        bool dragging = IsLocalPlayerDragging(localPlayer, localHighlight);
        bool actionActive = WeaponSync.TryGetBodyAction(
            localPlayer,
            out int actionHash,
            out float actionTime
        );

        if (CoopPlugin.ProbeEnabled && Time.unscaledTime >= _nextLeanLog)
        {
            _nextLeanLog = Time.unscaledTime + 0.5f;
            CoopPlugin.LogSource.LogInfo(
                $"[pose] lean={lean:F2} leanAmount={localPlayer.leanAmount:F2} " +
                $"leanTwist={(localIk != null ? localIk.LeanTwist : 0.0f):F2} " +
                $"dragging={dragging} scene='{sceneName}' ready={ready}"
            );
        }

        PoseFlags flags = PoseFlags.None;
        if (localPlayer.isCrouched)
        {
            flags |= PoseFlags.Crouch;
        }

        if (localPlayer.IsSprinting)
        {
            flags |= PoseFlags.Sprint;
        }

        if (localPlayer.IsOnGround)
        {
            flags |= PoseFlags.Grounded;
        }

        if (localPlayer.IsInAir)
        {
            flags |= PoseFlags.InAir;
        }

        if (localPlayer.IsJumping)
        {
            flags |= PoseFlags.Jumping;
        }

        if (localPlayer.LedgeGrab)
        {
            flags |= PoseFlags.Shimmy;
        }

        var writer = new NetDataWriter();
        WritePose(
            writer,
            _localId,
            position,
            yaw,
            animatorDirection,
            speed,
            crouchTimer,
            flags,
            sceneName,
            ready,
            lean,
            dragging,
            actionHash,
            actionTime,
            actionActive
        );

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.Unreliable);
            _poseSent++;
            return;
        }

        NetPeer host = _net.FirstPeer;
        if (host != null)
        {
            host.Send(writer, DeliveryMethod.Unreliable);
            _poseSent++;
        }
    }

    public void SendDoorState(string key, bool open, bool locked)
    {
        if (_settings.Role != CoopRole.Host || _net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteDoorState(writer, key, open, locked);
        _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendDoorStateTo(NetPeer peer, string key, bool open, bool locked)
    {
        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteDoorState(writer, key, open, locked);
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static void WriteDoorState(NetDataWriter writer, string key, bool open, bool locked)
    {
        writer.Put((byte)PacketType.DoorState);
        writer.Put(key);
        writer.Put(open);
        writer.Put(locked);
    }

    public void SendItemState(
        string key,
        bool alive,
        bool active,
        Vector3 position,
        Quaternion rotation,
        bool moving,
        bool reliable
    )
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteItemState(writer, key, alive, active, position, rotation, moving);
        DeliveryMethod delivery = reliable
            ? DeliveryMethod.ReliableOrdered
            : DeliveryMethod.Sequenced;

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, delivery);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, delivery);
    }

    public void SendItemStateTo(
        NetPeer peer,
        string key,
        bool alive,
        bool active,
        Vector3 position,
        Quaternion rotation,
        bool moving,
        bool reliable
    )
    {
        var writer = new NetDataWriter();
        WriteItemState(writer, key, alive, active, position, rotation, moving);
        peer.Send(
            writer,
            reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced
        );
    }

    private static void WriteItemState(
        NetDataWriter writer,
        string key,
        bool alive,
        bool active,
        Vector3 position,
        Quaternion rotation,
        bool moving
    )
    {
        writer.Put((byte)PacketType.ItemState);
        writer.Put(key);
        writer.Put(alive);
        writer.Put(active);
        writer.Put(moving);
        writer.Put(position.x);
        writer.Put(position.y);
        writer.Put(position.z);
        writer.Put(rotation.x);
        writer.Put(rotation.y);
        writer.Put(rotation.z);
        writer.Put(rotation.w);
    }

    /// <summary>
    /// Immediate, reliable pickup/drop notification. The polling channel still
    /// carries the transforms; this event makes a share instant and carries the
    /// item identity so a peer that never saw the object can spawn it.
    /// </summary>
    public void SendItemEvent(
        string key,
        byte kind,
        Vector3 position,
        Quaternion rotation,
        string itemName,
        string templateName
    )
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteItemEvent(writer, key, kind, position, rotation, itemName, templateName);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static void WriteItemEvent(
        NetDataWriter writer,
        string key,
        byte kind,
        Vector3 position,
        Quaternion rotation,
        string itemName,
        string templateName
    )
    {
        writer.Put((byte)PacketType.ItemEvent);
        writer.Put(key);
        writer.Put(kind);
        writer.Put(position.x);
        writer.Put(position.y);
        writer.Put(position.z);
        writer.Put(rotation.x);
        writer.Put(rotation.y);
        writer.Put(rotation.z);
        writer.Put(rotation.w);
        writer.Put(itemName ?? string.Empty);
        writer.Put(templateName ?? string.Empty);
    }

    /// <summary>
    /// Host-only: broadcasts the authoritative state of one enemy/NPC so clients
    /// can mirror it. Sequenced (not reliable) because a newer snapshot replaces
    /// an older one anyway.
    /// </summary>
    public void SendEnemyState(
        string key,
        Vector3 position,
        Quaternion rotation,
        int hp,
        int maxHp,
        int stateHash,
        float normalizedTime,
        float speed,
        int[] animHashes,
        byte[] animTypes,
        float[] animValues,
        int animCount
    )
    {
        if (_settings.Role != CoopRole.Host || _net.ConnectedPeersCount == 0)
        {
            return;
        }

        if (animCount < 0)
        {
            animCount = 0;
        }

        if (animCount > animHashes.Length ||
            animCount > animTypes.Length ||
            animCount > animValues.Length)
        {
            animCount = Math.Min(
                animHashes.Length,
                Math.Min(animTypes.Length, animValues.Length)
            );
        }

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.EnemyState);
        writer.Put(key);
        writer.Put(position.x);
        writer.Put(position.y);
        writer.Put(position.z);
        writer.Put(rotation.x);
        writer.Put(rotation.y);
        writer.Put(rotation.z);
        writer.Put(rotation.w);
        writer.Put(hp);
        writer.Put(maxHp);
        writer.Put(stateHash);
        writer.Put(normalizedTime);
        writer.Put(speed);
        writer.Put((byte)animCount);
        for (int i = 0; i < animCount; i++)
        {
            writer.Put(animHashes[i]);
            writer.Put(animTypes[i]);
            writer.Put(animValues[i]);
        }

        _net.SendToAll(writer, DeliveryMethod.Sequenced);
    }

    /// <summary>
    /// Reports that an enemy died. Both directions are allowed: either player can
    /// land the killing blow, and the host relays a client's report to the others.
    /// </summary>
    public void SendEnemyDeath(string key)
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.EnemyDeath);
        writer.Put(key);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Reliable story event: a consumed dialogue/quest object or a scripted
    /// teleport, together with any world GameObjects that must be switched on the
    /// other side. Both directions are allowed; the host relays between clients.
    /// </summary>
    public void SendStoryEvent(
        string key,
        byte kind,
        Vector3 point,
        string[] objectKeys,
        bool[] objectStates
    )
    {
        SendStoryEvent(key, kind, point, string.Empty, 0, objectKeys, objectStates);
    }

    public void SendStoryEvent(
        string key,
        byte kind,
        Vector3 point,
        string text,
        int number,
        string[] objectKeys,
        bool[] objectStates
    )
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteStoryEvent(writer, key, kind, point, text, number, objectKeys, objectStates);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static void WriteStoryEvent(
        NetDataWriter writer,
        string key,
        byte kind,
        Vector3 point,
        string text,
        int number,
        string[] objectKeys,
        bool[] objectStates
    )
    {
        writer.Put((byte)PacketType.StoryEvent);
        writer.Put(key ?? string.Empty);
        writer.Put(kind);
        writer.Put(point.x);
        writer.Put(point.y);
        writer.Put(point.z);
        writer.Put(text ?? string.Empty);
        writer.Put(number);

        if (objectKeys == null || objectStates == null)
        {
            writer.Put(0);
            return;
        }

        int count = Mathf.Min(objectKeys.Length, objectStates.Length);
        writer.Put(count);
        for (int i = 0; i < count; i++)
        {
            writer.Put(objectKeys[i] ?? string.Empty);
            writer.Put(objectStates[i]);
        }
    }

    /// <summary>
    /// Reliable player death/revive event. Sent by the player whose health
    /// changed; the host relays it to the other clients.
    /// </summary>
    public void SendPlayerDeath(int id, bool dead, Vector3 position, Quaternion rotation)
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WritePlayerDeath(writer, id, dead, position, rotation);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static void WritePlayerDeath(
        NetDataWriter writer,
        int id,
        bool dead,
        Vector3 position,
        Quaternion rotation
    )
    {
        writer.Put((byte)PacketType.PlayerDeath);
        writer.Put(id);
        writer.Put(dead);
        writer.Put(position.x);
        writer.Put(position.y);
        writer.Put(position.z);
        writer.Put(rotation.x);
        writer.Put(rotation.y);
        writer.Put(rotation.z);
        writer.Put(rotation.w);
    }

    public void SendWeaponState(WeaponSnapshot snapshot)
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteWeaponState(writer, _localId, snapshot);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.Sequenced);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.Sequenced);
    }

    public void SendWeaponEvent(byte eventKind)
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.WeaponEvent);
        writer.Put(_localId);
        writer.Put(eventKind);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Reports a bullet hit on another player. The victim applies the damage on
    /// its own machine through the game's real HealthScript, so the shooter
    /// never mutates remote health directly.
    /// </summary>
    public void SendDamage(int targetId, int amount, bool headshot, Vector3 point)
    {
        SendDamage(targetId, _localId, amount, headshot, point);
    }

    /// <summary>
    /// Reports a hit with an explicit dealer. The host uses this for NPC damage,
    /// where the shooter is not a player at all and there is no avatar to credit;
    /// <paramref name="dealerId"/> is then a negative sentinel so the victim does
    /// not mistake the damage for friendly fire from the other player.
    /// </summary>
    public void SendDamage(int targetId, int dealerId, int amount, bool headshot, Vector3 point)
    {
        if (_net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteDamage(writer, targetId, dealerId, amount, headshot, point);

        if (_settings.Role == CoopRole.Host)
        {
            NetPeer? target = FindPeer(targetId);
            target?.Send(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static void WriteDamage(
        NetDataWriter writer,
        int targetId,
        int dealerId,
        int amount,
        bool headshot,
        Vector3 point
    )
    {
        writer.Put((byte)PacketType.Damage);
        writer.Put(targetId);
        writer.Put(dealerId);
        writer.Put(amount);
        writer.Put(headshot);
        writer.Put(point.x);
        writer.Put(point.y);
        writer.Put(point.z);
    }

    /// <summary>
    /// Client only: reports damage the local player dealt to an NPC. The host
    /// owns every NPC, so without this the hit is thrown away as soon as the next
    /// snapshot overwrites the mirrored health, and the enemy never turns on the
    /// shooter. <paramref name="key"/> identifies the NPC on the host.
    /// </summary>
    public void SendNpcDamage(string key, int amount, bool headshot, Vector3 point)
    {
        if (_settings.Role != CoopRole.Client || _net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.NpcDamage);
        writer.Put(key ?? string.Empty);
        writer.Put(amount);
        writer.Put(headshot);
        writer.Put(point.x);
        writer.Put(point.y);
        writer.Put(point.z);

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private NetPeer? FindPeer(int id)
    {
        foreach (KeyValuePair<NetPeer, int> pair in _peerIds)
        {
            if (pair.Value == id)
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static void WriteWeaponState(NetDataWriter writer, int senderId, WeaponSnapshot snapshot)
    {
        writer.Put((byte)PacketType.WeaponState);
        writer.Put(senderId);
        writer.Put(snapshot.Visible);
        writer.Put(snapshot.ItemName ?? string.Empty);
        writer.Put(snapshot.TemplateName ?? string.Empty);
        writer.Put(snapshot.ParentPath ?? string.Empty);
        writer.Put(snapshot.MuzzlePath ?? string.Empty);
        writer.Put(snapshot.VisualHash);
        writer.Put(snapshot.LocalPosition.x);
        writer.Put(snapshot.LocalPosition.y);
        writer.Put(snapshot.LocalPosition.z);
        writer.Put(snapshot.LocalRotation.x);
        writer.Put(snapshot.LocalRotation.y);
        writer.Put(snapshot.LocalRotation.z);
        writer.Put(snapshot.LocalRotation.w);
        writer.Put(snapshot.LocalScale.x);
        writer.Put(snapshot.LocalScale.y);
        writer.Put(snapshot.LocalScale.z);
        writer.Put(snapshot.LossyScale.x);
        writer.Put(snapshot.LossyScale.y);
        writer.Put(snapshot.LossyScale.z);
        writer.Put(snapshot.RootRotation.x);
        writer.Put(snapshot.RootRotation.y);
        writer.Put(snapshot.RootRotation.z);
        writer.Put(snapshot.RootRotation.w);
        writer.Put(snapshot.CameraAim.x);
        writer.Put(snapshot.CameraAim.y);
        writer.Put(snapshot.CameraAim.z);
        writer.Put(snapshot.HasHandGrip);
        writer.Put(snapshot.HandGripLocalPosition.x);
        writer.Put(snapshot.HandGripLocalPosition.y);
        writer.Put(snapshot.HandGripLocalPosition.z);
        writer.Put(snapshot.HandGripLocalRotation.x);
        writer.Put(snapshot.HandGripLocalRotation.y);
        writer.Put(snapshot.HandGripLocalRotation.z);
        writer.Put(snapshot.HandGripLocalRotation.w);
        writer.Put(snapshot.RightGrabPath ?? string.Empty);
        writer.Put(snapshot.LeftGrabPath ?? string.Empty);
        writer.Put(snapshot.OneHanded);
        writer.Put(snapshot.HandRotationMod.x);
        writer.Put(snapshot.HandRotationMod.y);
        writer.Put(snapshot.HandRotationMod.z);
        writer.Put(snapshot.AnimatorHash);
        writer.Put(snapshot.AnimatorTime);
        writer.Put(snapshot.AnimatorActive);
    }

    private static WeaponSnapshot ReadWeaponState(NetDataReader reader, out int senderId)
    {
        senderId = reader.GetInt();
        bool visible = reader.GetBool();
        string itemName = reader.GetString();
        string templateName = reader.GetString();
        string parentPath = reader.GetString();
        string muzzlePath = reader.GetString();
        int visualHash = reader.GetInt();
        var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var rotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        var scale = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var lossyScale = new Vector3(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        var rootRotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        var cameraAim = new Vector3(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        bool hasHandGrip = reader.GetBool();
        var handGripLocalPosition = new Vector3(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        var handGripLocalRotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        string rightGrabPath = reader.GetString();
        string leftGrabPath = reader.GetString();
        bool oneHanded = reader.GetBool();
        var handRotationMod = new Vector3(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        int animatorHash = reader.GetInt();
        float animatorTime = reader.GetFloat();
        bool animatorActive = reader.GetBool();

        return new WeaponSnapshot(
            visible,
            itemName,
            templateName,
            parentPath,
            string.Empty,
            muzzlePath,
            visualHash,
            position,
            rotation,
            scale,
            lossyScale,
            rootRotation,
            cameraAim,
            hasHandGrip,
            handGripLocalPosition,
            handGripLocalRotation,
            rightGrabPath,
            leftGrabPath,
            oneHanded,
            handRotationMod,
            animatorHash,
            animatorTime,
            animatorActive,
            null
        );
    }

    private static void WritePose(
        NetDataWriter writer,
        int id,
        Vector3 position,
        float yaw,
        Vector2 animatorDirection,
        float speed,
        float crouchTimer,
        PoseFlags flags,
        string sceneName,
        bool ready,
        float lean,
        bool dragging,
        int actionHash,
        float actionTime,
        bool actionActive)
    {
        writer.Put((byte)PacketType.Pose);
        writer.Put(id);
        writer.Put(position.x);
        writer.Put(position.y);
        writer.Put(position.z);
        writer.Put(yaw);
        writer.Put(animatorDirection.x);
        writer.Put(animatorDirection.y);
        writer.Put(speed);
        writer.Put(crouchTimer);
        writer.Put((byte)flags);
        writer.Put(sceneName);
        writer.Put(ready);
        writer.Put(lean);
        writer.Put(dragging);
        writer.Put(actionHash);
        writer.Put(actionTime);
        writer.Put(actionActive);
    }

    private static float SanitizeLean(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0.0f;
        }

        // Send degrees when IKControl is available. Clamp so a bad read cannot
        // fold the remote skeleton in half.
        return Mathf.Clamp(value, -45.0f, 45.0f);
    }

    /// <summary>
    /// Carries a teammate's corpse: look at the body, hold the game's interact
    /// key, and the body follows the aim while its pose is streamed to the other
    /// machine. Releasing the key drops it, and it is pinned again once settled.
    ///
    /// The carry is latched on the grab: while the key stays down the body keeps
    /// following the aim even when the crosshair no longer touches it, so the
    /// body cannot be dropped and re-grabbed every frame as it is pulled along.
    /// </summary>
    private void UpdateCorpseDrag(PlayerMovement? localPlayer)
    {
        ObjectHighlight? highlight = null;
        Camera? camera = null;
        bool holding = false;

        if (CoopPlugin.PlayerDragRemoteBody &&
            _settings.Role != CoopRole.Offline &&
            localPlayer != null &&
            _avatars.Count > 0)
        {
            highlight = LocalHighlight(localPlayer);
            camera = Camera.main;
            holding = highlight != null &&
                      highlight.interactBind != null &&
                      highlight.interactBind.down() &&
                      camera != null;
        }

        if (!holding)
        {
            ReleaseCorpseDrag();
            return;
        }

        Transform view = camera!.transform;

        // Keep the body we already grabbed as long as it is still down.
        if (_corpseDragTarget != null &&
            (!_avatars.ContainsValue(_corpseDragTarget) || !_corpseDragTarget.IsDead))
        {
            _corpseDragTarget = null;
        }

        if (_corpseDragTarget == null)
        {
            if (!TryGrabCorpse(view, out RemoteAvatar? grabbed, out float hold))
            {
                ReleaseCorpseDrag();
                return;
            }

            _corpseDragTarget = grabbed;
            _corpseDragHold = hold;
            CoopPlugin.LogSource.LogInfo(
                $"[death] holding the corpse of remote {grabbed!.Id} at {hold:F2}m."
            );
        }

        Vector3 holdPoint = view.position + view.forward * _corpseDragHold;
        _corpseDragTarget!.BeginCorpseDrag(holdPoint);
        _corpseDragTarget.DragCorpse(holdPoint);
    }

    /// <summary>
    /// Finds the corpse the player is looking at. Every hit along the view ray is
    /// considered so a collider that is not part of a ragdoll (the local player's
    /// own body, the clone's capsule) cannot shadow the body behind it.
    /// </summary>
    private bool TryGrabCorpse(
        Transform view,
        out RemoteAvatar? target,
        out float hold)
    {
        target = null;
        hold = DefaultDragHold;

        try
        {
            RaycastHit[] hits = Physics.RaycastAll(
                view.position,
                view.forward,
                DragReach,
                ~0,
                QueryTriggerInteraction.Ignore
            );

            float best = float.MaxValue;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                {
                    continue;
                }

                foreach (RemoteAvatar avatar in _avatars.Values)
                {
                    if (!avatar.IsRagdollCollider(hit.collider))
                    {
                        continue;
                    }

                    if (hit.distance < best)
                    {
                        best = hit.distance;
                        target = avatar;
                    }

                    break;
                }
            }

            if (target != null)
            {
                hold = Mathf.Clamp(best, 1.0f, DragReach);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] corpse grab check failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }

        return target != null;
    }

    /// <summary>Drops the carried corpse (if any) and lets it settle again.</summary>
    private void ReleaseCorpseDrag()
    {
        if (_corpseDragTarget == null)
        {
            return;
        }

        _corpseDragTarget = null;
        foreach (RemoteAvatar avatar in _avatars.Values)
        {
            avatar.EndCorpseDrag();
        }
    }

    private ObjectHighlight? LocalHighlight(PlayerMovement localPlayer)
    {
        if (_localHighlight != null)
        {
            return _localHighlight;
        }

        _localHighlight = localPlayer.GetComponentInChildren<ObjectHighlight>();
        if (_localHighlight == null)
        {
            // ObjectHighlight may live on the camera pivot/frob helper rather
            // than under the Player root.
            _localHighlight = UnityEngine.Object.FindObjectOfType<ObjectHighlight>();
        }

        return _localHighlight;
    }

    private static bool IsLocalPlayerDragging(
        PlayerMovement localPlayer,
        ObjectHighlight? highlight)
    {
        // IKControl.dragging is not a reliable gameplay flag: it stays true
        // while the hands are merely doing weapon/IK work. ObjectHighlight owns
        // the real world-object drag and exposes isDragging publicly.
        if (highlight != null &&
            (highlight.isDragging || highlight.objectDragged != null || highlight.attached != null))
        {
            return true;
        }

        return localPlayer.holdingTo != null || localPlayer.droptimer > 0.01f;
    }

    private static bool IsLocalPlayerReady(PlayerMovement? localPlayer)
    {
        if (localPlayer == null || !localPlayer.gameObject.activeInHierarchy)
        {
            return false;
        }

        string sceneName = localPlayer.gameObject.scene.name;
        return !string.IsNullOrEmpty(sceneName) &&
               sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>
    /// Debug-only solo test helper. Moves the local player in a small circle so
    /// the other instance can prove that pose replication actually updates the
    /// remote avatar. Never enabled unless -coop-autopilot is passed.
    /// </summary>
    private void ApplyAutoPilot(PlayerMovement? localPlayer)
    {
        if (!_settings.AutoPilot || localPlayer == null)
        {
            return;
        }

        int playerId = localPlayer.GetInstanceID();
        if (playerId != _autopilotPlayerId)
        {
            _autopilotPlayerId = playerId;
            _autopilotOriginSet = false;
            _autopilotGroundedSince = 0.0f;
        }

        if (!_autopilotOriginSet)
        {
            // The save load happens after the tutorial scene is created. The
            // first player object can therefore stand at a temporary spawn
            // high above the level. Capturing that position made the test
            // client hover in the sky forever. Wait until the player has been
            // grounded for a moment, then use the loaded save position.
            if (!localPlayer.IsOnGround)
            {
                _autopilotGroundedSince = 0.0f;
                return;
            }

            if (_autopilotGroundedSince <= 0.0f)
            {
                _autopilotGroundedSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _autopilotGroundedSince < 2.0f)
            {
                return;
            }

            _autopilotOrigin = localPlayer.transform.position;
            _autopilotOriginSet = true;
        }

        if (_settings.AutoPilotHunt)
        {
            ApplyAutoPilotHunt(localPlayer);
            return;
        }

        if (Time.unscaledTime < _nextAutopilotTime)
        {
            return;
        }

        _nextAutopilotTime = Time.unscaledTime + 0.1f;

        float angle = Time.unscaledTime * 1.2f;
        var target = _autopilotOrigin + new Vector3(Mathf.Cos(angle) * 2.0f, 0.0f, Mathf.Sin(angle) * 2.0f);

        localPlayer.TeleportPlayer(target, true);
    }

    /// <summary>
    /// Debug-only solo test mode (-coop-autopilot-hunt). On the host it walks the
    /// host player to the nearest hostile enemy; on the client it follows the host
    /// avatar. Keeping both players together is the point: the host only simulates
    /// AI near its own body, so an enemy the client walked to on its own stays
    /// dormant until the host arrives too.
    /// </summary>
    private void ApplyAutoPilotHunt(PlayerMovement localPlayer)
    {
        if (Time.unscaledTime < _nextAutopilotTime)
        {
            return;
        }

        _nextAutopilotTime = Time.unscaledTime + 0.4f;

        if (_settings.Role == CoopRole.Host)
        {
            HuntHost(localPlayer);
        }
        else
        {
            FollowHost(localPlayer);

            if (_settings.AutoPilotShoot)
            {
                TestClientNpcDamage(localPlayer);
            }
        }
    }

    /// <summary>
    /// Debug-only: makes the client deal damage to the nearest hostile NPC so the
    /// host can prove that client damage is forwarded, applied to the real enemy
    /// and answered. Damage goes through the game's own HealthScript.TakeDMG, which
    /// is exactly the call the mod hooks, so it exercises the real path.
    /// </summary>
    private void TestClientNpcDamage(PlayerMovement localPlayer)
    {
        if (Time.unscaledTime < _nextShootTime)
        {
            return;
        }

        _nextShootTime = Time.unscaledTime + 2.5f;

        Vector3 position = localPlayer.transform.position;
        EnemyController? nearest = null;
        float best = 45.0f;

        foreach (EnemyController enemy in UnityEngine.Object.FindObjectsOfType<EnemyController>())
        {
            if (enemy == null || enemy.gameObject == null)
            {
                continue;
            }

            bool friendly;
            try
            {
                friendly = enemy.friendly;
            }
            catch (Exception)
            {
                friendly = true;
            }

            if (friendly)
            {
                continue;
            }

            float distance = Vector3.Distance(position, enemy.transform.position);
            if (distance < best)
            {
                best = distance;
                nearest = enemy;
            }
        }

        if (nearest == null)
        {
            LogHunt("client: shoot test found no hostile NPC within 45m");
            return;
        }

        HealthScript? health;
        try
        {
            health = nearest.health;
        }
        catch (Exception)
        {
            health = null;
        }

        if (health == null || health.dead)
        {
            LogHunt($"client: shoot test skipped, '{nearest.CharacterName}' has no live health");
            return;
        }

        Vector3 point;
        try
        {
            point = health.centerOfMass != null ? health.centerOfMass.position : nearest.transform.position;
        }
        catch (Exception)
        {
            point = nearest.transform.position;
        }

        LogHunt($"client: shooting '{nearest.CharacterName}' d={best:F1} hp={health.HP}");
        try
        {
            health.TakeDMG(false, 5, localPlayer.transform, false, point, false);
        }
        catch (Exception exception)
        {
            LogHunt($"client: shoot test failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void HuntHost(PlayerMovement localPlayer)
    {
        Vector3 position = localPlayer.transform.position;
        EnemyController? nearest = null;
        float best = float.MaxValue;
        int hostiles = 0;

        foreach (EnemyController enemy in UnityEngine.Object.FindObjectsOfType<EnemyController>())
        {
            if (enemy == null)
            {
                continue;
            }

            bool friendly;
            try
            {
                friendly = enemy.friendly;
            }
            catch (Exception)
            {
                friendly = true;
            }

            if (friendly)
            {
                continue;
            }

            hostiles++;
            float distance = Vector3.Distance(position, enemy.transform.position);
            if (distance < best)
            {
                best = distance;
                nearest = enemy;
            }
        }

        if (nearest == null)
        {
            LogHunt($"host: no hostile enemy found (hostiles={hostiles}) self={position}");
            return;
        }

        Vector3 target = nearest.transform.position;
        if (best <= 12.0f)
        {
            LogHunt($"host: holding near '{nearest.CharacterName}' at {target} d={best:F1} self={position}");
            return;
        }

        Vector3 away = position - target;
        away.y = 0.0f;
        if (away.sqrMagnitude < 0.01f)
        {
            away = Vector3.forward;
        }

        away.Normalize();
        Vector3 destination = target + away * 6.0f;
        destination.y = target.y + 0.2f;

        LogHunt($"host: hunting '{nearest.CharacterName}' at {target} d={best:F1} self={position} -> {destination}");
        localPlayer.TeleportPlayer(destination, true);
    }

    private void FollowHost(PlayerMovement localPlayer)
    {
        Transform? host = GetAvatarRoot(0);
        if (host == null)
        {
            LogHunt("client: host avatar not available yet");
            return;
        }

        Vector3 target = host.position;
        Vector3 position = localPlayer.transform.position;
        float distance = Vector3.Distance(position, target);
        if (distance <= 3.0f)
        {
            LogHunt($"client: with host at {target} d={distance:F1} self={position}");
            return;
        }

        Vector3 destination = target + new Vector3(2.0f, 0.0f, 2.0f);
        LogHunt($"client: following host at {target} d={distance:F1} self={position} -> {destination}");
        localPlayer.TeleportPlayer(destination, true);
    }

    private void LogHunt(string message)
    {
        if (Time.unscaledTime < _nextHuntLog)
        {
            return;
        }

        _nextHuntLog = Time.unscaledTime + 2.0f;
        CoopPlugin.LogSource.LogInfo($"[hunt] {message}");
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_settings.Role != CoopRole.Host)
        {
            request.Reject();
            return;
        }

        CoopPlugin.LogSource.LogInfo($"[net] Connection request from {request.RemoteEndPoint}.");
        request.AcceptIfKey(ConnectionKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        if (_settings.Role == CoopRole.Host)
        {
            int id = _nextPeerId++;
            _peerIds[peer] = id;

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.Welcome);
            writer.Put(id);
            writer.Put(ProtocolVersion);
            writer.Put(PluginInfo.Version);
            writer.Put(Application.version);
            writer.Put(_settings.PlayerName);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);

            EnsureAvatar(id, $"Player {id}");
            ItemSync.SendSnapshot();
            _statusLine = "peer connected";

            CoopPlugin.LogSource.LogInfo(
                $"[net] Peer connected: id={id} endpoint={peer.Address}:{peer.Port}."
            );
            return;
        }

        if (_settings.Role == CoopRole.Client)
        {
            _reconnectPending = false;
            _statusLine = "connected";
            CoopPlugin.LogSource.LogInfo($"[net] Handshake: connected to {peer.Address}:{peer.Port}, sending Hello.");

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.Hello);
            writer.Put(ProtocolVersion);
            writer.Put(PluginInfo.Version);
            writer.Put(Application.version);
            writer.Put(_settings.PlayerName);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);

            EnsureAvatar(0, "Host");

            CoopPlugin.LogSource.LogInfo($"[net] Connected to host {peer.Address}:{peer.Port}.");
        }
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_peerIds.TryGetValue(peer, out int id))
        {
            _peerIds.Remove(peer);

            if (_avatars.TryGetValue(id, out RemoteAvatar? avatar) && avatar != null)
            {
                avatar.Destroy();
                _avatars.Remove(id);
            }

            CoopPlugin.LogSource.LogInfo(
                $"[net] Peer {id} disconnected: {disconnectInfo.Reason}."
            );
        }

        if (_settings.Role == CoopRole.Client)
        {
            foreach (RemoteAvatar avatar in _avatars.Values)
            {
                avatar.Destroy();
            }

            _avatars.Clear();
            _reconnectPending = true;
            _nextReconnectTime = Time.unscaledTime + 2.0f;
            _statusLine = $"lost: {disconnectInfo.Reason}";
            CoopPlugin.LogSource.LogInfo(
                $"[net] Lost connection to host ({peer.Address}:{peer.Port}): {disconnectInfo.Reason}. " +
                "Reconnecting in 2 s."
            );
        }
        else
        {
            _statusLine = "no peers";
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes < 1)
        {
            return;
        }

        var type = (PacketType)reader.GetByte();

        if (type != PacketType.Pose &&
            type != PacketType.DoorState &&
            type != PacketType.ItemState &&
            type != PacketType.WeaponState &&
            type != PacketType.WeaponEvent &&
            type != PacketType.Damage &&
            type != PacketType.ItemEvent &&
            type != PacketType.EnemyState &&
            type != PacketType.EnemyDeath &&
            type != PacketType.PlayerDeath &&
            type != PacketType.StoryEvent &&
            type != PacketType.NpcDamage)
        {
            CoopPlugin.LogSource.LogInfo($"[net] Received {type} from {peer.Address}:{peer.Port}.");
        }

        switch (type)
        {
            case PacketType.Hello:
                ReceiveHello(peer, reader);
                break;

            case PacketType.Welcome:
                ReceiveWelcome(peer, reader);
                break;

            case PacketType.Pose:
                ReceivePose(peer, reader);
                break;

            case PacketType.DoorState:
                ReceiveDoorState(reader);
                break;

            case PacketType.ItemState:
                ReceiveItemState(peer, reader);
                break;

            case PacketType.WeaponState:
                ReceiveWeaponState(peer, reader);
                break;

            case PacketType.WeaponEvent:
                ReceiveWeaponEvent(peer, reader);
                break;

            case PacketType.Damage:
                ReceiveDamage(peer, reader);
                break;

            case PacketType.ItemEvent:
                ReceiveItemEvent(peer, reader);
                break;

            case PacketType.EnemyState:
                ReceiveEnemyState(peer, reader);
                break;

            case PacketType.EnemyDeath:
                ReceiveEnemyDeath(peer, reader);
                break;

            case PacketType.PlayerDeath:
                ReceivePlayerDeath(peer, reader);
                break;

            case PacketType.StoryEvent:
                ReceiveStoryEvent(peer, reader);
                break;

            case PacketType.SaveLoad:
                ReceiveSaveLoad(peer, reader);
                break;

            case PacketType.SceneChange:
                ReceiveSceneChange(peer, reader);
                break;

            case PacketType.CorpsePose:
                ReceiveCorpsePose(peer, reader);
                break;

            case PacketType.NpcDamage:
                ReceiveNpcDamage(peer, reader);
                break;
        }
    }

    /// <summary>
    /// Host only: tells every client which save the host just rolled back to, so
    /// both machines rewind the shared world together.
    /// </summary>
    public void SendSaveLoad(string saveName)
    {
        if (_settings.Role != CoopRole.Host || _net.ConnectedPeersCount == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.SaveLoad);
        writer.Put(saveName ?? string.Empty);
        _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
    }

    private void ReceiveSaveLoad(NetPeer peer, NetDataReader reader)
    {
        if (_settings.Role == CoopRole.Host)
        {
            return;
        }

        string saveName = reader.GetString();
        SaveLoadSync.ApplyRemoteLoad(saveName);
    }

    /// <summary>
    /// A level transition. Either player can lead; the host relays it to the
    /// other clients so the whole party follows the same one.
    /// </summary>
    public void SendSceneChange(string levelName)
    {
        if (_settings.Role == CoopRole.Offline ||
            _net.ConnectedPeersCount == 0 ||
            string.IsNullOrEmpty(levelName))
        {
            return;
        }

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.SceneChange);
        writer.Put(levelName);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private void ReceiveSceneChange(NetPeer peer, NetDataReader reader)
    {
        string levelName = reader.GetString();

        if (_settings.Role == CoopRole.Host)
        {
            var relay = new NetDataWriter();
            relay.Put((byte)PacketType.SceneChange);
            relay.Put(levelName);
            foreach (NetPeer other in _net.ConnectedPeerList)
            {
                if (other != peer)
                {
                    other.Send(relay, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        SceneSync.ApplyRemoteTransition(levelName);
    }

    /// <summary>
    /// Pose of a remote corpse while the local player is carrying it: one
    /// position/rotation per ragdoll part. Sent unreliably and often, like the
    /// player pose, so a lost packet is harmless.
    /// </summary>
    public void SendCorpsePose(int id, Vector3[] positions, Quaternion[] rotations)
    {
        if (_settings.Role == CoopRole.Offline ||
            _net.ConnectedPeersCount == 0 ||
            positions == null ||
            rotations == null)
        {
            return;
        }

        int count = Mathf.Min(positions.Length, rotations.Length);
        if (count == 0)
        {
            return;
        }

        var writer = new NetDataWriter();
        WriteCorpsePose(writer, id, positions, rotations, count);

        if (_settings.Role == CoopRole.Host)
        {
            _net.SendToAll(writer, DeliveryMethod.Unreliable);
            return;
        }

        NetPeer? host = _net.FirstPeer;
        host?.Send(writer, DeliveryMethod.Unreliable);
    }

    private static void WriteCorpsePose(
        NetDataWriter writer,
        int id,
        Vector3[] positions,
        Quaternion[] rotations,
        int count
    )
    {
        writer.Put((byte)PacketType.CorpsePose);
        writer.Put(id);
        writer.Put((byte)count);
        for (int i = 0; i < count; i++)
        {
            writer.Put(positions[i].x);
            writer.Put(positions[i].y);
            writer.Put(positions[i].z);
            writer.Put(rotations[i].x);
            writer.Put(rotations[i].y);
            writer.Put(rotations[i].z);
            writer.Put(rotations[i].w);
        }
    }

    private void ReceiveCorpsePose(NetPeer peer, NetDataReader reader)
    {
        int id = reader.GetInt();
        int count = reader.GetByte();

        var positions = new Vector3[count];
        var rotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            rotations[i] = new Quaternion(
                reader.GetFloat(),
                reader.GetFloat(),
                reader.GetFloat(),
                reader.GetFloat()
            );
        }

        if (id != _localId && _avatars.TryGetValue(id, out RemoteAvatar? avatar))
        {
            avatar.ApplyRemoteCorpsePose(positions, rotations);
        }

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        WriteCorpsePose(relay, id, positions, rotations, count);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.Unreliable);
            }
        }
    }

    private void ReceiveHello(NetPeer peer, NetDataReader reader)
    {
        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        int protocol = reader.GetInt();
        string modVersion = reader.GetString();
        string gameVersion = reader.GetString();
        string name = reader.GetString();

        if (!ValidatePeer(peer, "client", protocol, modVersion, gameVersion))
        {
            return;
        }

        if (!_peerIds.TryGetValue(peer, out int id))
        {
            return;
        }

        RemoteAvatar avatar = EnsureAvatar(id, name);
        avatar.Name = name;

        DoorSync.SendSnapshot(peer);

        CoopPlugin.LogSource.LogInfo($"[net] Player {id} is '{name}'.");
    }

    private void ReceiveWelcome(NetPeer peer, NetDataReader reader)
    {
        if (_settings.Role != CoopRole.Client)
        {
            return;
        }

        int localId = reader.GetInt();
        int protocol = reader.GetInt();
        string modVersion = reader.GetString();
        string gameVersion = reader.GetString();
        string hostName = reader.GetString();

        CoopPlugin.LogSource.LogInfo(
            $"[net] Welcome payload: id={localId}, protocol={protocol}, mod='{modVersion}', game='{gameVersion}', host='{hostName}'."
        );

        if (!ValidatePeer(peer, "host", protocol, modVersion, gameVersion))
        {
            return;
        }

        _localId = localId;

        RemoteAvatar host = EnsureAvatar(0, hostName);
        host.Name = hostName;

        CoopPlugin.LogSource.LogInfo($"[net] Welcome: local id={localId}, host='{hostName}'.");
    }

    private bool ValidatePeer(
        NetPeer peer,
        string role,
        int protocol,
        string modVersion,
        string gameVersion)
    {
        if (protocol != ProtocolVersion)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[net] Rejecting {role} {peer.Address}:{peer.Port}: protocol {protocol} != {ProtocolVersion}."
            );
            peer.Disconnect();
            return false;
        }

        if (!string.Equals(modVersion, PluginInfo.Version, StringComparison.Ordinal))
        {
            CoopPlugin.LogSource.LogWarning(
                $"[net] Rejecting {role} {peer.Address}:{peer.Port}: mod version '{modVersion}' != '{PluginInfo.Version}'."
            );
            peer.Disconnect();
            return false;
        }

        if (!string.IsNullOrEmpty(gameVersion) &&
            !string.IsNullOrEmpty(Application.version) &&
            !string.Equals(gameVersion, Application.version, StringComparison.Ordinal))
        {
            CoopPlugin.LogSource.LogWarning(
                $"[net] Rejecting {role} {peer.Address}:{peer.Port}: game version '{gameVersion}' != '{Application.version}'."
            );
            peer.Disconnect();
            return false;
        }

        return true;
    }

    private void ReceivePose(NetPeer peer, NetDataReader reader)
    {
        int senderId = reader.GetInt();
        var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        float yaw = reader.GetFloat();
        float horizontal = reader.GetFloat();
        float vertical = reader.GetFloat();
        float speed = reader.GetFloat();
        float crouchTimer = reader.GetFloat();
        PoseFlags flags = (PoseFlags)reader.GetByte();
        string remoteScene = reader.GetString();
        bool remoteReady = reader.GetBool();
        float remoteLean = reader.GetFloat();
        bool remoteDragging = reader.GetBool();
        int actionHash = reader.GetInt();
        float actionTime = reader.GetFloat();
        bool actionActive = reader.GetBool();

        if (senderId == _localId)
        {
            return;
        }

        RemoteAvatar avatar = EnsureAvatar(senderId, senderId == 0 ? "Host" : $"Player {senderId}");
        avatar.SetLevel(remoteScene, remoteReady);
        avatar.SetTarget(
            position,
            yaw,
            remoteScene,
            (flags & PoseFlags.Grounded) != 0
        );
        avatar.SetAnimation(
            vertical,
            horizontal,
            speed,
            crouchTimer,
            (flags & PoseFlags.Crouch) != 0,
            (flags & PoseFlags.Sprint) != 0,
            (flags & PoseFlags.InAir) != 0,
            (flags & PoseFlags.Jumping) != 0,
            (flags & PoseFlags.Shimmy) != 0
        );
        avatar.SetPoseExtras(remoteLean, remoteDragging);
        avatar.SetActionState(actionHash, actionTime, actionActive);

        _poseReceived++;

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        // Relay client poses to other clients (needed for 3+ players; harmless for 2).
        var relay = new NetDataWriter();
        WritePose(
            relay,
            senderId,
            position,
            yaw,
            new Vector2(horizontal, vertical),
            speed,
            crouchTimer,
            flags,
            remoteScene,
            remoteReady,
            remoteLean,
            remoteDragging,
            actionHash,
            actionTime,
            actionActive
        );

        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.Unreliable);
            }
        }
    }

    private void ReceiveDoorState(NetDataReader reader)
    {
        if (_settings.Role != CoopRole.Client)
        {
            return;
        }

        string key = reader.GetString();
        bool open = reader.GetBool();
        bool locked = reader.GetBool();

        DoorSync.Apply(key, open, locked);
    }

    private void ReceiveItemState(NetPeer peer, NetDataReader reader)
    {
        string key = reader.GetString();
        bool alive = reader.GetBool();
        bool active = reader.GetBool();
        bool moving = reader.GetBool();
        var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var rotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );

        bool applied = ItemSync.Apply(key, alive, active, position, rotation, moving);

        // The host also forwards a client's change to any other clients. With
        // two players this is only the host applying the state, but it keeps
        // the protocol ready for a third peer. Unknown keys and NPC-owned items
        // are deliberately not relayed: doing that created a hide/show loop.
        if (applied && _settings.Role == CoopRole.Host)
        {
            foreach (NetPeer other in _net.ConnectedPeerList)
            {
                if (other != peer)
                {
                    SendItemStateTo(
                        other,
                        key,
                        alive,
                        active,
                        position,
                        rotation,
                        moving,
                        reliable: !moving
                    );
                }
            }
        }
    }

    private void ReceiveStoryEvent(NetPeer peer, NetDataReader reader)
    {
        string key = reader.GetString();
        byte kind = reader.GetByte();
        var point = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        string text = reader.GetString();
        int number = reader.GetInt();
        int count = reader.GetInt();
        if (count < 0)
        {
            count = 0;
        }

        var objectKeys = new string[count];
        var objectStates = new bool[count];
        for (int i = 0; i < count; i++)
        {
            objectKeys[i] = reader.GetString();
            objectStates[i] = reader.GetBool();
        }

        StorySync.ApplyEvent(key, kind, point, text, number, objectKeys, objectStates);

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        WriteStoryEvent(relay, key, kind, point, text, number, objectKeys, objectStates);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void ReceivePlayerDeath(NetPeer peer, NetDataReader reader)
    {
        int id = reader.GetInt();
        bool dead = reader.GetBool();
        var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var rotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );

        if (id != _localId)
        {
            PlayerStateSync.Apply(id, dead, position, rotation);
        }

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        WritePlayerDeath(relay, id, dead, position, rotation);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void ReceiveEnemyState(NetPeer peer, NetDataReader reader)
    {
        string key = reader.GetString();
        var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var rotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        int hp = reader.GetInt();
        int maxHp = reader.GetInt();
        int stateHash = reader.GetInt();
        float normalizedTime = reader.GetFloat();
        float speed = reader.GetFloat();

        int animCount = reader.GetByte();
        if (animCount > _animHashes.Length)
        {
            animCount = _animHashes.Length;
        }

        for (int i = 0; i < animCount; i++)
        {
            _animHashes[i] = reader.GetInt();
            _animTypes[i] = reader.GetByte();
            _animValues[i] = reader.GetFloat();
        }

        EnemySync.ApplyState(
            key,
            position,
            rotation,
            hp,
            maxHp,
            stateHash,
            normalizedTime,
            speed,
            _animHashes,
            _animTypes,
            _animValues,
            animCount
        );
    }

    private void ReceiveEnemyDeath(NetPeer peer, NetDataReader reader)
    {
        string key = reader.GetString();
        EnemySync.ApplyDeath(key);

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        relay.Put((byte)PacketType.EnemyDeath);
        relay.Put(key);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void ReceiveItemEvent(NetPeer peer, NetDataReader reader)
    {
        string key = reader.GetString();
        byte kind = reader.GetByte();
        var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var rotation = new Quaternion(
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat(),
            reader.GetFloat()
        );
        string itemName = reader.GetString();
        string templateName = reader.GetString();

        ItemSync.ApplyEvent(key, kind, position, rotation, itemName, templateName);

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        WriteItemEvent(relay, key, kind, position, rotation, itemName, templateName);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void ReceiveWeaponState(NetPeer peer, NetDataReader reader)
    {
        WeaponSnapshot snapshot = ReadWeaponState(reader, out int senderId);
        if (senderId == _localId)
        {
            return;
        }

        RemoteAvatar avatar = EnsureAvatar(
            senderId,
            senderId == 0 ? "Host" : $"Player {senderId}"
        );
        avatar.SetWeaponState(snapshot);

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        WriteWeaponState(relay, senderId, snapshot);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.Sequenced);
            }
        }
    }

    private void ReceiveDamage(NetPeer peer, NetDataReader reader)
    {
        int targetId = reader.GetInt();
        int dealerId = reader.GetInt();
        int amount = reader.GetInt();
        bool headshot = reader.GetBool();
        Vector3 point = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        if (targetId == _localId)
        {
            CombatSync.ApplyLocalDamage(dealerId, amount, headshot, point);
            return;
        }

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        // The host relays damage that one client aimed at another client.
        NetPeer? target = FindPeer(targetId);
        if (target != null && target != peer)
        {
            var relay = new NetDataWriter();
            WriteDamage(relay, targetId, dealerId, amount, headshot, point);
            target.Send(relay, DeliveryMethod.ReliableOrdered);
        }
    }

    private void ReceiveNpcDamage(NetPeer peer, NetDataReader reader)
    {
        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        string key = reader.GetString();
        int amount = reader.GetInt();
        bool headshot = reader.GetBool();
        var point = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        int fromId = _peerIds.TryGetValue(peer, out int id) ? id : -1;
        NpcDamageSync.Apply(key, amount, headshot, point, fromId);
    }

    private void ReceiveWeaponEvent(NetPeer peer, NetDataReader reader)
    {
        int senderId = reader.GetInt();
        byte kind = reader.GetByte();
        if (senderId == _localId)
        {
            return;
        }

        RemoteAvatar avatar = EnsureAvatar(
            senderId,
            senderId == 0 ? "Host" : $"Player {senderId}"
        );
        avatar.ApplyWeaponEvent((WeaponEvent)kind);

        if (_settings.Role != CoopRole.Host)
        {
            return;
        }

        var relay = new NetDataWriter();
        relay.Put((byte)PacketType.WeaponEvent);
        relay.Put(senderId);
        relay.Put(kind);
        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    /// <summary>
    /// Runs after the Animator has evaluated the current frame. Additive lean
    /// and drag poses are applied here so the game's animation does not
    /// overwrite them in the same frame.
    /// </summary>
    public void LateTick()
    {
        if (_disposed)
        {
            return;
        }

        foreach (RemoteAvatar avatar in _avatars.Values)
        {
            avatar.ApplyLatePose();
        }

        EnemySync.LateTick();
    }

    private RemoteAvatar EnsureAvatar(int id, string name)
    {
        if (_avatars.TryGetValue(id, out RemoteAvatar? existing) && existing != null)
        {
            return existing;
        }

        var avatar = new RemoteAvatar(id, name, Vector3.zero, 0.0f, _localPlayer != null ? _localPlayer.transform : null);
        _avatars[id] = avatar;

        CoopPlugin.LogSource.LogInfo($"[net] Spawned remote avatar id={id} name='{name}'.");

        return avatar;
    }
}
