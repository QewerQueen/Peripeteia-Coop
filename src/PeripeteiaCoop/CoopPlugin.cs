using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Entry point for the co-op prototype:
/// 1) BepInEx 6 loads the managed plugin into this IL2CPP build;
/// 2) a persistent CoopRunner MonoBehaviour drives network ticks;
/// 3) host/client exchange poses and spawn a cloned remote player;
/// 4) a client cannot overwrite save files.
/// </summary>
[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class CoopPlugin : BasePlugin
{
    internal static CoopPlugin Instance { get; private set; } = null!;

    internal static ManualLogSource LogSource { get; private set; } = null!;

    internal static bool ProbeEnabled { get; private set; }

    internal static bool DumpHierarchy { get; private set; }

    internal static bool BlockClientSaves { get; private set; } = true;

    internal static bool ShowStatusOverlay { get; private set; } = true;

    internal static float WeaponGripDown { get; private set; }

    internal static float WeaponGripForward { get; private set; }

    internal static float WeaponPalmOffset { get; private set; } = 0.03f;

    internal static bool CombatEnabled { get; private set; } = true;

    internal static bool CombatPlayerDamage { get; private set; } = true;

    internal static bool CombatPlayerCollision { get; private set; } = true;

    internal static int CombatLayerOverride { get; private set; } = -1;

    internal static float CombatDamageScale { get; private set; } = 1.0f;

    internal static bool ItemsSpawnMissing { get; private set; } = true;

    internal static bool AiEnabled { get; private set; } = true;

    internal static bool AiSyncDeaths { get; private set; } = true;

    internal static bool AiMirrorEnemies { get; private set; } = true;

    internal static bool AiFreezeClientAi { get; private set; } = true;

    internal static bool AiTargetRemotePlayer { get; private set; } = true;

    internal static bool StoryEnabled { get; private set; } = true;

    internal static bool StoryLockDialogues { get; private set; } = true;

    internal static bool StoryLockQuestActions { get; private set; } = true;

    internal static bool StorySyncActivations { get; private set; } = true;

    internal static bool StorySyncTeleports { get; private set; } = true;

    internal static bool StorySyncCutscenes { get; private set; } = true;

    internal static bool StorySyncDialogueActions { get; private set; } = true;

    internal static bool StorySyncQuestEffects { get; private set; } = true;

    internal static bool StoryShareItemRewards { get; private set; } = true;

    internal static bool StoryShareQuestRewards { get; private set; } = true;

    internal static bool SyncSceneTransitions { get; private set; } = true;

    internal static bool PlayerSyncDeath { get; private set; } = true;

    internal static bool PlayerDeathRagdoll { get; private set; } = true;

    internal static bool PlayerRespawnOnDeath { get; private set; } = true;

    internal static bool PlayerDragRemoteBody { get; private set; } = true;

    internal static bool BlockClientRollback { get; private set; } = true;

    internal static CoopSettings Settings { get; private set; } = null!;

    internal static CoopNetwork? Network { get; private set; }

    internal static bool IsCoopActive => Settings.Role != CoopRole.Offline;

    internal static bool ShouldBlockClientSaves =>
        BlockClientSaves && Settings.Role == CoopRole.Client;

    internal static bool ShouldBlockClientRollback =>
        BlockClientRollback && Settings.Role == CoopRole.Client;

    public override void Load()
    {
        Instance = this;
        LogSource = Log;

        // Two game instances share a single BepInEx config file. When a new key
        // is bound, BepInEx rewrites the file; if both instances start at the
        // same time, one of them hits a file lock and the whole plugin fails to
        // load (IOException in CoopPlugin.Load). Disable the automatic save
        // during binding and write the file once, guarded, at the end.
        Config.SaveOnConfigSet = false;

        ProbeEnabled = Config.Bind(
            "Probe",
            "Enabled",
            false,
            "Log basic player state every few seconds. Development only."
        ).Value;

        DumpHierarchy = Config.Bind(
            "Probe",
            "DumpHierarchy",
            false,
            "Development only: dump the player hierarchy and playerSkins prefabs once."
        ).Value;

        BlockClientSaves = Config.Bind(
            "Client",
            "BlockSaves",
            true,
            "Prevent a co-op client from writing save files. Loading is unaffected."
        ).Value;

        ShowStatusOverlay = Config.Bind(
            "Ui",
            "ShowStatus",
            true,
            "Show a small connection status overlay in co-op mode."
        ).Value;

        WeaponGripDown = Config.Bind(
            "Weapon",
            "GripDown",
            0.0f,
            "Optional fine-tune: lower the remote weapon in the hand's local space, in meters. Leave at 0 to copy the sender exactly."
        ).Value;

        WeaponGripForward = Config.Bind(
            "Weapon",
            "GripForward",
            0.0f,
            "Optional fine-tune: move the remote weapon toward the fingers in the hand's local space, in meters. Leave at 0 to copy the sender exactly."
        ).Value;

        WeaponPalmOffset = Config.Bind(
            "Weapon",
            "PalmOffset",
            0.03f,
            "Fallback distance from the wrist to the palm along the hand bone, in meters. Used when the real hand-grip offset is unavailable. 0.02-0.05 is normal."
        ).Value;

        CombatEnabled = Config.Bind(
            "Combat",
            "Enabled",
            true,
            "Enable player-versus-player combat: remote players get a physics proxy so they can be shot."
        ).Value;

        CombatPlayerDamage = Config.Bind(
            "Combat",
            "PlayerDamage",
            true,
            "Allow bullets to actually damage the other player. Turn off to keep the hit proxy but ignore damage."
        ).Value;

        CombatPlayerCollision = Config.Bind(
            "Combat",
            "PlayerCollision",
            true,
            "Make the other player solid so you cannot walk through them. Uses per-collider ignore settings and never touches the global layer matrix."
        ).Value;

        CombatLayerOverride = Config.Bind(
            "Combat",
            "CollisionLayer",
            -1,
            "Physics layer for the remote player's hit/collision proxy. -1 copies the local player's own layer."
        ).Value;

        CombatDamageScale = Config.Bind(
            "Combat",
            "DamageScale",
            1.0f,
            "Multiplier applied to weapon damage before it is sent to the other player."
        ).Value;

        ItemsSpawnMissing = Config.Bind(
            "Items",
            "SpawnMissing",
            true,
            "When a player drops an item this side never had (for example a quest reward), clone a matching template from the scene so the teammate can see and pick it up. Turn off to only sync items both sides already have."
        ).Value;

        AiEnabled = Config.Bind(
            "Ai",
            "Enabled",
            true,
            "Synchronize NPCs/enemies as part of the shared world. Inventory, money and player health stay personal regardless of this setting."
        ).Value;

        AiSyncDeaths = Config.Bind(
            "Ai",
            "SyncDeaths",
            true,
            "Share enemy deaths: a kill by either player counts for both, even when MirrorEnemies is off."
        ).Value;

        AiMirrorEnemies = Config.Bind(
            "Ai",
            "MirrorEnemies",
            true,
            "Host-authoritative NPC mirroring: the host simulates the AI and clients follow its transform, animation and health. Turn off to let each side run its own AI (only deaths stay shared)."
        ).Value;

        AiFreezeClientAi = Config.Bind(
            "Ai",
            "FreezeClientAi",
            true,
            "When mirroring, stop the client's own AI for mirrored enemies so it cannot fight the host's simulation. Requires MirrorEnemies."
        ).Value;

        AiTargetRemotePlayer = Config.Bind(
            "Ai",
            "TargetRemotePlayer",
            true,
            "Host only: register the remote player as an AI target so enemies can notice, chase and shoot at the client. Experimental."
        ).Value;

        StoryEnabled = Config.Bind(
            "Story",
            "Enabled",
            true,
            "Share the story: dialogues, quest objects and scripted teleports follow one branch, consumed by whichever player reaches them first."
        ).Value;

        StoryLockDialogues = Config.Bind(
            "Story",
            "LockDialogues",
            true,
            "A dialogue started by one player cannot be started by the other. Keeps quest rewards (items, money) from being granted twice."
        ).Value;

        StoryLockQuestActions = Config.Bind(
            "Story",
            "LockQuestActions",
            true,
            "A one-time quest object used by one player is locked for the other. Objects with multipleUse stay available to both."
        ).Value;

        StorySyncActivations = Config.Bind(
            "Story",
            "SyncActivations",
            true,
            "Replay the world GameObjects a quest object switches on/off on the other machine, so both see the same doors and props."
        ).Value;

        StorySyncTeleports = Config.Bind(
            "Story",
            "SyncTeleports",
            true,
            "A scripted (non walk-in) teleport moves both players. A teleporter the player simply walks into stays local and repeatable."
        ).Value;

        StorySyncCutscenes = Config.Bind(
            "Story",
            "SyncCutscenes",
            true,
            "Replay a video cutscene started by one player on the other machine, so the whole party sees it."
        ).Value;

        StorySyncDialogueActions = Config.Bind(
            "Story",
            "SyncDialogueActions",
            true,
            "Share the quest-log effects of a dialogue branch (NPCConversation.SetQuestLog / RemoveQuestLog). Other item and reward actions are never replayed."
        ).Value;

        StorySyncQuestEffects = Config.Bind(
            "Story",
            "SyncQuestEffects",
            true,
            "Replay the world-effect listeners a quest object drives (doors, pistons, elevators, GameObject toggles) on the other machine. Reward and inventory listeners are never replayed."
        ).Value;

        StoryShareItemRewards = Config.Bind(
            "Story",
            "ShareItemRewards",
            true,
            "Share the game's item grants (GiftGiver.GiveAllGifts / GiveGiftAtIndex) where they happen, so both players get the same starting weapons, magazines and kit (the intro Thermoptic Camo implant). Turn off if a unique quest item must stay single."
        ).Value;

        StoryShareQuestRewards = Config.Bind(
            "Story",
            "ShareQuestRewards",
            true,
            "Also replay the item rewards a quest object hands out (ItemInfo.pickupitem), so a quest reward reaches both players and the two inventories match. Replayed rewards do not echo back as world pickups. Turn off if a quest reward must stay with the first player only."
        ).Value;

        PlayerSyncDeath = Config.Bind(
            "Player",
            "SyncDeath",
            true,
            "Share player death: when one player dies the other sees the body go down, and it stands back up on revive."
        ).Value;

        PlayerDeathRagdoll = Config.Bind(
            "Player",
            "DeathRagdoll",
            true,
            "Use the game's own ragdoll rig for the remote death (falls and stays down). If off, the body simply stops animating."
        ).Value;

        PlayerRespawnOnDeath = Config.Bind(
            "Player",
            "RespawnOnDeath",
            true,
            "On death, stand the player back up at the last save point instead of reloading the level. Keeps the shared world (doors, NPCs, quest state) exactly as it was for both players. Turn off to use the game's normal death-load."
        ).Value;

        PlayerDragRemoteBody = Config.Bind(
            "Player",
            "DragRemoteBody",
            true,
            "Let you pick up the other player's body: look at the corpse and hold the game's interact key to carry it, and the other machine sees it move. The corpse is released when you let go, and pinned again once it settles."
        ).Value;

        SyncSceneTransitions = Config.Bind(
            "World",
            "SyncSceneTransitions",
            true,
            "Keep level transitions in step: when one player walks into a level exit, the other machine plays the same transition and follows them into the new level. Turn off to move between levels independently."
        ).Value;

        BlockClientRollback = Config.Bind(
            "Client",
            "BlockRollback",
            true,
            "Stop a co-op client from loading a save mid-level on its own; that would rewind the client's copy of the shared world and desync it from the host. A rollback is a host decision: the host loads the save and the client loads the same one on request. The initial load and scene transitions are still allowed."
        ).Value;

        Settings = CoopSettings.Parse(Environment.GetCommandLineArgs());

        try
        {
            // Single write, after every key is bound. Non-fatal if the other
            // instance happens to hold the file: the in-memory config is used.
            Config.Save();
        }
        catch (Exception exception)
        {
            LogSource.LogWarning(
                $"[config] could not save config (another instance may hold it): " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }

        if (Settings.Role != CoopRole.Offline)
        {
            // Critical for testing two instances on one PC: the unfocused
            // window must keep simulating the game world. Do not touch this in
            // a normal single-player launch.
            Application.runInBackground = true;
        }

        LogSource.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loading.");
        LogSource.LogInfo($"Game: {Application.productName} {Application.version}; Unity {Application.unityVersion}.");
        LogSource.LogInfo($"Probe enabled: {ProbeEnabled}");
        LogSource.LogInfo(
            $"Coop role={Settings.Role}, port={Settings.Port}, name='{Settings.PlayerName}', autopilot={Settings.AutoPilot}."
        );

        if (Settings.Role != CoopRole.Offline)
        {
            // The remote grip solver is a managed MonoBehaviour added at runtime.
            // IL2CPP must know the type before AddComponent can create it.
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<RemoteWeaponIK>();
                ClassInjector.RegisterTypeInIl2Cpp<RemoteHitProxy>();
                LogSource.LogInfo("Remote weapon IK and combat hit proxy components registered.");
            }
            catch (Exception exception)
            {
                LogSource.LogError(
                    $"Failed to register RemoteWeaponIK: {exception.GetType().Name}: {exception.Message}"
                );
            }

            Network = new CoopNetwork(Settings);
            Network.Start();
        }
        else
        {
            LogSource.LogInfo("Coop networking is off. Use -coop-host or -coop-connect <address> to enable.");
        }

        if (Network != null)
        {
            try
            {
                AddComponent<CoopRunner>();
                LogSource.LogInfo("Network tick: CoopRunner MonoBehaviour created.");
            }
            catch (Exception exception)
            {
                LogSource.LogError(
                    $"Failed to create CoopRunner: {exception.GetType().Name}: {exception.Message}"
                );
            }
        }

        var harmony = new Harmony(PluginInfo.Guid);

        void TryPatch(Type type)
        {
            try
            {
                harmony.PatchAll(type);
            }
            catch (Exception exception)
            {
                LogSource.LogWarning(
                    $"[harmony] patch '{type.Name}' failed: " +
                    $"{exception.GetType().Name}: {exception.Message}"
                );
            }
        }

        TryPatch(typeof(PlayerProbePatch));
        harmony.PatchAll(typeof(PlayerHierarchyProbe));
        harmony.PatchAll(typeof(ClientBlockSaveRequestPatch));
        harmony.PatchAll(typeof(ClientBlockQueuedSavePatch));
        harmony.PatchAll(typeof(ClientBlockAutosavePatch));

        if (Settings.Role != CoopRole.Offline)
        {
            TryPatch(typeof(DoorRegisterStartPatch));
            TryPatch(typeof(DoorRegisterOnStartPatch));
            TryPatch(typeof(ItemRegisterStartPatch));
            TryPatch(typeof(ItemRegisterOnStartPatch));
            TryPatch(typeof(ItemRegisterOnLoadPatch));
            TryPatch(typeof(ItemRegisterDroppedPatch));
            TryPatch(typeof(ItemRegisterDroppedDetailedPatch));
            TryPatch(typeof(ItemPickupEventPatch));
            TryPatch(typeof(ItemBottleBreakPatch));
            TryPatch(typeof(ItemGenericBreakPatch));
            TryPatch(typeof(WeaponFireSimplePatch));
            TryPatch(typeof(WeaponFireAtPointPatch));
            TryPatch(typeof(WeaponFireAtPointTriggerPatch));
            TryPatch(typeof(CombatFireSimplePatch));
            TryPatch(typeof(CombatFireAtPointPatch));
            TryPatch(typeof(CombatFireAtPointTriggerPatch));
            TryPatch(typeof(EnemyControllerUpdatePatch));
            TryPatch(typeof(EnemyMoverLateUpdatePatch));
            TryPatch(typeof(StoryConversationStartPatch));
            TryPatch(typeof(StoryQuestActionPatch));
            TryPatch(typeof(StoryTeleportWalkInPatch));
            TryPatch(typeof(StoryTeleportPatch));
            TryPatch(typeof(StoryPuzzleTeleportPatch));
            TryPatch(typeof(StoryCutscenePlayPatch));
            TryPatch(typeof(StoryDialogueActionPatch));
            TryPatch(typeof(StoryDialogueActionV2Patch));
            TryPatch(typeof(StoryGiftAllPatch));
            TryPatch(typeof(StoryGiftAtIndexPatch));
            TryPatch(typeof(DisableTimeControllerPausePatch));
            TryPatch(typeof(DisableFocusPausePatch));
            TryPatch(typeof(MainMenuOpenProbePatch));
            TryPatch(typeof(DeathRespawnPatch));
            TryPatch(typeof(DeathSavePointSavePatch));
            TryPatch(typeof(DeathSavePointQueuedPatch));
            TryPatch(typeof(DeathSavePointAutosavePatch));
            TryPatch(typeof(SaveLoadProcedurePatch));
            TryPatch(typeof(SceneTransitionPatch));
        }

        LogSource.LogInfo(
            "Harmony patches applied: probes, network tick, client save protection, " +
            "doors, items, weapons, combat and co-op pause policy."
        );
        LogSource.LogInfo("Ready. Solo test: launch a second instance; see docs/SOLO_TESTING.md.");
    }

    internal static void Tick(PlayerMovement? localPlayer)
    {
        DeathRespawn.Tick(localPlayer);
        Network?.Tick(localPlayer);
    }

    internal static void LateTick()
    {
        Network?.LateTick();
    }
}
