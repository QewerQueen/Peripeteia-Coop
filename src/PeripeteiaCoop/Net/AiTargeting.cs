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

    private const string HealthProxyName = "PeripeteiaCoop_AiHealth";
    private const string PerceptionProxyName = "PeripeteiaCoop_AiProxy";

    private static string _scene = string.Empty;
    private static float _nextScan;
    private static bool _loggedFactions;
    private static bool _dumped;

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
            AiPerception.Clear();
            _loggedFactions = false;
            _dumped = false;
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

        // Teach the perception swap where each puppet's body is. Done once per
        // avatar per scan, independent of the per-controller registration below,
        // because the swap only needs the collider and not a faction member entry.
        foreach (int id in network.RemotePlayerIds())
        {
            AiPerception.Register(network.GetAvatarRoot(id));
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

                // Key on the avatar root too: after a respawn the puppet can be a brand
                // new GameObject, which must be registered again.
                string key = controller.GetInstanceID() + ":" + id + ":" + root.GetInstanceID();
                if (Done.Contains(key) || Failed.Contains(key))
                {
                    continue;
                }

                HealthScript? health = EnsureHealthProxy(root);
                if (health == null)
                {
                    Failed.Add(key);
                    continue;
                }

                // Register the perception proxy (at the body centre) as the centre of
                // mass when it exists, so the AI aims and looks at the body instead of
                // the feet. Falls back to the avatar root.
                Transform aim = ResolveAimTransform(root);

                try
                {
                    // (add, centerOfMass, sightRange, faction, ec, HP, dmg, aiTarget, senses, hboxes)
                    // The avatar has no EnemyController / HitBoxes on this machine, but the
                    // game's perception refuses any target without a live HealthScript (see
                    // EnsureHealthProxy), so the inert stand-in is supplied as HP.
                    controller.addCustomTarget(
                        true,
                        aim,
                        -1.0f,
                        faction,
                        null!,
                        health,
                        null!,
                        null!,
                        null!,
                        null!
                    );
                    Done.Add(key);
                    CoopPlugin.LogSource.LogInfo(
                        $"[ai] remote player {id} registered as a target on '{controller.name}' " +
                        $"(faction={faction}, HP proxy on '{root.name}', aim on '{aim.name}')."
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

        if (!_dumped && RegisteredCount > 0 && controllers.Length > 0)
        {
            _dumped = true;
            DumpDiagnostics(controllers[0], localPlayer);
        }
    }

    /// <summary>
    /// One-shot, per-scene dump of everything the game's AI uses to decide whether
    /// it can see someone. Written at Info so a retest can be diagnosed without
    /// turning the probe on: it shows the local player's components and colliders
    /// next to the AI's own masks and the member lists the AI actually iterates.
    /// </summary>
    private static void DumpDiagnostics(AIController controller, PlayerMovement? localPlayer)
    {
        try
        {
            if (localPlayer != null)
            {
                GameObject player = localPlayer.gameObject;
                CoopPlugin.LogSource.LogInfo(
                    $"[ai] local player '{player.name}': layer={player.layer}({LayerMask.LayerToName(player.layer)}) " +
                    $"AITarget={player.GetComponentInChildren<AITarget>(true) != null} " +
                    $"Senses={player.GetComponentInChildren<Senses>(true) != null} " +
                    $"HitBoxes={player.GetComponentInChildren<HitBoxes>(true) != null} " +
                    $"HitBox={player.GetComponentInChildren<HitBox>(true) != null} " +
                    $"damageScript={player.GetComponentInChildren<damageScript>(true) != null} " +
                    $"HealthScript={player.GetComponentInChildren<HealthScript>(true) != null}."
                );

                foreach (Collider collider in player.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null)
                    {
                        continue;
                    }

                    CoopPlugin.LogSource.LogInfo(
                        $"[ai]   player collider {BuildPath(collider.transform, player.transform)}: " +
                        $"layer={collider.gameObject.layer}({LayerMask.LayerToName(collider.gameObject.layer)}) " +
                        $"enabled={collider.enabled} active={collider.gameObject.activeInHierarchy}."
                    );
                }
            }

            CoopPlugin.LogSource.LogInfo(
                $"[ai] '{controller.name}': raycastLayers={DescribeMask(controller.raycastLayers)} " +
                $"playerMask={DescribeMask(controller.playerMask)} goreMask={DescribeMask(controller.goreMask)} " +
                $"cullDistance={controller.AICullDistance} allowedRaycasts={controller.allowedRaycasts}."
            );

            int factions = controller.factions != null ? controller.factions.Length : 0;
            int logged = 0;
            for (int faction = 0; faction < factions && logged < 60; faction++)
            {
                AIController.factionMemberData[]? members;
                try
                {
                    members = controller.getFactionMembers(faction);
                }
                catch (Exception)
                {
                    continue;
                }

                if (members == null)
                {
                    continue;
                }

                for (int i = 0; i < members.Length && logged < 60; i++)
                {
                    logged++;
                    AIController.factionMemberData member = members[i];
                    string name = member.trans != null
                        ? member.trans.name
                        : (member.GO != null ? member.GO.name : "<none>");
                    CoopPlugin.LogSource.LogInfo(
                        $"[ai]   faction[{faction}][{i}] '{name}' alive={member.alive} " +
                        $"goodToGo={member.goodToGo} con={member.con != null} senses={member.senses != null} " +
                        $"AITar={member.AITar != null} HP={member.HP != null} DMG={member.DMG != null} " +
                        $"hboxes={member.hboxes != null} head={member.head != null} " +
                        $"sight={member.sight:F1} snipper={member.snipper:F1} pos={member.pos}."
                    );
                }
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[ai] diagnostics failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// The game only accepts a target whose member data carries a live
    /// <see cref="HealthScript"/> (or a destructible <c>damageScript</c>):
    /// <c>checkCone</c> bails out when the target has neither, so a member
    /// registered with a null HP is silently ignored and enemies never react.
    /// The remote avatar owns no health on this machine (health lives on the
    /// client), so it gets an inert stand-in. The component is disabled the
    /// instant it is added, so none of HealthScript's lifecycle code runs and it
    /// can never be damaged or die.
    /// </summary>
    private static HealthScript? EnsureHealthProxy(Transform root)
    {
        try
        {
            Transform? existing = root.Find(HealthProxyName);
            if (existing != null)
            {
                HealthScript? cached = existing.GetComponent<HealthScript>();
                if (cached != null)
                {
                    return cached;
                }
            }

            var proxy = new GameObject(HealthProxyName);
            proxy.transform.SetParent(root, false);
            proxy.transform.localPosition = Vector3.zero;

            HealthScript health = proxy.AddComponent<HealthScript>();
            health.enabled = false;
            health.HP = 1;
            health.maxHP = 1;
            health.dead = false;
            health.unconcious = false;
            health.fallen = false;
            health.enemy = false;
            health.centerOfMass = FindCenterOfMass(root);
            return health;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[ai] could not create the health proxy on '{root.name}': " +
                $"{exception.GetType().Name}: {exception.Message}. Remote targeting disabled for this avatar."
            );
            return null;
        }
    }

    /// <summary>
    /// The transform the AI should treat as the avatar's centre of mass: the
    /// perception proxy, whose own transform sits at the body centre. Falls back
    /// to the avatar root when the proxy is absent (for example when the extra
    /// perception collider is disabled in the config).
    /// </summary>
    private static Transform ResolveAimTransform(Transform? root)
    {
        if (root == null)
        {
            return null!;
        }

        Transform? proxy = root.Find(PerceptionProxyName);
        return proxy != null ? proxy : root;
    }

    /// <summary>
    /// A torso transform to stand in for the player's centre of mass, so the AI
    /// aims at the body rather than the feet. Falls back to the avatar root.
    /// </summary>
    private static Transform FindCenterOfMass(Transform root)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child == null)
            {
                continue;
            }

            string name = child.name;
            if (name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return child;
            }
        }

        return root;
    }

    private static string DescribeMask(LayerMask mask)
    {
        var parts = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            if ((mask.value & (1 << i)) != 0)
            {
                parts.Add($"{i}({LayerMask.LayerToName(i)})");
            }
        }

        return parts.Count > 0 ? string.Join(",", parts) : "<none>";
    }

    private static string BuildPath(Transform transform, Transform root)
    {
        var parts = new List<string>();
        Transform? current = transform;
        while (current != null && current != root)
        {
            parts.Insert(0, current.name);
            current = current.parent;
        }

        return parts.Count > 0 ? string.Join("/", parts) : transform.name;
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
