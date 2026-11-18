using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using LiteNetLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Zero-configuration LAN discovery for the in-game "Peripeteia Coop" menu.
///
/// The host broadcasts a small beacon (its name, game port, current scene and
/// save count) on <see cref="DiscoveryPort"/> once a second, from an ephemeral
/// local port. A joining player listens on that port and builds the list of
/// hosts shown in the menu. Nothing here touches the game's own network port,
/// and the beacon is plain UTF-8 so a firewall/Wireshark can see it easily.
///
/// The beacon carries the mod version, so the menu can warn about a mismatch
/// before the connection is attempted. <see cref="Tick"/> must be pumped from
/// the main thread (the menu does it every frame).
/// </summary>
internal static class LanDiscovery
{
    public const int DiscoveryPort = 47777;

    private const string Prefix = "PCOOP1";
    private const float BeaconInterval = 1.0f;
    private const float HostTimeout = 4.0f;

    public sealed class HostEntry
    {
        public string Name = string.Empty;
        public string Address = string.Empty;
        public int Port;
        public string Scene = string.Empty;
        public int SaveCount;
        public string ModVersion = string.Empty;
        public float LastSeen;
        public bool IsLoopback;
        public int AddressRank;

        public string Key => Name.ToLowerInvariant() + "|" + Port;
    }

    private static NetManager? _net;
    private static EventBasedNetListener? _listener;
    private static bool _advertising;
    private static bool _listening;
    private static string _advertisedName = string.Empty;
    private static int _advertisedPort;
    private static string _advertisedScene = string.Empty;
    private static int _advertisedSaves;
    private static byte[] _beacon = Array.Empty<byte>();
    private static float _nextBeacon;
    private static float _nextPrune;
    private static bool _loggedMismatch;

    private static readonly Dictionary<string, HostEntry> HostsByKey = new();
    private static readonly List<HostEntry> Snapshot = new();

    public static bool IsAdvertising => _advertising;

    public static bool IsListening => _listening;

    /// <summary>Discovered hosts, sorted by name. Valid until the next call.</summary>
    public static IReadOnlyList<HostEntry> Hosts
    {
        get
        {
            Snapshot.Clear();
            foreach (HostEntry entry in HostsByKey.Values)
            {
                Snapshot.Add(entry);
            }

            Snapshot.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return Snapshot;
        }
    }

    /// <summary>Host side: start broadcasting this machine as joinable.</summary>
    public static void Advertise(string name, int gamePort)
    {
        EnsureNet();
        if (_listening)
        {
            StopNetwork();
            EnsureNet();
        }

        _advertising = true;
        _advertisedName = Sanitize(name);
        _advertisedPort = gamePort;
        RebuildBeacon();
        _nextBeacon = 0.0f;

        try
        {
            // The socket must be started before anything can be sent from it.
            if (!_net!.Start())
            {
                CoopPlugin.LogSource.LogWarning(
                    "[lan] could not start the discovery socket; this host will not appear in the list."
                );
                return;
            }

            CoopPlugin.LogSource.LogInfo(
                $"[lan] advertising '{_advertisedName}' on UDP {DiscoveryPort} (game port {gamePort})."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[lan] advertise failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>Keep the beacon fresh: scene and save count change over time.</summary>
    public static void UpdateBeacon(string scene, int saveCount)
    {
        if (!_advertising)
        {
            return;
        }

        string safeScene = Sanitize(scene);
        if (string.Equals(safeScene, _advertisedScene, StringComparison.Ordinal) &&
            saveCount == _advertisedSaves)
        {
            return;
        }

        _advertisedScene = safeScene;
        _advertisedSaves = saveCount;
        RebuildBeacon();
    }

    /// <summary>Client side: start listening for host beacons.</summary>
    public static void Listen()
    {
        EnsureNet();
        if (_advertising)
        {
            StopNetwork();
            EnsureNet();
        }

        _listening = true;
        try
        {
            if (!_net!.Start(DiscoveryPort))
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[lan] could not bind discovery port UDP {DiscoveryPort}; the host list will stay empty."
                );
            }
            else
            {
                CoopPlugin.LogSource.LogInfo($"[lan] listening for co-op hosts on UDP {DiscoveryPort}.");
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[lan] discovery listen failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    public static void Stop()
    {
        StopNetwork();
        _advertising = false;
        _listening = false;
        HostsByKey.Clear();
    }

    /// <summary>Main-thread pump: sends beacons, receives them, expires stale hosts.</summary>
    public static void Tick()
    {
        if (_net == null)
        {
            return;
        }

        try
        {
            _net.PollEvents();
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[lan] poll failed: {exception.GetType().Name}: {exception.Message}"
            );
        }

        float now = Time.unscaledTime;

        if (_advertising && now >= _nextBeacon)
        {
            _nextBeacon = now + BeaconInterval;
            try
            {
                _net.SendBroadcast(_beacon, DiscoveryPort);
                // Broadcast is not always delivered to the sender's own machine,
                // so also send straight to loopback: that is what makes the two
                // local test instances see each other.
                _net.SendUnconnectedMessage(_beacon, new IPEndPoint(IPAddress.Loopback, DiscoveryPort));
            }
            catch (Exception exception)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[lan] beacon failed: {exception.GetType().Name}: {exception.Message}"
                );
            }
        }

        if (now >= _nextPrune)
        {
            _nextPrune = now + 1.0f;
            List<string>? dead = null;
            foreach (KeyValuePair<string, HostEntry> pair in HostsByKey)
            {
                if (now - pair.Value.LastSeen > HostTimeout)
                {
                    (dead ??= new List<string>()).Add(pair.Key);
                }
            }

            if (dead != null)
            {
                foreach (string key in dead)
                {
                    HostsByKey.Remove(key);
                }
            }
        }
    }

    private static void EnsureNet()
    {
        if (_net != null)
        {
            return;
        }

        _listener = new EventBasedNetListener();
        _listener.NetworkReceiveUnconnectedEvent += OnReceiveUnconnected;
        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,
            UpdateTime = 15,
        };
    }

    private static void StopNetwork()
    {
        try
        {
            _net?.Stop();
        }
        catch (Exception)
        {
            // Ignore: the socket may already be gone.
        }

        _net = null;
        _listener = null;
    }

    private static void RebuildBeacon()
    {
        string payload = string.Join(
            "|",
            Prefix,
            _advertisedName,
            PluginInfo.Version,
            Application.version,
            _advertisedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _advertisedScene,
            _advertisedSaves.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        _beacon = Encoding.UTF8.GetBytes(payload);
    }

    private static void OnReceiveUnconnected(
        IPEndPoint remote,
        NetPacketReader reader,
        UnconnectedMessageType type
    )
    {
        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(reader.GetRemainingBytes());
        }
        catch (Exception)
        {
            return;
        }

        string[] parts = payload.Split('|');
        if (parts.Length < 7 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return;
        }

        if (!int.TryParse(parts[4], out int port) || port <= 0 || port > 65535)
        {
            return;
        }

        int.TryParse(parts[6], out int saves);

        // The same host answers on loopback, its LAN address and IPv6. Collapse
        // those into one entry and keep the address most likely to connect.
        int rank = RankAddress(remote.Address);
        string address = remote.Address.ToString();
        string key = parts[1].ToLowerInvariant() + "|" + port;

        if (HostsByKey.TryGetValue(key, out HostEntry? existing))
        {
            existing.LastSeen = Time.unscaledTime;
            existing.Scene = parts[5];
            existing.SaveCount = saves;
            existing.ModVersion = parts[2];

            if (rank > existing.AddressRank)
            {
                existing.Address = address;
                existing.IsLoopback = IPAddress.IsLoopback(remote.Address);
                existing.AddressRank = rank;
            }

            return;
        }

        var entry = new HostEntry
        {
            Name = parts[1],
            ModVersion = parts[2],
            Address = address,
            Port = port,
            Scene = parts[5],
            SaveCount = saves,
            LastSeen = Time.unscaledTime,
            IsLoopback = IPAddress.IsLoopback(remote.Address),
            AddressRank = rank,
        };

        HostsByKey[key] = entry;

        CoopPlugin.LogSource.LogInfo(
            $"[lan] found host '{entry.Name}' at {entry.Address}:{entry.Port} (scene='{entry.Scene}', mod={entry.ModVersion})."
        );

        if (!_loggedMismatch &&
            !string.Equals(entry.ModVersion, PluginInfo.Version, StringComparison.Ordinal))
        {
            _loggedMismatch = true;
            CoopPlugin.LogSource.LogWarning(
                $"[lan] host '{entry.Name}' runs mod {entry.ModVersion}, this build is {PluginInfo.Version}."
            );
        }
    }

    /// <summary>
    /// Higher is better. A routable IPv4 address is what a client on another
    /// machine needs; loopback only works on this PC; IPv6 is a last resort.
    /// </summary>
    private static int RankAddress(IPAddress address)
    {
        bool loopback = IPAddress.IsLoopback(address);
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return loopback ? 2 : 3;
        }

        return loopback ? 0 : 1;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Player";
        }

        return value.Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
