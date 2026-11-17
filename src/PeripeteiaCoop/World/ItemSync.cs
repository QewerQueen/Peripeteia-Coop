using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeripeteiaCoop;

/// <summary>
/// Prototype world-item replication.
///
/// Both peers poll the ItemInfo objects that are lying in the current scene and
/// broadcast position, rotation and visibility changes. A logical item is
/// identified by the hierarchy path of its original ItemInfo; inventory copies
/// (ItemInfo.originalItem) are mapped back to that original. This lets a pickup
/// hide the item on the other side and a later drop bring it back without
/// transferring the whole inventory system yet.
///
/// This is intentionally peer-to-peer for the prototype: the host is not yet
/// authoritative for item interactions. If both players manipulate the same
/// object at the same moment, the last state wins.
/// </summary>
internal static class ItemSync
{
    private sealed class ItemState
    {
        public ItemState(
            bool alive,
            bool active,
            Vector3 position,
            Quaternion rotation,
            bool moving
        )
        {
            Alive = alive;
            Active = active;
            Position = position;
            Rotation = rotation;
            Moving = moving;
        }

        public bool Alive { get; }

        public bool Active { get; }

        public Vector3 Position { get; }

        public Quaternion Rotation { get; }

        /// <summary>
        /// True while the owner is actively dragging this item. Remote peers
        /// temporarily make the body kinematic, otherwise teleporting a dynamic
        /// rigidbody into a wall/floor creates a large collision impulse and the
        /// item visibly shoots away.
        /// </summary>
        public bool Moving { get; }

        public bool NearlyEquals(ItemState other)
        {
            if (Alive != other.Alive || Active != other.Active || Moving != other.Moving)
            {
                return false;
            }

            if (!Active)
            {
                return true;
            }

            return (Position - other.Position).sqrMagnitude < 0.0025f &&
                   Quaternion.Angle(Rotation, other.Rotation) < 0.5f;
        }
    }

    private sealed class RemoteBodyState
    {
        public RemoteBodyState(Rigidbody body, bool wasKinematic)
        {
            Body = body;
            WasKinematic = wasKinematic;
        }

        public Rigidbody Body { get; }

        public bool WasKinematic { get; }
    }

    internal enum ItemEventKind : byte
    {
        Pickup = 0,
        Drop = 1,
    }

    private static readonly List<ItemInfo> KnownItems = new();
    private static readonly Dictionary<ItemInfo, string> KeysByItem = new();
    private static readonly Dictionary<string, ItemState> LastSent = new();
    private static readonly HashSet<string> PreviouslyKnownKeys = new();
    private static readonly HashSet<string> SeenActiveKeys = new();
    private static readonly HashSet<string> LocalBusyKeys = new();
    private static readonly Dictionary<string, RemoteBodyState> RemoteBodies = new();

    private static Transform? _localPlayer;
    private static ObjectHighlight? _localHighlight;
    private static string _scene = string.Empty;
    private static float _nextPollTime;
    private static float _nextRefreshTime;
    private static float _nextLogTime;
    private static int _loggedCount = -1;
    private static int _sentChanges;
    private static int _appliedChanges;
    private static int _eventsSent;
    private static int _eventsApplied;

    public static void Register(ItemInfo? item)
    {
        if (item == null || item.gameObject == null)
        {
            return;
        }

        EnsureScene();
        if (!IsSceneObject(item) || IsHeldOrNpcItem(item))
        {
            return;
        }

        if (!KnownItems.Contains(item))
        {
            KnownItems.Add(item);
        }

        // A dropped object is often a fresh clone with its own hierarchy path.
        // Map it back to the hidden original so both peers agree on the logical
        // item instead of leaving the original hidden forever.
        TryAliasDroppedClone(item);

        // Cache keys early. The path is stable while the item lies in the
        // world; inventory copies keep pointing at the original world item.
        GetKey(item);
    }

    public static void LogDropped(ItemInfo? item)
    {
        if (!CoopPlugin.ProbeEnabled || item == null || item.gameObject == null)
        {
            return;
        }

        string original = item.originalItem != null
            ? BuildKey(item.originalItem)
            : "<null>";
        string inventoryCopy = item.InventoryCopy != null
            ? BuildKeyOfTransform(item.InventoryCopy)
            : "<null>";

        CoopPlugin.LogSource.LogInfo(
            $"[items] dropped object='{item.gameObject.name}' item='{item.ItemName}' " +
            $"original='{original}' invCopy='{inventoryCopy}' " +
            $"active={item.gameObject.activeInHierarchy} " +
            $"path='{BuildKey(item)}'."
        );
    }

    private static string BuildKeyOfTransform(Transform transform)
    {
        var builder = new StringBuilder(160);
        Transform? current = transform;

        while (current != null)
        {
            builder.Insert(0, $"/{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        return builder.ToString();
    }

    private static bool IsWorldCloneCandidate(ItemInfo item)
    {
        if (IsHeldOrNpcItem(item))
        {
            return false;
        }

        // A dropped item normally sits directly under ItemParent or another
        // world container. If a clone has another ItemInfo above it, it is a
        // magazine/attachment spawned by that weapon, not a separate pickup.
        Transform? current = item.transform.parent;
        while (current != null)
        {
            if (current.GetComponent<ItemInfo>() != null)
            {
                return false;
            }

            string name = current.name;
            if (name.IndexOf("EnemyWeapon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("WeaponChestPivot", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            current = current.parent;
        }

        return true;
    }

    private static void TryAliasDroppedClone(ItemInfo item)
    {
        if (KeysByItem.ContainsKey(item))
        {
            return;
        }

        string objectName = item.gameObject.name;
        if (!objectName.EndsWith("(Clone)", StringComparison.Ordinal))
        {
            return;
        }

        // A clone can also be a magazine, attachment or an NPC-held weapon
        // spawned under another item. Those are parts of a weapon/NPC, not
        // separate world pickups. Aliasing them to a nearby item made the
        // original disappear and produced the "item flew away" reports.
        if (!IsWorldCloneCandidate(item))
        {
            return;
        }

        string baseName = objectName.Substring(0, objectName.Length - "(Clone)".Length).Trim();
        ItemInfo? match = null;

        // Prefer an explicit inventory-copy link: the game moves that copy out
        // of the inventory when the item is dropped.
        foreach (ItemInfo candidate in KnownItems)
        {
            if (candidate == null || candidate == item || candidate.gameObject == null)
            {
                continue;
            }

            if (candidate.InventoryCopy != null &&
                (candidate.InventoryCopy == item.transform ||
                 item.transform.IsChildOf(candidate.InventoryCopy)))
            {
                match = candidate;
                break;
            }
        }

        if (match == null)
        {
            int exactMatches = 0;
            foreach (ItemInfo candidate in KnownItems)
            {
                if (candidate == null || candidate == item || candidate.gameObject == null)
                {
                    continue;
                }

                if (IsHeldOrNpcItem(candidate))
                {
                    continue;
                }

                if (!string.Equals(candidate.gameObject.name, baseName, StringComparison.Ordinal) &&
                    !string.Equals(candidate.ItemName, item.ItemName, StringComparison.Ordinal))
                {
                    continue;
                }

                string candidateKey = GetKey(candidate);
                bool hidden = !candidate.gameObject.activeInHierarchy ||
                              (LastSent.TryGetValue(candidateKey, out ItemState? state) &&
                               !state.Active);
                if (!hidden)
                {
                    continue;
                }

                exactMatches++;
                match = candidate;
            }

            // Ambiguous names are worse than no alias: we would hide/show the
            // wrong Makarov. Keep the new key until the game gives us a link.
            if (exactMatches > 1)
            {
                match = null;
            }
        }

        if (match == null)
        {
            return;
        }

        string key = GetKey(match);
        KeysByItem[item] = key;
        CoopPlugin.LogSource.LogInfo(
            $"[items] drop alias '{item.gameObject.name}' -> '{key}'."
        );
    }

    public static void Tick(Transform? localPlayer)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        _localPlayer = localPlayer;
        EnsureScene();
        RefreshLocalBusy();

        float now = Time.unscaledTime;
        if (now >= _nextRefreshTime)
        {
            _nextRefreshTime = now + 2.0f;
            Refresh();
        }

        if (now < _nextPollTime)
        {
            return;
        }

        // Dragging needs a higher update rate; idle items can be polled slowly
        // to keep the prototype's bandwidth low (the scene has hundreds).
        _nextPollTime = now + (LocalBusyKeys.Count > 0 ? 0.1f : 0.25f);
        PollAndSend(now);
    }

    public static bool Apply(
        string key,
        bool alive,
        bool active,
        Vector3 position,
        Quaternion rotation,
        bool moving
    )
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline ||
            string.IsNullOrEmpty(key) ||
            !key.StartsWith(_scene + "|", StringComparison.Ordinal))
        {
            return false;
        }

        // Never overwrite an item the local player is currently holding or
        // dragging. The local physics/joint is the authority for that object;
        // applying a remote transform to it is what made items fly away.
        if (LocalBusyKeys.Contains(key))
        {
            return false;
        }

        List<ItemInfo>? instances = FindInstances(key);
        if (instances == null)
        {
            // The item may have been registered a fraction after the packet.
            Refresh();
            instances = FindInstances(key);
            if (instances == null)
            {
                if (CoopPlugin.ProbeEnabled)
                {
                    CoopPlugin.LogSource.LogInfo(
                        $"[items] ignoring remote state for unknown key '{key}'."
                    );
                }

                return false;
            }
        }

        if (!HasSyncableInstance(instances))
        {
            // NPC weapons and children of the local player use hierarchy paths
            // that also look like item keys. They are owned by another system;
            // accepting their state here created an endless hide/show feedback.
            return false;
        }

        var remote = new ItemState(alive, active, position, rotation, moving);
        _appliedChanges++;

        if (!alive || !active)
        {
            EndRemoteMove(key);
            SetWorldInstancesActive(instances, false);
            LastSent[key] = remote;
            LogApplied(key, alive ? "hidden" : "removed");
            return true;
        }

        ItemInfo? target = FindBestWorldTarget(instances);
        if (target == null)
        {
            // The local instance is currently in the inventory or in another
            // scene. Keep the remote state cached; a later refresh can show it.
            EndRemoteMove(key);
            LastSent[key] = remote;
            if (CoopPlugin.ProbeEnabled)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[items] no world target yet for key='{key}'."
                );
            }

            return true;
        }

        if (IsHeldOrNpcItem(target))
        {
            // A late packet for an item that has just been picked up locally.
            return false;
        }

        bool wasActive = target.gameObject.activeSelf;
        if (!wasActive)
        {
            target.gameObject.SetActive(true);
            // A dropped item may have had collision disabled while it was
            // carried. Re-enable it so both sides can interact with it.
            try
            {
                target.setCollisionOn();
            }
            catch (Exception)
            {
                // Older items may not implement collision setup; ignoring this
                // is harmless for the visual prototype.
            }
        }

        bool wasMoving = LastSent.TryGetValue(key, out ItemState? previous) && previous.Moving;

        if (moving)
        {
            BeginRemoteMove(key, target);
        }

        // While an item is being dragged, snap every update to keep the two
        // views together. For free-standing physics objects only correct large
        // deviations: constantly teleporting them creates jitter and impulses.
        bool force = !wasActive || moving || wasMoving;
        if (force || NeedsMove(target, position, rotation))
        {
            ApplyTransform(target, position, rotation);
        }

        if (!moving && wasMoving)
        {
            // The remote player released the item. Restore the local body so it
            // falls/collides normally from the final synchronized position.
            EndRemoteMove(key);
            ApplyTransform(target, position, rotation);
        }

        // Deactivate duplicate world copies (for example an inventory copy that
        // was placed in the scene by the other player). Inventory copies are
        // left alone because they live in DontDestroyOnLoad.
        foreach (ItemInfo instance in instances)
        {
            if (instance == null || instance == target)
            {
                continue;
            }

            if (IsWorldInstance(instance))
            {
                instance.gameObject.SetActive(false);
            }
        }

        LastSent[key] = remote;
        LogApplied(key, wasActive ? "moved" : "shown");
        return true;
    }

    public static void SendSnapshot()
    {
        if (CoopPlugin.Network == null || CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, ItemState> pair in LastSent)
        {
            if (pair.Value.Alive && SeenActiveKeys.Contains(pair.Key))
            {
                CoopPlugin.Network.SendItemState(
                    pair.Key,
                    true,
                    pair.Value.Active,
                    pair.Value.Position,
                    pair.Value.Rotation,
                    pair.Value.Moving,
                    reliable: true
                );
            }
        }
    }

    /// <summary>
    /// Announces an immediate pickup so the other side hides the item without
    /// waiting for the next poll.
    /// </summary>
    public static void OnLocalPickup(ItemInfo? item)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline || item == null || item.gameObject == null)
        {
            return;
        }

        // After a pickup the item lives under the player, so only the NPC test
        // applies here; the hierarchy key still resolves to the world original.
        if (IsNpcItem(item))
        {
            return;
        }

        string key = GetKey(item);
        if (!key.StartsWith(_scene + "|", StringComparison.Ordinal))
        {
            return;
        }

        _eventsSent++;
        CoopPlugin.Network?.SendItemEvent(
            key,
            (byte)ItemEventKind.Pickup,
            item.transform.position,
            item.transform.rotation,
            item.ItemName ?? string.Empty,
            TemplateNameOf(item)
        );

        CoopPlugin.LogSource.LogInfo(
            $"[items] pickup event key='{key}' item='{item.ItemName}'."
        );
    }

    /// <summary>
    /// Announces an immediate drop with the final world transform and the item
    /// identity, so the other player can pick the item up (or spawn it when the
    /// object does not exist locally).
    /// </summary>
    public static void OnLocalDrop(ItemInfo? item)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline || item == null || item.gameObject == null)
        {
            return;
        }

        // Make sure a freshly dropped clone is aliased to the hidden original
        // before the key is computed, so both peers agree on the logical item.
        Register(item);

        if (IsNpcItem(item))
        {
            return;
        }

        string key = GetKey(item);
        if (!key.StartsWith(_scene + "|", StringComparison.Ordinal))
        {
            return;
        }

        _eventsSent++;
        CoopPlugin.Network?.SendItemEvent(
            key,
            (byte)ItemEventKind.Drop,
            item.transform.position,
            item.transform.rotation,
            item.ItemName ?? string.Empty,
            TemplateNameOf(item)
        );

        CoopPlugin.LogSource.LogInfo(
            $"[items] drop event key='{key}' item='{item.ItemName}' " +
            $"pos={item.transform.position}."
        );
    }

    /// <summary>Applies a pickup/drop event received from another player.</summary>
    public static void ApplyEvent(
        string key,
        byte kind,
        Vector3 position,
        Quaternion rotation,
        string itemName,
        string templateName
    )
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline || string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!key.StartsWith(_scene + "|", StringComparison.Ordinal))
        {
            return;
        }

        // The local player owns whatever is in their hands right now.
        if (LocalBusyKeys.Contains(key))
        {
            return;
        }

        _eventsApplied++;

        if (kind == (byte)ItemEventKind.Pickup)
        {
            Apply(key, true, false, Vector3.zero, Quaternion.identity, false);
            return;
        }

        List<ItemInfo>? instances = FindInstances(key);
        if (instances == null)
        {
            Refresh();
            instances = FindInstances(key);
        }

        if (instances == null)
        {
            if (CoopPlugin.ItemsSpawnMissing)
            {
                TrySpawnDropped(key, position, rotation, itemName, templateName);
            }
            else if (CoopPlugin.ProbeEnabled)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[items] drop for unknown key '{key}' item='{itemName}' " +
                    "(Items.SpawnMissing is off)."
                );
            }

            return;
        }

        Apply(key, true, true, position, rotation, false);
    }

    private static string TemplateNameOf(ItemInfo item)
    {
        ItemInfo root = FollowOriginal(item);
        if (root != null && root.gameObject != null)
        {
            return root.gameObject.name;
        }

        return item.gameObject.name;
    }

    /// <summary>
    /// Experimental: recreates a dropped item that never existed on this side by
    /// cloning a matching template from the scene. Enabled with
    /// Items.SpawnMissing; off by default because a badly matched template can
    /// spawn the wrong object.
    /// </summary>
    private static void TrySpawnDropped(
        string key,
        Vector3 position,
        Quaternion rotation,
        string itemName,
        string templateName
    )
    {
        try
        {
            ItemInfo? template = FindSpawnTemplate(itemName, templateName);
            if (template == null || template.gameObject == null)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[items] no template for dropped item '{itemName}' (key '{key}')."
                );
                return;
            }

            GameObject spawn = UnityEngine.Object.Instantiate(
                template.gameObject,
                position,
                rotation
            );
            spawn.name = StripCloneSuffix(template.gameObject.name) + " (CoopDrop)";
            spawn.SetActive(true);

            ItemInfo? spawned = spawn.GetComponentInChildren<ItemInfo>();
            if (spawned == null)
            {
                UnityEngine.Object.Destroy(spawn);
                return;
            }

            KeysByItem[spawned] = key;
            if (!KnownItems.Contains(spawned))
            {
                KnownItems.Add(spawned);
            }

            LastSent[key] = new ItemState(true, true, position, rotation, false);
            PreviouslyKnownKeys.Add(key);
            SeenActiveKeys.Add(key);

            CoopPlugin.LogSource.LogInfo(
                $"[items] spawned dropped item '{itemName}' for key '{key}'."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[items] failed to spawn dropped item: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static ItemInfo? FindSpawnTemplate(string itemName, string templateName)
    {
        string wantedTemplate = StripCloneSuffix(templateName);
        ItemInfo? fallback = null;

        foreach (ItemInfo candidate in Resources.FindObjectsOfTypeAll<ItemInfo>())
        {
            if (candidate == null || candidate.gameObject == null || IsHeldOrNpcItem(candidate))
            {
                continue;
            }

            string objectName = StripCloneSuffix(candidate.gameObject.name);
            if (!string.IsNullOrEmpty(wantedTemplate) &&
                string.Equals(objectName, wantedTemplate, StringComparison.Ordinal))
            {
                return candidate;
            }

            if (fallback == null &&
                !string.IsNullOrEmpty(itemName) &&
                string.Equals(candidate.ItemName, itemName, StringComparison.Ordinal))
            {
                fallback = candidate;
            }
        }

        return fallback;
    }

    private static string StripCloneSuffix(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        const string suffix = "(Clone)";
        return name.EndsWith(suffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - suffix.Length).Trim()
            : name;
    }

    private static void PollAndSend(float now)
    {
        PruneDestroyed();
        RefreshIfEmpty();

        var groups = new Dictionary<string, List<ItemInfo>>();
        foreach (ItemInfo item in KnownItems)
        {
            if (item == null || IsHeldOrNpcItem(item))
            {
                continue;
            }

            string key = GetKey(item);
            bool activeWorld = IsWorldInstance(item);
            if (activeWorld)
            {
                SeenActiveKeys.Add(key);
            }
            else if (!SeenActiveKeys.Contains(key))
            {
                // Inactive objects at the scene root are usually inventory
                // originals created by the save adapter. They were never lying
                // in the world, and broadcasting them as "hidden" made real
                // weapons disappear on the other side.
                continue;
            }

            if (!groups.TryGetValue(key, out List<ItemInfo>? list))
            {
                list = new List<ItemInfo>();
                groups[key] = list;
            }

            list.Add(item);
        }

        var staleKeys = new List<string>();
        foreach (KeyValuePair<string, ItemState> pair in LastSent)
        {
            if (!pair.Value.Alive || groups.ContainsKey(pair.Key))
            {
                continue;
            }

            // Only announce disappearance for keys that this peer actually
            // owned. Remote keys can otherwise be relayed back and forth as
            // fake removals, which made NPC weapons vanish on the other side.
            if (!PreviouslyKnownKeys.Contains(pair.Key))
            {
                staleKeys.Add(pair.Key);
                continue;
            }

            CoopPlugin.Network?.SendItemState(
                pair.Key,
                false,
                false,
                Vector3.zero,
                Quaternion.identity,
                false,
                reliable: true
            );
            LastSent[pair.Key] = new ItemState(
                false,
                false,
                Vector3.zero,
                Quaternion.identity,
                false
            );
            PreviouslyKnownKeys.Remove(pair.Key);
            SeenActiveKeys.Remove(pair.Key);
            CoopPlugin.LogSource.LogInfo($"[items] item disappeared key='{pair.Key}'.");
        }

        foreach (string staleKey in staleKeys)
        {
            LastSent.Remove(staleKey);
        }

        foreach (KeyValuePair<string, List<ItemInfo>> group in groups)
        {
            if (!group.Key.StartsWith(_scene + "|", StringComparison.Ordinal))
            {
                continue;
            }

            ItemState state = Describe(group.Value);
            bool changed = !LastSent.TryGetValue(group.Key, out ItemState? previous) ||
                           !state.NearlyEquals(previous);
            if (!changed)
            {
                continue;
            }

            bool visibilityChanged = previous == null ||
                                     previous.Alive != state.Alive ||
                                     previous.Active != state.Active;
            LastSent[group.Key] = state;
            PreviouslyKnownKeys.Add(group.Key);
            _sentChanges++;
            CoopPlugin.Network?.SendItemState(
                group.Key,
                state.Alive,
                state.Active,
                state.Position,
                state.Rotation,
                state.Moving,
                reliable: visibilityChanged
            );
        }

        if (KnownItems.Count != _loggedCount && now >= _nextLogTime)
        {
            _loggedCount = KnownItems.Count;
            _nextLogTime = now + 3.0f;
            CoopPlugin.LogSource.LogInfo(
                $"[items] known={KnownItems.Count} groups={groups.Count} scene='{_scene}' " +
                $"sent={_sentChanges} applied={_appliedChanges}."
            );
        }
    }

    private static ItemState Describe(List<ItemInfo> instances)
    {
        ItemInfo? world = null;
        bool anyAlive = false;

        foreach (ItemInfo item in instances)
        {
            if (item == null || item.gameObject == null || IsHeldOrNpcItem(item))
            {
                continue;
            }

            anyAlive = true;
            if (IsWorldInstance(item))
            {
                world = item;
                break;
            }
        }

        if (world != null)
        {
            string key = GetKey(world);
            return new ItemState(
                true,
                true,
                world.transform.position,
                world.transform.rotation,
                LocalBusyKeys.Contains(key)
            );
        }

        return new ItemState(anyAlive, false, Vector3.zero, Quaternion.identity, false);
    }

    private static bool HasSyncableInstance(List<ItemInfo> instances)
    {
        foreach (ItemInfo item in instances)
        {
            if (item != null && !IsHeldOrNpcItem(item))
            {
                return true;
            }
        }

        return false;
    }

    private static ItemInfo? FindBestWorldTarget(List<ItemInfo> instances)
    {
        foreach (ItemInfo item in instances)
        {
            if (item != null && !IsHeldOrNpcItem(item) && IsWorldInstance(item))
            {
                return item;
            }
        }

        // Picked-up items are inactive; if one of their objects still belongs
        // to the active scene, it is safe to reactivate for a remote drop.
        foreach (ItemInfo item in instances)
        {
            if (item == null || item.gameObject == null || IsHeldOrNpcItem(item))
            {
                continue;
            }

            string scene = item.gameObject.scene.name;
            if (string.Equals(scene, _scene, StringComparison.Ordinal) &&
                !IsPlayerChild(item))
            {
                return item;
            }
        }

        return null;
    }

    private static bool IsWorldInstance(ItemInfo item)
    {
        if (item == null || item.gameObject == null)
        {
            return false;
        }

        return item.gameObject.activeInHierarchy &&
               string.Equals(item.gameObject.scene.name, _scene, StringComparison.Ordinal) &&
               !IsHeldOrNpcItem(item);
    }

    private static bool IsPlayerChild(ItemInfo item)
    {
        return _localPlayer != null && item.transform.IsChildOf(_localPlayer);
    }

    /// <summary>
    /// Weapons in NPC hands are also ItemInfo objects, but their transforms
    /// follow the AI skeleton every frame. They are gameplay state of the NPC,
    /// not shared pickups, so replicating their transforms would fight the
    /// animation and create an endless stream of item packets.
    /// </summary>
    private static bool IsHeldOrNpcItem(ItemInfo item)
    {
        return IsNpcItem(item) || IsPlayerChild(item);
    }

    /// <summary>
    /// True for items owned by an NPC (weapon slots, enemy inventories). This is
    /// checked without the "is a child of the local player" test so a pickup can
    /// still be announced after the item has moved into the player's hands.
    /// </summary>
    private static bool IsNpcItem(ItemInfo item)
    {
        if (item == null)
        {
            return true;
        }

        if (item.hiddenInventoryContainer != null)
        {
            return true;
        }

        // The generic parent components are not always present on the same
        // object on IL2CPP builds, but NPC weapon slots have stable names.
        // This catches guns and props carried by an AI character.
        Transform? current = item.transform;
        while (current != null)
        {
            string name = current.name;
            if (string.Equals(name, "EnemyWeaponSlot", StringComparison.Ordinal) ||
                string.Equals(name, "EnemyInventory", StringComparison.Ordinal) ||
                string.Equals(name, "EnemyWeaponPivot", StringComparison.Ordinal) ||
                string.Equals(name, "WeaponChestPivot", StringComparison.Ordinal))
            {
                return true;
            }

            current = current.parent;
        }

        if (item.transform.GetComponentInParent<AIController>() != null)
        {
            return true;
        }

        if (item.transform.GetComponentInParent<EnemyInventory>() != null)
        {
            return true;
        }

        return false;
    }

    private static void RefreshLocalBusy()
    {
        LocalBusyKeys.Clear();

        if (_localPlayer == null)
        {
            return;
        }

        ObjectHighlight? highlight = FindLocalHighlight();
        if (highlight != null)
        {
            AddBusy(highlight.objectDragged);
            AddBusy(highlight.attached);
        }

        PlayerMovement? movement = _localPlayer.GetComponent<PlayerMovement>();
        if (movement == null)
        {
            movement = _localPlayer.GetComponentInParent<PlayerMovement>();
        }

        AddBusy(movement?.holdingTo);
    }

    private static ObjectHighlight? FindLocalHighlight()
    {
        if (_localHighlight == null)
        {
            _localHighlight = UnityEngine.Object.FindObjectOfType<ObjectHighlight>();
        }

        return _localHighlight;
    }

    private static void AddBusy(Component? component)
    {
        ItemInfo? item = FindItem(component);
        if (item != null)
        {
            LocalBusyKeys.Add(GetKey(item));
        }
    }

    private static ItemInfo? FindItem(Component? component)
    {
        if (component == null)
        {
            return null;
        }

        ItemInfo? item = component.GetComponentInParent<ItemInfo>();
        if (item == null)
        {
            item = component.GetComponentInChildren<ItemInfo>();
        }

        return item;
    }

    private static Rigidbody? GetBody(ItemInfo item)
    {
        if (item.attachedRB != null)
        {
            return item.attachedRB;
        }

        Rigidbody? body = item.GetComponent<Rigidbody>();
        if (body == null)
        {
            body = item.GetComponentInChildren<Rigidbody>();
        }

        return body;
    }

    private static void BeginRemoteMove(string key, ItemInfo target)
    {
        Rigidbody? body = GetBody(target);
        if (body == null)
        {
            return;
        }

        if (RemoteBodies.TryGetValue(key, out RemoteBodyState? existing))
        {
            if (existing.Body == body)
            {
                return;
            }

            EndRemoteMove(key);
        }

        RemoteBodies[key] = new RemoteBodyState(body, body.isKinematic);
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.isKinematic = true;
    }

    private static void EndRemoteMove(string key)
    {
        if (!RemoteBodies.TryGetValue(key, out RemoteBodyState? state))
        {
            return;
        }

        RemoteBodies.Remove(key);

        if (state.Body == null)
        {
            return;
        }

        state.Body.isKinematic = state.WasKinematic;
        state.Body.velocity = Vector3.zero;
        state.Body.angularVelocity = Vector3.zero;
    }

    private static void EndAllRemoteMoves()
    {
        foreach (string key in new List<string>(RemoteBodies.Keys))
        {
            EndRemoteMove(key);
        }
    }

    private static void ApplyTransform(ItemInfo target, Vector3 position, Quaternion rotation)
    {
        Rigidbody? body = GetBody(target);
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        target.transform.position = position;
        target.transform.rotation = rotation;

        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    private static bool NeedsMove(ItemInfo target, Vector3 position, Quaternion rotation)
    {
        return (target.transform.position - position).sqrMagnitude > 0.09f ||
               Quaternion.Angle(target.transform.rotation, rotation) > 6.0f;
    }

    private static void SetWorldInstancesActive(List<ItemInfo> instances, bool active)
    {
        foreach (ItemInfo item in instances)
        {
            if (item != null && IsWorldInstance(item) && item.gameObject.activeSelf != active)
            {
                item.gameObject.SetActive(active);
            }
        }
    }

    private static List<ItemInfo>? FindInstances(string key)
    {
        var result = new List<ItemInfo>();
        foreach (ItemInfo item in KnownItems)
        {
            if (item != null && string.Equals(GetKey(item), key, StringComparison.Ordinal))
            {
                result.Add(item);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static string GetKey(ItemInfo item)
    {
        if (KeysByItem.TryGetValue(item, out string? cached))
        {
            return cached;
        }

        ItemInfo root = FollowOriginal(item);
        if (root != item)
        {
            Register(root);
        }

        string key = BuildKey(root);
        KeysByItem[item] = key;
        return key;
    }

    private static ItemInfo FollowOriginal(ItemInfo item)
    {
        ItemInfo current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            ItemInfo? original = current.originalItem;
            if (original == null || original == current)
            {
                break;
            }

            current = original;
        }

        return current;
    }

    private static string BuildKey(ItemInfo item)
    {
        var builder = new StringBuilder(160);
        Transform? current = item.transform;

        while (current != null)
        {
            builder.Insert(0, $"/{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        string scene = item.gameObject.scene.name;
        if (string.IsNullOrEmpty(scene))
        {
            scene = SceneManager.GetActiveScene().name;
        }

        return $"{scene}|{builder}";
    }

    private static void Refresh()
    {
        foreach (ItemInfo item in Resources.FindObjectsOfTypeAll<ItemInfo>())
        {
            if (item == null || item.gameObject == null || !IsSceneObject(item))
            {
                continue;
            }

            Register(item);
        }

        PruneDestroyed();
    }

    private static void RefreshIfEmpty()
    {
        if (KnownItems.Count == 0)
        {
            Refresh();
        }
    }

    private static void PruneDestroyed()
    {
        for (int index = KnownItems.Count - 1; index >= 0; index--)
        {
            if (KnownItems[index] == null)
            {
                KnownItems.RemoveAt(index);
            }
        }

        // KeysByItem holds Unity object keys, so stale wrappers must not keep
        // accumulating over a long session.
        if (KeysByItem.Count > KnownItems.Count * 4 + 256)
        {
            KeysByItem.Clear();
            foreach (ItemInfo item in KnownItems)
            {
                if (item != null)
                {
                    GetKey(item);
                }
            }
        }
    }

    private static bool IsSceneObject(ItemInfo item)
    {
        return item.gameObject.scene.IsValid() && !string.IsNullOrEmpty(item.gameObject.scene.name);
    }

    private static void LogApplied(string key, string action)
    {
        // Movement packets are frequent while an item is dragged; log only the
        // useful visibility changes and a periodic summary.
        if (!string.Equals(action, "moved", StringComparison.Ordinal) &&
            (_appliedChanges <= 20 || _appliedChanges % 25 == 0))
        {
            CoopPlugin.LogSource.LogInfo($"[items] applied {action} key='{key}'.");
        }
    }

    private static void EnsureScene()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (scene == _scene)
        {
            return;
        }

        _scene = scene;
        EndAllRemoteMoves();
        KnownItems.Clear();
        KeysByItem.Clear();
        LastSent.Clear();
        PreviouslyKnownKeys.Clear();
        SeenActiveKeys.Clear();
        LocalBusyKeys.Clear();
        _localHighlight = null;
        _nextPollTime = 0.0f;
        _nextRefreshTime = 0.0f;
        _nextLogTime = 0.0f;
        _loggedCount = -1;
        _sentChanges = 0;
        _appliedChanges = 0;
    }
}

[HarmonyPatch(typeof(ItemInfo), "Start")]
internal static class ItemRegisterStartPatch
{
    private static void Postfix(ItemInfo __instance)
    {
        ItemSync.Register(__instance);
    }
}

[HarmonyPatch(typeof(ItemInfo), "onStart")]
internal static class ItemRegisterOnStartPatch
{
    private static void Postfix(ItemInfo __instance)
    {
        ItemSync.Register(__instance);
    }
}

[HarmonyPatch(typeof(ItemInfo), "onLoad")]
internal static class ItemRegisterOnLoadPatch
{
    private static void Postfix(ItemInfo __instance)
    {
        ItemSync.Register(__instance);
    }
}

[HarmonyPatch(typeof(ItemInfo), "pickupitem", new Type[] { })]
internal static class ItemPickupEventPatch
{
    private static void Postfix(ItemInfo __instance)
    {
        ItemSync.OnLocalPickup(__instance);
    }
}

[HarmonyPatch(typeof(ItemInfo), "dropOnGround", new Type[] { typeof(Vector3), typeof(Vector3), typeof(float) })]
internal static class ItemRegisterDroppedPatch
{
    private static void Postfix(ItemInfo __instance)
    {
        ItemSync.Register(__instance);
        ItemSync.LogDropped(__instance);
        ItemSync.OnLocalDrop(__instance);
    }
}

[HarmonyPatch(
    typeof(ItemInfo),
    "dropOnGround",
    new Type[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool), typeof(ObjectHighlight) }
)]
internal static class ItemRegisterDroppedDetailedPatch
{
    private static void Postfix(ItemInfo __instance)
    {
        ItemSync.Register(__instance);
        ItemSync.LogDropped(__instance);
        ItemSync.OnLocalDrop(__instance);
    }
}
