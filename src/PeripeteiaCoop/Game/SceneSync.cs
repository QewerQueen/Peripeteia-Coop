using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Keeps a level transition in step across the party.
///
/// Every way into another level (a doorway trigger, a scripted teleport, the
/// loading screen) funnels through <c>LoadingScreen.loadLevel(string, bool)</c>.
/// Whoever walks through first announces the level name, and the other machine
/// plays the same transition through its own loading screen, so both players end
/// up in the same level instead of one of them being left behind in the old one.
///
/// The transition is deliberately symmetrical: either player can lead. That
/// matches the rule the story layer already follows for scripted teleports (the
/// player who trips the script moves both), and it keeps the party together when
/// the host starts a run from the menu while the client waits there.
/// </summary>
internal static class SceneSync
{
    private static bool _applyingRemote;
    private static float _lastSentTime;
    private static string _lastSentScene = string.Empty;
    private static string _lastSentActiveScene = string.Empty;
    private static int _changesSent;
    private static int _changesApplied;

    public static bool Enabled =>
        CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.SyncSceneTransitions;

    public static int ChangesSent => _changesSent;

    public static int ChangesApplied => _changesApplied;

    /// <summary>True while this machine is following the other player's transition.</summary>
    public static bool IsApplyingRemote => _applyingRemote;

    /// <summary>
    /// Called from the loading screen before it really loads <paramref name="levelName"/>.
    /// Only announces; the local transition always goes through.
    /// </summary>
    public static void OnLocalTransition(string levelName)
    {
        if (!Enabled || _applyingRemote || string.IsNullOrEmpty(levelName))
        {
            return;
        }

        if (IsMenuScene(levelName))
        {
            return;
        }

        // One transition can pass through loadLevel more than once (a fade out
        // and a fade in); announce each distinct target only once. Once the
        // active scene has actually changed, a move back is a new transition.
        string current = SceneManager.GetActiveScene().name;
        if (string.Equals(levelName, _lastSentScene, StringComparison.Ordinal) &&
            string.Equals(current, _lastSentActiveScene, StringComparison.Ordinal) &&
            Time.unscaledTime - _lastSentTime < 10.0f)
        {
            return;
        }

        _lastSentScene = levelName;
        _lastSentActiveScene = current;
        _lastSentTime = Time.unscaledTime;
        _changesSent++;

        CoopPlugin.LogSource.LogInfo(
            $"[scene] level transition to '{levelName}' shared with the other player."
        );
        CoopPlugin.Network?.SendSceneChange(levelName);
    }

    /// <summary>
    /// Plays the other player's transition on this machine. The local player's
    /// own save is what the new level loads from, exactly as when the local
    /// player walked through the door.
    /// </summary>
    public static void ApplyRemoteTransition(string levelName)
    {
        if (!Enabled || string.IsNullOrEmpty(levelName) || IsMenuScene(levelName))
        {
            return;
        }

        string current = SceneManager.GetActiveScene().name;
        if (string.Equals(current, levelName, StringComparison.Ordinal))
        {
            // Already there: the local transition is the one the other side saw.
            return;
        }

        LoadingScreen? screen = UnityEngine.Object.FindObjectOfType<LoadingScreen>();
        try
        {
            _applyingRemote = true;
            _changesApplied++;

            if (screen != null)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[scene] following the other player to '{levelName}'."
                );
                screen.loadLevel(levelName, false);
            }
            else
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[scene] following the other player to '{levelName}' " +
                    "(no loading screen, loading the scene directly)."
                );
                SceneManager.LoadScene(levelName);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[scene] could not follow the level transition to '{levelName}': " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    public static void Reset()
    {
        _applyingRemote = false;
        _lastSentScene = string.Empty;
        _lastSentActiveScene = string.Empty;
        _lastSentTime = 0.0f;
    }

    private static bool _testDone;
    private static float _testAt;

    /// <summary>
    /// Debug-only (<c>-coop-scene-test NAME</c>): once both players are in and the
    /// level has settled, walk this machine through the real loading-screen
    /// transition to <c>NAME</c>. That is the same funnel every level exit uses, so
    /// a retest can prove the other player follows without hunting for a door.
    /// Host only, and only when the flag is set.
    /// </summary>
    public static void MaybeRunTest(bool ready)
    {
        if (_testDone ||
            !Enabled ||
            CoopPlugin.Settings.Role != CoopRole.Host ||
            string.IsNullOrEmpty(CoopPlugin.Settings.SceneTest))
        {
            return;
        }

        if (!ready)
        {
            _testAt = 0.0f;
            return;
        }

        if (_testAt <= 0.0f)
        {
            _testAt = Time.unscaledTime + 20.0f;
            return;
        }

        if (Time.unscaledTime < _testAt)
        {
            return;
        }

        _testDone = true;

        string target = CoopPlugin.Settings.SceneTest;
        string current = SceneManager.GetActiveScene().name;
        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            CoopPlugin.LogSource.LogInfo($"[scene] test target '{target}' is already the active scene.");
            return;
        }

        CoopPlugin.LogSource.LogInfo($"[scene] test: forcing a transition from '{current}' to '{target}'.");
        try
        {
            LoadingScreen? screen = UnityEngine.Object.FindObjectOfType<LoadingScreen>();
            if (screen != null)
            {
                screen.loadLevel(target, false);
            }
            else
            {
                SceneManager.LoadScene(target);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[scene] test transition failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static bool IsMenuScene(string sceneName)
    {
        return sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>
/// Watches the single funnel every level transition goes through. The prefix
/// only announces the move; the original transition still runs, so the local
/// player really leaves the level.
/// </summary>
[HarmonyPatch(typeof(LoadingScreen), "loadLevel")]
internal static class SceneTransitionPatch
{
    private static void Prefix(string levelname)
    {
        SceneSync.OnLocalTransition(levelname);
    }
}
