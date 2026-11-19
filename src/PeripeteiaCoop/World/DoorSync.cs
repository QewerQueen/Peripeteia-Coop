using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Host-authoritative door state replication. The host polls every door in the
/// active scene and sends open/locked changes; clients apply them. A periodic
/// full snapshot corrects late joiners and reverts local client edits.
///
/// Doors are identified by scene + hierarchy path + sibling index, which is
/// stable because both instances load the same level.
/// </summary>
internal static class DoorSync
{
    private sealed class DoorState
    {
        public DoorState(bool open, bool locked)
        {
            Open = open;
            Locked = locked;
        }

        public bool Open { get; }

        public bool Locked { get; }
    }

    private static readonly Dictionary<string, doorScript> Doors = new();
    private static readonly Dictionary<string, DoorState> LastSent = new();

    private static string _scene = string.Empty;
    private static float _nextPollTime;
    private static float _nextSnapshotTime;
    private static float _nextRefreshTime;
    private static float _nextDoorTestTime;
    private static int _loggedDoorCount = -1;

    // A client defers to the host's door state until it has received one: its
    // own level starts with every door closed, and sending that would slam the
    // host's already-open doors shut the moment the client joins.
    private static bool _syncedOnce;

    public static void Register(doorScript door)
    {
        if (door == null)
        {
            return;
        }

        EnsureScene();
        Doors[GetKey(door)] = door;
    }

    public static void Tick()
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        EnsureScene();

        float now = Time.unscaledTime;
        if (now < _nextPollTime)
        {
            return;
        }

        _nextPollTime = now + 0.25f;

        if (Doors.Count == 0 && now >= _nextRefreshTime)
        {
            _nextRefreshTime = now + 2.0f;
            Refresh();
        }

        if (Doors.Count != _loggedDoorCount)
        {
            _loggedDoorCount = Doors.Count;
            CoopPlugin.LogSource.LogInfo($"[doors] registered={Doors.Count} scene='{_scene}'.");
        }

        if (CoopPlugin.Network == null || CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        bool isHost = CoopPlugin.Settings.Role == CoopRole.Host;

        // The client waits for the host's first snapshot before it starts
        // sending, so its unsynced initial states cannot overwrite the host's.
        if (!isHost && !_syncedOnce)
        {
            return;
        }

        // Only the host keeps sending the periodic full snapshot; it is the
        // drift corrector. Both sides still send their own changes.
        bool fullSnapshot = isHost && now >= _nextSnapshotTime;
        if (fullSnapshot)
        {
            _nextSnapshotTime = now + 3.0f;
        }

        if (isHost && CoopPlugin.Settings.DoorTest && now >= _nextDoorTestTime)
        {
            _nextDoorTestTime = now + 5.0f;
            ToggleTestDoor();
        }

        foreach (KeyValuePair<string, doorScript> pair in Doors)
        {
            doorScript? door = pair.Value;
            if (door == null)
            {
                continue;
            }

            var state = new DoorState(door.open, door.locked);
            bool changed = !LastSent.TryGetValue(pair.Key, out DoorState? previous) ||
                           previous.Open != state.Open ||
                           previous.Locked != state.Locked;

            if (fullSnapshot || changed)
            {
                CoopPlugin.Network.SendDoorState(pair.Key, state.Open, state.Locked);
                LastSent[pair.Key] = state;
            }
        }
    }

    public static void SendSnapshot(NetPeer peer)
    {
        if (CoopPlugin.Settings.Role != CoopRole.Host || CoopPlugin.Network == null)
        {
            return;
        }

        EnsureScene();
        if (Doors.Count == 0)
        {
            Refresh();
        }

        foreach (KeyValuePair<string, doorScript> pair in Doors)
        {
            doorScript? door = pair.Value;
            if (door == null)
            {
                continue;
            }

            CoopPlugin.Network.SendDoorStateTo(peer, pair.Key, door.open, door.locked);
            LastSent[pair.Key] = new DoorState(door.open, door.locked);
        }
    }

    public static void Apply(string key, bool open, bool locked)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        if (!Doors.TryGetValue(key, out doorScript? door) || door == null)
        {
            Refresh();
            if (!Doors.TryGetValue(key, out door) || door == null)
            {
                return;
            }
        }

        // Receiving a door we can actually apply means the host's snapshot has
        // reached us, so this side may now start sending its own door changes.
        // Setting it earlier marks a wrong-scene client as "synced" and lets it
        // overwrite host doors with stale local state.
        _syncedOnce = true;

        if (door.open == open && door.locked == locked)
        {
            LastSent[key] = new DoorState(open, locked);
            return;
        }

        if (!open && door.open)
        {
            door.open = false;
        }
        else if (open && !door.open)
        {
            bool wasLocked = door.locked;
            door.locked = false;
            door.OpenDoor();
            door.open = true;
            door.locked = wasLocked;
        }

        door.locked = locked;

        // Suppress the echo: this change came from the peer, so do not send it
        // straight back.
        LastSent[key] = new DoorState(open, locked);
        CoopPlugin.LogSource.LogInfo($"[doors] applied open={open} locked={locked}.");
    }

    private static void ToggleTestDoor()
    {
        doorScript? testDoor = null;
        foreach (doorScript? candidate in Doors.Values)
        {
            if (candidate != null)
            {
                testDoor = candidate;
                break;
            }
        }

        if (testDoor == null)
        {
            CoopPlugin.LogSource.LogWarning("[doors] test requested but no door is registered.");
            return;
        }

        if (testDoor.locked)
        {
            testDoor.locked = false;
        }

        if (testDoor.open)
        {
            testDoor.open = false;
            CoopPlugin.LogSource.LogInfo("[doors] test: host closed the first door.");
        }
        else
        {
            testDoor.OpenDoor();
            CoopPlugin.LogSource.LogInfo("[doors] test: host opened the first door.");
        }
    }

    private static void Refresh()
    {
        foreach (doorScript door in Object.FindObjectsOfType<doorScript>())
        {
            Register(door);
        }

        if (Doors.Count != _loggedDoorCount)
        {
            _loggedDoorCount = Doors.Count;
            CoopPlugin.LogSource.LogInfo($"[doors] registered={Doors.Count} scene='{_scene}' (refresh).");
        }
    }

    /// <summary>Called for same-scene save loads as well as scene changes.</summary>
    public static void ResetForLoad()
    {
        Doors.Clear();
        LastSent.Clear();
        _syncedOnce = false;
        _nextRefreshTime = 0.0f;
        _nextSnapshotTime = 0.0f;
        _nextDoorTestTime = 0.0f;
        _loggedDoorCount = -1;
    }

    private static void EnsureScene()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (scene == _scene)
        {
            return;
        }

        _scene = scene;
        ResetForLoad();
    }

    private static string GetKey(doorScript door)
    {
        var builder = new StringBuilder(128);
        Transform? current = door.transform;

        while (current != null)
        {
            builder.Insert(0, $"/{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        string scene = door.gameObject.scene.name;
        if (string.IsNullOrEmpty(scene))
        {
            scene = SceneManager.GetActiveScene().name;
        }

        return $"{scene}|{builder}";
    }
}

[HarmonyPatch(typeof(doorScript), "Start")]
internal static class DoorRegisterStartPatch
{
    private static void Postfix(doorScript __instance)
    {
        DoorSync.Register(__instance);
    }
}

[HarmonyPatch(typeof(doorScript), "onStart")]
internal static class DoorRegisterOnStartPatch
{
    private static void Postfix(doorScript __instance)
    {
        DoorSync.Register(__instance);
    }
}
