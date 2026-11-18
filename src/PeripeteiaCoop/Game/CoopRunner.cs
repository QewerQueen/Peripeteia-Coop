using System;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Independent Unity heartbeat for the network layer. PlayerMovement.Update is
/// not called while a level is loading, which used to delay the handshake until
/// the connection timed out. This component is owned by BepInEx, survives scene
/// loads and finds the local player lazily.
/// </summary>
public sealed class CoopRunner : MonoBehaviour
{
    private PlayerMovement? _localPlayer;
    private float _nextFindTime;
    private bool _autoLoadAttempted;
    private float _nextAutoLoadTime;
    private bool _storyScanDone;
    private int _storyScanCount;
    private float _nextStoryScanTime;
    private bool _keyTestDone;
    private float _nextKeyTestTime;
    private float _lastTimeScale = 1.0f;

    private void Update()
    {
        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            if (Time.unscaledTime >= _nextFindTime)
            {
                _nextFindTime = Time.unscaledTime + 1.0f;
                _localPlayer = FindObjectOfType<PlayerMovement>();
            }
        }

        if (CoopPlugin.ProbeEnabled &&
            Mathf.Abs(Time.timeScale - _lastTimeScale) > 0.001f)
        {
            _lastTimeScale = Time.timeScale;
            CoopPlugin.LogSource.LogInfo($"[pause] timeScale={Time.timeScale:F2}");
        }

        TryAutoLoadSave();
        SaveLoadSync.TickAutoStart();
        TryStoryScan();
        TryKeyTest();
        CoopPlugin.Tick(_localPlayer);
    }

    private void LateUpdate()
    {
        CoopPlugin.LateTick();
    }

    private void TryAutoLoadSave()
    {
        if (_autoLoadAttempted || string.IsNullOrEmpty(CoopPlugin.Settings.LoadSaveName))
        {
            return;
        }

        if (Time.unscaledTime < _nextAutoLoadTime)
        {
            return;
        }

        _nextAutoLoadTime = Time.unscaledTime + 1.0f;

        if (SaveLoadSync.TryStartFromMainMenu(CoopPlugin.Settings.LoadSaveName))
        {
            _autoLoadAttempted = true;
            CoopPlugin.LogSource.LogInfo(
                $"[test] Auto-loading save '{CoopPlugin.Settings.LoadSaveName}' from the main menu."
            );
        }
    }

    /// <summary>
    /// Debug-only (-coop-story-scan): dump the story components (VIDE_Assign,
    /// keycard, questmanagerTUTORIAL) present in the loaded scene once the level
    /// has streamed in.
    /// </summary>
    private void TryStoryScan()
    {
        if (_storyScanDone || !CoopPlugin.Settings.StoryScan)
        {
            return;
        }

        if (Time.unscaledTime < _nextStoryScanTime)
        {
            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        _storyScanCount++;
        _nextStoryScanTime = Time.unscaledTime + 3.0f;

        CoopPlugin.LogSource.LogInfo($"[test] story component scan #{_storyScanCount} for the loaded scene:");
        LogTypeCount<PlayerMovement>("PlayerMovement (control)");
        LogTypeCount<doorScript>("doorScript (control)");
        LogTypeCount<ItemInfo>("ItemInfo (control)");
        LogTypeCount<FlagsForSaveFiles>("FlagsForSaveFiles");
        LogTypeCount<questmanagerTUTORIAL>("questmanagerTUTORIAL");
        LogTypeCount<QuestAction>("QuestAction");
        LogTypeCount<QuestLog>("QuestLog");
        LogTypeCount<NPCConversation>("NPCConversation");
        LogTypeCount<VIDE_Assign>("VIDE_Assign");
        LogTypeCount<keycard>("keycard");
        LogTypeCount<questifelsebranch>("questifelsebranch");
        LogTypeCount<Interaction>("Interaction");

        if (_storyScanCount >= 4)
        {
            _storyScanDone = true;
        }

        LogStoryDetails();
    }

    /// <summary>
    /// Debug-only (-coop-key-test &lt;item name&gt;): pick up a named key/quest item
    /// once in a level, so a solo retest can prove that the item stays in the
    /// teammate's world (key items are not synced as pickups).
    /// </summary>
    private void TryKeyTest()
    {
        if (_keyTestDone || string.IsNullOrEmpty(CoopPlugin.Settings.KeyTestName))
        {
            return;
        }

        if (Time.unscaledTime < _nextKeyTestTime)
        {
            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        _nextKeyTestTime = Time.unscaledTime + 3.0f;

        string wanted = CoopPlugin.Settings.KeyTestName;
        ItemInfo? found = null;
        foreach (ItemInfo item in UnityEngine.Object.FindObjectsOfType<ItemInfo>())
        {
            if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (string.Equals(item.ItemName, wanted, StringComparison.OrdinalIgnoreCase))
            {
                found = item;
                break;
            }
        }

        if (found == null)
        {
            return;
        }

        _keyTestDone = true;
        CoopPlugin.LogSource.LogInfo(
            $"[test] key test: picking up '{wanted}' (object '{found.gameObject.name}')."
        );

        try
        {
            found.SendMessage("pickupitem", SendMessageOptions.DontRequireReceiver);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test] key test pickup failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void LogStoryDetails()
    {
        try
        {
            foreach (VIDE_Assign va in UnityEngine.Object.FindObjectsOfType<VIDE_Assign>())
            {
                if (va == null)
                {
                    continue;
                }

                CoopPlugin.LogSource.LogInfo(
                    $"[test]   VIDE '{va.gameObject.name}' alias='{va.alias}' " +
                    $"dialogue='{va.assignedDialogue}' overrideStartNode={va.overrideStartNode} " +
                    $"interactionCount={va.interactionCount} startp={va.startp} " +
                    $"assignedIndex={va.assignedIndex} assignedID={va.assignedID}"
                );
            }

            foreach (keycard kc in UnityEngine.Object.FindObjectsOfType<keycard>())
            {
                if (kc == null)
                {
                    continue;
                }

                string keyName = kc.key != null ? kc.key.ItemName : "?";
                CoopPlugin.LogSource.LogInfo(
                    $"[test]   keycard '{kc.gameObject.name}' oneuse={kc.oneuse} key='{keyName}' " +
                    $"passed={kc.hasPassedAlready} failed={kc.hasFailedAlready} " +
                    $"triggers={kc.Triggers?.Length ?? 0} fails={kc.FailTriggers?.Length ?? 0}"
                );
            }

            foreach (questmanagerTUTORIAL qm in UnityEngine.Object.FindObjectsOfType<questmanagerTUTORIAL>())
            {
                if (qm == null)
                {
                    continue;
                }

                CoopPlugin.LogSource.LogInfo(
                    $"[test]   questmanagerTUTORIAL '{qm.gameObject.name}' " +
                    $"exited={qm.exitedchambernormalway} bulb={qm.haslightbulb} " +
                    $"betrayedTop={qm.betrayedtopsr} betrayedUS={qm.betrayedtoussr} " +
                    $"killedBarman={qm.killedbarman} killedPSR={qm.killedpsr} killedUSSR={qm.killedussr} " +
                    $"debugQuestNum={qm.debugQuestNum} triggers={qm.triggerlist?.Count ?? 0}"
                );
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test] story detail dump failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void LogTypeCount<T>(string label) where T : Component
    {
        try
        {
            T[] all = UnityEngine.Object.FindObjectsOfType<T>();
            CoopPlugin.LogSource.LogInfo($"[test]   {label}: {all.Length} in loaded scene(s).");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test]   {label}: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private void OnGUI()
    {
        if (!CoopPlugin.ShowStatusOverlay || CoopPlugin.Network == null)
        {
            return;
        }

        float width = Mathf.Min(760.0f, Screen.width - 20.0f);
        GUI.Box(new Rect(10.0f, 10.0f, width, 26.0f), CoopPlugin.Network.StatusText);
    }
}
