using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Replicates the locally equipped weapon and the short actions that the
/// first-person weapon scripts drive (fire, reload, swing/stab).
///
/// The body puppet is cloned from the local player, but its weapon would also
/// be the local player's weapon; that is wrong when the two players carry
/// different guns. This class captures a small weapon descriptor plus the
/// weapon's local transform, and the remote avatar builds a fresh visual clone
/// from a matching ItemInfo in its own scene.
/// </summary>
internal static class WeaponSync
{
    private const float SendInterval = 1.0f / 20.0f;

    private static readonly Dictionary<string, ItemInfo> TemplateCache = new();
    private static readonly List<Renderer> RendererBuffer = new();
    private static readonly StringBuilder PathBuilder = new(160);

    private static PlayerShootScript? _shoot;
    private static float _nextSendTime;
    private static float _nextHiddenSendTime;
    private static float _nextProbeTime;
    private static float _nextShotNotify;
    private static bool _lastVisible;
    private static string _lastDescriptor = string.Empty;

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline ||
            localPlayer == null ||
            CoopPlugin.Network == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextSendTime)
        {
            return;
        }

        _nextSendTime = Time.unscaledTime + SendInterval;

        if (!TryCapture(localPlayer, out WeaponSnapshot snapshot))
        {
            // Send the "unarmed" state periodically. A single unreliable packet
            // could be lost and leave a ghost weapon in the remote player's
            // hands forever.
            if (!_lastVisible && Time.unscaledTime < _nextHiddenSendTime)
            {
                return;
            }

            _lastVisible = false;
            _lastDescriptor = string.Empty;
            _nextHiddenSendTime = Time.unscaledTime + 1.0f;
            CoopPlugin.Network.SendWeaponState(snapshot);
            return;
        }

        _lastVisible = true;
        string descriptor = snapshot.Descriptor;
        if (!string.Equals(descriptor, _lastDescriptor, StringComparison.Ordinal))
        {
            _lastDescriptor = descriptor;
            CoopPlugin.LogSource.LogInfo(
                $"[weapon] local item='{snapshot.ItemName}' template='{snapshot.TemplateName}' " +
                $"parent='{snapshot.ParentPath}' hash={snapshot.VisualHash} path='{snapshot.WeaponPath}'."
            );
        }

        CoopPlugin.Network.SendWeaponState(snapshot);

        if (CoopPlugin.ProbeEnabled && Time.unscaledTime >= _nextProbeTime)
        {
            _nextProbeTime = Time.unscaledTime + 1.0f;
            CoopPlugin.LogSource.LogInfo(
                $"[weapon] probe shoot='{snapshot.ItemName}' visible={snapshot.Visible} " +
                $"parent='{snapshot.ParentPath}' muzzle='{snapshot.MuzzlePath}' " +
                $"anim={snapshot.AnimatorActive} hash={snapshot.AnimatorHash} " +
                $"renderers={(snapshot.WeaponTransform != null ? CountRenderers(snapshot.WeaponTransform) : 0)}."
            );
        }
    }

    /// <summary>
    /// Returns the currently playing body action (reload, swing, etc.) so the
    /// remote puppet can play the same Animator state instead of only
    /// reproducing locomotion parameters.
    /// </summary>
    public static bool TryGetBodyAction(
        PlayerMovement localPlayer,
        out int stateHash,
        out float normalizedTime
    )
    {
        stateHash = 0;
        normalizedTime = 0.0f;

        PlayerShootScript? shoot = GetShoot(localPlayer);
        if (shoot == null)
        {
            return false;
        }

        Animator? animator = FindBodyAnimator(localPlayer);
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return false;
        }

        string clipName = GetCurrentClipName(animator);
        bool action = shoot.reloading ||
                      shoot.isSwinging ||
                      shoot.stab ||
                      IsActionClip(clipName);

        if (!action)
        {
            return false;
        }

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        stateHash = state.fullPathHash;
        normalizedTime = Mathf.Repeat(state.normalizedTime, 1.0f);
        return stateHash != 0;
    }

    public static void NotifyShot(Shoot shoot)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline ||
            shoot == null ||
            CoopPlugin.Network == null)
        {
            return;
        }

        // Only the player's own weapon is interesting. NPC weapons use the
        // same Shoot component, and firing one must not make the remote player
        // flash a muzzle.
        if (shoot.GetComponentInParent<PlayerMovement>() == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextShotNotify)
        {
            return;
        }

        _nextShotNotify = Time.unscaledTime + 0.015f;
        CoopPlugin.Network.SendWeaponEvent((byte)WeaponEvent.Shot);
    }

    private static PlayerShootScript? GetShoot(PlayerMovement localPlayer)
    {
        if (_shoot != null && _shoot.gameObject != null)
        {
            return _shoot;
        }

        _shoot = localPlayer.GetComponentInChildren<PlayerShootScript>();
        if (_shoot == null)
        {
            _shoot = localPlayer.GetComponentInParent<PlayerShootScript>();
        }

        if (_shoot == null)
        {
            // On some setups the shooting controller lives on the camera rig,
            // next to the player rather than below it. There is only one local
            // shooting controller in a normal single-player/co-op instance.
            foreach (PlayerShootScript candidate in Resources.FindObjectsOfTypeAll<PlayerShootScript>())
            {
                if (candidate == null ||
                    candidate.gameObject == null ||
                    !candidate.gameObject.activeInHierarchy ||
                    candidate.gameObject.scene != localPlayer.gameObject.scene)
                {
                    continue;
                }

                _shoot = candidate;
                break;
            }
        }

        return _shoot;
    }

    private static Animator? FindBodyAnimator(PlayerMovement localPlayer)
    {
        try
        {
            Transform? upper = localPlayer.upperBody;
            if (upper != null)
            {
                Animator? upperAnimator = upper.GetComponent<Animator>();
                if (upperAnimator != null && upperAnimator.avatar != null)
                {
                    return upperAnimator;
                }

                Transform? upperMesh = upper.Find("UpperMesh");
                if (upperMesh != null)
                {
                    Animator? meshAnimator = upperMesh.GetComponent<Animator>();
                    if (meshAnimator != null && meshAnimator.avatar != null)
                    {
                        return meshAnimator;
                    }
                }
            }
        }
        catch
        {
            // Some player variants do not expose the same upper-body hierarchy.
        }

        foreach (Animator animator in localPlayer.GetComponentsInChildren<Animator>(true))
        {
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                return animator;
            }
        }

        return null;
    }

    private static string GetCurrentClipName(Animator animator)
    {
        try
        {
            AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
            if (clips.Length > 0 && clips[0].clip != null)
            {
                return clips[0].clip.name;
            }
        }
        catch
        {
            // The Animator wrapper may be unavailable during a scene transition.
        }

        return string.Empty;
    }

    private static bool IsActionClip(string clipName)
    {
        if (string.IsNullOrEmpty(clipName))
        {
            return false;
        }

        string lower = clipName.ToLowerInvariant();
        return lower.Contains("reload") ||
               lower.Contains("shoot") ||
               lower.Contains("fire") ||
               lower.Contains("swing") ||
               lower.Contains("stab") ||
               lower.Contains("melee") ||
               lower.Contains("attack") ||
               lower.Contains("punch") ||
               lower.Contains("equip") ||
               lower.Contains("holster") ||
               lower.Contains("throw") ||
               lower.Contains("inspect");
    }

    private static bool TryCapture(PlayerMovement localPlayer, out WeaponSnapshot snapshot)
    {
        snapshot = default;

        PlayerShootScript? shoot = GetShoot(localPlayer);
        Transform? weapon = ResolveWeaponRoot(shoot != null ? shoot.currentlyHeld : null);
        if (weapon == null)
        {
            weapon = FindFallbackWeapon(localPlayer);
        }

        if (weapon == null ||
            !weapon.gameObject.activeInHierarchy ||
            CountRenderers(weapon) == 0)
        {
            return false;
        }

        ItemInfo? item = weapon.GetComponentInParent<ItemInfo>();
        if (item == null)
        {
            item = weapon.GetComponent<ItemInfo>();
        }

        if (item == null && shoot != null && shoot.AttachedGun != null)
        {
            item = shoot.AttachedGun.itemInfo;
        }

        string itemName = item != null ? item.ItemName : string.Empty;
        string templateName = item != null ? item.gameObject.name : weapon.gameObject.name;
        string parentPath = BuildRelativePath(weapon.parent, localPlayer.transform);
        string weaponPath = BuildRelativePath(weapon, localPlayer.transform);
        string muzzlePath = string.Empty;

        if (shoot != null && shoot.AttachedGun != null && shoot.AttachedGun.muzzle != null)
        {
            muzzlePath = BuildRelativePath(shoot.AttachedGun.muzzle, weapon);
        }

        Animator? weaponAnimator = FindWeaponAnimator(weapon);
        int animatorHash = 0;
        float animatorTime = 0.0f;
        bool animatorActive = false;
        if (weaponAnimator != null)
        {
            try
            {
                AnimatorStateInfo state = weaponAnimator.GetCurrentAnimatorStateInfo(0);
                animatorHash = state.fullPathHash;
                animatorTime = Mathf.Repeat(state.normalizedTime, 1.0f);
                animatorActive = animatorHash != 0;
            }
            catch
            {
                animatorActive = false;
            }
        }

        snapshot = new WeaponSnapshot(
            visible: true,
            itemName,
            templateName,
            parentPath,
            weaponPath,
            muzzlePath,
            ComputeVisualHash(weapon),
            weapon.localPosition,
            weapon.localRotation,
            weapon.localScale,
            Quaternion.Inverse(localPlayer.transform.rotation) * weapon.rotation,
            animatorHash,
            animatorTime,
            animatorActive,
            weapon
        );

        return true;
    }

    private static Transform? FindFallbackWeapon(PlayerMovement localPlayer)
    {
        Transform? location = localPlayer.WeaponLocation;
        if (location == null)
        {
            return null;
        }

        // WeaponLocation is a container. It can contain hand IK helpers and
        // first-person arm renderers, so having renderers alone does not mean
        // that it is a weapon. Require an ItemInfo or a Shoot component.
        Transform? exact = ResolveWeaponRoot(location);
        if (exact != null && exact != location)
        {
            return exact;
        }

        for (int i = 0; i < location.childCount; i++)
        {
            Transform child = location.GetChild(i);
            Transform? resolved = ResolveWeaponRoot(child);
            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static Transform? ResolveWeaponRoot(Transform? candidate)
    {
        if (candidate == null)
        {
            return null;
        }

        if (candidate.GetComponent<ItemInfo>() != null ||
            candidate.GetComponent<Shoot>() != null)
        {
            return candidate;
        }

        for (int i = 0; i < candidate.childCount; i++)
        {
            Transform? resolved = ResolveWeaponRoot(candidate.GetChild(i));
            if (resolved != null && CountRenderers(resolved) > 0)
            {
                return resolved;
            }
        }

        return null;
    }

    private static Animator? FindWeaponAnimator(Transform weapon)
    {
        foreach (Animator animator in weapon.GetComponentsInChildren<Animator>(true))
        {
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                return animator;
            }
        }

        return null;
    }

    private static int CountRenderers(Transform transform)
    {
        RendererBuffer.Clear();
        if (transform == null)
        {
            return 0;
        }

        RendererBuffer.AddRange(transform.GetComponentsInChildren<Renderer>(true));
        return RendererBuffer.Count;
    }

    private static int ComputeVisualHash(Transform weapon)
    {
        int hash = 17;
        foreach (Renderer renderer in weapon.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
            {
                continue;
            }

            string path = BuildRelativePath(renderer.transform, weapon);
            hash = unchecked(hash * 31 + path.GetHashCode());
            hash = unchecked(hash * 31 + (renderer.enabled ? 1 : 0));
            hash = unchecked(hash * 31 + (renderer.gameObject.activeSelf ? 1 : 0));
        }

        return hash;
    }

    private static string BuildRelativePath(Transform? target, Transform root)
    {
        if (target == null || root == null)
        {
            return string.Empty;
        }

        PathBuilder.Clear();
        Transform? current = target;
        while (current != null && current != root)
        {
            if (PathBuilder.Length > 0)
            {
                PathBuilder.Insert(0, '/');
            }

            PathBuilder.Insert(0, current.name);
            current = current.parent;
        }

        return PathBuilder.ToString();
    }

    /// <summary>
    /// Builds a stripped-down visual clone of an item for a remote weapon.
    /// The remote peer has the same game assets, so the weapon is found by its
    /// ItemName/template name inside the local scene instead of sending meshes
    /// over the network.
    /// </summary>
    public static GameObject? CreateWeaponVisual(string itemName, string templateName, int visualHash)
    {
        ItemInfo? template = FindTemplate(itemName, templateName, visualHash);
        if (template == null || template.gameObject == null)
        {
            return null;
        }

        try
        {
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject);
            clone.name = $"PeripeteiaCoop_Weapon_{templateName}";
            clone.SetActive(false);

            foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true))
            {
                if (animator != null)
                {
                    animator.enabled = false;
                    animator.applyRootMotion = false;
                }
            }

            foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null)
                {
                    UnityEngine.Object.DestroyImmediate(behaviour, true);
                }
            }

            foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null)
                {
                    UnityEngine.Object.DestroyImmediate(collider, true);
                }
            }

            foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body != null)
                {
                    UnityEngine.Object.DestroyImmediate(body, true);
                }
            }

            foreach (AudioSource audioSource in clone.GetComponentsInChildren<AudioSource>(true))
            {
                if (audioSource != null)
                {
                    UnityEngine.Object.DestroyImmediate(audioSource, true);
                }
            }

            foreach (ParticleSystem particle in clone.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particle != null)
                {
                    particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    particle.gameObject.SetActive(false);
                }
            }

            clone.SetActive(true);
            return clone;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[weapon] failed to build '{itemName}' visual: {exception.GetType().Name}: {exception.Message}"
            );
            return null;
        }
    }

    private static ItemInfo? FindTemplate(string itemName, string templateName, int visualHash)
    {
        string cacheKey = $"{itemName}|{templateName}|{visualHash}";
        if (TemplateCache.TryGetValue(cacheKey, out ItemInfo? cached) && cached != null)
        {
            return cached;
        }

        ItemInfo? best = null;
        int bestScore = int.MinValue;
        string wantedTemplate = StripCloneSuffix(templateName);

        foreach (ItemInfo item in Resources.FindObjectsOfTypeAll<ItemInfo>())
        {
            if (item == null || item.gameObject == null)
            {
                continue;
            }

            string objectName = StripCloneSuffix(item.gameObject.name);
            bool nameMatches = !string.IsNullOrEmpty(itemName) &&
                               string.Equals(item.ItemName, itemName, StringComparison.Ordinal);
            bool templateMatches = !string.IsNullOrEmpty(wantedTemplate) &&
                                   string.Equals(objectName, wantedTemplate, StringComparison.Ordinal);
            if (!nameMatches && !templateMatches)
            {
                continue;
            }

            // NPC weapons are live gameplay objects, never templates. The
            // local player's matching weapon is allowed: when both players
            // carry the same gun there may be no separate world copy to clone.
            if (item.GetComponentInParent<AIController>() != null ||
                item.GetComponentInParent<EnemyInventory>() != null)
            {
                continue;
            }

            int score = 0;
            if (nameMatches)
            {
                score += 20;
            }

            if (templateMatches)
            {
                score += 20;
            }

            if (item.gameObject.activeInHierarchy)
            {
                score += 10;
            }

            if (item.transform.root != item.transform)
            {
                score += 1;
            }

            // Prefer a world item whose visual shape matches the sender's
            // equipped weapon (attachments change the renderer set).
            if (ComputeVisualHash(item.transform) == visualHash)
            {
                score += 100;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        if (best != null)
        {
            TemplateCache[cacheKey] = best;
        }

        return best;
    }

    private static string StripCloneSuffix(string name)
    {
        const string suffix = "(Clone)";
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return name.Substring(0, name.Length - suffix.Length).Trim();
        }

        return name;
    }
}

internal enum WeaponEvent : byte
{
    Shot = 1,
}

internal readonly struct WeaponSnapshot
{
    public WeaponSnapshot(
        bool visible,
        string itemName,
        string templateName,
        string parentPath,
        string weaponPath,
        string muzzlePath,
        int visualHash,
        Vector3 localPosition,
        Quaternion localRotation,
        Vector3 localScale,
        Quaternion rootRotation,
        int animatorHash,
        float animatorTime,
        bool animatorActive,
        Transform? weaponTransform
    )
    {
        Visible = visible;
        ItemName = itemName;
        TemplateName = templateName;
        ParentPath = parentPath;
        WeaponPath = weaponPath;
        MuzzlePath = muzzlePath;
        VisualHash = visualHash;
        LocalPosition = localPosition;
        LocalRotation = localRotation;
        LocalScale = localScale;
        RootRotation = rootRotation;
        AnimatorHash = animatorHash;
        AnimatorTime = animatorTime;
        AnimatorActive = animatorActive;
        WeaponTransform = weaponTransform;
    }

    public bool Visible { get; }

    public string ItemName { get; }

    public string TemplateName { get; }

    public string ParentPath { get; }

    public string WeaponPath { get; }

    public string MuzzlePath { get; }

    public int VisualHash { get; }

    public Vector3 LocalPosition { get; }

    public Quaternion LocalRotation { get; }

    public Vector3 LocalScale { get; }

    /// <summary>
    /// Weapon rotation relative to the player root. The local weapon lives on a
    /// first-person helper that is not animated on the cloned third-person
    /// body, so the remote side uses this world-aim rotation instead.
    /// </summary>
    public Quaternion RootRotation { get; }

    public int AnimatorHash { get; }

    public float AnimatorTime { get; }

    public bool AnimatorActive { get; }

    public Transform? WeaponTransform { get; }

    public string Descriptor =>
        $"{ItemName}|{TemplateName}|{VisualHash}|{ParentPath}|{MuzzlePath}";
}

[HarmonyPatch(typeof(Shoot), "Fire", new Type[] { typeof(LayerMask), typeof(int) })]
internal static class WeaponFireSimplePatch
{
    private static void Postfix(Shoot __instance)
    {
        WeaponSync.NotifyShot(__instance);
    }
}

[HarmonyPatch(
    typeof(Shoot),
    "Fire",
    new Type[] { typeof(Vector3), typeof(LayerMask), typeof(int) }
)]
internal static class WeaponFireAtPointPatch
{
    private static void Postfix(Shoot __instance)
    {
        WeaponSync.NotifyShot(__instance);
    }
}

[HarmonyPatch(
    typeof(Shoot),
    "Fire",
    new Type[] { typeof(Vector3), typeof(LayerMask), typeof(int), typeof(bool) }
)]
internal static class WeaponFireAtPointTriggerPatch
{
    private static void Postfix(Shoot __instance)
    {
        WeaponSync.NotifyShot(__instance);
    }
}
