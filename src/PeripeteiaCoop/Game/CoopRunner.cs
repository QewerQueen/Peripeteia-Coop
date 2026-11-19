using System;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    private bool _saveTestDone;
    private float _nextSaveTestTime;
    private bool _deathTestDone;
    private int _deathTestPhase;
    private float _nextDeathTestTime;
    private float _nextNpcDeathTestTime;
    private int _npcDeathsForced;
    private int _containerScanCount;
    private float _nextContainerScanTime;
    private float _nextLootTestTime;
    private int _lootTests;
    private string _autosaveScene = string.Empty;
    private float _autosaveAt;
    private int _personalSaves;
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
        TrySaveTest();
        TryDeathTest();
        TryNpcDeathTest();
        TryLootTest();
        TryContainerScan();
        TryClientAutosave();
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

    /// <summary>
    /// Debug-only (-coop-save-test): force one game save once in a level, so a
    /// retest can prove that a co-op client may write its own 'Coop - &lt;name&gt;'
    /// save and therefore keep its personal inventory.
    /// </summary>
    private void TrySaveTest()
    {
        if (_saveTestDone || !CoopPlugin.Settings.SaveTest)
        {
            return;
        }

        if (Time.unscaledTime < _nextSaveTestTime)
        {
            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        _nextSaveTestTime = Time.unscaledTime + 5.0f;

        SaveHandler? handler = UnityEngine.Object.FindObjectOfType<SaveHandler>();
        if (handler == null || handler.onMainMenuScene)
        {
            return;
        }

        _saveTestDone = true;
        string name = SaveVersionController.currentSaveName ?? string.Empty;
        CoopPlugin.LogSource.LogInfo($"[test] save test: SaveProcedure(false, '{name}').");

        try
        {
            handler.SaveProcedure(false, name);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test] save test failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Debug-only (-coop-death-test): kill the local player once the level has
    /// streamed in, so the co-op death respawn can be retested on a single
    /// machine. The respawn should put the player back at the save point with
    /// the world untouched - no scene reload, no story locks cleared, no dead
    /// NPCs back on their feet.
    /// </summary>
    private void TryDeathTest()
    {
        if (_deathTestDone || !CoopPlugin.Settings.DeathTest)
        {
            return;
        }

        if (Time.unscaledTime < _nextDeathTestTime)
        {
            return;
        }

        if (_deathTestPhase == 1)
        {
            // Phase 1: the death screen normally waits for the player to press
            // the revive key; drive that same call so the patched respawn runs.
            SaveHandler? reviveHandler = UnityEngine.Object.FindObjectOfType<SaveHandler>();
            if (reviveHandler == null)
            {
                return;
            }

            _deathTestDone = true;
            CoopPlugin.LogSource.LogInfo(
                "[test] death test: calling SaveHandler.LoadProcedureFromDeath()."
            );

            try
            {
                reviveHandler.LoadProcedureFromDeath();
            }
            catch (Exception exception)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[test] death test revive failed: {exception.GetType().Name}: {exception.Message}"
                );
            }

            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        SaveHandler? handler = UnityEngine.Object.FindObjectOfType<SaveHandler>();
        if (handler == null || handler.onMainMenuScene)
        {
            return;
        }

        {
            HealthScript? health = _localPlayer.GetComponent<HealthScript>();
            if (health == null)
            {
                health = _localPlayer.GetComponentInParent<HealthScript>();
            }

            if (health == null)
            {
                return;
            }

            _deathTestPhase = 1;
            _nextDeathTestTime = Time.unscaledTime + 3.0f;
            CoopPlugin.LogSource.LogInfo(
                $"[test] death test: forcing a local death (hp={health.HP}/{health.maxHP})."
            );

            try
            {
                // Apply lethal damage through the game's real path so the death
                // screen and its revive run exactly as they would in a fight.
                health.TakeDMG(
                    false,
                    health.maxHP + 9999,
                    _localPlayer.transform,
                    false,
                    health.transform.position,
                    false
                );
            }
            catch (Exception exception)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[test] death test failed: {exception.GetType().Name}: {exception.Message}"
                );
            }

            return;
        }
    }

    /// <summary>
    /// Debug-only (-coop-npc-death-test): the host force-kills the nearest NPC
    /// every few seconds, so a solo retest can prove that a death the client did
    /// not deal itself still produces a corpse on the client. This is the case
    /// the "NPC corpses never appear" report is about: when the client shoots an
    /// NPC its own game also applies the damage locally, so its corpse appears by
    /// accident, but a kill the host makes only reaches the client through the
    /// network. Never enabled unless -coop-npc-death-test is passed.
    /// </summary>
    private void TryNpcDeathTest()
    {
        if (!CoopPlugin.Settings.NpcDeathTest || CoopPlugin.Settings.Role != CoopRole.Host)
        {
            return;
        }

        if (Time.unscaledTime < _nextNpcDeathTestTime)
        {
            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        _nextNpcDeathTestTime = Time.unscaledTime + 12.0f;

        EnemyController? nearest = null;
        float best = float.MaxValue;
        Vector3 origin = _localPlayer.transform.position;

        foreach (EnemyController enemy in UnityEngine.Object.FindObjectsOfType<EnemyController>())
        {
            if (enemy == null || enemy.gameObject == null)
            {
                continue;
            }

            HealthScript? hp = enemy.health;
            if (hp == null || hp.dead)
            {
                continue;
            }

            float distance;
            try
            {
                distance = Vector3.Distance(origin, enemy.transform.position);
            }
            catch (Exception)
            {
                continue;
            }

            if (distance < best)
            {
                best = distance;
                nearest = enemy;
            }
        }

        if (nearest == null)
        {
            return;
        }

        _npcDeathsForced++;
        CoopPlugin.LogSource.LogInfo(
            $"[test] npc death test #{_npcDeathsForced}: force-killing '{nearest.gameObject.name}' at {best:F1} m."
        );

        try
        {
            nearest.health?.ForceDeath();
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test] npc death test failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Debug-only (-coop-loot-test): the host takes one item out of a corpse
    /// container every few seconds, so a solo retest can prove that the other
    /// machine loses the same item from the same corpse.
    /// </summary>
    private void TryLootTest()
    {
        bool clientDriven = CoopPlugin.Settings.LootTestClient;
        if ((!CoopPlugin.Settings.LootTest && !clientDriven) ||
            (clientDriven ? CoopPlugin.Settings.Role != CoopRole.Client : CoopPlugin.Settings.Role != CoopRole.Host))
        {
            return;
        }

        if (Time.unscaledTime < _nextLootTestTime || _localPlayer == null)
        {
            return;
        }

        _nextLootTestTime = Time.unscaledTime + 9.0f;

        try
        {
            foreach (container c in UnityEngine.Object.FindObjectsOfType<container>())
            {
                if (c == null || c.gameObject == null || c.isShop || c.attachedHealth == null)
                {
                    continue;
                }

                Il2CppSystem.Collections.Generic.List<ItemInfo>? items = c.objectsContained;
                if (items == null || items.Count == 0)
                {
                    continue;
                }

                ItemInfo? item = items[0];
                if (item == null || item.gameObject == null)
                {
                    continue;
                }

                _lootTests++;
                CoopPlugin.LogSource.LogInfo(
                    $"[test] loot test #{_lootTests}: removing first item from corpse '{BuildPath(c.transform)}'."
                );
                c.removeItem(item);
                break;
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test] loot test failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Debug-only (-coop-container-scan): dump every loot container in the loaded
    /// scene with its contents, so the container sync can be designed against the
    /// real object graph instead of a guess.
    /// </summary>
    private void TryContainerScan()
    {
        if (!CoopPlugin.Settings.ContainerScan)
        {
            return;
        }

        if (Time.unscaledTime < _nextContainerScanTime)
        {
            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        _nextContainerScanTime = Time.unscaledTime + 8.0f;
        _containerScanCount++;

        try
        {
            container[] all = UnityEngine.Object.FindObjectsOfType<container>();
            CoopPlugin.LogSource.LogInfo(
                $"[test] container scan #{_containerScanCount}: {all.Length} container(s)."
            );

            int shown = 0;
            foreach (container c in all)
            {
                if (c == null || c.gameObject == null)
                {
                    continue;
                }

                int objects = c.objects != null ? c.objects.Count : -1;
                int contained = c.objectsContained != null ? c.objectsContained.Count : -1;
                if (shown >= 40)
                {
                    continue;
                }

                shown++;
                CoopPlugin.LogSource.LogInfo(
                    $"[test]   container '{BuildPath(c.transform)}' active={c.gameObject.activeInHierarchy} " +
                    $"objects={objects} contained={contained} looted={c.looted} shop={c.isShop} " +
                    $"physical={c.physicalSpace} attachedHealth={c.attachedHealth != null} " +
                    $"lootTransform={c.lootTransform != null}"
                );

                LogContainerItems(c.objects, "objects");
                LogContainerItems(c.objectsContained, "contained");
            }

            LootUI? ui = UnityEngine.Object.FindObjectOfType<LootUI>();
            if (ui != null)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[test]   LootUI lootOn={ui.lootOn} onUi={ui.onUi} " +
                    $"currentContainer='{(ui.currentContainer != null ? BuildPath(ui.currentContainer.transform) : "<null>")}' " +
                    $"displayed={(ui.DisplayedItems != null ? ui.DisplayedItems.Count : -1)}"
                );
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[test] container scan failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void LogContainerItems(Il2CppSystem.Collections.Generic.List<ItemInfo>? items, string label)
    {
        if (items == null || items.Count == 0)
        {
            return;
        }

        for (int i = 0; i < items.Count && i < 12; i++)
        {
            ItemInfo? item = items[i];
            if (item == null || item.gameObject == null)
            {
                CoopPlugin.LogSource.LogInfo($"[test]     {label}[{i}] = <destroyed>");
                continue;
            }

            CoopPlugin.LogSource.LogInfo(
                $"[test]     {label}[{i}] '{item.ItemName}' obj='{item.gameObject.name}' " +
                $"active={item.gameObject.activeInHierarchy} grid={item.startGrid} " +
                $"inScene={item.gameObject.scene.IsValid()}"
            );
        }
    }

    private static string BuildPath(Transform transform)
    {
        var builder = new System.Text.StringBuilder(120);
        Transform? current = transform;
        while (current != null)
        {
            builder.Insert(0, $"/{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Client side: write this player's own co-op save once a level has streamed
    /// in, the way the host autosaves on level start. The game does not call its
    /// own level-start autosave on a client, so without this the client's
    /// inventory, money and health would never be written and would reset to the
    /// host's on the next join. The save always lands in the client's own
    /// 'Coop - &lt;name&gt;' folder, never the host's.
    /// </summary>
    private void TryClientAutosave()
    {
        if (CoopPlugin.Settings.Role != CoopRole.Client || !CoopPlugin.ClientAutosavePersonalState)
        {
            return;
        }

        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            return;
        }

        string scene = SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(scene) ||
            scene.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return;
        }

        if (!string.Equals(scene, _autosaveScene, StringComparison.Ordinal))
        {
            // A new level appeared; let it stream in before saving it.
            _autosaveScene = scene;
            _autosaveAt = Time.unscaledTime + 10.0f;
            return;
        }

        if (_autosaveAt <= 0.0f || Time.unscaledTime < _autosaveAt)
        {
            return;
        }

        _autosaveAt = 0.0f;

        SaveHandler? handler = UnityEngine.Object.FindObjectOfType<SaveHandler>();
        if (handler == null || handler.onMainMenuScene)
        {
            return;
        }

        string name = SaveVersionController.currentSaveName ?? string.Empty;
        if (!SaveTransfer.IsCoopLocalSave(name))
        {
            // Not playing from this machine's own co-op save; leave it alone.
            return;
        }

        _personalSaves++;
        CoopPlugin.LogSource.LogInfo(
            $"[save] client personal autosave #{_personalSaves} for '{name}'."
        );

        try
        {
            handler.SaveProcedure(false, name);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[save] client personal autosave failed: {exception.GetType().Name}: {exception.Message}"
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
        if (CoopPlugin.Network == null)
        {
            return;
        }

        if (CoopPlugin.ShowStatusOverlay)
        {
            float width = Mathf.Min(760.0f, Screen.width - 20.0f);
            GUI.Box(new Rect(10.0f, 10.0f, width, 26.0f), CoopPlugin.Network.StatusText);
        }

        if (CoopPlugin.TeammateHud)
        {
            DrawTeammateHud();
        }
    }

    /// <summary>
    /// Small panel with the teammate's name, health bar and down/dead state.
    /// Drawn with plain rects because GUILayout is stripped from this IL2CPP
    /// build.
    /// </summary>
    private static void DrawTeammateHud()
    {
        RemoteAvatar? teammate = CoopPlugin.Network?.Teammate;
        if (teammate == null)
        {
            return;
        }

        const float width = 240.0f;
        float x = Mathf.Max(10.0f, Screen.width - width - 10.0f);
        const float y = 10.0f;
        var panel = new Rect(x, y, width, 50.0f);

        Color previous = GUI.color;
        GUI.color = new Color(0.06f, 0.07f, 0.08f, 0.85f);
        GUI.Box(panel, GUIContent.none);
        GUI.color = previous;

        string name = string.IsNullOrEmpty(teammate.Name) ? "Teammate" : teammate.Name;
        string line = teammate.HealthKnown
            ? $"{name}   {teammate.HealthHp}/{teammate.HealthMaxHp}" +
              (teammate.IsDead ? "   DOWN" : string.Empty)
            : $"{name}   ...";
        GUI.Label(new Rect(x + 8.0f, y + 3.0f, width - 16.0f, 20.0f), line);

        var barBack = new Rect(x + 8.0f, y + 28.0f, width - 16.0f, 14.0f);
        GUI.color = new Color(0.16f, 0.05f, 0.05f, 0.9f);
        GUI.Box(barBack, GUIContent.none);

        if (teammate.HealthKnown && teammate.HealthMaxHp > 0)
        {
            float ratio = Mathf.Clamp01((float)teammate.HealthHp / teammate.HealthMaxHp);
            var barFill = new Rect(
                barBack.x + 1.0f,
                barBack.y + 1.0f,
                (barBack.width - 2.0f) * ratio,
                barBack.height - 2.0f
            );
            GUI.color = teammate.IsDead
                ? new Color(0.35f, 0.35f, 0.35f, 0.9f)
                : Color.Lerp(
                    new Color(0.75f, 0.15f, 0.15f, 0.95f),
                    new Color(0.2f, 0.7f, 0.25f, 0.95f),
                    ratio
                );
            GUI.Box(barFill, GUIContent.none);
        }

        GUI.color = previous;
    }
}
