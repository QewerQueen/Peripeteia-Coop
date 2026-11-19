using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Shared contents for lootable containers: corpse inventories, crates and any
/// other <c>container</c> in the level (shops are deliberately excluded, because
/// buying something from a vendor is a personal action).
///
/// Without this, each machine simulates the same loot container independently,
/// so both players can take the very same rifle out of a corpse. Loot is part of
/// the shared world, so a removal is an event just like an opened door: whoever
/// takes the item announces it, and every other machine removes the same item
/// from its own copy of the container.
///
/// The game's own <c>container.removeItem</c> is used on the receiving side, so
/// grid state, the loot UI and save data stay consistent. A short-lived guard
/// stops the replay from announcing the removal a second time.
/// </summary>
internal static class ContainerSync
{
    /// <summary>Set while replaying a remote removal, to stop an echo.</summary>
    private static bool _applyingRemote;

    /// <summary>Last few locally-taken items, to avoid patching both the UI and
    /// the container method for the same single click.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, float> _recent =
        new(StringComparer.Ordinal);

    private static int _sent;
    private static int _applied;
    private static int _skippedShops;
    private static string _lastScene = string.Empty;

    public static int Sent => _sent;
    public static int Applied => _applied;

    /// <summary>Called every network tick; clears per-level state on a scene change.</summary>
    public static void Tick()
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!string.Equals(scene, _lastScene, StringComparison.Ordinal))
        {
            _lastScene = scene;
            ResetForLoad();
        }
    }

    /// <summary>
    /// Stable identity for a container across two machines that loaded the same
    /// save: scene name plus the transform path from the scene root. This is the
    /// same scheme EnemySync / ItemSync / DoorSync already use.
    /// </summary>
    public static string KeyOf(container? value)
    {
        if (value == null || value.gameObject == null)
        {
            return string.Empty;
        }

        GameObject go = value.gameObject;
        string scene = go.scene.IsValid() ? go.scene.name : "?";
        var builder = new StringBuilder(96);
        builder.Append(scene);

        Transform? current = go.transform;
        while (current != null)
        {
            builder.Append('/').Append(current.name).Append('[').Append(current.GetSiblingIndex()).Append(']');
            current = current.parent;
        }

        return builder.ToString();
    }

    private static container? Find(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (container value in UnityEngine.Object.FindObjectsOfType<container>())
        {
            if (value != null && value.gameObject != null && string.Equals(KeyOf(value), key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Called by the Harmony patches when an item leaves a container locally.
    /// Works on both roles: on a client the host applies and relays the removal;
    /// on the host it is broadcast straight to every client.
    /// </summary>
    public static void OnLocalLoot(container? owner, ItemInfo? item)
    {
        if (_applyingRemote || owner == null || item == null || owner.gameObject == null || item.gameObject == null)
        {
            return;
        }

        if (CoopPlugin.Network == null || CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        // Buying from a shop is personal: a vendor never runs out for the other
        // player, and syncing it would drain both inventories at once.
        if (owner.isShop)
        {
            _skippedShops++;
            return;
        }

        string key = KeyOf(owner);
        if (key.Length == 0)
        {
            return;
        }

        int index = -1;
        Il2CppSystem.Collections.Generic.List<ItemInfo>? contained = owner.objectsContained;
        if (contained != null)
        {
            for (int i = 0; i < contained.Count; i++)
            {
                if (ReferenceEquals(contained[i], item))
                {
                    index = i;
                    break;
                }
            }
        }

        if (index < 0 && owner.objects != null)
        {
            for (int i = 0; i < owner.objects.Count; i++)
            {
                if (ReferenceEquals(owner.objects[i], item))
                {
                    index = i;
                    break;
                }
            }
        }

        string itemName = item.ItemName ?? string.Empty;
        string dedupe = key + "|" + index + "|" + itemName;

        float now = Time.unscaledTime;
        if (_recent.TryGetValue(dedupe, out float last) && now - last < 1.5f)
        {
            return;
        }

        _recent[dedupe] = now;
        if (_recent.Count > 64)
        {
            var stale = new System.Collections.Generic.List<string>();
            foreach (var pair in _recent)
            {
                if (now - pair.Value > 5.0f)
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (string old in stale)
            {
                _recent.Remove(old);
            }
        }

        _sent++;
        CoopPlugin.LogSource.LogInfo(
            $"[loot] took '{itemName}' #{index} from '{key}' (shop={owner.isShop})."
        );
        CoopPlugin.Network.SendContainerLoot(key, index, itemName);
    }

    /// <summary>Replays a removal received from either peer.</summary>
    public static void ApplyLoot(string key, int index, string itemName)
    {
        container? owner = Find(key);
        if (owner == null)
        {
            CoopPlugin.LogSource.LogWarning($"[loot] container '{key}' not found; ignored.");
            return;
        }

        if (owner.isShop)
        {
            return;
        }

        ItemInfo? item = null;
        Il2CppSystem.Collections.Generic.List<ItemInfo>? contained = owner.objectsContained;
        if (contained != null && index >= 0 && index < contained.Count)
        {
            ItemInfo? candidate = contained[index];
            if (candidate != null && string.Equals(candidate.ItemName, itemName, StringComparison.OrdinalIgnoreCase))
            {
                item = candidate;
            }
        }

        if (item == null && contained != null)
        {
            for (int i = 0; i < contained.Count; i++)
            {
                ItemInfo? candidate = contained[i];
                if (candidate != null && string.Equals(candidate.ItemName, itemName, StringComparison.OrdinalIgnoreCase))
                {
                    item = candidate;
                    break;
                }
            }
        }

        if (item == null && owner.objects != null && index >= 0 && index < owner.objects.Count)
        {
            item = owner.objects[index];
        }

        if (item == null || item.gameObject == null)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[loot] item '{itemName}' #{index} no longer in '{key}'; ignored."
            );
            return;
        }

        _applied = 0;
        _applyingRemote = true;
        try
        {
            owner.removeItem(item);
            _applied++;
            CoopPlugin.LogSource.LogInfo(
                $"[loot] applied remote removal of '{itemName}' from '{key}'."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[loot] failed to remove '{itemName}' from '{key}': {exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    public static void ResetForLoad()
    {
        _recent.Clear();
        _applyingRemote = false;
    }
}

/// <summary>Catches the actual removal, whatever UI path the game used.</summary>
[HarmonyPatch(typeof(container), "removeItem")]
internal static class ContainerRemovePatch
{
    private static void Prefix(container __instance, ItemInfo item)
    {
        ContainerSync.OnLocalLoot(__instance, item);
    }
}

/// <summary>
/// The loot UI also removes items directly in some paths (Take All, vendor
/// screens). ContainerSync de-duplicates a click that reaches both patches.
/// </summary>
[HarmonyPatch(typeof(LootUI), "removeItem")]
internal static class LootUIRemovePatch
{
    private static void Prefix(LootUI __instance, ItemInfo item)
    {
        ContainerSync.OnLocalLoot(__instance.currentContainer, item);
    }
}
