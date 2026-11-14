using HarmonyLib;

namespace PeripeteiaCoop;

/// <summary>
/// A co-op client must never overwrite the host's save files. These prefixes
/// suppress manual saves, queued saves and the level-start autosave while still
/// allowing LoadProcedure to work.
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
