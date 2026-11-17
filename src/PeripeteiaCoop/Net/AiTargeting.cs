using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Makes the host's AI aware of the remote player.
///
/// The host simulates every enemy, but the game's perception only knows the
/// host's own <c>PlayerMovement</c>. The remote player is a visual clone on the
/// host, so by default enemies ignore the client. The game exposes
/// <c>AIController.addCustomTarget</c> for exactly this: a target that is not an
/// <c>EnemyController</c> (the player itself is registered this way). We register
/// the remote avatar as such a target so enemies can notice, chase and shoot at
/// the client.
///
/// This is host-only, config-gated (<c>Ai.TargetRemotePlayer</c>) and defensive:
/// if the game rejects the registration, the pair is disabled and logged once,
/// it never retries and never breaks the local frame.
/// </summary>
internal static class AiTargeting
{
    private static readonly HashSet<string> Done = new();
    private static readonly HashSet<string> Failed = new();

    private static string _scene = string.Empty;
    private static float _nextScan;
    private static bool _loggedFactions;

    public static int RegisteredCount => Done.Count;

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (CoopPlugin.Settings.Role != CoopRole.Host)
        {
            return;
        }

        if (!CoopPlugin.AiEnabled || !CoopPlugin.AiTargetRemotePlayer)
        {
            return;
        }

        CoopNetwork? network = CoopPlugin.Network;
        if (network == null)
        {
            return;
        }

        string scene = localPlayer != null ? localPlayer.gameObject.scene.name : string.Empty;
        if (!string.Equals(scene, _scene, StringComparison.Ordinal))
        {
            _scene = scene;
            Done.Clear();
            Failed.Clear();
            _loggedFactions = false;
        }

        if (Time.unscaledTime < _nextScan)
        {
            return;
        }

        _nextScan = Time.unscaledTime + 2.0f;

        AIController[] controllers;
        try
        {
            controllers = UnityEngine.Object.FindObjectsOfType<AIController>();
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[ai] could not enumerate AIController: {exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        if (controllers == null || controllers.Length == 0)
        {
            return;
        }

        foreach (AIController controller in controllers)
        {
            if (controller == null)
            {
                continue;
            }

            int faction = ResolvePlayerFaction(controller);

            foreach (int id in network.RemotePlayerIds())
            {
                Transform? root = network.GetAvatarRoot(id);
                if (root == null)
                {
                    continue;
                }

                string key = controller.GetInstanceID() + ":" + id;
                if (Done.Contains(key) || Failed.Contains(key))
                {
                    continue;
                }

                try
                {
                    // (add, centerOfMass, sightRange, faction, ec, HP, dmg, aiTarget, senses, hboxes)
                    // The remote avatar has no EnemyController / HealthScript / HitBoxes on
                    // this machine, so only the transform and the faction are supplied.
                    controller.addCustomTarget(
                        true,
                        root,
                        -1.0f,
                        faction,
                        null!,
                        null!,
                        null!,
                        null!,
                        null!,
                        null!
                    );
                    Done.Add(key);
                    CoopPlugin.LogSource.LogInfo(
                        $"[ai] remote player {id} registered as a target on '{controller.name}' " +
                        $"(faction={faction})."
                    );
                }
                catch (Exception exception)
                {
                    Failed.Add(key);
                    CoopPlugin.LogSource.LogWarning(
                        $"[ai] addCustomTarget failed on '{controller.name}': " +
                        $"{exception.GetType().Name}: {exception.Message}. Remote targeting disabled for this pair."
                    );
                }
            }
        }
    }

    /// <summary>
    /// The player's faction number is not documented; the game's own player
    /// registration uses the default (-1). We log the faction names once so the
    /// correct number can be pinned down, and prefer a faction named like the
    /// player when one exists.
    /// </summary>
    private static int ResolvePlayerFaction(AIController controller)
    {
        try
        {
            AIController.faction[]? factions = controller.factions;
            if (factions != null && factions.Length > 0)
            {
                if (!_loggedFactions)
                {
                    _loggedFactions = true;
                    for (int i = 0; i < factions.Length; i++)
                    {
                        CoopPlugin.LogSource.LogInfo($"[ai] faction[{i}] = '{factions[i].name}'.");
                    }
                }

                for (int i = 0; i < factions.Length; i++)
                {
                    string name = factions[i].name ?? string.Empty;
                    if (name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("marie", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return i;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return -1;
    }
}
