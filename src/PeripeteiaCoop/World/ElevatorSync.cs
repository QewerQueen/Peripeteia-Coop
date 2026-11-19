using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Replicates the world elevators (and the trains/conveyors that share the same
/// component). A lift is a shared piece of the map: whoever presses the call
/// button, both players should ride the same cabin.
///
/// The game drives an elevator through <c>elevator.selectFloor</c> (a call
/// button or a quest listener) and the component then moves its own transform
/// in <c>Update</c>. There is no network-visible "target floor" beyond the
/// public <c>currentFloor</c> field, so the model is:
///
/// - every local <c>selectFloor</c> call is broadcast to the other machine,
///   which replays it on its own copy of the same elevator. This is what lets a
///   client call a lift at all;
/// - the host additionally broadcasts <c>currentFloor</c> and the cabin
///   transform a few times a second, so a late joiner, or a call that was
///   missed while loading, is corrected. The client only snaps its cabin when
///   the two copies are far apart, so the two elevators running the same motion
///   are never fought over.
///
/// Elevators are identified by scene + hierarchy path, the same key scheme the
/// doors use, which is stable because both instances load the same level.
/// </summary>
internal static class ElevatorSync
{
    private sealed class ElevatorState
    {
        public ElevatorState(int floor, Vector3 position)
        {
            Floor = floor;
            Position = position;
        }

        public int Floor { get; }

        public Vector3 Position { get; }
    }

    private static readonly Dictionary<string, elevator> Elevators = new();
    private static readonly Dictionary<string, ElevatorState> LastSent = new();
    private static readonly Dictionary<string, float> LastCallTime = new();
    private static readonly Dictionary<string, int> LastLocalCallFloor = new();
    private static readonly Dictionary<string, float> LastLocalCallTime = new();

    private static string _scene = string.Empty;
    private static float _nextPollTime;
    private static float _nextRefreshTime;
    private static float _nextSnapshotTime;
    private static float _nextElevatorTestTime;
    private static float _nextDiagTime;
    private static int _loggedCount = -1;
    private static int _callsSent;
    private static int _callsApplied;
    private static int _statesApplied;

    // Bumped while a call is being replayed from the network or from a quest
    // world-effect, so the patch does not echo it straight back.
    private static int _suppressDepth;

    public static int CallsSent => _callsSent;

    public static int CallsApplied => _callsApplied;

    public static int StatesApplied => _statesApplied;

    private const float PollInterval = 0.2f;
    private const float FullSnapshotInterval = 3.0f;
    private const float MoveEpsilon = 0.1f;
    private const float SnapDistance = 3.0f;
    private const float CallCooldown = 0.5f;

    public static bool Enabled =>
        CoopPlugin.WorldSyncElevators &&
        CoopPlugin.Settings.Role != CoopRole.Offline;

    public static void BeginRemoteApply()
    {
        _suppressDepth++;
    }

    public static void EndRemoteApply()
    {
        if (_suppressDepth > 0)
        {
            _suppressDepth--;
        }
    }

    public static void Register(elevator? lift)
    {
        if (lift == null || lift.gameObject == null)
        {
            return;
        }

        EnsureScene();
        Elevators[GetKey(lift)] = lift;
    }

    public static string GetKey(elevator lift)
    {
        var builder = new StringBuilder(128);
        Transform? current = lift.transform;

        while (current != null)
        {
            builder.Insert(0, $"/{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        string scene = lift.gameObject.scene.name;
        if (string.IsNullOrEmpty(scene))
        {
            scene = SceneManager.GetActiveScene().name;
        }

        return $"{scene}|{builder}";
    }

    /// <summary>
    /// Called right after the local game runs <c>elevator.selectFloor</c> (the
    /// call button, or a quest listener). Forwards the request so the other
    /// machine moves its own copy of the same lift.
    /// </summary>
    public static void OnLocalSelect(elevator? lift, int floorNo)
    {
        if (!Enabled || lift == null || lift.gameObject == null)
        {
            return;
        }

        if (_suppressDepth > 0 || StorySync.IsApplyingRemote)
        {
            return;
        }

        if (CoopPlugin.Network == null || CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        Register(lift);
        string key = GetKey(lift);

        // Some elevators (trains, conveyors) re-issue selectFloor on a loop. Do
        // not flood the peer with the same call; only a changed floor, or the
        // same one after a pause, is worth sending.
        float now = Time.unscaledTime;
        if (LastLocalCallFloor.TryGetValue(key, out int lastFloor) &&
            lastFloor == floorNo &&
            LastLocalCallTime.TryGetValue(key, out float lastTime) &&
            now - lastTime < CallCooldown)
        {
            return;
        }

        LastLocalCallFloor[key] = floorNo;
        LastLocalCallTime[key] = now;

        _callsSent++;
        CoopPlugin.LogSource.LogInfo($"[elev] selectFloor({floorNo}) on '{key}' (sharing).");
        CoopPlugin.Network.SendElevatorCall(key, floorNo);
    }

    public static void Tick()
    {
        if (!Enabled)
        {
            return;
        }

        EnsureScene();

        float now = Time.unscaledTime;
        if (now < _nextPollTime)
        {
            return;
        }

        _nextPollTime = now + PollInterval;

        if (Elevators.Count == 0 && now >= _nextRefreshTime)
        {
            _nextRefreshTime = now + 2.0f;
            Refresh();
        }

        if (Elevators.Count != _loggedCount)
        {
            _loggedCount = Elevators.Count;
            CoopPlugin.LogSource.LogInfo($"[elev] registered={Elevators.Count} scene='{_scene}'.");
        }

        if (CoopPlugin.Network == null || CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        // Debug-only: the client drives the lift so the reported "a client
        // cannot call the elevator" path is exercised on one machine.
        if (CoopPlugin.Settings.ElevatorTest &&
            CoopPlugin.Settings.Role == CoopRole.Client &&
            now >= _nextElevatorTestTime)
        {
            _nextElevatorTestTime = now + 8.0f;
            ToggleTestElevator();
        }

        // Only the host broadcasts the state; a client just follows it.
        if (CoopPlugin.Settings.Role != CoopRole.Host)
        {
            return;
        }

        bool fullSnapshot = now >= _nextSnapshotTime;
        if (fullSnapshot)
        {
            _nextSnapshotTime = now + FullSnapshotInterval;
        }

        foreach (KeyValuePair<string, elevator> pair in Elevators)
        {
            elevator? lift = pair.Value;
            if (lift == null || lift.gameObject == null)
            {
                continue;
            }

            int floor;
            Vector3 position;
            Quaternion rotation;
            try
            {
                floor = lift.currentFloor;
                position = lift.transform.position;
                rotation = lift.transform.rotation;
            }
            catch (Exception)
            {
                continue;
            }

            bool changed = !LastSent.TryGetValue(pair.Key, out ElevatorState? previous) ||
                           previous == null ||
                           previous.Floor != floor ||
                           Vector3.Distance(previous.Position, position) > MoveEpsilon;

            if (fullSnapshot || changed)
            {
                CoopPlugin.Network.SendElevatorState(pair.Key, floor, position, rotation);
                LastSent[pair.Key] = new ElevatorState(floor, position);
            }
        }
    }

    /// <summary>Host only: pushes every elevator's state to a freshly joined peer.</summary>
    public static void SendSnapshot(NetPeer peer)
    {
        if (!Enabled || CoopPlugin.Network == null)
        {
            return;
        }

        if (CoopPlugin.Settings.Role != CoopRole.Host)
        {
            return;
        }

        EnsureScene();
        if (Elevators.Count == 0)
        {
            Refresh();
        }

        foreach (KeyValuePair<string, elevator> pair in Elevators)
        {
            elevator? lift = pair.Value;
            if (lift == null || lift.gameObject == null)
            {
                continue;
            }

            try
            {
                CoopPlugin.Network.SendElevatorStateTo(
                    peer,
                    pair.Key,
                    lift.currentFloor,
                    lift.transform.position,
                    lift.transform.rotation
                );
                LastSent[pair.Key] = new ElevatorState(lift.currentFloor, lift.transform.position);
            }
            catch (Exception)
            {
                // Best effort; the periodic state corrects a missed snapshot.
            }
        }
    }

    /// <summary>Replays a call the other player made.</summary>
    public static void ApplyCall(string key, int floorNo)
    {
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!TryFind(key, out elevator? lift) || lift == null)
        {
            return;
        }

        try
        {
            BeginRemoteApply();
            lift.selectFloor(floorNo);
            LastSent[key] = new ElevatorState(floorNo, lift.transform.position);
            _callsApplied++;
            CoopPlugin.LogSource.LogInfo($"[elev] applied remote call floor={floorNo} on '{key}'.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[elev] remote call failed for '{key}': {exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            EndRemoteApply();
        }
    }

    /// <summary>Client only: follows the host's elevator floor and cabin.</summary>
    public static void ApplyState(string key, int floorNo, Vector3 position, Quaternion rotation)
    {
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            return;
        }

        if (CoopPlugin.Settings.Role != CoopRole.Client)
        {
            return;
        }

        if (!TryFind(key, out elevator? lift) || lift == null)
        {
            return;
        }

        int localFloor;
        Vector3 localPosition;
        try
        {
            localFloor = lift.currentFloor;
            localPosition = lift.transform.position;
        }
        catch (Exception)
        {
            return;
        }

        if (CoopPlugin.Settings.ElevatorTest && Time.unscaledTime >= _nextDiagTime)
        {
            _nextDiagTime = Time.unscaledTime + 1.0f;
            CoopPlugin.LogSource.LogInfo(
                $"[elev] diag '{key}' client={localPosition} host={position} " +
                $"dist={Vector3.Distance(localPosition, position):F2} floor={localFloor}/{floorNo}"
            );
        }

        // A different floor means this side missed a call (late join, or the
        // host moved while we were loading). Re-issue it locally so the game's
        // own door/floor logic runs, throttled so a ride in progress does not
        // spam the call.
        if (localFloor != floorNo)
        {
            float now = Time.unscaledTime;
            if (!LastCallTime.TryGetValue(key, out float last) || now - last >= CallCooldown)
            {
                LastCallTime[key] = now;
                ApplyCall(key, floorNo);
            }

            return;
        }

        // Same floor: only correct a large positional desync (a missed call the
        // floor check could not see, or a late join onto a moving cabin), so two
        // cabins running the same motion are never dragged against each other.
        if (Vector3.Distance(localPosition, position) > SnapDistance)
        {
            try
            {
                lift.transform.position = position;
                lift.transform.rotation = rotation;
                _statesApplied++;
                CoopPlugin.LogSource.LogInfo($"[elev] snapped '{key}' to {position}.");
            }
            catch (Exception)
            {
                // Ignore; the next state packet tries again.
            }
        }
    }

    private static bool TryFind(string key, out elevator? lift)
    {
        if (Elevators.TryGetValue(key, out lift) && lift != null && lift.gameObject != null)
        {
            return true;
        }

        Refresh();
        return Elevators.TryGetValue(key, out lift) && lift != null && lift.gameObject != null;
    }

    private static void ToggleTestElevator()
    {
        elevator? target = null;
        foreach (elevator? candidate in Elevators.Values)
        {
            if (candidate == null || candidate.gameObject == null)
            {
                continue;
            }

            try
            {
                if (candidate.destinations != null && candidate.destinations.Length > 1)
                {
                    target = candidate;
                    break;
                }
            }
            catch (Exception)
            {
                // Skip a component whose fields are not ready yet.
            }
        }

        if (target == null)
        {
            CoopPlugin.LogSource.LogWarning(
                "[elev] test requested but no multi-floor elevator is registered."
            );
            return;
        }

        int floors = target.destinations.Length;
        int next = (target.currentFloor + 1) % floors;
        CoopPlugin.LogSource.LogInfo($"[elev] test: host calls floor {next} of {floors}.");
        target.selectFloor(next);
    }

    private static void Refresh()
    {
        try
        {
            foreach (elevator lift in UnityEngine.Object.FindObjectsOfType<elevator>())
            {
                Register(lift);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[elev] refresh failed: {exception.GetType().Name}: {exception.Message}"
            );
        }

        if (Elevators.Count != _loggedCount)
        {
            _loggedCount = Elevators.Count;
            CoopPlugin.LogSource.LogInfo($"[elev] registered={Elevators.Count} scene='{_scene}' (refresh).");
        }
    }

    private static void EnsureScene()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (scene == _scene)
        {
            return;
        }

        _scene = scene;
        Elevators.Clear();
        LastSent.Clear();
        LastCallTime.Clear();
        LastLocalCallFloor.Clear();
        LastLocalCallTime.Clear();
        _nextSnapshotTime = 0.0f;
        _loggedCount = -1;
    }
}

[HarmonyPatch(typeof(elevator), "selectFloor")]
internal static class ElevatorSelectFloorPatch
{
    private static void Postfix(elevator __instance, int floorNo)
    {
        ElevatorSync.OnLocalSelect(__instance, floorNo);
    }
}

[HarmonyPatch(typeof(elevator), "Start")]
internal static class ElevatorRegisterStartPatch
{
    private static void Postfix(elevator __instance)
    {
        ElevatorSync.Register(__instance);
    }
}

[HarmonyPatch(typeof(elevator), "onStart")]
internal static class ElevatorRegisterOnStartPatch
{
    private static void Postfix(elevator __instance)
    {
        ElevatorSync.Register(__instance);
    }
}
