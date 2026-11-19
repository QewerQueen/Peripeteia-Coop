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

    /// <summary>
    /// Hard cap on the number of Animator parameters mirrored per actor. The
    /// receive buffers on the other side are sized larger, so an actor with an
    /// unusual controller can never overflow them.
    /// </summary>
    public const int MaxAnimatorParameters = 24;

    /// <summary>
    /// Mirrored NPCs within this distance of the local player are forced to keep
    /// animating on the host, so the snapshot the client mirrors is live.
    /// </summary>
    private const float HostAnimationRadius = 60.0f;

    private sealed class MirrorState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float SentAt;
        public int StateHash;
        public int AnimSignature;
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

        /// <summary>
        /// When the parameter-driven Animator state machine first failed to reach
        /// the host's state. Used as a safety net so a trigger-based transition
        /// cannot leave the puppet frozen.
        /// </summary>
        public float StateMismatchSince;
    }

    private static readonly Dictionary<string, EnemyController> KnownEnemies = new();
    private static readonly Dictionary<string, int> EnemyIds = new();
    private static readonly Dictionary<string, MirrorState> LastMirror = new();
    private static readonly Dictionary<string, MirrorTarget> Targets = new();
    private static readonly HashSet<string> AnnouncedDead = new();
    private static readonly HashSet<int> FrozenIds = new();
    private static readonly HashSet<int> HostAnimatedIds = new();

    private static readonly Dictionary<int, AnimParamSet> ParamCache = new();

    private static bool _animParamsLogged;

    private static Transform? _localPlayer;

    private static string _scene = string.Empty;
    private static float _nextPoll;
    private static bool _firstPoll = true;
    private static int _deathsSent;
    private static int _deathsApplied;
    private static int _mirrorsSent;

    // Reusable buffers for sampling the host's Animator parameters. Filled in
    // PollMirror, read by SendEnemyState, never allocated per packet.
    private static readonly int[] AnimHashes = new int[MaxAnimatorParameters];
    private static readonly byte[] AnimTypes = new byte[MaxAnimatorParameters];
    private static readonly float[] AnimValues = new float[MaxAnimatorParameters];

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
            if (!FrozenIds.Contains(enemy.GetInstanceID()))
            {
                return false;
            }

            // A dead NPC is handed straight back to the game. The corpse ragdoll,
            // the lootable body and the death cleanup are all driven by the game's
            // own Update / LateUpdate; keeping them frozen left the client with no
            // visible corpse (the ragdoll existed but nothing positioned it).
            HealthScript? health = enemy.health;
            return health == null || !health.dead;
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

        _localPlayer = localPlayer != null ? localPlayer.transform : null;

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
        float speed,
        int[] animHashes,
        byte[] animTypes,
        float[] animValues,
        int animCount
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

        // A snapshot already in flight must not re-attach a mirror target to a
        // corpse: LateTick would ease the ragdoll root back to its last standing
        // position and undo the collapse.
        HealthScript? health = enemy.health;
        if (health != null && health.dead)
        {
            Targets.Remove(key);
            LastMirror.Remove(key);
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

        if (health != null && !health.dead && hp > 0 && hp <= Mathf.Max(maxHp, 1))
        {
            health.HP = hp;
        }

        Animator? animator = enemy.anim;
        if (animator != null && animator.runtimeAnimatorController != null)
        {
            try
            {
                animator.speed = speed > 0.01f ? speed : 1.0f;
                if (!animator.enabled)
                {
                    animator.enabled = true;
                }

                // The client's own AI is frozen, so nothing drives its Animator
                // parameters any more. Without this the state machine sat in
                // whatever state it was in when the AI stopped, which is why the
                // mirrored NPCs looked like unanimated statues. Copying the
                // host's parameters lets the client's own state machine play the
                // same locomotion/attack clips.
                bool paramsApplied = animCount > 0;
                if (paramsApplied)
                {
                    ApplyAnimatorParameters(animator, animHashes, animTypes, animValues, animCount);
                }

                if (stateHash != 0)
                {
                    AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
                    bool sameState = current.fullPathHash == stateHash;
                    bool drifted =
                        Mathf.Abs(
                            Mathf.Repeat(current.normalizedTime, 1.0f) -
                            Mathf.Repeat(normalizedTime, 1.0f)
                        ) > 0.25f;

                    if (!paramsApplied)
                    {
                        // No parameters to drive the machine: fall back to
                        // playing the host's clip directly. Only restart when the
                        // state really changed; calling Play every packet reset
                        // the animation several times a second.
                        if (!sameState || drifted)
                        {
                            animator.Play(stateHash, 0, normalizedTime);
                        }
                    }
                    else if (!sameState)
                    {
                        // The parameters should have moved the machine to the
                        // host's state. If a trigger-based transition keeps it
                        // elsewhere, force the clip after a short grace period.
                        if (target.StateMismatchSince <= 0.0f)
                        {
                            target.StateMismatchSince = Time.unscaledTime;
                        }
                        else if (Time.unscaledTime - target.StateMismatchSince > 0.6f)
                        {
                            animator.Play(stateHash, 0, normalizedTime);
                            target.StateMismatchSince = 0.0f;
                        }
                    }
                    else
                    {
                        target.StateMismatchSince = 0.0f;
                    }
                }
            }
            catch (Exception)
            {
                // Animator hash from a different controller revision; ignore.
            }
        }
    }

    private static void ApplyAnimatorParameters(
        Animator animator,
        int[] hashes,
        byte[] types,
        float[] values,
        int count
    )
    {
        for (int i = 0; i < count; i++)
        {
            switch ((AnimatorControllerParameterType)types[i])
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(hashes[i], values[i]);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(hashes[i], (int)values[i]);
                    break;
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(hashes[i], values[i] > 0.5f);
                    break;
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
            CoopPlugin.LogSource.LogWarning(
                $"[ai] remote death for unknown key '{key}' (tracked={KnownEnemies.Count}, " +
                $"scene='{_scene}'). The corpse will not appear on this machine."
            );
            return;
        }

        HealthScript? health = enemy.health;
        if (health == null)
        {
            CoopPlugin.LogSource.LogWarning($"[ai] remote death for '{key}' has no HealthScript.");
            return;
        }

        // A client-side mirror can already be dead from forwarded damage by the
        // time the authoritative death arrives. Record the key even then, or the
        // next PollDeaths announces the same kill back to the host.
        AnnouncedDead.Add(key);

        if (health.dead)
        {
            Targets.Remove(key);
            LastMirror.Remove(key);
            return;
        }

        _deathsApplied++;
        try
        {
            health.ForceDeath();

            // Stop easing this actor's transform from the last snapshot: once the
            // body is a ragdoll the network must leave it alone, otherwise the
            // per-frame interpolation drags the corpse back to where the host
            // last saw it standing and it never collapses.
            Targets.Remove(key);
            LastMirror.Remove(key);

            CoopPlugin.LogSource.LogInfo(
                $"[ai] applied remote death key='{key}' (dead={health.dead}, ragdoll={RagdollState(enemy)})."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[ai] ForceDeath failed for '{key}': {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Short description of an enemy's ragdoll state, for the death diagnostics.
    /// A corpse that never appears is almost always a ragdoll that never switched
    /// on, so this tells the two cases apart in one log line.
    /// </summary>
    private static string RagdollState(EnemyController enemy)
    {
        try
        {
            HitBoxes? boxes = enemy.hitBoxes;
            if (boxes == null)
            {
                return "no HitBoxes";
            }

            int bodies = 0;
            foreach (Rigidbody body in enemy.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body != null && !body.isKinematic)
                {
                    bodies++;
                }
            }

            return $"HitBoxes.isRagdoll={boxes.isRagdoll}, simulated={bodies}";
        }
        catch (Exception exception)
        {
            return $"unreadable ({exception.GetType().Name})";
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
        HostAnimatedIds.Clear();
        ParamCache.Clear();
        _animParamsLogged = false;
        _firstPoll = true;
        CoopPlugin.LogSource.LogInfo($"[ai] enemy sync tracking scene '{scene}'.");
    }

    /// <summary>
    /// Drops the mirrored enemy state after a save is loaded. A same-scene
    /// reload keeps the scene name, so the normal scene check would not notice
    /// it and the NPCs would keep the dead / aggressive state from before the
    /// rollback (the "everything is still hostile after I load" bug). Clearing
    /// here makes the mirror re-read the freshly loaded world instead.
    /// </summary>
    public static void ResetForLoad()
    {
        try
        {
            Reset(SceneManager.GetActiveScene().name);
        }
        catch (Exception)
        {
            // Best effort; the next Tick re-reads the scene anyway.
        }
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

        // Sibling indices in a key can change when a corpse is reparented or a
        // save is reloaded. Migrate the cached state instead of leaving the old
        // key pointing at an enemy the next death packet can no longer find.
        var migrated = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, EnemyController> pair in KnownEnemies)
        {
            if (pair.Value == null)
            {
                continue;
            }

            string current = KeyOf(pair.Value);
            if (!string.Equals(current, pair.Key, StringComparison.Ordinal) &&
                !KnownEnemies.ContainsKey(current))
            {
                migrated.Add(new KeyValuePair<string, string>(pair.Key, current));
            }
        }

        foreach (KeyValuePair<string, string> move in migrated)
        {
            EnemyController? enemy = null;
            if (KnownEnemies.TryGetValue(move.Key, out enemy))
            {
                KnownEnemies.Remove(move.Key);
                KnownEnemies[move.Value] = enemy;
            }

            if (EnemyIds.TryGetValue(move.Key, out int id))
            {
                EnemyIds.Remove(move.Key);
                EnemyIds[move.Value] = id;
            }

            if (LastMirror.TryGetValue(move.Key, out MirrorState? mirror))
            {
                LastMirror.Remove(move.Key);
                LastMirror[move.Value] = mirror;
            }

            if (Targets.TryGetValue(move.Key, out MirrorTarget? target))
            {
                Targets.Remove(move.Key);
                Targets[move.Value] = target;
            }

            if (AnnouncedDead.Remove(move.Key))
            {
                AnnouncedDead.Add(move.Value);
            }
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
                HostAnimatedIds.Remove(id);
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

            // The body may be a ragdoll now; drop the mirror target so LateTick
            // stops easing its transform back onto the last live snapshot.
            Targets.Remove(pair.Key);
            LastMirror.Remove(pair.Key);

            _deathsSent++;
            CoopPlugin.LogSource.LogInfo(
                $"[ai] sharing death of '{pair.Key}' (ragdoll={RagdollState(enemy)})."
            );
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

            EnsureHostAnimation(enemy);

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
            int animCount = 0;
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

                animCount = SampleAnimatorParameters(animator);
                LogAnimatorParametersOnce(animator, enemy);
            }

            int animSignature = AnimSignature(animCount);

            if (!LastMirror.TryGetValue(pair.Key, out MirrorState? last) || last == null)
            {
                last = new MirrorState();
                LastMirror[pair.Key] = last;
            }

            bool moved = Vector3.Distance(last.Position, position) > MoveEpsilon;
            bool animChanged =
                stateHash != last.StateHash || animSignature != last.AnimSignature;
            bool stale = now - last.SentAt >= FullResendInterval;
            if (!moved && !animChanged && !stale)
            {
                continue;
            }

            last.Position = position;
            last.Rotation = rotation;
            last.SentAt = now;
            last.StateHash = stateHash;
            last.AnimSignature = animSignature;
            _mirrorsSent++;

            CoopPlugin.Network?.SendEnemyState(
                pair.Key,
                position,
                rotation,
                health != null ? health.HP : 0,
                health != null ? health.maxHP : 0,
                stateHash,
                normalizedTime,
                speed,
                AnimHashes,
                AnimTypes,
                AnimValues,
                animCount
            );
        }
    }

    /// <summary>
    /// Samples every non-trigger Animator parameter of an actor into the shared
    /// buffers and returns how many were written.
    /// </summary>
    private static int SampleAnimatorParameters(Animator animator)
    {
        int count = 0;
        try
        {
            AnimParamSet? set = GetParamSet(animator);
            if (set == null)
            {
                return 0;
            }

            for (int i = 0; i < set.Hashes.Length; i++)
            {
                AnimatorControllerParameterType type =
                    (AnimatorControllerParameterType)set.Types[i];

                float value;
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        value = animator.GetFloat(set.Hashes[i]);
                        break;
                    case AnimatorControllerParameterType.Int:
                        value = animator.GetInteger(set.Hashes[i]);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        value = animator.GetBool(set.Hashes[i]) ? 1.0f : 0.0f;
                        break;
                    default:
                        continue;
                }

                AnimHashes[count] = set.Hashes[i];
                AnimTypes[count] = set.Types[i];
                AnimValues[count] = value;
                count++;
            }
        }
        catch (Exception)
        {
            count = 0;
        }

        return count;
    }

    /// <summary>
    /// The parameters of a controller, worked out once per controller.
    /// <c>Animator.parameters</c> allocates a fresh array (and a wrapper per
    /// parameter) on every call, and this runs for every mirrored actor several
    /// times a second, so the descriptor list is cached by controller instance.
    /// </summary>
    private static AnimParamSet? GetParamSet(Animator animator)
    {
        RuntimeAnimatorController? controller = animator.runtimeAnimatorController;
        if (controller == null)
        {
            return null;
        }

        int id = controller.GetInstanceID();
        if (ParamCache.TryGetValue(id, out AnimParamSet? cached) && cached != null)
        {
            return cached;
        }

        var hashes = new List<int>(MaxAnimatorParameters);
        var types = new List<byte>(MaxAnimatorParameters);
        var names = new List<string>(MaxAnimatorParameters);

        try
        {
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                AnimatorControllerParameterType type = parameter.type;
                if (type == AnimatorControllerParameterType.Trigger)
                {
                    // A trigger is a one-shot event, not a readable value; it
                    // cannot be replicated by polling.
                    continue;
                }

                if (hashes.Count >= MaxAnimatorParameters)
                {
                    break;
                }

                hashes.Add(parameter.nameHash);
                types.Add((byte)type);
                names.Add(parameter.name ?? string.Empty);
            }
        }
        catch (Exception)
        {
            return null;
        }

        var set = new AnimParamSet
        {
            Hashes = hashes.ToArray(),
            Types = types.ToArray(),
            Names = names.ToArray(),
        };

        ParamCache[id] = set;
        return set;
    }

    private sealed class AnimParamSet
    {
        public int[] Hashes = Array.Empty<int>();
        public byte[] Types = Array.Empty<byte>();
        public string[] Names = Array.Empty<string>();
    }

    /// <summary>
    /// Logs the Animator parameters of the first mirrored actor once per scene.
    /// Without it a "the NPCs still do not move" report cannot be told apart from
    /// a controller that simply has no parameters to sync.
    /// </summary>
    private static void LogAnimatorParametersOnce(Animator animator, EnemyController enemy)
    {
        if (_animParamsLogged)
        {
            return;
        }

        _animParamsLogged = true;

        try
        {
            AnimParamSet? set = GetParamSet(animator);
            string text = "none";
            if (set != null && set.Names.Length > 0)
            {
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < set.Names.Length; i++)
                {
                    if (names.Length > 0)
                    {
                        names.Append(", ");
                    }

                    names.Append(set.Names[i]);
                    names.Append('(');
                    names.Append((AnimatorControllerParameterType)set.Types[i]);
                    names.Append(')');
                }

                text = names.ToString();
            }

            CoopPlugin.LogSource.LogInfo(
                $"[sync] animator parameters for '{enemy.gameObject.name}': {text}"
            );
        }
        catch (Exception)
        {
            // Diagnostic only.
        }
    }

    /// <summary>Cheap signature of the sampled parameters, used to detect changes.</summary>
    private static int AnimSignature(int count)
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < count; i++)
            {
                hash = hash * 31 + AnimHashes[i];
                hash = hash * 31 + AnimValues[i].GetHashCode();
            }

            return hash;
        }
    }

    /// <summary>
    /// Keeps the host's own copy of a mirrored NPC animating while it is near a
    /// player. The game culls off-screen Animators by default; a culled actor
    /// stops advancing, so the host would broadcast a frozen animation state and
    /// the client would mirror a statue. Only actors close to the local player
    /// are forced, so a whole level of NPCs does not have to animate at once.
    /// </summary>
    private static void EnsureHostAnimation(EnemyController enemy)
    {
        try
        {
            if (_localPlayer == null || _localPlayer.gameObject == null)
            {
                return;
            }

            if (Vector3.Distance(_localPlayer.position, enemy.transform.position) >
                HostAnimationRadius)
            {
                return;
            }

            int id = enemy.GetInstanceID();
            if (!HostAnimatedIds.Add(id))
            {
                return;
            }

            Animator animator = enemy.anim;
            if (animator != null)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }
        catch (Exception)
        {
            // Animator is optional; ignore.
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

        // A mirrored NPC is driven entirely by the network now, so it must keep
        // animating even when it is off-screen. The game's default culling would
        // otherwise pause the Animator and the puppet would look frozen.
        try
        {
            Animator animator = enemy.anim;
            if (animator != null)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (!animator.enabled)
                {
                    animator.enabled = true;
                }
            }
        }
        catch (Exception)
        {
            // Animator is optional on some NPC prefabs.
        }
    }

    private static EnemyController? Find(string key)
    {
        return KnownEnemies.TryGetValue(key, out EnemyController? enemy) ? enemy : null;
    }

    /// <summary>Looks up a live enemy by its network key. Used by NPC damage forwarding.</summary>
    internal static EnemyController? FindByKey(string key)
    {
        return string.IsNullOrEmpty(key) ? null : Find(key);
    }

    internal static string KeyOf(EnemyController enemy)
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
