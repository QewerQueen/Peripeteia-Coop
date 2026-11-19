using System;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Keeps keycard checks usable in co-op.
///
/// Key/quest items such as the projector Bulb are shared: each player carries
/// their own copy. The game's keycard stores a direct reference to one ItemInfo,
/// so a break or a late join can leave that reference pointing at an object the
/// local player no longer owns. Before FindItem runs we rebind the reference to
/// an equivalent item that is actually in the local inventory. If the check
/// still fails, the shared dialogue lock is released so the other player can
/// try the same conversation instead of both being stuck on a dead branch.
/// </summary>
internal static class KeycardSync
{
    private static bool _wasPassed;
    private static bool _wasFailed;

    public static void BeginCheck(keycard card)
    {
        _wasPassed = GetFlag(card, "hasPassedAlready");
        _wasFailed = GetFlag(card, "hasFailedAlready");
    }

    public static void PrepareCheck(keycard card)
    {
        if (card == null ||
            CoopPlugin.Settings.Role == CoopRole.Offline ||
            !CoopPlugin.ItemsShareKeys)
        {
            return;
        }

        try
        {
            ItemInfo? key = card.key;
            if (key != null && key.gameObject != null &&
                card.inv != null && card.inv.itemsInInventory != null &&
                card.inv.itemsInInventory.Contains(key))
            {
                return;
            }

            string? name = key != null ? key.ItemName : null;
            if (string.IsNullOrEmpty(name) ||
                card.inv == null || card.inv.itemsInInventory == null)
            {
                return;
            }

            ItemInfo? replacement = null;
            foreach (ItemInfo candidate in card.inv.itemsInInventory)
            {
                if (candidate == null || candidate.gameObject == null)
                {
                    continue;
                }

                if (string.Equals(candidate.ItemName, name, StringComparison.Ordinal))
                {
                    replacement = candidate;
                    break;
                }
            }

            if (replacement == null)
            {
                return;
            }

            card.key = replacement;
            CoopPlugin.LogSource.LogInfo(
                $"[items] keycard key '{name}' rebound to the local copy before FindItem."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[items] keycard check preparation failed: {exception.GetType().Name}: " +
                exception.Message
            );
        }
    }

    public static void EndCheck(keycard card)
    {
        if (card == null || CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        bool passed = GetFlag(card, "hasPassedAlready");
        bool failed = GetFlag(card, "hasFailedAlready");
        if (passed || !failed || _wasFailed)
        {
            return;
        }

        StorySync.OnKeyItemCheckFailed(card);
    }

    private static bool GetFlag(keycard card, string field)
    {
        try
        {
            return Traverse.Create(card).Field(field).GetValue<bool>();
        }
        catch (Exception)
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(keycard), "FindItem")]
internal static class KeycardFindItemPatch
{
    private static void Prefix(keycard __instance)
    {
        KeycardSync.BeginCheck(__instance);
        KeycardSync.PrepareCheck(__instance);
    }

    private static void Postfix(keycard __instance)
    {
        KeycardSync.EndCheck(__instance);
    }
}