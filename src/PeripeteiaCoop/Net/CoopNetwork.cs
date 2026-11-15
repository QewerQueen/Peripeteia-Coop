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
    private const int ProtocolVersion = 11;
    private const float PoseInterval = 1.0f / 30.0f;
    private const float StatsInterval = 5.0f;

    private readonly CoopSettings _settings;
    private readonly EventBasedNetListener _listener;
    private readonly NetManager _net;
    private readonly Dictionary<int, RemoteAvatar> _avatars = new();
    private readonly Dictionary<NetPeer, int> _peerIds = new();

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
    private PlayerMovement? _localPlayer;
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

        if (Time.unscaledTime < _nextAutopilotTime)
        {
            return;
        }

        _nextAutopilotTime = Time.unscaledTime + 0.1f;

        float angle = Time.unscaledTime * 1.2f;
        var target = _autopilotOrigin + new Vector3(Mathf.Cos(angle) * 2.0f, 0.0f, Mathf.Sin(angle) * 2.0f);

        localPlayer.TeleportPlayer(target, true);
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
            type != PacketType.WeaponEvent)
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
