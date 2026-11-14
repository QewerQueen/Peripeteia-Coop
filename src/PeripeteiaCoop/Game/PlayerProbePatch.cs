using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Proof-of-life patch. Logs the player state every few seconds and lets F8
/// print the player hierarchy. This is intentionally read-only: it must not
/// change gameplay while the networking layer is being built.
/// </summary>
[HarmonyPatch(typeof(PlayerMovement), "Update")]
internal static class PlayerProbePatch
{
    private static float _nextLogTime;
    private static int _updateCount;

    private static void Postfix(PlayerMovement __instance)
    {
        if (!CoopPlugin.ProbeEnabled || __instance == null)
        {
            return;
        }

        _updateCount++;

        if (Time.unscaledTime < _nextLogTime)
        {
            return;
        }

        _nextLogTime = Time.unscaledTime + 5f;

        Transform transform = __instance.transform;
        Vector3 position = transform.position;
        string scene = SceneManager.GetActiveScene().name;
        string formattedPosition = string.Format(
            CultureInfo.InvariantCulture,
            "({0:F2}, {1:F2}, {2:F2})",
            position.x,
            position.y,
            position.z
        );

        CoopPlugin.LogSource.LogInfo(
            $"[probe] scene='{scene}' pos={formattedPosition} " +
            $"ground={__instance.IsOnGround} crouch={__instance.isCrouched} sprint={__instance.IsSprinting} " +
            $"updates={_updateCount}"
        );

        if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
        {
            CoopPlugin.LogSource.LogInfo(
                $"[probe] F8 player='{transform.name}' parent='{(transform.parent != null ? transform.parent.name : "<none>")}' " +
                $"children={transform.childCount} scene='{scene}'"
            );
        }
    }
}
