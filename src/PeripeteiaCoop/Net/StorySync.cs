using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
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
    private static readonly HashSet<string> Consumed = new();
    private static readonly Dictionary<string, GameObject> ObjectsByKey = new();

    private static string _scene = string.Empty;
    private static bool _walkInTeleport;
    private static int _eventsSent;
    private static int _eventsApplied;
    private static bool _suppressBroadcast;
    private static string _lastCutsceneKey = string.Empty;
    private static float _lastCutsceneTime;
    private static string _lastActionSignature = string.Empty;
    private static float _lastActionTime;

    public static bool Enabled => CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.StoryEnabled;

    public static int EventsSent => _eventsSent;

    public static int EventsApplied => _eventsApplied;

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            return;
        }

        EnsureScene();
    }

    // ------------------------------------------------------------------
    // Dialogue
    // ------------------------------------------------------------------

    public static bool BlockDialogue(NPCConversation conversation)
    {
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

        return false;
    }

    public static void OnDialogueStarted(NPCConversation conversation)
    {
        if (!Enabled || conversation == null)
        {
            return;
        }

        string key = KeyOf(conversation.transform);
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
        if (!Enabled || action == null)
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

        if (!Enabled || !CoopPlugin.StorySyncTeleports || source == null)
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
        if (!Enabled || !CoopPlugin.StorySyncTeleports || source == null)
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
        if (!Enabled || !CoopPlugin.StorySyncCutscenes || _suppressBroadcast || player == null)
        {
            return;
        }

        cutsceneplayer? cutscene = null;
        try
        {
            cutscene = player.GetComponent<cutsceneplayer>();
        }
        catch (Exception)
        {
            return;
        }

        if (cutscene == null)
        {
            return;
        }

        string key = KeyOf(cutscene.transform);
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

    private static void PlayRemoteCutscene(string key)
    {
        GameObject? target = FindObject(key);
        if (target == null)
        {
            return;
        }

        try
        {
            cutsceneplayer? cutscene = target.GetComponent<cutsceneplayer>();
            VideoPlayer? player = cutscene != null ? cutscene.vp : null;
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

    private static bool IsSharedDialogueMethod(string method)
    {
        return string.Equals(method, "SetQuestLog", StringComparison.Ordinal)
            || string.Equals(method, "RemoveQuestLog", StringComparison.Ordinal)
            || string.Equals(method, "setSituation", StringComparison.Ordinal)
            || string.Equals(method, "VIDE_Override", StringComparison.Ordinal);
    }

    private static void ApplyDialogueAction(string gameObjectName, string method, int number)
    {
        if (string.IsNullOrEmpty(gameObjectName) ||
            string.IsNullOrEmpty(method) ||
            !IsSharedDialogueMethod(method))
        {
            return;
        }

        GameObject? target = FindObjectByName(gameObjectName);
        if (target == null)
        {
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
