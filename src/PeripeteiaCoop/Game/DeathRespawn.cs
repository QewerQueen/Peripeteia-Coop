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
    private static string _scene = string.Empty;
    private static bool _sceneRecorded;

    public static bool Enabled =>
        CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.PlayerRespawnOnDeath;

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
        }

        if (!_sceneRecorded && localPlayer != null && localPlayer.gameObject != null)
        {
            _sceneRecorded = true;
            RecordSavePoint("level start");
        }
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
            // cannot loop.
            return true;
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

        DEAD? deathScreen = health.deathScreen;
        if (deathScreen != null)
        {
            deathScreen.Revive();
        }

        if (health.dead)
        {
            // The game put the player straight back down; use the normal load.
            return false;
        }

        CoopPlugin.LogSource.LogInfo(
            $"[death] respawned at the save point {health.transform.position} " +
            $"(world kept, no reload)."
        );
        return true;
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
        return DeathRespawn.TryRespawn();
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

/// <summary>
/// A co-op client must not load a save mid-level on its own: the client's copy
/// of the shared world (doors, NPCs, quest state) would rewind while the host's
/// did not. The initial load from the main menu, scene transitions and the
/// death path are still allowed.
/// </summary>
[HarmonyPatch(typeof(SaveHandler), "LoadProcedure")]
internal static class ClientBlockRollbackPatch
{
    private static bool Prefix(SaveHandler __instance, bool enteringScene, bool fromDEAD)
    {
        if (!CoopPlugin.ShouldBlockClientRollback)
        {
            return true;
        }

        if (enteringScene || fromDEAD || __instance.onMainMenuScene)
        {
            return true;
        }

        CoopPlugin.LogSource.LogInfo(
            "[save] Client blocked an in-level load: rolling back alone would " +
            "desync the shared world. Only the host can rewind a save."
        );
        return false;
    }
}
