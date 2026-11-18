using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Player-versus-player combat for the co-op prototype.
///
/// The remote player is a visual-only clone: it has no gameplay scripts and no
/// colliders, so originally bullets passed through the other player and you
/// could walk through them. This module gives every remote puppet a physics
/// proxy (<see cref="RemoteHitProxy"/>) and translates local shots that hit that
/// proxy into a network damage event for the player it belongs to.
///
/// Damage is applied on the victim's own machine through the game's real
/// <see cref="HealthScript.TakeDMG(bool, int, Transform, bool, Vector3, bool)"/>
/// so limbs, bleeding, death and the HUD keep working. The shooter only sends the
/// hit; it never mutates the other player's health directly.
///
/// Collision is forced per collider pair with
/// <see cref="Physics.IgnoreCollision(Collider, Collider, bool)"/> instead of
/// touching the global layer matrix, so single-player behaviour is untouched.
/// </summary>
internal static class CombatSync
{
    private static readonly Dictionary<int, RemoteHitProxy> ProxyByCollider = new();
    private static readonly List<RemoteHitProxy> Proxies = new();

    // The three Shoot.Fire overloads can chain into each other, which would
    // report one trigger pull several times. Deduplicate per weapon so akimbo
    // (two guns firing in the same frame) still counts twice.
    private static readonly Dictionary<int, float> LastShotByWeapon = new();

    private static PlayerMovement? _localPlayer;
    private static CharacterController? _localController;
    private static int _proxyLayer = -1;
    private static float _nextLogTime;
    private static int _shotsSent;
    private static int _shotsHit;
    private static int _damageTaken;

    /// <summary>How many forwarded NPC hits are written to the log before it goes quiet.</summary>
    private const int NpcHitLogLimit = 10;

    private static int _npcHitsLogged;
    private static int _npcDamageLogged;

    /// <summary>Physics layer the remote proxies live on, or -1 before the first avatar.</summary>
    public static int ProxyLayer => _proxyLayer;

    public static void RegisterProxy(RemoteHitProxy proxy, Collider? body, Collider? head)
    {
        if (proxy == null)
        {
            return;
        }

        if (body != null)
        {
            ProxyByCollider[body.GetInstanceID()] = proxy;
        }

        if (head != null)
        {
            ProxyByCollider[head.GetInstanceID()] = proxy;
        }

        if (!Proxies.Contains(proxy))
        {
            Proxies.Add(proxy);
        }

        ApplyCollisionPolicy();
    }

    public static void UnregisterProxy(RemoteHitProxy proxy)
    {
        if (proxy == null)
        {
            return;
        }

        Proxies.Remove(proxy);

        var stale = new List<int>();
        foreach (KeyValuePair<int, RemoteHitProxy> pair in ProxyByCollider)
        {
            if (pair.Value == proxy)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (int key in stale)
        {
            ProxyByCollider.Remove(key);
        }
    }

    // Colliders that belong to a remote avatar but are not part of the combat
    // proxy itself - currently the AI perception capsule. They sit between the
    // muzzle and the proxy, so they must never count as a wall.
    private static readonly HashSet<int> AvatarExtraColliders = new();

    public static void RegisterAvatarCollider(Collider? collider)
    {
        if (collider == null)
        {
            return;
        }

        AvatarExtraColliders.Add(collider.GetInstanceID());
    }

    public static void ClearAvatarColliders()
    {
        AvatarExtraColliders.Clear();
    }

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        _localPlayer = localPlayer;

        CharacterController? controller = localPlayer != null
            ? localPlayer.GetComponent<CharacterController>()
            : null;
        if (controller != _localController)
        {
            _localController = controller;
            ApplyCollisionPolicy();
        }

        if (CoopPlugin.ProbeEnabled &&
            Proxies.Count > 0 &&
            Time.unscaledTime >= _nextLogTime)
        {
            _nextLogTime = Time.unscaledTime + 15.0f;
            CoopPlugin.LogSource.LogInfo(
                $"[combat] proxies={Proxies.Count} colliders={ProxyByCollider.Count} " +
                $"layer={_proxyLayer}({LayerMask.LayerToName(Mathf.Max(_proxyLayer, 0))}) " +
                $"shots={_shotsSent} hits={_shotsHit} taken={_damageTaken} " +
                $"damage={CoopPlugin.CombatPlayerDamage} collision={CoopPlugin.CombatPlayerCollision}"
            );
        }
    }

    /// <summary>
    /// Layer for the remote hit/collision proxy. Defaults to the local player's
    /// own layer so the physics matrix treats the other player like a player.
    /// </summary>
    public static int ResolveProxyLayer(Transform? localPlayer)
    {
        if (CoopPlugin.CombatLayerOverride >= 0)
        {
            return Mathf.Clamp(CoopPlugin.CombatLayerOverride, 0, 31);
        }

        if (localPlayer != null)
        {
            CharacterController? controller = localPlayer.GetComponent<CharacterController>();
            if (controller != null)
            {
                return controller.gameObject.layer;
            }

            if (localPlayer.gameObject.layer != 0)
            {
                return localPlayer.gameObject.layer;
            }
        }

        return 0;
    }

    /// <summary>
    /// Builds the body/head proxy for a freshly cloned remote puppet. Called
    /// from <see cref="RemoteAvatar"/> after the visual is assembled so the
    /// colliders are children of the current root and are destroyed with it.
    /// </summary>
    public static RemoteHitProxy? AttachHitProxy(
        GameObject root,
        Transform? source,
        Animator? animator,
        int avatarId
    )
    {
        if (root == null)
        {
            return null;
        }

        if (!CoopPlugin.CombatEnabled && !CoopPlugin.CombatPlayerCollision)
        {
            return null;
        }

        try
        {
            int layer = ResolveProxyLayer(source);
            _proxyLayer = layer;

            float height = 1.8f;
            float radius = 0.3f;
            Vector3 center = new Vector3(0.0f, 0.9f, 0.0f);
            CharacterController? controller = source != null
                ? source.GetComponent<CharacterController>()
                : null;
            if (controller != null)
            {
                height = Mathf.Max(controller.height, 0.6f);
                radius = Mathf.Clamp(controller.radius, 0.1f, height * 0.5f);
                center = controller.center;
            }

            var proxyObject = new GameObject("PeripeteiaCoop_HitProxy");
            proxyObject.transform.SetParent(root.transform, false);
            proxyObject.layer = layer;

            Rigidbody body = proxyObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.detectCollisions = true;

            var capsule = proxyObject.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.center = center;
            capsule.height = height;
            capsule.radius = radius;
            capsule.isTrigger = false;

            Transform? headBone = null;
            if (animator != null && animator.isHuman)
            {
                try
                {
                    headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                }
                catch
                {
                    headBone = null;
                }
            }

            float headRadius = Mathf.Clamp(radius * 0.42f, 0.08f, 0.16f);
            var headObject = new GameObject("PeripeteiaCoop_HitProxy_Head");
            headObject.transform.SetParent(proxyObject.transform, false);
            headObject.layer = layer;
            var headSphere = headObject.AddComponent<SphereCollider>();
            headSphere.radius = headRadius;
            headSphere.center = Vector3.zero;
            headSphere.isTrigger = false;

            Vector3 headOffset = new Vector3(0.0f, headRadius * 0.9f, 0.0f);
            Vector3 headPosition = headBone != null
                ? headBone.position
                : root.transform.TransformPoint(
                    center + Vector3.up * (height * 0.5f - headRadius)
                );
            headObject.transform.position = headPosition + headOffset;

            var proxy = proxyObject.AddComponent<RemoteHitProxy>();
            proxy.Configure(avatarId, capsule, headSphere, height);
            proxy.BindHead(headBone, headOffset);

            ApplyCollisionPolicy();

            CoopPlugin.LogSource.LogInfo(
                $"[combat] hit proxy for avatar {avatarId}: layer={layer}" +
                $"({LayerMask.LayerToName(layer)}) height={height:F2} radius={radius:F2} " +
                $"headRadius={headRadius:F2} headBone={(headBone != null ? headBone.name : "<none>")}"
            );

            return proxy;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[combat] failed to build hit proxy for avatar {avatarId}: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            return null;
        }
    }

    /// <summary>
    /// Adds an inert collider on the layer the game's AI uses to detect the
    /// player, so the host's perception rays can find the remote puppet.
    ///
    /// The combat proxy lives on the player's own root layer, which is "Ignore
    /// Raycast" so the mod's own bullet rays stay isolated from the rest of the
    /// world. The AI, however, looks for the player on the hitbox layer, so
    /// without a collider there its perception rays pass straight through the
    /// puppet and enemies never notice the client. The extra capsule only exists
    /// to be raycast against: it is kinematic and the hitbox layer collides with
    /// nothing else, so it never pushes the player or the level around.
    ///
    /// Returns the created object so the caller can switch it off while the
    /// puppet is a corpse.
    /// </summary>
    public static GameObject? AttachPerceptionProxy(
        GameObject root,
        Transform? source,
        int avatarId
    )
    {
        // There is exactly one remote puppet, so any collider registered by a
        // previous build of it is stale by now.
        ClearAvatarColliders();

        if (root == null ||
            CoopPlugin.Settings.Role != CoopRole.Host ||
            !CoopPlugin.AiEnabled ||
            !CoopPlugin.AiTargetRemotePlayer ||
            !CoopPlugin.AiPerceptionCollider)
        {
            return null;
        }

        int layer = ResolvePerceptionLayer(source);
        if (layer < 0)
        {
            return null;
        }

        try
        {
            float height = 1.8f;
            float radius = 0.3f;
            Vector3 center = new Vector3(0.0f, 0.9f, 0.0f);
            CharacterController? controller = source != null
                ? source.GetComponent<CharacterController>()
                : null;
            if (controller != null)
            {
                height = Mathf.Max(controller.height, 0.6f);
                radius = Mathf.Clamp(controller.radius, 0.1f, height * 0.5f);
                center = controller.center;
            }

            var proxyObject = new GameObject("PeripeteiaCoop_AiProxy");
            proxyObject.transform.SetParent(root.transform, false);
            proxyObject.layer = layer;

            Rigidbody body = proxyObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.detectCollisions = true;

            var capsule = proxyObject.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.center = center;
            capsule.height = height;
            capsule.radius = radius;
            capsule.isTrigger = false;

            // Register it so the shot raycast never mistakes the puppet's own
            // body for a wall between a muzzle and the combat proxy.
            RegisterAvatarCollider(capsule);

            CoopPlugin.LogSource.LogInfo(
                $"[ai] perception collider for avatar {avatarId}: " +
                $"layer={layer}({LayerMask.LayerToName(layer)}) height={height:F2} radius={radius:F2}."
            );

            return proxyObject;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[ai] failed to build the perception collider for avatar {avatarId}: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            return null;
        }
    }

    /// <summary>
    /// The layer the game's AI uses to detect the player: the layer of the local
    /// player's own hitbox colliders. Returns -1 when it cannot be determined.
    /// </summary>
    private static int ResolvePerceptionLayer(Transform? localPlayer)
    {
        if (localPlayer == null)
        {
            return -1;
        }

        try
        {
            foreach (HitBox hitBox in localPlayer.GetComponentsInChildren<HitBox>(true))
            {
                if (hitBox != null && hitBox.gameObject != null && hitBox.gameObject.layer != 0)
                {
                    return hitBox.gameObject.layer;
                }
            }

            foreach (Collider collider in localPlayer.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null &&
                    collider.gameObject != null &&
                    collider.gameObject != localPlayer.gameObject &&
                    collider.gameObject.layer != 0)
                {
                    return collider.gameObject.layer;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the "unknown" result.
        }

        return -1;
    }

    private static void ApplyCollisionPolicy()
    {
        if (_localController == null)
        {
            return;
        }

        bool solid = CoopPlugin.CombatPlayerCollision;
        foreach (RemoteHitProxy proxy in Proxies)
        {
            if (proxy == null || proxy.gameObject == null)
            {
                continue;
            }

            foreach (Collider collider in proxy.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null)
                {
                    continue;
                }

                try
                {
                    Physics.IgnoreCollision(_localController, collider, !solid);
                }
                catch (Exception)
                {
                    // A collider can be torn down mid-frame while an avatar is
                    // rebuilt; a failed re-apply is harmless.
                }
            }
        }
    }

    /// <summary>
    /// Called from the Shoot.Fire patches. Works out whether the bullet hit a
    /// remote proxy and, if so, reports the hit to that player.
    ///
    /// Two kinds of shooter can hit the remote player: the local player
    /// (player-versus-player) and an NPC. Enemies fire through the very same
    /// <see cref="Shoot"/> component, and the host simulates all of them, so an
    /// enemy shot that reaches the client is detected here too and forwarded as
    /// damage for the client to apply. <see cref="WeaponSync.IsLocalPlayerWeapon"/>
    /// tells the two apart.
    /// </summary>
    public static void OnShot(Shoot shoot)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline ||
            !CoopPlugin.CombatEnabled ||
            shoot == null ||
            _localPlayer == null ||
            ProxyByCollider.Count == 0 ||
            CoopPlugin.Network == null ||
            CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        bool byLocalPlayer = WeaponSync.IsLocalPlayerWeapon(shoot);
        if (byLocalPlayer)
        {
            if (!CoopPlugin.CombatPlayerDamage)
            {
                return;
            }
        }
        else if (!CoopPlugin.AiEnabled ||
                 !CoopPlugin.AiTargetRemotePlayer ||
                 !CoopPlugin.AiDamageRemotePlayer)
        {
            return;
        }

        int weaponId = shoot.GetInstanceID();
        if (LastShotByWeapon.TryGetValue(weaponId, out float lastShot) &&
            Time.unscaledTime - lastShot < 0.02f)
        {
            return;
        }

        LastShotByWeapon[weaponId] = Time.unscaledTime;

        int damage = Mathf.RoundToInt(shoot.DMG * CoopPlugin.CombatDamageScale);
        if (damage <= 0)
        {
            return;
        }

        if (!TryResolveShotRay(shoot, out Vector3 origin, out Vector3 direction, out float range))
        {
            return;
        }

        _shotsSent++;

        int mask = 1 << Mathf.Max(_proxyLayer, 0);
        RaycastHit[] hits;
        try
        {
            hits = Physics.RaycastAll(origin, direction, range, mask, QueryTriggerInteraction.Ignore);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[combat] proxy raycast failed: {exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        RemoteHitProxy? best = null;
        int bestColliderId = 0;
        float bestDistance = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null ||
                hit.distance >= bestDistance ||
                IsLocalPlayerCollider(hit.collider))
            {
                continue;
            }

            int id = hit.collider.GetInstanceID();
            if (!ProxyByCollider.TryGetValue(id, out RemoteHitProxy? proxy) || proxy == null)
            {
                continue;
            }

            best = proxy;
            bestColliderId = id;
            bestDistance = hit.distance;
        }

        if (best == null)
        {
            return;
        }

        // A wall between the muzzle and the puppet blocks the shot. The proxy
        // layer is excluded from this test so the shooter's own body cannot
        // shadow the target. Hits closer than a few centimetres are ignored so
        // a muzzle buried in the shooter's own geometry does not block the shot.
        try
        {
            RaycastHit[] blockers = Physics.RaycastAll(
                origin,
                direction,
                bestDistance,
                ~mask,
                QueryTriggerInteraction.Ignore
            );
            foreach (RaycastHit blocker in blockers)
            {
                if (blocker.collider == null ||
                    blocker.distance < 0.05f ||
                    (byLocalPlayer && IsLocalPlayerCollider(blocker.collider)) ||
                    IsAvatarCollider(blocker.collider))
                {
                    continue;
                }

                return;
            }
        }
        catch (Exception)
        {
            // If the wall test is unavailable, still accept the proxy hit.
        }

        bool headshot = best.IsHead(bestColliderId);
        Vector3 point = origin + direction * bestDistance;
        _shotsHit++;

        // An NPC has no player to credit, so it reports a negative dealer and the
        // victim's machine will not treat it as friendly fire from the partner.
        int dealerId = byLocalPlayer ? CoopPlugin.Network.LocalId : -1;
        CoopPlugin.Network.SendDamage(best.AvatarId, dealerId, damage, headshot, point);

        if (byLocalPlayer)
        {
            SpawnHitMarker(point, headshot);
        }

        if (!byLocalPlayer && _npcHitsLogged < NpcHitLogLimit)
        {
            // Proof that enemy fire is being forwarded; capped so sustained
            // combat cannot flood the log.
            _npcHitsLogged++;
            CoopPlugin.LogSource.LogInfo(
                $"[combat] npc hit avatar {best.AvatarId} for {damage} headshot={headshot} " +
                $"distance={bestDistance:F2} point={point} ({_npcHitsLogged}/{NpcHitLogLimit})"
            );
        }
        else if (CoopPlugin.ProbeEnabled)
        {
            CoopPlugin.LogSource.LogInfo(
                $"[combat] {(byLocalPlayer ? "player" : "npc")} hit avatar {best.AvatarId} " +
                $"for {damage} headshot={headshot} distance={bestDistance:F2} point={point}"
            );
        }
    }

    /// <summary>Applies damage that the network delivered for the local player.</summary>
    public static void ApplyLocalDamage(int dealerId, int amount, bool headshot, Vector3 point)
    {
        if (!CoopPlugin.CombatEnabled || amount <= 0)
        {
            return;
        }

        // A negative dealer means the damage came from an NPC, not from the
        // partner, so the player-versus-player switch does not apply to it.
        bool fromPlayer = dealerId >= 0;
        if (fromPlayer && !CoopPlugin.CombatPlayerDamage)
        {
            return;
        }

        if (fromPlayer && CoopPlugin.Network != null && dealerId == CoopPlugin.Network.LocalId)
        {
            return;
        }

        if (_localPlayer == null || _localPlayer.gameObject == null)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[combat] damage for {amount} arrived but the local player is not ready."
            );
            return;
        }

        HealthScript? health = _localPlayer.GetComponent<HealthScript>();
        if (health == null)
        {
            health = _localPlayer.GetComponentInParent<HealthScript>();
        }

        if (health == null)
        {
            CoopPlugin.LogSource.LogWarning(
                "[combat] local player has no HealthScript; incoming damage was dropped."
            );
            return;
        }

        Transform? dealer = CoopPlugin.Network != null
            ? CoopPlugin.Network.GetAvatarRoot(dealerId)
            : null;

        _damageTaken++;
        try
        {
            health.TakeDMG(headshot, amount, dealer, false, point, false);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[combat] TakeDMG failed: {exception.GetType().Name}: {exception.Message}"
            );
        }

        if (!fromPlayer && _npcDamageLogged < NpcHitLogLimit)
        {
            // Client-side proof that an NPC hit was actually applied locally.
            _npcDamageLogged++;
            CoopPlugin.LogSource.LogInfo(
                $"[combat] took {amount} NPC damage headshot={headshot} point={point} " +
                $"hp={health.HP} ({_npcDamageLogged}/{NpcHitLogLimit})"
            );
        }
        else if (CoopPlugin.ProbeEnabled)
        {
            string source = fromPlayer ? $"avatar {dealerId}" : "an NPC";
            CoopPlugin.LogSource.LogInfo(
                $"[combat] took {amount} damage from {source} " +
                $"headshot={headshot} point={point} hp={(health != null ? health.HP : 0)}"
            );
        }
    }

    private static bool TryResolveShotRay(
        Shoot shoot,
        out Vector3 origin,
        out Vector3 direction,
        out float range
    )
    {
        origin = shoot.muzzle != null ? shoot.muzzle.position : shoot.transform.position;
        range = shoot.EffectiveRange > 1.0f ? shoot.EffectiveRange : 120.0f;
        direction = Vector3.zero;

        PlayerShootScript? shootScript = shoot.GetComponentInParent<PlayerShootScript>();
        Vector3 aimPoint = shootScript != null ? shootScript.cameraLookPoint : Vector3.zero;
        Vector3 raw = shoot.shootDirection;

        if (raw.sqrMagnitude > 0.0001f)
        {
            // On this build shootDirection is sometimes a normalised direction
            // and sometimes a world-space aim point. A far-away or crosshair
            // matching value is a point; anything else is already a direction.
            bool looksLikePoint = raw.sqrMagnitude > 4.0f ||
                                  (aimPoint.sqrMagnitude > 0.0001f &&
                                   Vector3.Distance(raw, aimPoint) < 1.5f);
            direction = looksLikePoint ? raw - origin : raw;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            Camera? camera = Camera.main;
            if (camera != null)
            {
                origin = camera.transform.position;
                direction = camera.transform.forward;
            }
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        direction.Normalize();
        return true;
    }

    private static bool IsLocalPlayerCollider(Collider collider)
    {
        if (_localPlayer == null || collider == null)
        {
            return false;
        }

        Transform colliderTransform = collider.transform;
        return colliderTransform == _localPlayer.transform ||
               colliderTransform.IsChildOf(_localPlayer.transform);
    }

    /// <summary>True for a collider that belongs to the remote avatar itself.</summary>
    private static bool IsAvatarCollider(Collider collider)
    {
        return collider != null && AvatarExtraColliders.Contains(collider.GetInstanceID());
    }

    private static void SpawnHitMarker(Vector3 point, bool headshot)
    {
        try
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "PeripeteiaCoop_HitMarker";

            Collider? collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            marker.transform.position = point;
            marker.transform.localScale = Vector3.one * (headshot ? 0.12f : 0.08f);

            Renderer? renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(0.75f, 0.04f, 0.04f, 1.0f);
            }

            UnityEngine.Object.Destroy(marker, 0.35f);
        }
        catch (Exception)
        {
            // The marker is cosmetic; never let it break the shot handling.
        }
    }
}

[HarmonyPatch(typeof(Shoot), "Fire", new Type[] { typeof(LayerMask), typeof(int) })]
internal static class CombatFireSimplePatch
{
    private static void Postfix(Shoot __instance)
    {
        CombatSync.OnShot(__instance);
    }
}

[HarmonyPatch(typeof(Shoot), "Fire", new Type[] { typeof(Vector3), typeof(LayerMask), typeof(int) })]
internal static class CombatFireAtPointPatch
{
    private static void Postfix(Shoot __instance)
    {
        CombatSync.OnShot(__instance);
    }
}

[HarmonyPatch(
    typeof(Shoot),
    "Fire",
    new Type[] { typeof(Vector3), typeof(LayerMask), typeof(int), typeof(bool) }
)]
internal static class CombatFireAtPointTriggerPatch
{
    private static void Postfix(Shoot __instance)
    {
        CombatSync.OnShot(__instance);
    }
}
