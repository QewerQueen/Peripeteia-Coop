using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Shares the one-way "fan shutdown" world state (first level terminal/keyboard).
///
/// The game stops the fan through <c>rotateFan.shutdownrotate()</c>, which is not
/// a QuestAction and is not part of the quest-effect whitelist, so the vanilla
/// call only changed the machine that touched it. This mirrors that call by
/// object hierarchy key: the player who shuts the fan down sends a reliable
/// StoryEvent, and the other machine calls its own copy of the same method.
///
/// A short periodic scan is also used because some interactions can set
/// <c>rotateFan.shutdown</c> through a UnityEvent chain that does not go through
/// the patched method on every code path. Both paths deduplicate by key, so the
/// state is sent once per scene.
/// </summary>
internal static class FanSync
{
    /// <summary>Marker used in the story snapshot so old clients ignore it.</summary>
    public const string SnapshotPrefix = "FAN:";

    private static readonly HashSet<string> Shutdown = new(StringComparer.Ordinal);
    private static string _scene = string.Empty;
    private static bool _applyingRemote;
    private static float _nextScan;

    public static bool Enabled => CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.StoryEnabled;

    public static void Tick()
    {
        if (!Enabled)
        {
            return;
        }

        EnsureScene();

        if (_applyingRemote || Time.unscaledTime < _nextScan)
        {
            return;
        }

        _nextScan = Time.unscaledTime + 0.75f;

        try
        {
            foreach (rotateFan fan in Resources.FindObjectsOfTypeAll<rotateFan>())
            {
                if (fan == null || !fan.shutdown)
                {
                    continue;
                }

                if (!string.Equals(fan.gameObject.scene.name, _scene, StringComparison.Ordinal))
                {
                    continue;
                }

                NoteShutdown(fan);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[fan] state scan failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>Called by the Harmony postfix on <c>rotateFan.shutdownrotate</c>.</summary>
    public static void OnShutdown(rotateFan fan)
    {
        if (!Enabled || _applyingRemote || fan == null)
        {
            return;
        }

        EnsureScene();
        NoteShutdown(fan);
    }

    /// <summary>Applies a shutdown event received from the other machine.</summary>
    public static void Apply(string key)
    {
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            return;
        }

        EnsureScene();
        Shutdown.Add(key);

        GameObject? target = StorySync.FindObject(key);
        if (target == null)
        {
            CoopPlugin.LogSource.LogWarning($"[fan] remote fan '{key}' was not found in this scene.");
            return;
        }

        _applyingRemote = true;
        try
        {
            foreach (rotateFan fan in target.GetComponentsInChildren<rotateFan>(true))
            {
                if (fan == null || fan.shutdown)
                {
                    continue;
                }

                fan.shutdownrotate();
                CoopPlugin.LogSource.LogInfo($"[fan] applied remote shutdown at '{key}'.");
            }
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    /// <summary>Restores a fan that was already shut when the client joined.</summary>
    public static void RestoreFromSnapshot(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        EnsureScene();
        if (!key.StartsWith(_scene + "|", StringComparison.Ordinal))
        {
            return;
        }

        Apply(key);
    }

    /// <summary>Fan keys to append to the host's story snapshot.</summary>
    public static IEnumerable<string> SnapshotKeys()
    {
        EnsureScene();
        var result = new List<string>();
        foreach (string key in Shutdown)
        {
            if (key.StartsWith(_scene + "|", StringComparison.Ordinal))
            {
                result.Add(SnapshotPrefix + key);
            }
        }

        return result;
    }

    private static void NoteShutdown(rotateFan fan)
    {
        string key = KeyOf(fan.transform);
        if (!Shutdown.Add(key))
        {
            return;
        }

        CoopPlugin.LogSource.LogInfo($"[fan] shutdown at '{key}' shared with the other player.");
        CoopPlugin.Network?.SendStoryEvent(
            key,
            (byte)StoryKind.FanShutdown,
            Vector3.zero,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    private static void EnsureScene()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (string.Equals(scene, _scene, StringComparison.Ordinal))
        {
            return;
        }

        _scene = scene;
        Shutdown.Clear();
        _nextScan = 0f;
    }

    private static string KeyOf(Transform transform)
    {
        EnsureScene();

        var builder = new StringBuilder();
        AppendNode(builder, transform);

        Transform? parent = transform.parent;
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

[HarmonyPatch(typeof(rotateFan), "shutdownrotate")]
internal static class FanShutdownPatch
{
    private static void Postfix(rotateFan __instance)
    {
        FanSync.OnShutdown(__instance);
    }
}
