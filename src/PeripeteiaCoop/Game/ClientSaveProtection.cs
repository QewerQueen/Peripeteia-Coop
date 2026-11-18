using System;
using HarmonyLib;

namespace PeripeteiaCoop;

/// <summary>
/// A co-op client must never overwrite the host's save files. These prefixes
/// suppress manual saves, queued saves and the level-start autosave while still
/// allowing LoadProcedure to work.
///
/// The block only applies to saves that would touch the host's files. The client
/// now plays its own copy of the host save under a local 'Coop - &lt;name&gt;'
/// name, and those saves are allowed, so the client's personal inventory, money
/// and health persist between sessions.
/// </summary>
[HarmonyPatch(typeof(SaveHandler), "RequestSave")]
internal static class ClientBlockSaveRequestPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!CoopPlugin.ShouldBlockClientSaves)
        {
            return true;
        }

        __result = false;
        CoopPlugin.LogSource.LogInfo("[save] Client blocked a manual save request.");
        return false;
    }
}

[HarmonyPatch(typeof(SaveHandler), "ProcessRequestedSave")]
internal static class ClientBlockQueuedSavePatch
{
    private static bool Prefix()
    {
        if (!CoopPlugin.ShouldBlockClientSaves)
        {
            return true;
        }

        CoopPlugin.LogSource.LogInfo("[save] Client blocked a queued save.");
        return false;
    }
}

[HarmonyPatch(typeof(SaveHandler), "AutosaveOnLevelStart")]
internal static class ClientBlockAutosavePatch
{
    private static bool Prefix()
    {
        if (!CoopPlugin.ShouldBlockClientSaves)
        {
            return true;
        }

        CoopPlugin.LogSource.LogInfo("[save] Client blocked a level-start autosave.");
        return false;
    }
}

/// <summary>
/// A co-op client's save belongs to the client alone. When the game asks to save
/// (level-start autosave, quicksave, a save room) it may hand out a name of its
/// own; on a client that name would land outside the client's own co-op save and
/// the personal inventory, money and health would be lost on the next join.
/// Redirect the write to the client's own 'Coop - &lt;name&gt;' save.
/// </summary>
[HarmonyPatch(typeof(SaveHandler), "SaveProcedure")]
internal static class ClientSaveRedirectPatch
{
    private static void Prefix(ref string saveName)
    {
        if (CoopPlugin.Settings.Role != CoopRole.Client)
        {
            return;
        }

        string current = SaveVersionController.currentSaveName ?? string.Empty;
        if (!SaveTransfer.IsCoopLocalSave(current))
        {
            return;
        }

        if (!string.Equals(saveName, current, StringComparison.Ordinal))
        {
            CoopPlugin.LogSource.LogInfo(
                $"[save] Client save '{saveName}' redirected to its own co-op save '{current}'."
            );
            saveName = current;
        }
    }

    private static void Postfix(string saveName)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Client)
        {
            CoopPlugin.LogSource.LogInfo($"[save] Client wrote its own save '{saveName}'.");
        }
    }
}
