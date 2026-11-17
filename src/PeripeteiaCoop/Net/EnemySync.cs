using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Host-authoritative enemy / NPC replication.
///
/// In single-player every instance simulates its own AI, so the same guard can
/// be dead on one screen and alive on the other, standing in two different
/// places. This module makes the NPCs part of the shared world:
///
/// - the host simulates the AI and broadcasts transform, animation state and
///   health; clients mirror the host and freeze their own AI for those actors;
/// - enemy deaths are shared in both directions, so a kill by either player
///   counts for both sides (this also works when Ai.MirrorEnemies is off).
///
/// Items, doors, inventory, money and health of the players themselves are
/// deliberately NOT part of this: inventory and money are personal per player,
/// exactly like a survival co-op game.
///
/// Known limitation: the host's AI only knows about the host's own player, so
/// while client AI is frozen the enemies will not chase or attack the client.
/// Set Ai.MirrorEnemies = false to keep each side's AI running locally (then
/// only deaths stay shared).
/// </summary>
internal static class EnemySync
{
    private const float SendInterval = 0.15f;
    private const float MoveEpsilon = 0.04f;
    private const float FullResendInterval = 1.5f;

    private sealed class MirrorState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float SentAt;
    }

    /// <summary>
    /// Latest snapshot received from the host for one actor. The transform is
    /// not written on packet receipt; <see cref="LateTick"/> eases towards the
    /// snapshot every frame, which is what keeps a mirrored NPC moving smoothly
    /// instead of stepping once per packet.
    /// </summary>
    private sealed class MirrorTarget
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Snap;
    }

    private static readonly Dictionary<string, EnemyController> KnownEnemies = new();
    private static readonly Dictionary<string, int> EnemyIds = new();
    private static readonly Dictionary<string, MirrorState> LastMirror = new();
    private static readonly Dictionary<string, MirrorTarget> Targets = new();
    private static readonly HashSet<string> AnnouncedDead = new();
    private static readonly HashSet<int> FrozenIds = new();

    private static string _scene = string.Empty;
    private static float _nextPoll;
    private static bool _firstPoll = true;
    private static int _deathsSent;
    private static int _deathsApplied;
    private static int _mirrorsSent;

    public static bool Enabled =>
        CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.AiEnabled;

    public static int DeathsSent => _deathsSent;

    public static int DeathsApplied => _deathsApplied;

    public static int MirrorsSent => _mirrorsSent;

    /// <summary>
    /// True when this enemy is driven by the network on this client and its own
    /// AI update must be skipped. Called from a Harmony prefix, so it has to be
    /// cheap.
    /// </summary>
    public static bool IsFrozen(EnemyController? enemy)
    {
        if (enemy == null || FrozenIds.Count == 0)
        {
            return false;
        }

        if (CoopPlugin.Settings.Role != CoopRole.Client || !CoopPlugin.AiFreezeClientAi)
        {
            return false;
        }

        try
        {
            return FrozenIds.Contains(enemy.GetInstanceID());
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (!Enabled)
        {
            return;
        }

        string scene = SceneManager.GetActiveScene().name;
        if (!string.Equals(scene, _scene, StringComparison.Ordinal))
        {
            Reset(scene);
        }

        float now = Time.unscaledTime;
        if (now < _nextPoll)
        {
            return;
        }

        _nextPoll = now + SendInterval;
        Refresh();

        if (CoopPlugin.AiSyncDeaths)
        {
            PollDeaths();
        }

        if (CoopPlugin.AiMirrorEnemies && CoopPlugin.Settings.Role == CoopRole.Host)
        {
            PollMirror(now);
        }
    }

    /// <summary>Applies a host snapshot to a local enemy on a client.</summary>
    public static void ApplyState(
        string key,
        Vector3 position,
        Quaternion rotation,
        int hp,
        int maxHp,
        int stateHash,
        float normalizedTime,
        float speed
    )
    {
        if (CoopPlugin.Settings.Role != CoopRole.Client || !Enabled)
        {
            return;
        }

        EnemyController? enemy = Find(key);
        if (enemy == null)
        {
            Refresh();
            enemy = Find(key);
        }

        if (enemy == null || enemy.gameObject == null)
        {
            return;
        }

        Freeze(enemy);

        // Store the snapshot; LateTick eases the transform towards it so the
        // actor keeps moving between the host's packets.
        if (!Targets.TryGetValue(key, out MirrorTarget? target) || target == null)
        {
            target = new MirrorTarget { Snap = true };
            Targets[key] = target;
        }

        target.Position = position;
        target.Rotation = rotation;

        HealthScript? health = enemy.health;
        if (health != null && !health.dead && hp > 0 && hp <= Mathf.Max(maxHp, 1))
        {
            health.HP = hp;
        }

        Animator? animator = enemy.anim;
        if (animator != null && animator.runtimeAnimatorController != null && stateHash != 0)
        {
            try
            {
                animator.speed = speed > 0.01f ? speed : 1.0f;

                // Only restart the clip when the state actually changed. Calling
                // Play with the host's normalized time on every packet reset the
                // animation several times a second, which is why the mirrored
                // NPCs looked like they had no animation at all.
                AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
                bool sameState = current.fullPathHash == stateHash;
                bool drifted =
                    Mathf.Abs(
                        Mathf.Repeat(current.normalizedTime, 1.0f) -
                        Mathf.Repeat(normalizedTime, 1.0f)
                    ) > 0.25f;

                if (!sameState || drifted)
                {
                    animator.Play(stateHash, 0, normalizedTime);
                }
            }
            catch (Exception)
            {
                // Animator hash from a different controller revision; ignore.
            }
        }
    }

    /// <summary>
    /// Per-frame easing for mirrored actors. Runs in LateUpdate so the smoothed
    /// transform is what the frame renders.
    /// </summary>
    public static void LateTick()
    {
        if (CoopPlugin.Settings.Role != CoopRole.Client || !Enabled || Targets.Count == 0)
        {
            return;
        }

        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0.0f)
        {
            return;
        }

        // Exponential smoothing with roughly a 70 ms time constant: fast enough
        // to track the host, slow enough to hide the 0.15 s packet interval.
        float blend = 1.0f - Mathf.Exp(-14.0f * deltaTime);

        foreach (KeyValuePair<string, MirrorTarget> pair in Targets)
        {
            EnemyController? enemy = Find(pair.Key);
            if (enemy == null || enemy.gameObject == null)
            {
                continue;
            }

            MirrorTarget target = pair.Value;
            try
            {
                Transform transform = enemy.transform;
                if (target.Snap)
                {
                    // First snapshot for this actor: jump straight to it so a
                    // newly mirrored NPC does not slide across the level.
                    target.Snap = false;
                    transform.position = target.Position;
                    transform.rotation = target.Rotation;
                    continue;
                }

                transform.position = Vector3.Lerp(transform.position, target.Position, blend);
                transform.rotation = Quaternion.Slerp(transform.rotation, target.Rotation, blend);
            }
            catch (Exception)
            {
                // Transform writes are best-effort; never break the local frame.
            }
        }
    }

    /// <summary>Kills the local copy of an enemy that died on another machine.</summary>
    public static void ApplyDeath(string key)
    {
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            return;
        }

        EnemyController? enemy = Find(key);
        if (enemy == null)
        {
            Refresh();
            enemy = Find(key);
        }

        if (enemy == null || enemy.gameObject == null)
        {
            return;
        }

        HealthScript? health = enemy.health;
        if (health == null || health.dead)
        {
            return;
        }

        _deathsApplied++;
        try
        {
            health.ForceDeath();
            CoopPlugin.LogSource.LogInfo($"[ai] applied remote death key='{key}'.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[ai] ForceDeath failed for '{key}': {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void Reset(string scene)
    {
        _scene = scene;
        KnownEnemies.Clear();
        EnemyIds.Clear();
        LastMirror.Clear();
        Targets.Clear();
        AnnouncedDead.Clear();
        FrozenIds.Clear();
        _firstPoll = true;
        CoopPlugin.LogSource.LogInfo($"[ai] enemy sync tracking scene '{scene}'.");
    }

    private static void Refresh()
    {
        try
        {
            foreach (EnemyController enemy in UnityEngine.Object.FindObjectsOfType<EnemyController>())
            {
                if (enemy == null || enemy.gameObject == null)
                {
                    continue;
                }

                if (!string.Equals(enemy.gameObject.scene.name, _scene, StringComparison.Ordinal))
                {
                    continue;
                }

                string key = KeyOf(enemy);
                if (!KnownEnemies.ContainsKey(key))
                {
                    KnownEnemies[key] = enemy;
                    try
                    {
                        EnemyIds[key] = enemy.GetInstanceID();
                    }
                    catch (Exception)
                    {
                        EnemyIds.Remove(key);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Scene is mid-unload; try again next tick.
        }

        if (KnownEnemies.Count == 0)
        {
            return;
        }

        var destroyed = new List<string>();
        foreach (KeyValuePair<string, EnemyController> pair in KnownEnemies)
        {
            if (pair.Value == null)
            {
                destroyed.Add(pair.Key);
            }
        }

        foreach (string key in destroyed)
        {
            if (EnemyIds.TryGetValue(key, out int id))
            {
                FrozenIds.Remove(id);
            }

            KnownEnemies.Remove(key);
            EnemyIds.Remove(key);
            LastMirror.Remove(key);
            Targets.Remove(key);
        }
    }

    private static void PollDeaths()
    {
        bool initialising = _firstPoll;
        _firstPoll = false;

        foreach (KeyValuePair<string, EnemyController> pair in KnownEnemies)
        {
            EnemyController enemy = pair.Value;
            if (enemy == null)
            {
                continue;
            }

            HealthScript? health = enemy.health;
            if (health == null || !health.dead)
            {
                continue;
            }

            if (!AnnouncedDead.Add(pair.Key))
            {
                continue;
            }

            if (initialising)
            {
                // Both instances loaded the same save: an enemy that is already
                // dead at scene load is consistent and must not be re-announced.
                continue;
            }

            _deathsSent++;
            CoopPlugin.Network?.SendEnemyDeath(pair.Key);
        }
    }

    private static void PollMirror(float now)
    {
        foreach (KeyValuePair<string, EnemyController> pair in KnownEnemies)
        {
            EnemyController enemy = pair.Value;
            if (enemy == null || enemy.gameObject == null)
            {
                continue;
            }

            HealthScript? health = enemy.health;
            if (health != null && health.dead)
            {
                continue;
            }

            Vector3 position;
            Quaternion rotation;
            int stateHash = 0;
            float normalizedTime = 0.0f;
            float speed = 1.0f;

            try
            {
                position = enemy.transform.position;
                rotation = enemy.transform.rotation;
            }
            catch (Exception)
            {
                continue;
            }

            Animator? animator = enemy.anim;
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                try
                {
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                    stateHash = info.fullPathHash;
                    normalizedTime = info.normalizedTime;
                    speed = animator.speed;
                }
                catch (Exception)
                {
                    stateHash = 0;
                }
            }

            if (!LastMirror.TryGetValue(pair.Key, out MirrorState? last) || last == null)
            {
                last = new MirrorState();
                LastMirror[pair.Key] = last;
            }

            bool moved = Vector3.Distance(last.Position, position) > MoveEpsilon;
            bool stale = now - last.SentAt >= FullResendInterval;
            if (!moved && !stale)
            {
                continue;
            }

            last.Position = position;
            last.Rotation = rotation;
            last.SentAt = now;
            _mirrorsSent++;

            CoopPlugin.Network?.SendEnemyState(
                pair.Key,
                position,
                rotation,
                health != null ? health.HP : 0,
                health != null ? health.maxHP : 0,
                stateHash,
                normalizedTime,
                speed
            );
        }
    }

    private static void Freeze(EnemyController enemy)
    {
        if (!CoopPlugin.AiFreezeClientAi)
        {
            return;
        }

        int id;
        try
        {
            id = enemy.GetInstanceID();
        }
        catch (Exception)
        {
            return;
        }

        if (!FrozenIds.Add(id))
        {
            return;
        }

        try
        {
            NavMeshAgent agent = enemy.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.enabled = false;
            }
        }
        catch (Exception)
        {
            // Movement component is optional; the Update prefix already stops AI.
        }
    }

    private static EnemyController? Find(string key)
    {
        return KnownEnemies.TryGetValue(key, out EnemyController? enemy) ? enemy : null;
    }

    private static string KeyOf(EnemyController enemy)
    {
        Transform transform = enemy.transform;
        var builder = new StringBuilder();
        AppendNode(builder, transform);

        Transform parent = transform.parent;
        while (parent != null)
        {
            builder.Insert(0, '/');
            AppendNode(builder, parent);
            parent = parent.parent;
        }

        return _scene + "|" + builder;
    }

    private static void AppendNode(StringBuilder builder, Transform node)
    {
        builder.Append(node.name);
        builder.Append('[');
        builder.Append(node.GetSiblingIndex());
        builder.Append(']');
    }
}

[HarmonyPatch(typeof(EnemyController), "Update")]
internal static class EnemyControllerUpdatePatch
{
    private static bool Prefix(EnemyController __instance)
    {
        return !EnemySync.IsFrozen(__instance);
    }
}

[HarmonyPatch(typeof(dynamicenemymover), "LateUpdate")]
internal static class EnemyMoverLateUpdatePatch
{
    private static bool Prefix(dynamicenemymover __instance)
    {
        return !EnemySync.IsFrozen(__instance != null ? __instance.lol : null);
    }
}
