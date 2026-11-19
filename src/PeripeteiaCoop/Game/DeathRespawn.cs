using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Keeps a death from rewinding the shared world.
///
/// In single player dying means "load the save again": the game calls
/// <see cref="SaveHandler.LoadProcedureFromDeath"/>, which reloads the whole
/// scene. In co-op that would rewind every door, quest object and NPC on one
/// machine only, so the two players would end up standing in two different
/// worlds - the dead player sees the level as it was when they saved while the
/// other one sees the level as they left it.
///
/// With <c>Player.RespawnOnDeath</c> the local player is instead put back on
/// their feet at the last recorded save point while the level keeps running. The
/// prefix returns false so the reload never happens; on any failure it returns
/// true and the game's normal death-load runs, so a death can never soft-lock.
///
/// The save point is the position the player had the last time a save was
/// written (manual, queued or the level-start autosave), falling back to where
/// the player entered the current level.
/// </summary>
internal static class DeathRespawn
{
    private static bool _hasSavePoint;
    private static Vector3 _savePosition;
    private static float _saveYaw;
    private static bool _respawning;
    private static bool _lastTryReentrant;
    private static bool _lastTryDeliberate;
    private static bool _deliberateLoad;
    private static float _deliberateLoadTime;
    private static string _scene = string.Empty;
    private static bool _sceneRecorded;
    private static bool[]? _savedAugs;

    // The held weapon and where it sat while the player was alive. On death the
    // game drops the gun (its parent becomes the world, not the hand slot) and
    // the full reload that would normally put it back is exactly what we skip,
    // so the respawned body is left empty-handed and its support-hand IK grabs
    // at the gun still lying where the player fell. Remembering the last good
    // pose lets the respawn put the gun back where it belongs.
    private static Transform? _heldWeapon;
    private static Vector3 _heldWeaponLocalPosition;
    private static Quaternion _heldWeaponLocalRotation = Quaternion.identity;
    private static Vector3 _heldWeaponLocalScale = Vector3.one;
    private static bool _hasHeldWeapon;
    private static PlayerShootScript? _shootCache;

    // The body and the weapon settle over a few frames after the game rebuilds
    // the player, so the repairs keep running for a short while after a respawn.
    private static int _postRespawnFrames;

    private const int PostRespawnFrames = 30;

    /// <summary>
    /// How long a deliberate-load mark stays valid. The mark is set just before
    /// <c>LoadProcedure</c> reaches <c>LoadProcedureFromDeath</c>; if the load
    /// bails out first (no <c>DEAD</c> yet) the mark goes stale and must not
    /// suppress a later death respawn.
    /// </summary>
    private const float DeliberateLoadWindow = 10.0f;

    public static bool Enabled =>
        CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.PlayerRespawnOnDeath;

    /// <summary>
    /// Marks the load that is about to run as a real rollback of the shared
    /// world (the player picked a save, or the host asked for one) instead of
    /// the game's own "come back from death" step.
    ///
    /// Both end up in <c>LoadProcedureFromDeath</c>: the death screen calls
    /// <c>DEAD.Revive()</c> which calls it, and so does
    /// <c>SaveHandler.LoadProcedure</c> for every in-level load. Without this
    /// flag the respawn would swallow deliberate rollbacks too.
    /// </summary>
    public static void MarkDeliberateLoad()
    {
        _deliberateLoad = true;
        _deliberateLoadTime = Time.unscaledTime;
    }

    /// <summary>
    /// True while the death prefix is rebuilding the local player in place. A
    /// load fired from that path is the game's internal "come back from a load"
    /// bookkeeping, not a real rollback, so it must not be shared.
    /// </summary>
    public static bool IsRespawning => _respawning;

    /// <summary>
    /// True when the last <see cref="TryRespawn"/> was a re-entry from inside
    /// the respawn itself (the game's own load bookkeeping calling back in).
    /// That load must not be treated as a rollback of the shared world.
    /// </summary>
    public static bool LastTryReentrant => _lastTryReentrant;

    /// <summary>
    /// True when the last <see cref="TryRespawn"/> stood aside because the
    /// player (or the host) deliberately loaded a save.
    /// </summary>
    public static bool LastTryDeliberate => _lastTryDeliberate;

    /// <summary>
    /// True while the local player is dead or unconscious - i.e. the game's
    /// death flow is running. Lets the load patches tell the game's own death
    /// reload apart from a deliberate save rollback.
    /// </summary>
    public static bool LocalPlayerIsDown()
    {
        try
        {
            PlayerMovement? player = UnityEngine.Object.FindObjectOfType<PlayerMovement>();
            if (player == null || player.gameObject == null)
            {
                return false;
            }

            HealthScript? health = player.GetComponent<HealthScript>();
            if (health == null)
            {
                health = player.GetComponentInParent<HealthScript>();
            }

            return health != null && (health.dead || health.unconcious);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Called when a save is loaded (by either machine) so the respawn point is
    /// re-read from wherever the loaded save puts the player.
    /// </summary>
    public static void OnLoad()
    {
        _scene = string.Empty;
        _sceneRecorded = false;
        _hasSavePoint = false;
        _postRespawnFrames = 0;
        _hasHeldWeapon = false;
        _shootCache = null;
    }

    /// <summary>Watches for level changes so each scene gets its own save point.</summary>
    public static void Tick(PlayerMovement? localPlayer)
    {
        if (!Enabled)
        {
            return;
        }

        string scene;
        try
        {
            scene = SceneManager.GetActiveScene().name;
        }
        catch (Exception)
        {
            return;
        }

        if (!string.Equals(scene, _scene, StringComparison.Ordinal))
        {
            _scene = scene;
            _sceneRecorded = false;
            _hasSavePoint = false;
            _deliberateLoad = false;
            _hasHeldWeapon = false;
            _shootCache = null;
        }

        if (!_sceneRecorded && localPlayer != null && localPlayer.gameObject != null)
        {
            _sceneRecorded = true;
            RecordSavePoint("level start");
        }

        // Keep the last good gun pose while the player is alive and holding it,
        // and finish the respawn repairs over the next few frames.
        CaptureHeldWeapon(localPlayer);
        ApplyPostRespawn(localPlayer);
    }

    /// <summary>Records the spot the player saved at, used as the respawn point.</summary>
    public static void RecordSavePoint(string reason)
    {
        if (!Enabled || _respawning)
        {
            return;
        }

        try
        {
            PlayerMovement? player = UnityEngine.Object.FindObjectOfType<PlayerMovement>();
            if (player == null || player.gameObject == null)
            {
                return;
            }

            _savePosition = player.transform.position;
            _saveYaw = player.transform.eulerAngles.y;
            _hasSavePoint = true;

            CoopPlugin.LogSource.LogInfo(
                $"[death] save point ({reason}) recorded at {_savePosition}."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] could not record the save point: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Puts the local player back on their feet without reloading the level.
    /// Returns false when the caller must be skipped (the respawn worked) and
    /// true when the game's original load should run instead.
    /// </summary>
    public static bool TryRespawn()
    {
        if (!Enabled)
        {
            return true;
        }

        if (_respawning)
        {
            // A revive path re-entered the load; let it through this once so we
            // cannot loop, but remember it was not a rollback.
            _lastTryReentrant = true;
            _lastTryDeliberate = false;
            return true;
        }

        _lastTryReentrant = false;
        _lastTryDeliberate = false;

        if (_deliberateLoad)
        {
            bool fresh = Time.unscaledTime - _deliberateLoadTime < DeliberateLoadWindow;
            _deliberateLoad = false;

            if (fresh)
            {
                // The player (or the host) is deliberately loading a save: the
                // shared world has to be rebuilt, so the respawn must stand
                // aside.
                _lastTryDeliberate = true;
                return true;
            }
        }

        try
        {
            _respawning = true;
            return !Respawn();
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] respawn failed, falling back to the normal load: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            return true;
        }
        finally
        {
            _respawning = false;
        }
    }

    private static bool Respawn()
    {
        PlayerMovement? player = UnityEngine.Object.FindObjectOfType<PlayerMovement>();
        if (player == null || player.gameObject == null)
        {
            return false;
        }

        HealthScript? health = player.GetComponent<HealthScript>();
        if (health == null)
        {
            health = player.GetComponentInParent<HealthScript>();
        }

        if (health == null)
        {
            return false;
        }

        // The game's own "coming back from a load" path: it clears the dead and
        // unconscious state, restores the health and rebuilds the visuals.
        health.HandleLoad(false, false);

        if (_hasSavePoint)
        {
            Teleport(health.transform, _savePosition, _saveYaw);
            if (player.transform != health.transform)
            {
                Teleport(player.transform, _savePosition, _saveYaw);
            }
        }

        // Belt and braces: some paths leave the flags set when the death screen
        // never finished its own bookkeeping.
        health.dead = false;
        health.unconcious = false;
        health.fallen = false;
        if (health.maxHP > 0)
        {
            health.HP = health.maxHP;
        }

        if (health.maxConciousness > 0)
        {
            health.Conciousness = health.maxConciousness;
        }

        // The game's death-load normally restores these from the save; skipping
        // that load would leave the per-limb damage, the ability state and the
        // dropped weapon behind, so put them back by hand. The rebuilt body keeps
        // settling for a few frames, so the same repairs are repeated from Tick
        // for a short while after this.
        ResetBodyDamage(player, true);
        RestoreAbilities(player);
        RestoreHeldWeapon(player);
        health.forceUIUpdate();
        _postRespawnFrames = PostRespawnFrames;

        // NOTE: never call deathScreen.Revive() from here. This prefix runs
        // *inside* DEAD.Revive() - the death screen is the only caller of
        // LoadProcedureFromDeath - and that method already rebuilt the player
        // body and tore the death screen down before it got here. Calling it
        // again re-enters the revive half way through and throws a
        // NullReferenceException, which used to fall back to the full load and
        // leave the player stuck at the origin.

        if (health.dead)
        {
            // The first HandleLoad can run before the game has finished tearing
            // the death state down. Bailing out here hands the whole thing back
            // to the game's scene reload, which rewinds the shared world (dead
            // NPCs back on their feet, story locks cleared) for both players.
            // Try the revive once more before giving up.
            CoopPlugin.LogSource.LogWarning(
                $"[death] still flagged dead after the first pass " +
                $"(hp={health.HP}/{health.maxHP} unconcious={health.unconcious} " +
                $"fallen={health.fallen}); retrying the revive."
            );

            try
            {
                health.HandleLoad(false, false);
            }
            catch (Exception)
            {
                // Fall through to the forced flags below.
            }

            health.dead = false;
            health.unconcious = false;
            health.fallen = false;
            if (health.maxHP > 0)
            {
                health.HP = health.maxHP;
            }

            if (health.maxConciousness > 0)
            {
                health.Conciousness = health.maxConciousness;
            }

            health.forceUIUpdate();
        }

        if (health.dead)
        {
            // The game can set the flag back on for a frame or two after the
            // revive (its death coroutines are still running). Bailing out to
            // the full load here is exactly what rewound the shared world -
            // dead NPCs back on their feet, story locks cleared, and the player
            // dying a second time. Instead the respawn stands and the
            // post-respawn pass keeps clearing the flag; ApplyPostRespawn falls
            // back to the game's load only if the player is still down when the
            // window closes.
            CoopPlugin.LogSource.LogWarning(
                $"[death] the flag came back right after the revive " +
                $"(hp={health.HP}/{health.maxHP} unconcious={health.unconcious}); " +
                $"holding the respawn and clearing it over the next frames."
            );
        }

        CoopPlugin.LogSource.LogInfo(
            $"[death] respawned at the save point {health.transform.position} " +
            $"(world kept, no reload)."
        );
        return true;
    }

    /// <summary>
    /// Remembers which abilities (augs) the player had switched on. The game
    /// rebuilds the body when the player comes back from death, and the fresh
    /// AugSelect starts with its icons and its effects out of step: the icon
    /// shows the pre-death state while the effect is off, and the next keypress
    /// flips them the wrong way round. Capturing the state before the rebuild
    /// lets <see cref="RestoreAbilities"/> put it back exactly.
    /// </summary>
    public static void CaptureAbilities(PlayerMovement? localPlayer)
    {
        _savedAugs = null;
        if (!Enabled)
        {
            return;
        }

        try
        {
            AugSelect? augs = FindAugSelect(localPlayer);
            _savedAugs = augs?.serializeAugs();
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] could not read the ability state: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void RestoreAbilities(PlayerMovement player)
    {
        try
        {
            AugSelect? augs = FindAugSelect(player);
            if (augs == null)
            {
                return;
            }

            // Start from a consistent state (icon and effect both off), then
            // switch back on what the player had before dying.
            augs.ForceAllAugsOff();

            int restored = 0;
            bool[]? saved = _savedAugs;
            if (saved != null)
            {
                for (int i = 0; i < saved.Length; i++)
                {
                    if (saved[i])
                    {
                        augs.deserializeAug(i, true);
                        restored++;
                    }
                }
            }

            augs.forceUIUpdate();

            CoopPlugin.LogSource.LogInfo(
                $"[death] ability state reset after respawn ({restored} restored)."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] could not restore the ability state: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static AugSelect? FindAugSelect(PlayerMovement? player)
    {
        if (player != null && player.gameObject != null)
        {
            AugSelect? onPlayer = player.GetComponentInChildren<AugSelect>(true);
            if (onPlayer != null)
            {
                return onPlayer;
            }
        }

        return UnityEngine.Object.FindObjectOfType<AugSelect>();
    }

    /// <summary>
    /// Clears the per-limb damage left over from the death. The body figure and
    /// the limb hitboxes keep the values they had when the player died, so
    /// without this a respawn still shows the old wrecked body. The game's own
    /// <c>healLimb</c> is used first so its HUD and hitbox bookkeeping run, then
    /// the raw arrays are forced for anything the heal path skips.
    /// </summary>
    private static void ResetBodyDamage(PlayerMovement player, bool log)
    {
        try
        {
            PlayerHealth? body = player.GetComponentInChildren<PlayerHealth>(true);
            if (body == null)
            {
                body = UnityEngine.Object.FindObjectOfType<PlayerHealth>();
            }

            if (body == null)
            {
                return;
            }

            int[]? amounts = body.HealthAmounts;
            int[]? maxima = body.maxHealthAmounts;
            int count = 0;
            if (amounts != null && maxima != null)
            {
                count = Math.Min(amounts.Length, maxima.Length);
                for (int i = 0; i < count; i++)
                {
                    if (amounts[i] < maxima[i])
                    {
                        body.healLimb(i, maxima[i] - amounts[i]);
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    amounts[i] = maxima[i];
                }
            }

            body.drawLimbs();
            body.healthEffectUpdate();

            if (log)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[death] cleared per-limb damage on respawn ({count} limbs)."
                );
            }
        }
        catch (Exception exception)
        {
            if (log)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[death] could not reset the body damage: " +
                    $"{exception.GetType().Name}: {exception.Message}"
                );
            }
        }
    }

    /// <summary>
    /// Repeats the respawn repairs for a few frames: the game rebuilds the
    /// player body and re-settles its weapon over several frames, so a single
    /// pass can land on the wrong instance.
    /// </summary>
    private static void ApplyPostRespawn(PlayerMovement? localPlayer)
    {
        if (_postRespawnFrames <= 0 || localPlayer == null || localPlayer.gameObject == null)
        {
            return;
        }

        _postRespawnFrames--;

        // Keep the player on their feet. The game's death coroutines can set the
        // flag back on for a frame or two after the revive; clearing it here is
        // what keeps the respawn from being mistaken for a failure and handing
        // the level back to a full reload.
        HealthScript? health = FindHealth(localPlayer);
        if (health != null && (health.dead || health.unconcious))
        {
            health.dead = false;
            health.unconcious = false;
            health.fallen = false;
            if (health.maxHP > 0)
            {
                health.HP = health.maxHP;
            }

            if (health.maxConciousness > 0)
            {
                health.Conciousness = health.maxConciousness;
            }

            health.forceUIUpdate();
        }

        ResetBodyDamage(localPlayer, false);
        RestoreHeldWeapon(localPlayer);

        if (_postRespawnFrames == 0 && health != null && health.dead)
        {
            // Last resort: the player is still down after the whole window, so
            // let the game's own load run rather than leave the game stuck. The
            // deliberate-load mark makes TryRespawn stand aside for this one.
            CoopPlugin.LogSource.LogWarning(
                "[death] the player is still down after the respawn window; " +
                "using the game's load."
            );

            try
            {
                MarkDeliberateLoad();
                SaveHandler? handler = UnityEngine.Object.FindObjectOfType<SaveHandler>();
                handler?.LoadProcedureFromDeath();
            }
            catch (Exception exception)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[death] fallback load failed: " +
                    $"{exception.GetType().Name}: {exception.Message}"
                );
            }
        }
    }

    private static HealthScript? FindHealth(PlayerMovement player)
    {
        try
        {
            HealthScript? health = player.GetComponent<HealthScript>();
            if (health == null)
            {
                health = player.GetComponentInParent<HealthScript>();
            }

            return health;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Remembers the gun the local player is holding and the local transform it
    /// had while it was properly attached to the hand slot. Only a correctly
    /// attached pose is remembered, so the dropped pose from the death frame is
    /// never captured.
    /// </summary>
    private static void CaptureHeldWeapon(PlayerMovement? localPlayer)
    {
        if (localPlayer == null || localPlayer.gameObject == null)
        {
            return;
        }

        try
        {
            PlayerShootScript? shoot = _shootCache;
            if (!IsLive(shoot))
            {
                shoot = localPlayer.GetComponentInChildren<PlayerShootScript>(true);
                if (shoot == null)
                {
                    shoot = localPlayer.GetComponentInParent<PlayerShootScript>();
                }

                _shootCache = shoot;
            }

            Transform? held = shoot != null ? shoot.currentlyHeld : null;
            Transform? slot = localPlayer.WeaponLocation;
            if (held == null || slot == null || !IsLive(held) || !IsLive(slot))
            {
                return;
            }

            if (held.parent != slot)
            {
                // Not in the hand slot (holstered or dropped): keep the last
                // good pose instead of recording a bad one.
                return;
            }

            _heldWeapon = held;
            _heldWeaponLocalPosition = held.localPosition;
            _heldWeaponLocalRotation = held.localRotation;
            _heldWeaponLocalScale = held.localScale;
            _hasHeldWeapon = true;
        }
        catch (Exception)
        {
            // Capturing is best-effort; a failure just means no repair later.
        }
    }

    /// <summary>
    /// Puts the held weapon back into the respawned body's hand slot. On death
    /// the game drops the gun and only the skipped scene reload would normally
    /// pick it back up, which leaves the support-hand IK grabbing at the gun on
    /// the ground.
    /// </summary>
    private static void RestoreHeldWeapon(PlayerMovement player)
    {
        if (!_hasHeldWeapon)
        {
            return;
        }

        try
        {
            Transform? gun = _heldWeapon;
            if (gun == null || !IsLive(gun))
            {
                _hasHeldWeapon = false;
                return;
            }

            // If the rebuilt body already holds a different weapon that is
            // properly attached, leave it alone rather than adding a second gun.
            PlayerShootScript? shoot = player.GetComponentInChildren<PlayerShootScript>(true);
            Transform? current = shoot != null ? shoot.currentlyHeld : null;
            if (current != null && current != gun && IsLive(current) &&
                current.IsChildOf(player.transform))
            {
                _hasHeldWeapon = false;
                return;
            }

            if (gun.IsChildOf(player.transform))
            {
                // The game put it back itself (or our earlier pass did).
                return;
            }

            Transform? slot = player.WeaponLocation;
            if (slot == null || !IsLive(slot))
            {
                return;
            }

            gun.SetParent(slot, false);
            gun.localPosition = _heldWeaponLocalPosition;
            gun.localRotation = _heldWeaponLocalRotation;
            gun.localScale = _heldWeaponLocalScale;

            CoopPlugin.LogSource.LogInfo("[death] re-attached the held weapon after respawn.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] could not re-attach the held weapon: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// True when a Unity object still has a live native counterpart. A destroyed
    /// object can survive as a non-null IL2CPP wrapper whose members throw, so a
    /// plain "!= null" test is not enough to know an object is usable.
    /// </summary>
    private static bool IsLive(Component? value)
    {
        if (value == null)
        {
            return false;
        }

        try
        {
            return value.gameObject != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Teleport(Transform target, Vector3 position, float yaw)
    {
        try
        {
            Quaternion rotation = Quaternion.Euler(0.0f, yaw, 0.0f);

            // A CharacterController must be off while its transform is moved,
            // otherwise it snaps back to where it was or drops through the floor.
            CharacterController? controller = target.GetComponent<CharacterController>();
            if (controller == null)
            {
                controller = target.GetComponentInChildren<CharacterController>();
            }

            if (controller != null)
            {
                bool wasEnabled = controller.enabled;
                controller.enabled = false;
                target.position = position;
                target.rotation = rotation;
                controller.enabled = wasEnabled;
            }
            else
            {
                target.position = position;
                target.rotation = rotation;
            }

            Rigidbody? body = target.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = position;
                body.rotation = rotation;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] teleport failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }
}

/// <summary>
/// Turns the game's death-load into a respawn at the save point. Returning false
/// from the prefix skips <c>LoadProcedureFromDeath</c> entirely, so the level is
/// never reloaded and the shared world stays as it is.
/// </summary>
[HarmonyPatch(typeof(SaveHandler), "LoadProcedureFromDeath")]
internal static class DeathRespawnPatch
{
    private static bool Prefix()
    {
        bool runOriginal = DeathRespawn.TryRespawn();
        if (runOriginal)
        {
            bool reentrant = DeathRespawn.LastTryReentrant;
            CoopPlugin.LogSource.LogWarning(
                $"[death] the game's death load is running " +
                $"(reentrant={reentrant}, deliberate={DeathRespawn.LastTryDeliberate})."
            );

            if (!reentrant)
            {
                // The game is reloading the save after all (a failed respawn,
                // or a deliberate rollback), so the world (and its story
                // progress) is rebuilt; the one-time locks have to follow. A
                // re-entry from inside the respawn is not a rollback, so its
                // locks stay put.
                StorySync.ResetLocks("a load");
            }
        }

        return runOriginal;
    }
}

[HarmonyPatch(typeof(SaveHandler), "SaveProcedure")]
internal static class DeathSavePointSavePatch
{
    private static void Postfix()
    {
        DeathRespawn.RecordSavePoint("save");
    }
}

[HarmonyPatch(typeof(SaveHandler), "ProcessRequestedSave")]
internal static class DeathSavePointQueuedPatch
{
    private static void Postfix()
    {
        DeathRespawn.RecordSavePoint("queued save");
    }
}

[HarmonyPatch(typeof(SaveHandler), "AutosaveOnLevelStart")]
internal static class DeathSavePointAutosavePatch
{
    private static void Postfix()
    {
        DeathRespawn.RecordSavePoint("level autosave");
    }
}
