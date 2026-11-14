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
/// - each side renders the other player as an interpolated cloned Marie model.
///
/// The pose packet already carries animator state, but the remote clone is
/// static until a safe animation setup is found. This is deliberately not
/// authoritative yet; it is the smallest useful network milestone and is fully
/// testable with two local game instances.
/// </summary>
internal sealed class CoopNetwork : IDisposable
{
    private enum PacketType : byte
    {
        Hello = 1,
        Welcome = 2,
        Pose = 3,
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
    private const int ProtocolVersion = 2;
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
    private int _poseSent;
    private int _poseReceived;
    private Vector3 _autopilotOrigin;
    private bool _autopilotOriginSet;
    private float _nextAutopilotTime;
    private PlayerMovement? _localPlayer;

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

            CoopPlugin.LogSource.LogInfo(
                $"[net] Host listening on UDP {_settings.Port} as '{_settings.PlayerName}' " +
                $"(protocol={ProtocolVersion}, mod={PluginInfo.Version}, game='{Application.version}')."
            );
            return;
        }

        if (_settings.Role == CoopRole.Client)
        {
            _net.Start();
            CoopPlugin.LogSource.LogInfo(
                $"[net] Client connecting to {_settings.Address}:{_settings.Port} as '{_settings.PlayerName}' " +
                $"(protocol={ProtocolVersion}, mod={PluginInfo.Version}, game='{Application.version}')."
            );
            _net.Connect(_settings.Address, _settings.Port, ConnectionKey);
        }
    }

    public void Tick(PlayerMovement? localPlayer)
    {
        _localPlayer = localPlayer;
        _net.PollEvents();

        if (localPlayer != null)
        {
            foreach (RemoteAvatar avatar in _avatars.Values)
            {
                avatar.TryUpgrade(localPlayer.transform);
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

        Vector2 animatorDirection = localPlayer.AnimatorDirectionNormalized;
        float speed = localPlayer.animatorMagnitude;
        float crouchTimer = localPlayer.crouchTimer;

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
        WritePose(writer, _localId, position, yaw, animatorDirection, speed, crouchTimer, flags);

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

    private static void WritePose(
        NetDataWriter writer,
        int id,
        Vector3 position,
        float yaw,
        Vector2 animatorDirection,
        float speed,
        float crouchTimer,
        PoseFlags flags)
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

        if (!_autopilotOriginSet)
        {
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

            CoopPlugin.LogSource.LogInfo(
                $"[net] Peer connected: id={id} endpoint={peer.Address}:{peer.Port}."
            );
            return;
        }

        if (_settings.Role == CoopRole.Client)
        {
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
            CoopPlugin.LogSource.LogInfo(
                $"[net] Lost connection to host ({peer.Address}:{peer.Port}): {disconnectInfo.Reason}."
            );
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes < 1)
        {
            return;
        }

        var type = (PacketType)reader.GetByte();

        if (type != PacketType.Pose)
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

        if (senderId == _localId)
        {
            return;
        }

        RemoteAvatar avatar = EnsureAvatar(senderId, senderId == 0 ? "Host" : $"Player {senderId}");
        avatar.SetTarget(position, yaw);
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
            flags
        );

        foreach (NetPeer other in _net.ConnectedPeerList)
        {
            if (other != peer)
            {
                other.Send(relay, DeliveryMethod.Unreliable);
            }
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
