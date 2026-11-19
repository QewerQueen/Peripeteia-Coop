using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using VIDE_Data;

namespace PeripeteiaCoop;

/// <summary>
/// Shared single-branch story: dialogues, quest objects and scripted teleports.
///
/// The design goal is a co-op campaign where the story advances once, on one
/// branch, instead of being simulated twice:
///
/// - A one-time story event (NPC dialogue, quest object, scripted teleport) is
///   consumed by whichever player reaches it first; the other player is locked
///   out of it. This is what stops quest items and rewards from being granted
///   twice (the reward stays on the player who actually spoke / delivered).
/// - World side effects of a quest object (the GameObjects it switches on and
///   off) are replayed on the other machine, so a door that opens or a bridge
///   that appears is visible to both.
/// - A teleporter that the player simply walks into is treated as repeatable and
///   stays local: each player is moved on their own machine. A teleport driven
///   by a script (not by walking in) is treated as a one-off and moves BOTH
///   players to the destination.
///
/// Inventory, money and player health stay personal; only the story/world state
/// is shared. Everything is behind the [Story] config section and is defensive:
/// a failed hook only logs, it never breaks the local frame.
/// </summary>
internal static class StorySync
{
    /// <summary>
    /// How close the local player has to be to an NPC to own the conversation it
    /// starts. A dialogue is raised by the local interaction ray, so the player is
    /// normally right next to the NPC; anything further away was caused by a
    /// remote avatar and must not consume the branch here.
    /// </summary>
    private const float DialogueReach = 8.0f;

    private static readonly HashSet<string> Consumed = new();
    private static readonly Dictionary<string, GameObject> ObjectsByKey = new();

    private static string _scene = string.Empty;
    private static PlayerMovement? _localPlayer;
    private static bool _walkInTeleport;
    private static int _eventsSent;
    private static int _eventsApplied;
    private static int _effectsApplied;
    private static bool _suppressBroadcast;
    private static bool _applyingRemote;
    private static FlagsForSaveFiles? _flagComponent;
    private static readonly Dictionary<string, bool> KnownFlags = new();
    private static int _flagsSent;
    private static int _flagsApplied;
    private static int _questEventsSent;
    private static int _questEventsApplied;
    private static string _lastCutsceneKey = string.Empty;
    private static float _lastCutsceneTime;
    private static float _lastCutsceneProbe;
    private static string _lastActionSignature = string.Empty;
    private static float _lastActionTime;
    private static string _blockedDialogueKey = string.Empty;

    public static bool Enabled => CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.StoryEnabled;

    /// <summary>
    /// True while this machine is replaying a story effect it received from the
    /// other player. Side-effect patches (an elevator call, a flag set) use this
    /// to avoid echoing the change straight back.
    /// </summary>
    public static bool IsApplyingRemote => _applyingRemote || _suppressBroadcast;

    public static int EventsSent => _eventsSent;

    public static int EventsApplied => _eventsApplied;

    public static int EffectsApplied => _effectsApplied;

    public static int FlagsSent => _flagsSent;

    public static int FlagsApplied => _flagsApplied;

    public static int QuestEventsSent => _questEventsSent;

    public static int QuestEventsApplied => _questEventsApplied;

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        _localPlayer = localPlayer;
        EnsureScene();
    }

    // ------------------------------------------------------------------
    // Dialogue
    // ------------------------------------------------------------------

    public static bool BlockDialogue(NPCConversation conversation)
    {
        _blockedDialogueKey = string.Empty;

        if (!Enabled || !CoopPlugin.StoryLockDialogues || conversation == null)
        {
            return false;
        }

        string key = KeyOf(conversation.transform);
        if (Consumed.Contains(key))
        {
            CoopPlugin.LogSource.LogInfo(
                $"[story] dialogue '{key}' is already taken by the other player; ignoring."
            );
            return true;
        }

        // The host registers the remote avatar as an AI target, so a friendly NPC
        // can react to the client before the client reaches it. A dialogue the
        // local player did not walk up to must not start (and consume the branch)
        // here; only the player actually standing at the NPC may start it.
        if (!LocalPlayerIsAt(conversation.transform))
        {
            _blockedDialogueKey = key;
            CoopPlugin.LogSource.LogInfo(
                $"[story] dialogue '{key}' started away from the local player; ignoring."
            );
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the local player is close enough to the NPC to be the one who
    /// started the conversation. Used to keep a remote avatar from starting a
    /// conversation that belongs to whoever walked up to the NPC.
    /// </summary>
    private static bool LocalPlayerIsAt(Transform npc)
    {
        Transform? local = _localPlayer != null ? _localPlayer.transform : null;
        if (local == null)
        {
            return true;
        }

        float distance = Vector3.Distance(local.position, npc.position);
        if (distance <= DialogueReach)
        {
            return true;
        }

        CoopPlugin.LogSource.LogInfo(
            $"[story] local player is {distance:0.0} m from the NPC " +
            $"(limit {DialogueReach:0.0} m)."
        );
        return false;
    }

    public static void OnDialogueStarted(NPCConversation conversation)
    {
        if (!Enabled || IsApplyingRemote || conversation == null)
        {
            return;
        }

        string key = KeyOf(conversation.transform);

        // A prefix that returns false skips the original method, but its postfix
        // still runs. Do not consume a conversation this machine just refused.
        if (!string.IsNullOrEmpty(_blockedDialogueKey) &&
            string.Equals(key, _blockedDialogueKey, StringComparison.Ordinal))
        {
            _blockedDialogueKey = string.Empty;
            return;
        }

        if (!Consumed.Add(key))
        {
            return;
        }

        _eventsSent++;
        CoopPlugin.LogSource.LogInfo($"[story] dialogue consumed: '{key}'.");
        CoopPlugin.Network?.SendStoryEvent(
            key,
            (byte)StoryKind.Dialogue,
            Vector3.zero,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    // ------------------------------------------------------------------
    // Quest objects
    // ------------------------------------------------------------------

    public static bool BlockQuestAction(QuestAction action)
    {
        if (!Enabled || !CoopPlugin.StoryLockQuestActions || action == null)
        {
            return false;
        }

        if (action.multipleUse)
        {
            return false;
        }

        string key = KeyOf(action.transform);
        if (Consumed.Contains(key))
        {
            CoopPlugin.LogSource.LogInfo(
                $"[story] quest action '{key}' is already taken by the other player; ignoring."
            );
            return true;
        }

        return false;
    }

    public static void OnQuestActionUsed(QuestAction action)
    {
        if (!Enabled || IsApplyingRemote || action == null)
        {
            return;
        }

        string key = KeyOf(action.transform);
        bool first = Consumed.Add(key);
        if (!first && !action.multipleUse)
        {
            return;
        }

        List<string> keys = new();
        List<bool> states = new();
        CollectActivations(action.activatedGameobjects, true, keys, states);
        CollectActivations(action.deactivatedGameobjects, false, keys, states);

        _eventsSent++;
        CoopPlugin.LogSource.LogInfo(
            $"[story] quest action '{key}' used (multipleUse={action.multipleUse}, " +
            $"worldChanges={keys.Count})."
        );
        LogQuestEffects(action);
        CoopPlugin.Network?.SendStoryEvent(
            key,
            (byte)StoryKind.QuestAction,
            Vector3.zero,
            keys.ToArray(),
            states.ToArray()
        );
    }

    // ------------------------------------------------------------------
    // Teleports
    // ------------------------------------------------------------------

    public static void NoteWalkInTeleport()
    {
        _walkInTeleport = true;
    }

    public static void ClearWalkInTeleport()
    {
        _walkInTeleport = false;
    }

    public static void OnTeleport(teleport source)
    {
        bool walkIn = _walkInTeleport;
        _walkInTeleport = false;

        if (!Enabled || !CoopPlugin.StorySyncTeleports || IsApplyingRemote || source == null)
        {
            return;
        }

        // Walking into a teleporter is repeatable: every player moves on their
        // own machine and nothing is shared.
        if (walkIn)
        {
            return;
        }

        Vector3 destination = source.teleportto != null
            ? source.teleportto.transform.position + source.adjustment
            : LocalPlayerPosition();

        BroadcastScriptedTeleport(KeyOf(source.transform), destination);
    }

    /// <summary>
    /// The Belgrade pendulum teleporter is driven by a script, not by walking in,
    /// so it is a one-off and must move both players. Its destination is not
    /// exposed, so the local player's position right after the move is used.
    /// </summary>
    public static void OnPuzzleTeleport(teleportpuzzlebelgrade source)
    {
        if (!Enabled || !CoopPlugin.StorySyncTeleports || IsApplyingRemote || source == null)
        {
            return;
        }

        BroadcastScriptedTeleport(KeyOf(source.transform), LocalPlayerPosition());
    }

    private static void BroadcastScriptedTeleport(string key, Vector3 destination)
    {
        _eventsSent++;
        CoopPlugin.LogSource.LogInfo(
            $"[story] scripted teleport '{key}' -> {destination} (both players)."
        );
        CoopPlugin.Network?.SendStoryEvent(
            key,
            (byte)StoryKind.Teleport,
            destination,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    private static Vector3 LocalPlayerPosition()
    {
        PlayerMovement? player = CoopPlugin.Network?.LocalPlayer;
        return player != null ? player.transform.position : Vector3.zero;
    }

    // ------------------------------------------------------------------
    // Cutscenes (video)
    // ------------------------------------------------------------------

    /// <summary>
    /// Called when a video starts playing locally. A cutscene is a video the
    /// whole party should see, so it is broadcast and replayed on the other
    /// machine. Videos that are not cutscenes (menus, UI) are ignored.
    /// </summary>
    public static void OnVideoPlayed(VideoPlayer player)
    {
        if (!Enabled || !CoopPlugin.StorySyncCutscenes || IsApplyingRemote || player == null)
        {
            return;
        }

        // Most cutscenes are owned by a cutsceneplayer, but a quest object can
        // also call VideoPlayer.Play directly on a bare video (the intro's
        // 'Cube (11)' does). In that case the video's own GameObject is the key,
        // and the other machine plays the clip it finds there.
        cutsceneplayer? cutscene = FindCutscene(player.gameObject);
        string key = cutscene != null
            ? KeyOf(cutscene.transform)
            : KeyOf(player.transform);

        if (string.IsNullOrEmpty(key))
        {
            if (Time.unscaledTime - _lastCutsceneProbe > 2.0f)
            {
                _lastCutsceneProbe = Time.unscaledTime;
                CoopPlugin.LogSource.LogInfo(
                    $"[story] video '{player.gameObject.name}' is not a scene object; not shared."
                );
            }

            return;
        }
        if (string.Equals(key, _lastCutsceneKey, StringComparison.Ordinal) &&
            Time.unscaledTime - _lastCutsceneTime < 2.0f)
        {
            return;
        }

        _lastCutsceneKey = key;
        _lastCutsceneTime = Time.unscaledTime;
        _eventsSent++;
        CoopPlugin.LogSource.LogInfo($"[story] cutscene '{key}' broadcast to the other player.");
        CoopPlugin.Network?.SendStoryEvent(
            key,
            (byte)StoryKind.Cutscene,
            Vector3.zero,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    /// <summary>
    /// Finds the cutsceneplayer that owns a video. It is not always on the same
    /// GameObject as the VideoPlayer (or one of its parents), so as a last resort
    /// every cutsceneplayer is scanned for one that references this VideoPlayer.
    /// </summary>
    private static cutsceneplayer? FindCutscene(GameObject go)
    {
        try
        {
            cutsceneplayer? direct = go.GetComponent<cutsceneplayer>();
            if (direct != null)
            {
                return direct;
            }

            cutsceneplayer? parent = go.GetComponentInParent<cutsceneplayer>();
            if (parent != null)
            {
                return parent;
            }

            VideoPlayer? player = go.GetComponent<VideoPlayer>();
            if (player == null)
            {
                player = go.GetComponentInParent<VideoPlayer>();
            }

            if (player == null)
            {
                return null;
            }

            int instanceId = player.GetInstanceID();
            foreach (cutsceneplayer candidate in Resources.FindObjectsOfTypeAll<cutsceneplayer>())
            {
                if (candidate == null || candidate.vp == null)
                {
                    continue;
                }

                if (candidate.vp.GetInstanceID() == instanceId)
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to no cutscene.
        }

        return null;
    }

    private static void PlayRemoteCutscene(string key)
    {
        GameObject? target = FindObject(key);
        if (target == null)
        {
            return;
        }

        try
        {
            cutsceneplayer? cutscene = FindCutscene(target);
            VideoPlayer? player = cutscene != null ? cutscene.vp : null;

            // A bare video (no cutsceneplayer) is played from the target itself.
            if (player == null)
            {
                player = target.GetComponent<VideoPlayer>();
            }

            if (player == null)
            {
                player = target.GetComponentInChildren<VideoPlayer>(true);
            }

            if (player == null)
            {
                player = target.GetComponentInParent<VideoPlayer>();
            }

            if (player == null)
            {
                return;
            }

            if (!target.activeSelf)
            {
                target.SetActive(true);
            }

            _suppressBroadcast = true;
            player.Play();
            CoopPlugin.LogSource.LogInfo($"[story] replayed remote cutscene '{key}'.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] cutscene replay failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _suppressBroadcast = false;
        }
    }

    // ------------------------------------------------------------------
    // Dialogue effects (quest log)
    // ------------------------------------------------------------------

    /// <summary>
    /// Called right after the VIDE dialogue runtime executes an action node.
    ///
    /// The story branch the player picked usually only updates the quest log,
    /// through <c>NPCConversation.SetQuestLog</c> / <c>RemoveQuestLog</c>. Those
    /// updates are shared so both players end up on the same branch, while
    /// actions that hand out items or rewards are deliberately never replayed:
    /// that is what keeps quest items and rewards single.
    /// </summary>
    public static void OnDialogueAction()
    {
        ActionNode? action = null;
        try
        {
            action = VD.currentActionNode;
        }
        catch (Exception)
        {
            action = null;
        }

        HandleDialogueAction(action);
    }

    public static void OnDialogueActionV2(VD2 instance)
    {
        ActionNode? action = null;
        try
        {
            action = instance != null ? instance.currentActionNode : null;
        }
        catch (Exception)
        {
            action = null;
        }

        HandleDialogueAction(action);
    }

    private static void HandleDialogueAction(ActionNode? action)
    {
        if (!Enabled || !CoopPlugin.StorySyncDialogueActions || _suppressBroadcast || action == null)
        {
            return;
        }

        string gameObjectName = action.gameObjectName ?? string.Empty;
        string method = action.methodName ?? string.Empty;
        if (string.IsNullOrEmpty(gameObjectName) || string.IsNullOrEmpty(method))
        {
            return;
        }

        // Gifts are shared where they happen (see OnGiftGiven and the GiftGiver
        // patches), not through the VIDE action stream, so a gift node must not be
        // sent from here as well or the teammate would receive the kit twice.
        if (IsSharedDialogueGiftMethod(method))
        {
            return;
        }

        if (!IsSharedDialogueMethod(method))
        {
            return;
        }

        int param = action.param_int;

        // VD and VD2 may both report the same node; send it once.
        string signature = gameObjectName + "." + method + "(" + param + ")";
        if (string.Equals(signature, _lastActionSignature, StringComparison.Ordinal) &&
            Time.unscaledTime - _lastActionTime < 1.0f)
        {
            return;
        }

        _lastActionSignature = signature;
        _lastActionTime = Time.unscaledTime;

        _eventsSent++;
        CoopPlugin.LogSource.LogInfo($"[story] dialogue effect '{signature}' shared with the other player.");
        CoopPlugin.Network?.SendStoryEvent(
            gameObjectName,
            (byte)StoryKind.DialogueAction,
            Vector3.zero,
            method,
            param,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    /// <summary>
    /// Called when a GiftGiver hands the local player a kit. In Peripeteia every
    /// starting weapon and piece of equipment (the intro Thermoptic Camo, the
    /// tutorial Makarov and its magazines, ...) is granted this way, either by a
    /// dialogue node or by a quest object. The VIDE action stream that drives the
    /// dialogue gifts is not replayed, so the gift is shared here, at its source:
    /// the other machine runs the same GiftGiver it already loaded, which is what
    /// makes both players end up with the same set.
    ///
    /// Suppressed while a remote effect is being replayed, so a shared gift is
    /// never echoed back to the machine it came from.
    /// </summary>
    public static void OnGiftGiven(GiftGiver? giver, string method, int index)
    {
        if (!Enabled ||
            !CoopPlugin.StoryShareItemRewards ||
            _suppressBroadcast ||
            ItemSync.IsRemoteApply ||
            giver == null)
        {
            return;
        }

        GameObject? go = null;
        try
        {
            go = giver.gameObject;
        }
        catch (Exception)
        {
            go = null;
        }

        if (go == null)
        {
            return;
        }

        // The full hierarchy key is stable across machines because both load the
        // same scene; the receiving side looks it up in its own object registry.
        string key = KeyOf(go.transform);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        // A single dialogue node can hand out several gifts; each one is its own
        // event, but a repeated identical node in the same instant is sent once.
        string signature = key + "." + method + "(" + index + ")";
        if (string.Equals(signature, _lastActionSignature, StringComparison.Ordinal) &&
            Time.unscaledTime - _lastActionTime < 1.0f)
        {
            return;
        }

        _lastActionSignature = signature;
        _lastActionTime = Time.unscaledTime;

        _eventsSent++;
        CoopPlugin.LogSource.LogInfo($"[story] gift '{signature}' shared with the other player.");
        CoopPlugin.Network?.SendStoryEvent(
            key,
            (byte)StoryKind.DialogueAction,
            Vector3.zero,
            method,
            index,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    private static bool IsSharedDialogueMethod(string method)
    {
        return string.Equals(method, "SetQuestLog", StringComparison.Ordinal)
            || string.Equals(method, "RemoveQuestLog", StringComparison.Ordinal)
            || string.Equals(method, "setSituation", StringComparison.Ordinal)
            || string.Equals(method, "VIDE_Override", StringComparison.Ordinal)
            || IsSharedDialogueGiftMethod(method);
    }

    /// <summary>
    /// A dialogue branch can also hand the player their starting equipment
    /// through a GiftGiver (the intro camo, for example). Those are replayed so
    /// the teammate receives the same kit, while every other item/reward action
    /// stays single.
    /// </summary>
    private static bool IsSharedDialogueGiftMethod(string method)
    {
        return CoopPlugin.StoryShareItemRewards &&
            (string.Equals(method, "GiveAllGifts", StringComparison.Ordinal) ||
             string.Equals(method, "GiveGiftAtIndex", StringComparison.Ordinal));
    }

    private static void ApplyDialogueAction(string gameObjectName, string method, int number)
    {
        if (string.IsNullOrEmpty(gameObjectName) ||
            string.IsNullOrEmpty(method) ||
            !IsSharedDialogueMethod(method))
        {
            return;
        }

        // The gift patches send the target's full hierarchy key; the older
        // dialogue actions send a plain GameObject name.
        GameObject? target = gameObjectName.IndexOf('|') >= 0
            ? FindObject(gameObjectName)
            : null;
        if (target == null)
        {
            target = FindObjectByName(gameObjectName);
        }

        if (target == null)
        {
            return;
        }

        if (IsSharedDialogueGiftMethod(method))
        {
            ApplyDialogueGift(target, gameObjectName, method, number);
            return;
        }

        NPCConversation? conversation = null;
        try
        {
            conversation = target.GetComponentInChildren<NPCConversation>(true);
        }
        catch (Exception)
        {
            conversation = null;
        }

        if (conversation == null)
        {
            return;
        }

        try
        {
            _suppressBroadcast = true;
            switch (method)
            {
                case "SetQuestLog":
                    conversation.SetQuestLog(number);
                    break;
                case "RemoveQuestLog":
                    conversation.RemoveQuestLog(number);
                    break;
                case "setSituation":
                    conversation.setSituation(number);
                    break;
                case "VIDE_Override":
                    conversation.VIDE_Override(number);
                    break;
            }

            _eventsApplied++;
            CoopPlugin.LogSource.LogInfo(
                $"[story] applied remote dialogue effect '{gameObjectName}.{method}({number})'."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] dialogue effect replay failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _suppressBroadcast = false;
        }
    }

    private static void ApplyDialogueGift(
        GameObject target,
        string gameObjectName,
        string method,
        int number
    )
    {
        // A gift spawns and immediately picks up its item, so keep those pickups
        // local instead of echoing them back to the giver.
        ItemSync.BeginRemoteApply();
        try
        {
            _suppressBroadcast = true;
            if (string.Equals(method, "GiveGiftAtIndex", StringComparison.Ordinal))
            {
                Il2CppSystem.Object? boxed = BoxArgument(number);
                if (boxed == null)
                {
                    return;
                }

                target.SendMessage(
                    method,
                    boxed,
                    SendMessageOptions.DontRequireReceiver
                );
            }
            else
            {
                target.SendMessage(method, SendMessageOptions.DontRequireReceiver);
            }

            _eventsApplied++;
            CoopPlugin.LogSource.LogInfo(
                $"[story] applied remote dialogue item reward " +
                $"'{gameObjectName}.{method}({number})'."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] dialogue item reward replay failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _suppressBroadcast = false;
            ItemSync.EndRemoteApply();
        }
    }

    private static GameObject? FindObjectByName(string name)
    {
        GameObject? found = null;
        try
        {
            found = GameObject.Find(name);
        }
        catch (Exception)
        {
            found = null;
        }

        if (found != null)
        {
            return found;
        }

        BuildObjectRegistry();
        foreach (KeyValuePair<string, GameObject> pair in ObjectsByKey)
        {
            if (pair.Value != null && string.Equals(pair.Value.name, name, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Save flags (FlagsForSaveFiles)
    //
    // The game's quest progression is driven by named save flags. Setting one
    // locally (SetFlag / SetTrue / SetFalse) is mirrored on the other machine,
    // so both copies of the world agree on which quests are done. Flags are
    // changed through SetFlag during gameplay; a save load writes the flag
    // array directly, so a load does not turn into a storm of packets.
    // ------------------------------------------------------------------

    public static void OnFlagChanged(FlagsForSaveFiles? component, string name, bool requested)
    {
        if (!Enabled || !CoopPlugin.StorySyncFlags || _applyingRemote || _suppressBroadcast)
        {
            return;
        }

        if (component == null || string.IsNullOrEmpty(name))
        {
            return;
        }

        bool value = requested;
        try
        {
            if (component.TryGetFlagValue(name, out bool current))
            {
                value = current;
            }
        }
        catch (Exception)
        {
            // Keep the requested value if the lookup is unavailable.
        }

        if (KnownFlags.TryGetValue(name, out bool known) && known == value)
        {
            return;
        }

        KnownFlags[name] = value;
        _flagsSent++;
        CoopPlugin.LogSource.LogInfo($"[story] flag '{name}' = {value} (sharing).");
        CoopPlugin.Network?.SendStoryEvent(
            name,
            (byte)StoryKind.Flag,
            Vector3.zero,
            ComponentNameOf(component),
            value ? 1 : 0,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    public static string NameOfFlagIndex(FlagsForSaveFiles? component, int index)
    {
        try
        {
            if (component != null && component.flags != null &&
                index >= 0 && index < component.flags.Length)
            {
                SaveFlag flag = component.flags[index];
                if (flag != null)
                {
                    return flag.name ?? string.Empty;
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return string.Empty;
    }

    private static string ComponentNameOf(FlagsForSaveFiles component)
    {
        try
        {
            return component.gameObject != null ? component.gameObject.name : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void ApplyRemoteFlag(string name, bool value, string componentName)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        FlagsForSaveFiles? component = FindFlagComponent(componentName);
        if (component == null)
        {
            return;
        }

        try
        {
            _applyingRemote = true;
            component.SetFlag(name, value);
            KnownFlags[name] = value;
            _flagsApplied++;
            CoopPlugin.LogSource.LogInfo($"[story] applied remote flag '{name}' = {value}.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] flag '{name}' apply failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static FlagsForSaveFiles? FindFlagComponent(string name)
    {
        try
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (FlagsForSaveFiles candidate in UnityEngine.Object.FindObjectsOfType<FlagsForSaveFiles>())
                {
                    if (candidate != null && candidate.gameObject != null &&
                        string.Equals(candidate.gameObject.name, name, StringComparison.Ordinal))
                    {
                        _flagComponent = candidate;
                        return candidate;
                    }
                }
            }

            if (_flagComponent != null && _flagComponent.gameObject != null)
            {
                return _flagComponent;
            }

            _flagComponent = UnityEngine.Object.FindObjectOfType<FlagsForSaveFiles>();
        }
        catch (Exception)
        {
            _flagComponent = null;
        }

        return _flagComponent;
    }

    // ------------------------------------------------------------------
    // Tutorial quest manager (questmanagerTUTORIAL)
    //
    // The intro quest manager advances through numbered events. Running one is
    // mirrored so both players see the same tutorial steps and their side
    // effects (flags, doors, spawns).
    // ------------------------------------------------------------------

    public static void OnQuestEvent(string method, int which)
    {
        if (!Enabled || !CoopPlugin.StorySyncQuestEvents || _applyingRemote || _suppressBroadcast)
        {
            return;
        }

        _questEventsSent++;
        CoopPlugin.LogSource.LogInfo($"[story] quest event {method}({which}) (sharing).");
        CoopPlugin.Network?.SendStoryEvent(
            "quest",
            (byte)StoryKind.QuestEvent,
            Vector3.zero,
            method,
            which,
            Array.Empty<string>(),
            Array.Empty<bool>()
        );
    }

    private static void ApplyRemoteQuestEvent(string method, int which)
    {
        questmanagerTUTORIAL? manager;
        try
        {
            manager = UnityEngine.Object.FindObjectOfType<questmanagerTUTORIAL>();
        }
        catch (Exception)
        {
            return;
        }

        if (manager == null)
        {
            return;
        }

        try
        {
            _applyingRemote = true;
            switch (method)
            {
                case "triggerevent":
                    manager.triggerevent(which);
                    break;
                case "blockevents":
                    manager.blockevents(which);
                    break;
                case "unblockevents":
                    manager.unblockevents(which);
                    break;
                default:
                    return;
            }

            _questEventsApplied++;
            CoopPlugin.LogSource.LogInfo($"[story] applied remote quest event {method}({which}).");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] quest event {method}({which}) apply failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    // ------------------------------------------------------------------
    // Receiving
    // ------------------------------------------------------------------

    public static void ApplyEvent(
        string key,
        byte kind,
        Vector3 point,
        string text,
        int number,
        string[] objectKeys,
        bool[] objectStates
    )
    {
        if (!Enabled)
        {
            return;
        }

        EnsureScene();

        if (kind == (byte)StoryKind.DialogueAction)
        {
            ApplyDialogueAction(key, text, number);
            return;
        }

        if (kind == (byte)StoryKind.Flag)
        {
            ApplyRemoteFlag(key, number != 0, text);
            return;
        }

        if (kind == (byte)StoryKind.QuestEvent)
        {
            ApplyRemoteQuestEvent(text, number);
            return;
        }

        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!key.StartsWith(_scene + "|", StringComparison.Ordinal))
        {
            return;
        }

        _eventsApplied++;
        Consumed.Add(key);

        if (CoopPlugin.StorySyncActivations && objectKeys != null && objectStates != null)
        {
            int count = Mathf.Min(objectKeys.Length, objectStates.Length);
            for (int i = 0; i < count; i++)
            {
                ApplyActivation(objectKeys[i], objectStates[i]);
            }
        }

        if (kind == (byte)StoryKind.QuestAction)
        {
            // The replay can drive an elevator, a door or a flag through the
            // game's own listeners. Suppress the side-effect broadcasts while it
            // runs so a replayed elevator call is not sent back to the sender.
            _suppressBroadcast = true;
            ElevatorSync.BeginRemoteApply();
            try
            {
                ReplayQuestEffects(key);
            }
            finally
            {
                ElevatorSync.EndRemoteApply();
                _suppressBroadcast = false;
            }
        }

        if (kind == (byte)StoryKind.Teleport)
        {
            TeleportLocalPlayer(point);
        }

        if (kind == (byte)StoryKind.Cutscene)
        {
            PlayRemoteCutscene(key);
        }

        CoopPlugin.LogSource.LogInfo(
            $"[story] applied remote {((StoryKind)kind)} '{key}' (objects={objectKeys?.Length ?? 0})."
        );
    }

    private static void TeleportLocalPlayer(Vector3 destination)
    {
        PlayerMovement? player = CoopPlugin.Network?.LocalPlayer;
        if (player == null || player.gameObject == null)
        {
            return;
        }

        try
        {
            CharacterController? controller = player.GetComponent<CharacterController>();
            if (controller != null)
            {
                controller.enabled = false;
                player.transform.position = destination;
                controller.enabled = true;
            }
            else
            {
                player.transform.position = destination;
            }

            CoopPlugin.LogSource.LogInfo($"[story] teleported local player to {destination}.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] teleport failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void ApplyActivation(string objectKey, bool active)
    {
        if (string.IsNullOrEmpty(objectKey))
        {
            return;
        }

        GameObject? target = FindObject(objectKey);
        if (target == null)
        {
            return;
        }

        try
        {
            target.SetActive(active);
        }
        catch (Exception)
        {
            // Some objects cannot be toggled mid-frame; ignore.
        }
    }

    // ------------------------------------------------------------------
    // Quest-action world effects
    //
    // A quest action often has empty activated/deactivated object lists and
    // instead drives the world through its Trigger UnityEvents (a piston door
    // calls moveuptimes.BeginUp, a gate calls doorScript.OpenDoor, ...). Those
    // listeners are identical on both machines because both load the same
    // scene, so the receiving side re-reads its own copy of the action and
    // invokes only the whitelisted world-effect targets. Reward and inventory
    // listeners are never replayed, which is what keeps quest items from being
    // granted twice.
    // ------------------------------------------------------------------

    private static readonly HashSet<string> WorldEffectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "doorScript",
        "moveuptimes",
        "elevator",
        "hind",
        "Triggerer",
        "triggeronbuild",
        "EnemyDoorTrigger",
        "turnonanddie",
        "turnononeframe",
        "horrorsetpiece",
        "MusicActivator",
        "bridgebuilder",
        "dynamicenemymover",
        "trainnode",
    };

    // Method names that are unambiguously a world change no matter which
    // component holds them. This is a safety net for the case where the
    // target's assembly type name is missing: GetType() on an Il2CppInterop
    // wrapper often reports the declared field type ("Object") instead of the
    // real component ("elevator"), which used to make the door button look
    // like a non-world effect.
    private static readonly HashSet<string> WorldEffectMethods = new(StringComparer.Ordinal)
    {
        "selectFloor",
        "teleporttofloor",
        "BeginUp",
        "stopup",
        "OpenDoor",
        "OpenStealth",
        "DestroyDoor",
    };

    private static bool IsWorldEffect(string typeName, string method)
    {
        // Toggling a plain GameObject is always a world change.
        if (string.Equals(method, "SetActive", StringComparison.Ordinal))
        {
            return true;
        }

        if (WorldEffectMethods.Contains(method))
        {
            return true;
        }

        if (string.Equals(typeName, "QuestAction", StringComparison.OrdinalIgnoreCase))
        {
            // Never replay useQuestObject: it can grant quest items again.
            return string.Equals(method, "activateQuestObject", StringComparison.Ordinal)
                || string.Equals(method, "deactivateQuestObject", StringComparison.Ordinal);
        }

        return WorldEffectTypes.Contains(typeName);
    }

    // Gifts (the starting kit the intro hands out through a GiftGiver) are NOT
    // replayed from here: they are shared at their source by the GiftGiver
    // patches (see StorySync.OnGiftGiven), so replaying the quest listener as
    // well would hand the teammate the same kit twice.
    //
    // ItemInfo.pickupitem is the one quest listener that is still replayed: it
    // picks up a shared world item (the tutorial Makarov, for example), so the
    // pickup has to happen on the other machine too or the two inventories drift
    // apart. Gated behind Story.ShareQuestRewards.
    private static bool IsQuestRewardEffect(string typeName, string method)
    {
        return CoopPlugin.StoryShareQuestRewards &&
            string.Equals(typeName, "ItemInfo", StringComparison.Ordinal) &&
            string.Equals(method, "pickupitem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsItemRewardEffect(string typeName, string method)
    {
        return IsQuestRewardEffect(typeName, method);
    }

    /// <summary>
    /// Resolves a readable component type name for a persistent listener.
    /// <c>PersistentCall.targetAssemblyTypeName</c> is the serialized
    /// "Namespace.Type, Assembly" of the real target and is reliable, unlike
    /// <c>target.GetType()</c> which Il2CppInterop can report as the base
    /// declared type.
    /// </summary>
    private static string ShortTypeName(string? assemblyTypeName, UnityEngine.Object? target)
    {
        string full = assemblyTypeName ?? string.Empty;
        int comma = full.IndexOf(',');
        if (comma >= 0)
        {
            full = full.Substring(0, comma);
        }

        full = full.Trim();
        if (full.Length > 0)
        {
            int dot = full.LastIndexOf('.');
            return dot >= 0 ? full.Substring(dot + 1) : full;
        }

        return target != null ? target.GetType().Name : string.Empty;
    }

    /// <summary>
    /// Logs every persistent listener a quest action drives. This is the
    /// diagnostic that reveals which world effect a button actually uses.
    /// </summary>
    private static void LogQuestEffects(QuestAction action)
    {
        try
        {
            var triggers = action.Triggers;
            if (triggers == null)
            {
                return;
            }

            for (int i = 0; i < triggers.Length; i++)
            {
                var trigger = triggers[i];
                UnityEvent? evt = trigger.assign;
                if (evt == null)
                {
                    continue;
                }

                var group = evt.m_PersistentCalls;
                var calls = group != null ? group.m_Calls : null;
                if (calls == null)
                {
                    continue;
                }

                for (int c = 0; c < calls.Count; c++)
                {
                    var call = calls[c];
                    if (call == null)
                    {
                        continue;
                    }

                    UnityEngine.Object? target = call.target;
                    string typeName = ShortTypeName(call.targetAssemblyTypeName, target);
                    CoopPlugin.LogSource.LogInfo(
                        $"[story] quest trigger[{i}].assign -> {typeName}.{call.methodName} " +
                        $"(mode={(int)call.mode}, target='{call.targetAssemblyTypeName}')."
                    );
                }
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] quest trigger dump failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void ReplayQuestEffects(string key)
    {
        if (!CoopPlugin.StorySyncQuestEffects)
        {
            return;
        }

        GameObject? target = FindObject(key);
        if (target == null)
        {
            return;
        }

        QuestAction? action = target.GetComponent<QuestAction>();
        if (action == null)
        {
            return;
        }

        try
        {
            var triggers = action.Triggers;
            if (triggers == null)
            {
                return;
            }

            for (int i = 0; i < triggers.Length; i++)
            {
                var trigger = triggers[i];
                UnityEvent? evt = trigger.assign;
                if (evt == null)
                {
                    continue;
                }

                ReplayUnityEvent(evt, key);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] quest effect replay failed for '{key}': " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void ReplayUnityEvent(UnityEvent evt, string key)
    {
        var group = evt.m_PersistentCalls;
        var calls = group != null ? group.m_Calls : null;
        if (calls == null)
        {
            return;
        }

        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            if (call == null)
            {
                continue;
            }

            UnityEngine.Object? target = call.target;
            string method = call.methodName ?? string.Empty;
            if (target == null || string.IsNullOrEmpty(method))
            {
                continue;
            }

            string typeName = ShortTypeName(call.targetAssemblyTypeName, target);
            bool worldEffect = IsWorldEffect(typeName, method);
            bool itemReward = !worldEffect && IsItemRewardEffect(typeName, method);
            if (!worldEffect && !itemReward)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[story] quest '{key}': skipping non-world effect " +
                    $"'{typeName}.{method}' (avoids duplicating rewards)."
                );
                continue;
            }

            int mode = (int)call.mode;
            var args = call.arguments;
            object?[] arguments = mode switch
            {
                2 => new object?[] { args != null ? args.unityObjectArgument : null },
                3 => new object?[] { args != null ? args.intArgument : 0 },
                4 => new object?[] { args != null ? args.floatArgument : 0.0f },
                5 => new object?[] { args != null ? args.stringArgument : string.Empty },
                6 => new object?[] { args != null && args.boolArgument },
                _ => Array.Empty<object?>(),
            };

            InvokeEffect(
                target,
                typeName,
                method,
                arguments,
                key,
                itemReward ? "item reward" : "world effect",
                itemReward
            );
        }
    }

    private static void InvokeEffect(
        UnityEngine.Object target,
        string typeName,
        string method,
        object?[] arguments,
        string key,
        string label,
        bool itemReward
    )
    {
        if (!itemReward)
        {
            InvokeEffectCore(target, typeName, method, arguments, key, label);
            return;
        }

        // A replayed reward (ItemInfo.pickupitem) runs the game's own pickup on
        // this machine. It must not echo back to the other machine as a fresh
        // world pickup, so the item events it causes stay local.
        ItemSync.BeginRemoteApply();
        try
        {
            InvokeEffectCore(target, typeName, method, arguments, key, label);
        }
        finally
        {
            ItemSync.EndRemoteApply();
        }
    }

    private static void InvokeEffectCore(
        UnityEngine.Object target,
        string typeName,
        string method,
        object?[] arguments,
        string key,
        string label
    )
    {
        try
        {
            // Il2CppInterop hands back the declared field type (UnityEngine.Object)
            // for a persistent call target, so target.GetType() cannot be used to
            // find the real method by reflection. SendMessage resolves the method
            // on the actual component instead, which is why the world effects are
            // limited to MonoBehaviours (all of the whitelisted types are).
            GameObject? go = AsGameObject(target);
            if (go == null)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[story] quest '{key}': cannot resolve target '{typeName}' " +
                    $"for '{method}'."
                );
                return;
            }

            if (string.Equals(method, "SetActive", StringComparison.Ordinal))
            {
                bool active = arguments.Length > 0 && arguments[0] is bool flag && flag;
                go.SetActive(active);
                _effectsApplied++;
                CoopPlugin.LogSource.LogInfo(
                    $"[story] quest '{key}': applied {label} '{typeName}.SetActive({active})'."
                );
                return;
            }

            if (arguments.Length == 0)
            {
                go.SendMessage(method, SendMessageOptions.DontRequireReceiver);
            }
            else if (arguments.Length == 1)
            {
                Il2CppSystem.Object? boxed = BoxArgument(arguments[0]);
                if (boxed == null)
                {
                    CoopPlugin.LogSource.LogWarning(
                        $"[story] quest '{key}': cannot box argument for " +
                        $"'{typeName}.{method}'."
                    );
                    return;
                }

                go.SendMessage(method, boxed, SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[story] quest '{key}': '{typeName}.{method}' takes " +
                    $"{arguments.Length} arguments; cannot replay."
                );
                return;
            }

            _effectsApplied++;
            CoopPlugin.LogSource.LogInfo(
                $"[story] quest '{key}': applied {label} '{typeName}.{method}'."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] quest '{key}': effect '{typeName}.{method}' failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Resolves the GameObject a persistent-call target belongs to, even when
    /// Il2CppInterop only exposes the base <see cref="UnityEngine.Object"/>
    /// wrapper.
    /// </summary>
    private static GameObject? AsGameObject(UnityEngine.Object target)
    {
        try
        {
            GameObject? go = target.TryCast<GameObject>();
            if (go != null)
            {
                return go;
            }
        }
        catch (Exception)
        {
            // Fall through to the component path.
        }

        try
        {
            Component? component = target.TryCast<Component>();
            if (component != null)
            {
                return component.gameObject;
            }
        }
        catch (Exception)
        {
            // Fall through.
        }

        return null;
    }

    /// <summary>
    /// Boxes a decoded UnityEvent argument into the Il2Cpp object the
    /// <c>SendMessage</c> overload expects.
    /// </summary>
    private static Il2CppSystem.Object? BoxArgument(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case int i:
                return i;
            case float f:
                return f;
            case bool b:
                return b;
            case string s:
                return s;
            case double d:
                return d;
            case uint u:
                return u;
            case long l:
                return l;
            case ulong ul:
                return ul;
            case short sh:
                return sh;
            case ushort ush:
                return ush;
            case byte by:
                return by;
            case sbyte sb:
                return sb;
            case char c:
                return c;
            case UnityEngine.Object o:
                try
                {
                    return o.Cast<Il2CppSystem.Object>();
                }
                catch (Exception)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    private static GameObject? FindObject(string key)
    {
        if (ObjectsByKey.TryGetValue(key, out GameObject? cached) && cached != null)
        {
            return cached;
        }

        BuildObjectRegistry();
        return ObjectsByKey.TryGetValue(key, out GameObject? found) ? found : null;
    }

    private static void BuildObjectRegistry()
    {
        ObjectsByKey.Clear();
        try
        {
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null)
                {
                    continue;
                }

                foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                {
                    if (node == null)
                    {
                        continue;
                    }

                    ObjectsByKey[KeyOf(node)] = node.gameObject;
                }
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[story] object registry failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void CollectActivations(
        GameObject[]? objects,
        bool active,
        List<string> keys,
        List<bool> states
    )
    {
        if (objects == null)
        {
            return;
        }

        foreach (GameObject go in objects)
        {
            if (go == null)
            {
                continue;
            }

            keys.Add(KeyOf(go.transform));
            states.Add(active);
        }
    }

    /// <summary>
    /// Drops the one-time story locks. Called when a save is loaded: the save
    /// restores the game's own story progress, so the local mirror of what has
    /// already been consumed must be rebuilt from the loaded world instead of
    /// blocking it (otherwise a reload would leave every door and dialogue locked
    /// forever).
    /// </summary>
    public static void ResetLocks(string reason)
    {
        int consumed = Consumed.Count;
        Consumed.Clear();
        ObjectsByKey.Clear();
        KnownFlags.Clear();
        _flagComponent = null;

        // The short dedup windows must not swallow genuine effects that happen
        // immediately after a load (the previous timeline is over).
        _lastCutsceneKey = string.Empty;
        _lastCutsceneTime = 0.0f;
        _lastActionSignature = string.Empty;
        _lastActionTime = 0.0f;
        _blockedDialogueKey = string.Empty;

        if (consumed > 0)
        {
            CoopPlugin.LogSource.LogInfo(
                $"[story] cleared {consumed} story lock(s) after {reason}."
            );
        }
    }

    private static void EnsureScene()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (string.Equals(scene, _scene, StringComparison.Ordinal))
        {
            return;
        }

        _scene = scene;
        Consumed.Clear();
        ObjectsByKey.Clear();
        KnownFlags.Clear();
        _flagComponent = null;
        CoopPlugin.LogSource.LogInfo($"[story] tracking scene '{scene}'.");
    }

    private static string KeyOf(Transform transform)
    {
        EnsureScene();

        var builder = new StringBuilder();
        AppendNode(builder, transform);

        Transform parent = transform.parent;
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

internal enum StoryKind : byte
{
    Dialogue = 0,
    QuestAction = 1,
    Teleport = 2,
    Cutscene = 3,
    DialogueAction = 4,
    Flag = 5,
    QuestEvent = 6,
}

[HarmonyPatch(typeof(NPCConversation), "StartConversation")]
internal static class StoryConversationStartPatch
{
    private static bool Prefix(NPCConversation __instance)
    {
        return !StorySync.BlockDialogue(__instance);
    }

    private static void Postfix(NPCConversation __instance)
    {
        StorySync.OnDialogueStarted(__instance);
    }
}

[HarmonyPatch(typeof(QuestAction), "useQuestObject")]
internal static class StoryQuestActionPatch
{
    private static bool Prefix(QuestAction __instance)
    {
        return !StorySync.BlockQuestAction(__instance);
    }

    private static void Postfix(QuestAction __instance)
    {
        StorySync.OnQuestActionUsed(__instance);
    }
}

[HarmonyPatch(typeof(teleport), "OnTriggerEnter")]
internal static class StoryTeleportWalkInPatch
{
    private static void Prefix()
    {
        StorySync.NoteWalkInTeleport();
    }

    private static void Postfix()
    {
        StorySync.ClearWalkInTeleport();
    }
}

[HarmonyPatch(typeof(teleport), "tyeleport")]
internal static class StoryTeleportPatch
{
    private static void Postfix(teleport __instance)
    {
        StorySync.OnTeleport(__instance);
    }
}

[HarmonyPatch(typeof(teleportpuzzlebelgrade), "teleportaway")]
internal static class StoryPuzzleTeleportPatch
{
    private static void Postfix(teleportpuzzlebelgrade __instance)
    {
        StorySync.OnPuzzleTeleport(__instance);
    }
}

[HarmonyPatch(typeof(VideoPlayer), "Play")]
internal static class StoryCutscenePlayPatch
{
    private static void Postfix(VideoPlayer __instance)
    {
        StorySync.OnVideoPlayed(__instance);
    }
}

[HarmonyPatch(typeof(VD), "DoAction")]
internal static class StoryDialogueActionPatch
{
    private static void Postfix()
    {
        StorySync.OnDialogueAction();
    }
}

[HarmonyPatch(typeof(VD2), "DoAction")]
internal static class StoryDialogueActionV2Patch
{
    private static void Postfix(VD2 __instance)
    {
        StorySync.OnDialogueActionV2(__instance);
    }
}

[HarmonyPatch(typeof(GiftGiver), "GiveAllGifts")]
internal static class StoryGiftAllPatch
{
    private static void Postfix(GiftGiver __instance)
    {
        StorySync.OnGiftGiven(__instance, "GiveAllGifts", -1);
    }
}

[HarmonyPatch(typeof(GiftGiver), "GiveGiftAtIndex")]
internal static class StoryGiftAtIndexPatch
{
    private static void Postfix(GiftGiver __instance, int __0)
    {
        StorySync.OnGiftGiven(__instance, "GiveGiftAtIndex", __0);
    }
}

// ------------------------------------------------------------------
// Save flags: mirror FlagsForSaveFiles.SetFlag / SetTrue / SetFalse.
//
// Only the string setters are authoritative; SetTrue/SetFalse usually call
// SetFlag internally, and the value dedupe in StorySync.OnFlagChanged drops
// the redundant second report. The int setters are resolved to a flag name
// through the component's own flags array.
// ------------------------------------------------------------------

[HarmonyPatch(typeof(FlagsForSaveFiles), "SetFlag", new[] { typeof(string), typeof(bool) })]
internal static class StoryFlagSetPatch
{
    private static void Postfix(FlagsForSaveFiles __instance, string __0, bool __1, bool __result)
    {
        if (__result)
        {
            StorySync.OnFlagChanged(__instance, __0, __1);
        }
    }
}

[HarmonyPatch(typeof(FlagsForSaveFiles), "SetFlag", new[] { typeof(int), typeof(bool) })]
internal static class StoryFlagSetIndexPatch
{
    private static void Postfix(FlagsForSaveFiles __instance, int __0, bool __1, bool __result)
    {
        if (!__result)
        {
            return;
        }

        string name = StorySync.NameOfFlagIndex(__instance, __0);
        if (!string.IsNullOrEmpty(name))
        {
            StorySync.OnFlagChanged(__instance, name, __1);
        }
    }
}

[HarmonyPatch(typeof(FlagsForSaveFiles), "SetTrue", new[] { typeof(string) })]
internal static class StoryFlagSetTruePatch
{
    private static void Postfix(FlagsForSaveFiles __instance, string __0)
    {
        StorySync.OnFlagChanged(__instance, __0, true);
    }
}

[HarmonyPatch(typeof(FlagsForSaveFiles), "SetFalse", new[] { typeof(string) })]
internal static class StoryFlagSetFalsePatch
{
    private static void Postfix(FlagsForSaveFiles __instance, string __0)
    {
        StorySync.OnFlagChanged(__instance, __0, false);
    }
}

[HarmonyPatch(typeof(FlagsForSaveFiles), "SetTrue", new[] { typeof(int) })]
internal static class StoryFlagSetTrueIndexPatch
{
    private static void Postfix(FlagsForSaveFiles __instance, int __0)
    {
        string name = StorySync.NameOfFlagIndex(__instance, __0);
        if (!string.IsNullOrEmpty(name))
        {
            StorySync.OnFlagChanged(__instance, name, true);
        }
    }
}

[HarmonyPatch(typeof(FlagsForSaveFiles), "SetFalse", new[] { typeof(int) })]
internal static class StoryFlagSetFalseIndexPatch
{
    private static void Postfix(FlagsForSaveFiles __instance, int __0)
    {
        string name = StorySync.NameOfFlagIndex(__instance, __0);
        if (!string.IsNullOrEmpty(name))
        {
            StorySync.OnFlagChanged(__instance, name, false);
        }
    }
}

// ------------------------------------------------------------------
// Tutorial quest manager: mirror questmanagerTUTORIAL events.
// ------------------------------------------------------------------

[HarmonyPatch(typeof(questmanagerTUTORIAL), "triggerevent")]
internal static class StoryQuestTriggerEventPatch
{
    private static void Postfix(questmanagerTUTORIAL __instance, int __0)
    {
        StorySync.OnQuestEvent("triggerevent", __0);
    }
}

[HarmonyPatch(typeof(questmanagerTUTORIAL), "blockevents")]
internal static class StoryQuestBlockEventsPatch
{
    private static void Postfix(questmanagerTUTORIAL __instance, int __0)
    {
        StorySync.OnQuestEvent("blockevents", __0);
    }
}

[HarmonyPatch(typeof(questmanagerTUTORIAL), "unblockevents")]
internal static class StoryQuestUnblockEventsPatch
{
    private static void Postfix(questmanagerTUTORIAL __instance, int __0)
    {
        StorySync.OnQuestEvent("unblockevents", __0);
    }
}
