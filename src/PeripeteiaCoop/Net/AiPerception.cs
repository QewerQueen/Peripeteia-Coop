using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Host-side bridge that lets the game's own AI perception "see" a remote player
/// puppet.
///
/// The game only ever evaluates threats for the faction members it knows about,
/// and a puppet registered with <c>AIController.addCustomTarget</c> never enters
/// that walk: <c>calculateThreat</c> is simply never called for it. Even when it
/// is called, the faction-0 (player) branch samples the <b>local</b> player's
/// body through <c>AIController.lightcontrol</c>, so a client target would be
/// tested against the host's own body.
///
/// Two mechanisms fix that. The main one feeds the puppet into the game's own
/// sense buffer through <c>Senses.seeTarget</c> right after
/// <c>Senses.clearTargets</c> (see <see cref="InjectRemotePlayers"/>), which is
/// the same path every real target takes, so suspicion, the threat list, the
/// chosen target, aiming and firing all run unmodified. The secondary one swaps
/// the local player's body samples for the puppet's own collider for the
/// duration of a single <c>calculateThreat</c> call (see <see cref="Swap"/>),
/// which keeps the game's own maths honest if a puppet ever does reach it.
/// </summary>
internal static class AiPerception
{
    private const string PerceptionProxyName = "PeripeteiaCoop_AiProxy";

    private sealed class Body
    {
        public Collider Collider = null!;
        public Transform Root = null!;
        public readonly Collider[] Colliders = new Collider[1];
        public readonly Vector3[] Positions = new Vector3[1];
    }

    private sealed class SwapState
    {
        public LightController Light = null!;
        public Collider[]? Colliders;
        public Vector3[]? Positions;
    }

    private static readonly Dictionary<int, Body> Bodies = new();
    private static readonly Dictionary<int, EnemyController?> Owners = new();
    private static bool _loggedSwap;
    private static int _injectLogs;
    private static int _failLogs;
    private static int _calls;
    private const int InjectLogLimit = 20;

    // Upper bound on how far an enemy is allowed to notice a puppet. The game's
    // own sight ranges run up to ~100 m; the cap only guards against a designer
    // value turning the whole level visible.
    private const float InjectRange = 90.0f;
    private const float InjectThreat = 50.0f;

    // Everything except "Ignore Raycast", so the puppet's perception proxy (on the
    // player layer) is hit but the shooter's own helper colliders are not.
    private const int SightMask = ~(1 << 2);

    public static int Count => Bodies.Count;

    /// <summary>True when the transform (or a close ancestor) is a registered avatar.</summary>
    public static bool IsRegistered(Transform? target)
    {
        return target != null && TryGetBody(target, out _);
    }

    public static void Clear()
    {
        Bodies.Clear();
        Owners.Clear();
        _loggedSwap = false;
    }
    /// <summary>
    /// Registers (or refreshes) the body samples for a remote avatar root. Safe to
    /// call every tick: it returns immediately once a live collider is cached, and
    /// otherwise retries the lookup so the entry appears as soon as the perception
    /// proxy is built.
    /// </summary>
    public static void Register(Transform? root)
    {
        if (root == null)
        {
            return;
        }

        int id = root.GetInstanceID();
        if (Bodies.TryGetValue(id, out Body? cached) &&
            cached.Collider != null &&
            cached.Collider.gameObject != null)
        {
            return;
        }

        Collider? collider = ResolvePerceptionCollider(root);
        if (collider == null)
        {
            return;
        }

        var body = new Body { Collider = collider, Root = collider.transform };
        body.Colliders[0] = collider;
        body.Positions[0] = collider.bounds.center;
        Bodies[id] = body;
    }

    /// <summary>
    /// Feeds a remote puppet into an enemy's perception result. The game's own
    /// threat evaluation only walks the members of the player faction it already
    /// knows about, and a registered puppet never makes it into that walk, so the
    /// enemy ignores the client. The game collects what an enemy can see into
    /// <c>Senses.tempVisibleTargets</c> and swaps that buffer into
    /// <c>visibleTargets</c> at the end of the update, so calling the game's own
    /// <c>seeTarget</c> right after <c>clearTargets</c> puts the puppet through
    /// exactly the same pipeline as any other target: suspicion, chase, aim and
    /// fire all run unmodified.
    /// </summary>
    public static void InjectRemotePlayers(Senses? senses)
    {
        if (senses == null ||
            CoopPlugin.Settings.Role != CoopRole.Host ||
            !CoopPlugin.AiEnabled ||
            !CoopPlugin.AiTargetRemotePlayer ||
            Bodies.Count == 0 ||
            CoopPlugin.Network == null ||
            CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        EnemyController? owner = OwnerOf(senses);
        if (owner == null || IsFriendly(owner))
        {
            return;
        }

        Vector3 eye;
        try
        {
            eye = senses.transform.position;
        }
        catch (Exception)
        {
            return;
        }

        _calls++;
        foreach (Body body in Bodies.Values)
        {
            Collider? collider = body.Collider;
            Transform? target = body.Root;
            if (collider == null || target == null || collider.gameObject == null)
            {
                continue;
            }

            Vector3 centre;
            try
            {
                centre = collider.bounds.center;
            }
            catch (Exception)
            {
                continue;
            }

            float distance = Vector3.Distance(eye, centre);
            if (distance > Range(owner) ||
                !InSightCone(owner, eye, centre) ||
                !HasLineOfSight(eye, centre, collider, distance))
            {
                continue;
            }

            try
            {
                senses.seeTarget(InjectThreat, target);
                senses.seesPlayer = true;
                NoteInjected(owner, target, distance);
            }
            catch (Exception exception)
            {
                NoteFailure(exception);
            }
        }
    }

    /// <summary>
    /// The enemy's own sight range, clamped to a sane cap so a stray designer
    /// value cannot make the whole level visible to the puppet check.
    /// </summary>
    private static float Range(EnemyController owner)
    {
        try
        {
            float range = owner.sightRange;
            if (range <= 1.0f || range > InjectRange)
            {
                return InjectRange;
            }

            return range;
        }
        catch (Exception)
        {
            return InjectRange;
        }
    }

    /// <summary>
    /// Repeats the game's own vision cone test in the horizontal plane. The game
    /// stores the cone half-angle as <c>angleCosine</c>, and a value that has not
    /// been initialised yet is treated as a full half-turn so the check can never
    /// hide a puppet on its own.
    /// </summary>
    private static bool InSightCone(EnemyController owner, Vector3 eye, Vector3 centre)
    {
        try
        {
            if (owner.blind)
            {
                return false;
            }

            Vector3 toTarget = centre - eye;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return true;
            }

            Vector3 forward = owner.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                return true;
            }

            float limit = owner.angleCosine;
            if (limit <= 0f)
            {
                limit = 0f;
            }

            return Vector3.Dot(toTarget.normalized, forward.normalized) >= limit;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void NoteFailure(Exception exception)
    {
        if (_failLogs >= InjectLogLimit)
        {
            return;
        }

        _failLogs++;
        CoopPlugin.LogSource.LogWarning(
            $"[ai] seeTarget injection failed: {exception.GetType().Name}: {exception.Message}"
        );
    }

    private static EnemyController? OwnerOf(Senses senses)
    {
        int id;
        try
        {
            id = senses.GetInstanceID();
        }
        catch (Exception)
        {
            return null;
        }

        if (Owners.TryGetValue(id, out EnemyController? cached))
        {
            if (cached != null && cached.gameObject != null)
            {
                return cached;
            }

            Owners.Remove(id);
        }

        EnemyController? owner;
        try
        {
            owner = senses.GetComponentInParent<EnemyController>();
        }
        catch (Exception)
        {
            return null;
        }

        Owners[id] = owner;
        return owner;
    }

    private static bool IsFriendly(EnemyController owner)
    {
        try
        {
            return owner.friendly;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// True when nothing but the puppet itself sits between the enemy's eye and
    /// the puppet's body. Rays that end on the puppet's own collider are accepted,
    /// so the puppet's body cannot shadow its own perception sample.
    /// </summary>
    private static bool HasLineOfSight(Vector3 eye, Vector3 centre, Collider body, float distance)
    {
        try
        {
            Vector3 direction = centre - eye;
            float length = direction.magnitude;
            if (length < 0.01f)
            {
                return true;
            }

            direction /= length;

            if (!Physics.Raycast(eye, direction, out RaycastHit hit, length + 1.0f, SightMask, QueryTriggerInteraction.Ignore))
            {
                return true;
            }

            return hit.collider == body || hit.distance >= distance - 0.75f;
        }
        catch (Exception)
        {
            // If the test is unavailable, treat the puppet as visible rather than
            // silently dropping the feature.
            return true;
        }
    }

    private static void NoteInjected(EnemyController owner, Transform target, float distance)
    {
        if (_injectLogs >= InjectLogLimit)
        {
            return;
        }

        _injectLogs++;
        CoopPlugin.LogSource.LogInfo(
            $"[ai] '{SafeName(owner)}' now sees remote target '{target.name}' at {distance:F1} m " +
            $"({_injectLogs}/{InjectLogLimit}, calls={_calls}) {ConeInfo(owner)}"
        );
    }

    private static string ConeInfo(EnemyController owner)
    {
        try
        {
            return $"sightRange={owner.sightRange:F0} sightAngle={owner.sightAngle:F0} " +
                   $"angleCosine={owner.angleCosine:F3} blind={owner.blind}";
        }
        catch (Exception)
        {
            return "sight=<unavailable>";
        }
    }

    private static string SafeName(EnemyController owner)
    {
        try
        {
            return owner.CharacterName ?? "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>
    /// Finds the body registered for a target transform. The registered centre of
    /// mass is normally the perception proxy (a direct child of the avatar root),
    /// so the lookup also walks a few parents to reach the registered root.
    /// </summary>
    private static bool TryGetBody(Transform target, out Body body)
    {
        body = null!;
        Transform? current = target;
        for (int depth = 0; current != null && depth < 4; depth++)
        {
            if (Bodies.TryGetValue(current.GetInstanceID(), out Body? found))
            {
                body = found;
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    /// <summary>
    /// Swaps the controller's LightController body samples for the avatar's own
    /// collider when the target is a registered remote avatar. Returns the state
    /// that must be handed back to <see cref="Restore"/>; <c>null</c> means nothing
    /// was changed.
    /// </summary>
    public static object? Swap(AIController? controller, Transform? target)
    {
        if (controller == null || target == null)
        {
            return null;
        }

        if (!TryGetBody(target, out Body? body))
        {
            return null;
        }

        Collider? collider = body.Collider;
        if (collider == null || collider.gameObject == null)
        {
            return null;
        }

        LightController? light = controller.lightcontrol;
        if (light == null)
        {
            return null;
        }

        // The puppet moves, so refresh the sample point from the live collider.
        body.Positions[0] = collider.bounds.center;

        var state = new SwapState
        {
            Light = light,
            Colliders = light.playerColliders,
            Positions = light.colliderPositions,
        };

        light.playerColliders = body.Colliders;
        light.colliderPositions = body.Positions;

        if (!_loggedSwap)
        {
            _loggedSwap = true;
            CoopPlugin.LogSource.LogInfo(
                $"[ai] perception swap engaged for '{target.name}' " +
                $"(collider='{collider.name}', layer={collider.gameObject.layer}," +
                $" sample={body.Positions[0]})."
            );
        }

        return state;
    }

    public static void Restore(object? state)
    {
        if (state is not SwapState swap)
        {
            return;
        }

        LightController? light = swap.Light;
        if (light == null)
        {
            return;
        }

        light.playerColliders = swap.Colliders;
        light.colliderPositions = swap.Positions;
    }

    private static Collider? ResolvePerceptionCollider(Transform root)
    {
        try
        {
            Transform? proxy = root.Find(PerceptionProxyName);
            if (proxy != null)
            {
                Collider? capsule = proxy.GetComponent<Collider>();
                if (capsule != null)
                {
                    return capsule;
                }
            }

            // Fall back to any enabled collider under the root that sits on a layer
            // the AI actually tests. The puppet's combat proxy lives on "Ignore
            // Raycast" (2) and the default layer (0) is never tested either, so
            // neither is a candidate.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled)
                {
                    continue;
                }

                int layer = collider.gameObject.layer;
                if (layer != 0 && layer != 2)
                {
                    return collider;
                }
            }
        }
        catch (Exception)
        {
            // Ignored: no collider means no swap, which is the previous behaviour.
        }

        return null;
    }
}

/// <summary>
/// Runs the body swap around the game's own threat evaluation. The prefix swaps
/// the local player's body samples for the remote puppet's when the target is a
/// registered avatar; the postfix puts them back. It also logs every distinct
/// target the AI evaluates, which is the cheapest proof of whether a puppet ever
/// enters the game's faction walk.
/// </summary>
[HarmonyPatch(typeof(AIController), "calculateThreat")]
internal static class AiCalculateThreatPatch
{
    private static readonly HashSet<string> SeenTargets = new();
    private static int _calls;

    private static void Prefix(AIController __instance, Transform target, out object? __state)
    {
        _calls++;
        __state = AiPerception.Swap(__instance, target);
    }

    private static void Postfix(Transform target, object? __state, float __result)
    {
        AiPerception.Restore(__state);

        if (target == null || _calls < 2)
        {
            return;
        }

        string name;
        try
        {
            name = target.name;
        }
        catch (Exception)
        {
            return;
        }

        bool registered = AiPerception.IsRegistered(target);
        string label = registered ? $"{name}(AVATAR)" : name;
        if (!SeenTargets.Add(label))
        {
            return;
        }

        CoopPlugin.LogSource.LogInfo(
            $"[aidbg] calculateThreat target='{name}' registered={registered} " +
            $"swap={(__state != null)} result={__result:F3} calls={_calls} " +
            $"distinct={SeenTargets.Count}"
        );
    }
}

/// <summary>
/// Adds a visible remote puppet to an enemy's perception result. The game clears
/// its per-frame sense buffer in <c>clearTargets</c> and only then runs its own
/// cone checks, so hooking the clear means the puppet is queued alongside every
/// real target and reaches the targeting logic through the normal path.
/// </summary>
[HarmonyPatch(typeof(Senses), "clearTargets")]
internal static class SensesClearTargetsPatch
{
    private static void Postfix(Senses __instance)
    {
        AiPerception.InjectRemotePlayers(__instance);
    }
}
