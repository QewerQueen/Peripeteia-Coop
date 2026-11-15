using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Co-op needs both game instances to keep simulating while only one window has
/// focus (solo testing), and a local menu must not freeze the other player.
/// ESC is allowed to open the normal MainMenu UI, but TimeController.Pause is
/// suppressed so the world keeps running. Focus loss no longer opens the menu
/// automatically. An ordinary single-player launch is untouched.
/// </summary>
[HarmonyPatch(typeof(TimeController), nameof(TimeController.Pause))]
internal static class DisableTimeControllerPausePatch
{
    private static bool Prefix()
    {
        return !CoopPlugin.IsCoopActive;
    }
}

[HarmonyPatch(typeof(MainMenu), "OnApplicationFocus")]
internal static class DisableFocusPausePatch
{
    private static bool Prefix(bool hasFocus)
    {
        // On focus gain let the game clean up its UI; on focus loss do not open
        // the pause menu in co-op.
        return !CoopPlugin.IsCoopActive || hasFocus;
    }
}

[HarmonyPatch(typeof(MainMenu), "OpenMenu")]
internal static class MainMenuOpenProbePatch
{
    private static void Postfix()
    {
        if (!CoopPlugin.ProbeEnabled)
        {
            return;
        }

        TimeController? controller = UnityEngine.Object.FindObjectOfType<TimeController>();
        CoopPlugin.LogSource.LogInfo(
            $"[pause] OpenMenu timeScale={Time.timeScale:F2} " +
            $"controllerPause={(controller != null && controller.pause)}"
        );
    }
}
