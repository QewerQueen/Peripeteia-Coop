using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.AI;
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
        public DoorState(bool open, bool locked, float openPercent, Quaternion localRotation)
        {
            Open = open;
            Locked = locked;
            OpenPercent = openPercent;
            LocalRotation = localRotation;
        }

        public bool Open { get; }

        public bool Locked { get; }

        public float OpenPercent { get; }

        public Quaternion LocalRotation { get; }

        public bool NearlyEquals(DoorState other)
        {
            return Open == other.Open &&
                   Locked == other.Locked &&
                   Mathf.Abs(OpenPercent - other.OpenPercent) < 0.01f &&
                   Quaternion.Angle(LocalRotation, other.LocalRotation) < 1.0f;
        }
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

            var state = new DoorState(
                door.open,
                door.locked,
                door.openpercent,
                door.transform.localRotation
            );
            bool changed = !LastSent.TryGetValue(pair.Key, out DoorState? previous) ||
                           !previous.NearlyEquals(state);

            if (fullSnapshot || changed)
            {
                CoopPlugin.Network.SendDoorState(
                    pair.Key,
                    state.Open,
                    state.Locked,
                    state.OpenPercent,
                    state.LocalRotation
                );
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

            CoopPlugin.Network.SendDoorStateTo(
                peer,
                pair.Key,
                door.open,
                door.locked,
                door.openpercent,
                door.transform.localRotation
            );
            LastSent[pair.Key] = new DoorState(door.open, door.locked, door.openpercent, door.transform.localRotation);
        }
    }

    public static void Apply(string key, bool open, bool locked, float openPercent, Quaternion localRotation)
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

        bool stateChanged = door.open != open || door.locked != locked;
        if (stateChanged)
        {
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
        }

        // Keep NavMeshObstacle in sync with the applied door state, otherwise
        // remote bots can path through a door that looks closed.
        try
        {
            if (door.obstacle != null)
            {
                door.obstacle.enabled = !door.open && !door.destroyed;
            }
        }
        catch (Exception)
        {
            // Ignore obstacle sync failures on unusual doors.
        }

        // Physical door hold (F): sync open percent and rotation so a door held
        // half-open by one player moves on the other side too.
        bool locallyHeld = IsLocallyHeld(door);
        if (!locallyHeld)
        {
            if (Mathf.Abs(door.openpercent - openPercent) > 0.001f)
            {
                door.openpercent = openPercent;
            }

            if (Quaternion.Angle(door.transform.localRotation, localRotation) > 0.5f)
            {
                ApplyLocalRotation(door, localRotation);
            }
        }

        LastSent[key] = new DoorState(open, locked, openPercent, localRotation);
        if (stateChanged)
        {
            CoopPlugin.LogSource.LogInfo($"[doors] applied open={open} locked={locked}.");
        }
    }

    private static bool IsLocallyHeld(doorScript door)
    {
        try
        {
            foreach (ObjectHighlight highlight in UnityEngine.Object.FindObjectsOfType<ObjectHighlight>())
            {
                if (highlight != null && highlight.isDragging && highlight.objectDragged != null)
                {
                    // ObjectHighlight holds a door via its doorHeld/door fields.
                    // When a player drags a physics door, doorHeld is set.
                    var traverse = Traverse.Create(highlight);
                    bool doorHeld = traverse.Field("doorHeld").GetValue<bool>();
                    doorScript? heldDoor = traverse.Field("door").GetValue<doorScript>();
                    if (doorHeld && heldDoor == door)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // If we cannot determine local hold, prefer applying remote state.
        }

        return false;
    }

    private static void ApplyLocalRotation(doorScript door, Quaternion localRotation)
    {
        try
        {
            Rigidbody? body = door.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = door.GetComponentInChildren<Rigidbody>();
            }

            if (body != null && !body.isKinematic)
            {
                body.MoveRotation(door.transform.parent != null
                    ? door.transform.parent.rotation * localRotation
                    : localRotation);
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            else
            {
                door.transform.localRotation = localRotation;
            }
        }
        catch (Exception)
        {
            try
            {
                door.transform.localRotation = localRotation;
            }
            catch (Exception)
            {
                // Ignore broken door transforms.
            }
        }
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
        foreach (doorScript door in UnityEngine.Object.FindObjectsOfType<doorScript>())
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
