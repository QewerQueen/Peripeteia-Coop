using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Gives the remote teammate a character model that differs from the local
/// player's own.
///
/// Both peers load the same save, and the puppet is a clone of the local player,
/// so by default the teammate wears exactly your Marie skin and the two players
/// look like twins. The game keeps every Marie model under the player and only
/// enables the active one (see <c>playerSkins.playModelList</c>), so each peer
/// broadcasts its own skin index and the puppet simply toggles the matching
/// third-person model on and the others off.
///
/// This is deliberately conservative: if the model list cannot be read, or the
/// clone does not contain the requested model, the puppet is left exactly as it
/// is rather than being hidden.
/// </summary>
internal static class PlayerSkinSync
{
    private static playerSkins? _cachedSkins;
    private static bool _loggedStructure;

    /// <summary>Local player's current Marie skin index, or -1 if unavailable.</summary>
    public static int GetLocalVersion()
    {
        playerSkins? skins = GetLocalSkins();
        if (skins == null)
        {
            return -1;
        }

        try
        {
            return skins.MarieVersion;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static playerSkins? GetLocalSkins()
    {
        if (_cachedSkins != null && _cachedSkins.gameObject != null)
        {
            return _cachedSkins;
        }

        try
        {
            PlayerMovement? player = UnityEngine.Object.FindObjectOfType<PlayerMovement>();
            if (player == null)
            {
                return null;
            }

            _cachedSkins = player.GetComponent<playerSkins>() ??
                           player.GetComponentInParent<playerSkins>();
        }
        catch (Exception)
        {
            _cachedSkins = null;
        }

        return _cachedSkins;
    }

    /// <summary>
    /// Switches <paramref name="clone"/> to the given Marie skin index. Does
    /// nothing when the index is invalid or the clone lacks that model.
    /// </summary>
    public static void ApplySkin(GameObject? clone, int version)
    {
        if (clone == null || version < 0)
        {
            return;
        }

        playerSkins? skins = GetLocalSkins();
        if (skins == null)
        {
            return;
        }

        var list = skins.playModelList;
        if (list == null || list.Length == 0)
        {
            return;
        }

        LogStructureOnce(skins, list);

        if (version >= list.Length)
        {
            return;
        }

        var target = new HashSet<string>(StringComparer.Ordinal);
        var others = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < list.Length; i++)
        {
            GameObject[]? models = list[i].thirdPersonModels;
            if (models == null)
            {
                continue;
            }

            foreach (GameObject? model in models)
            {
                if (model == null)
                {
                    continue;
                }

                if (i == version)
                {
                    target.Add(model.name);
                }
                else
                {
                    others.Add(model.name);
                }
            }
        }

        if (target.Count == 0)
        {
            return;
        }

        // Never disable a name that also belongs to the requested model.
        others.ExceptWith(target);

        Transform cloneTransform = clone.transform;
        bool hasTarget = false;
        foreach (Transform child in cloneTransform.GetComponentsInChildren<Transform>(true))
        {
            if (child != null && target.Contains(child.gameObject.name))
            {
                hasTarget = true;
                break;
            }
        }

        if (!hasTarget)
        {
            if (CoopPlugin.ProbeEnabled)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[skin] clone has no model for skin {version}; leaving the puppet unchanged."
                );
            }

            return;
        }

        int enabled = 0;
        int disabled = 0;
        foreach (Transform child in cloneTransform.GetComponentsInChildren<Transform>(true))
        {
            if (child == null)
            {
                continue;
            }

            string name = child.gameObject.name;
            if (target.Contains(name))
            {
                if (!child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(true);
                    enabled++;
                }
            }
            else if (others.Contains(name) && child.gameObject.activeSelf)
            {
                child.gameObject.SetActive(false);
                disabled++;
            }
        }

        CoopPlugin.LogSource.LogInfo(
            $"[skin] applied skin {version} to remote puppet: enabled={enabled} disabled={disabled}."
        );
    }

    private static void LogStructureOnce(playerSkins skins, playerSkins.playerModels[] list)
    {
        if (_loggedStructure || !CoopPlugin.ProbeEnabled)
        {
            return;
        }

        _loggedStructure = true;
        CoopPlugin.LogSource.LogInfo(
            $"[skin] local MarieVersion={skins.MarieVersion} models={list.Length}"
        );
        for (int i = 0; i < list.Length; i++)
        {
            GameObject[]? models = list[i].thirdPersonModels;
            var names = new List<string>();
            if (models != null)
            {
                foreach (GameObject? model in models)
                {
                    if (model != null)
                    {
                        names.Add(model.name);
                    }
                }
            }

            CoopPlugin.LogSource.LogInfo(
                $"[skin]   model[{i}] name='{list[i].name}' thirdPersonModels={names.Count} " +
                $"[{string.Join(", ", names)}]"
            );
        }
    }
}
