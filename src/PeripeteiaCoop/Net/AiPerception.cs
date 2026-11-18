using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Host-side bridge that lets the game's own AI perception "see" a remote player
/// puppet.
///
/// The game decides visibility for every member of the player faction
/// (faction index 0) through <c>AIController.lightcontrol</c>, which is the
/// <c>LightController</c> of the LOCAL player. Inside
/// <c>AIController.calculateThreat</c> the faction-0 branch raycasts from the
/// enemy's eye to each collider in <c>lightcontrol.playerColliders</c> and counts
/// the target as visible when one of them is hit. That is correct for the host's
/// own body, but the remote puppet's colliders are not in that list, so a
/// registered client target is sampled against the host's body and the enemy
/// never reacts to the client.
///
/// The fix swaps the LightController's body samples for the puppet's own
/// perception collider for the duration of one <c>calculateThreat</c> call, and
/// only when the target being evaluated is a registered remote avatar. Every
/// other part of the game's logic (threat maths, aim, senses registration) then
/// runs unchanged, just against the correct body.
/// </summary>
internal static class AiPerception
{
    private const string PerceptionProxyName = "PeripeteiaCoop_AiProxy";

    private sealed class Body
    {
        public Collider Collider = null!;
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
    private static bool _loggedSwap;

    public static int Count => Bodies.Count;

    public static void Clear()
    {
        Bodies.Clear();
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

        var body = new Body { Collider = collider };
        body.Colliders[0] = collider;
        body.Positions[0] = collider.bounds.center;
        Bodies[id] = body;
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

        if (!Bodies.TryGetValue(target.GetInstanceID(), out Body? body))
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

            // Fall back to any enabled collider under the root that is not on the
            // default layer: the puppet's combat proxy sits on "Ignore Raycast",
            // which the AI never tests, so it is not a candidate.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null && collider.enabled && collider.gameObject.layer != 0)
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
/// registered avatar; the postfix puts them back.
/// </summary>
[HarmonyPatch(typeof(AIController), "calculateThreat")]
internal static class AiCalculateThreatPatch
{
    private static void Prefix(AIController __instance, Transform target, out object? __state)
    {
        __state = AiPerception.Swap(__instance, target);
    }

    private static void Postfix(object? __state)
    {
        AiPerception.Restore(__state);
    }
}
