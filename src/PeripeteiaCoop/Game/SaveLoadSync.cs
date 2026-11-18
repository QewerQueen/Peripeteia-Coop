using System;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Keeps a deliberate save rollback in step across the party.
///
/// A mid-level load rewinds the whole shared world, so it is a host decision:
/// the host loads the save and tells every client to load the same one. Without
/// the hand-off the host would stand in the rolled-back level while the client
/// stayed in the world as it was, and every door, NPC and quest flag would
/// disagree between them.
///
/// A client's own mid-level loads are blocked (<c>Client.BlockRollback</c>) for
/// the same reason; only the load the host asked for is let through.
/// </summary>
internal static class SaveLoadSync
{
    private static bool _applyingRemoteLoad;
    private static float _lastRequestTime;
    private static string _lastRequestName = string.Empty;
    private static int _loadsRequested;
    private static int _loadsApplied;

    public static bool Enabled => CoopPlugin.Settings.Role != CoopRole.Offline;

    public static int LoadsRequested => _loadsRequested;

    public static int LoadsApplied => _loadsApplied;

    /// <summary>True while this machine is loading the save the host asked for.</summary>
    public static bool IsApplyingRemoteLoad => _applyingRemoteLoad;

    /// <summary>
    /// Host side: a mid-level load is a rollback of the shared world, so tell the
    /// clients which save to load. Repeats are collapsed, because one rollback can
    /// pass through the load method more than once.
    /// </summary>
    public static void OnHostLoad(string saveName)
    {
        if (!Enabled ||
            CoopPlugin.Settings.Role != CoopRole.Host ||
            string.IsNullOrEmpty(saveName))
        {
            return;
        }

        if (string.Equals(saveName, _lastRequestName, StringComparison.Ordinal) &&
            Time.unscaledTime - _lastRequestTime < 3.0f)
        {
            return;
        }

        _lastRequestName = saveName;
        _lastRequestTime = Time.unscaledTime;
        _loadsRequested++;

        CoopPlugin.LogSource.LogInfo(
            $"[save] Host rolled back to '{saveName}'; asking the other player to load it too."
        );
        CoopPlugin.Network?.SendSaveLoad(saveName);
    }

    /// <summary>Client side: load the save the host just rolled back to.</summary>
    public static void ApplyRemoteLoad(string saveName)
    {
        if (!Enabled ||
            CoopPlugin.Settings.Role == CoopRole.Host ||
            string.IsNullOrEmpty(saveName))
        {
            return;
        }

        SaveHandler? handler = UnityEngine.Object.FindObjectOfType<SaveHandler>();
        if (handler == null)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[save] Host rolled back to '{saveName}' but this machine has no " +
                "SaveHandler yet; the rollback was ignored."
            );
            return;
        }

        try
        {
            _applyingRemoteLoad = true;
            _loadsApplied++;
            CoopPlugin.LogSource.LogInfo(
                $"[save] Following the host's rollback to '{saveName}'."
            );
            handler.LoadProcedure(false, saveName, false);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[save] Could not follow the host's rollback: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _applyingRemoteLoad = false;
        }
    }
}

/// <summary>
/// Watches <c>SaveHandler.LoadProcedure</c>. The host announces a deliberate
/// mid-level rollback; the client refuses to roll back on its own, and only
/// follows a load the host explicitly asked for.
/// </summary>
[HarmonyPatch(typeof(SaveHandler), "LoadProcedure")]
internal static class SaveLoadProcedurePatch
{
    private static bool Prefix(
        SaveHandler __instance,
        bool enteringScene,
        string saveName,
        bool fromDEAD
    )
    {
        if (DeathRespawn.IsRespawning)
        {
            // The death respawn calls the game's own "come back from a load"
            // bookkeeping; that is not a rollback of the shared world.
            return true;
        }

        if (CoopPlugin.Settings.Role == CoopRole.Host)
        {
            if (!enteringScene && !fromDEAD && !__instance.onMainMenuScene)
            {
                // A deliberate rollback, not the death screen's own revive:
                // the respawn must not swallow it.
                DeathRespawn.MarkDeliberateLoad();
                SaveLoadSync.OnHostLoad(saveName);
            }

            // A load moves the player; the respawn point has to be re-read from
            // wherever the loaded save puts them, and the story locks have to be
            // rebuilt from the loaded world.
            DeathRespawn.OnLoad();
            StorySync.ResetLocks("the host loaded a save");
            return true;
        }

        if (!CoopPlugin.ShouldBlockClientRollback ||
            enteringScene ||
            fromDEAD ||
            __instance.onMainMenuScene ||
            SaveLoadSync.IsApplyingRemoteLoad)
        {
            if (!enteringScene && !fromDEAD && !__instance.onMainMenuScene)
            {
                // Same reasoning as the host branch above.
                DeathRespawn.MarkDeliberateLoad();
            }

            DeathRespawn.OnLoad();
            StorySync.ResetLocks("a save was loaded");
            return true;
        }

        CoopPlugin.LogSource.LogInfo(
            "[save] Client blocked an in-level load: rolling back alone would " +
            "desync the shared world. Only the host can rewind a save."
        );
        return false;
    }
}
