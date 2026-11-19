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

    internal static bool CombatLimbHitboxes { get; private set; } = true;

    internal static bool WeaponSyncAttachments { get; private set; } = true;

    internal static bool PlayerSyncSkins { get; private set; } = true;

    internal static bool TeammateHud { get; private set; } = true;

    internal static bool ItemsSpawnMissing { get; private set; } = true;

    internal static bool ItemsShareKeys { get; private set; } = true;

    internal static bool ItemsHostAuthoritative { get; private set; } = true;

    internal static bool AiEnabled { get; private set; } = true;

    internal static bool AiSyncDeaths { get; private set; } = true;

    internal static bool AiMirrorEnemies { get; private set; } = true;

    internal static bool AiFreezeClientAi { get; private set; } = true;

    internal static bool AiTargetRemotePlayer { get; private set; } = true;

    internal static bool AiPerceptionCollider { get; private set; } = true;

    internal static bool AiDamageRemotePlayer { get; private set; } = true;

    internal static bool AiNpcDamageFromClient { get; private set; } = true;

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

    internal static bool StorySyncFlags { get; private set; } = true;

    internal static bool StorySyncQuestEvents { get; private set; } = true;

    internal static bool SyncSceneTransitions { get; private set; } = true;

    internal static bool WorldSyncElevators { get; private set; } = true;

    internal static bool PlayerSyncDeath { get; private set; } = true;

    internal static bool PlayerDeathRagdoll { get; private set; } = true;

    internal static bool PlayerRespawnOnDeath { get; private set; } = true;

    internal static bool PlayerDragRemoteBody { get; private set; } = true;

    internal static bool BlockClientRollback { get; private set; } = true;

    internal static bool ClientAutosavePersonalState { get; private set; } = true;

    internal static bool AdvertiseEnabled { get; private set; } = true;

    internal static bool AutoDownloadSave { get; private set; } = true;

    internal static bool AutoStartDownloadedSave { get; private set; } = true;

    internal static bool SpawnNearHost { get; private set; } = true;

    internal static bool ClientKeepPersonalInventory { get; private set; } = true;

    internal static string DefaultPlayerName { get; private set; } = string.Empty;

    internal static CoopSettings Settings { get; private set; } = null!;

    internal static CoopNetwork? Network { get; private set; }

    private static Harmony? _harmony;
    private static CoopRunner? _runner;
    private static CoopMenu? _menu;
    private static bool _coopPatchesApplied;
    private static bool _typesRegistered;

    internal static bool IsCoopActive => Settings.Role != CoopRole.Offline;

    internal static bool ShouldBlockClientSaves =>
        BlockClientSaves &&
        Settings.Role == CoopRole.Client &&
        !SaveTransfer.IsCoopLocalSave(SaveVersionController.currentSaveName);

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

        ClientAutosavePersonalState = Config.Bind(
            "Client",
            "AutosavePersonalState",
            true,
            "Client only: when a level starts, write this player's own co-op save ('Coop - <name>'), the way the host autosaves. That is what keeps your inventory, money and health across rejoins even when the game's own autosave is off. Saves still go only to your own co-op save, never the host's."
        ).Value;

        ClientKeepPersonalInventory = Config.Bind(
            "Client",
            "KeepPersonalInventory",
            true,
            "Client only: keep this player's own inventory, money and health when the host's save is downloaded again. The host save is stored under a local 'Coop - <name>' folder, and this player's own inventory save (inv.sfile) is left untouched there, so rejoining never resets your kit to the host's. Turn off to always take the host's loadout."
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

        CombatLimbHitboxes = Config.Bind(
            "Combat",
            "LimbHitboxes",
            true,
            "Give the remote player capsule hitboxes on the arms and legs (following the animated bones) so shots at a limb register instead of passing through. Turn off to keep only the torso capsule and head sphere."
        ).Value;

        WeaponSyncAttachments = Config.Bind(
            "Weapon",
            "SyncAttachments",
            true,
            "Mirror the sender's per-part visibility (attachments, magazine, folded stock) onto the remote weapon visual. Turn off to always show the plain template weapon."
        ).Value;

        PlayerSyncSkins = Config.Bind(
            "Player",
            "SyncSkins",
            true,
            "Give the remote teammate a different character model than your own (each peer keeps its own Marie skin), so the two players in co-op do not look like clones of each other."
        ).Value;

        TeammateHud = Config.Bind(
            "Player",
            "TeammateHud",
            true,
            "Draw a small panel with the teammate's name, health and down/dead state while connected."
        ).Value;

        ItemsSpawnMissing = Config.Bind(
            "Items",
            "SpawnMissing",
            true,
            "When a player drops an item this side never had (for example a quest reward), clone a matching template from the scene so the teammate can see and pick it up. Turn off to only sync items both sides already have."
        ).Value;

        ItemsShareKeys = Config.Bind(
            "Items",
            "ShareKeyItems",
            true,
            "Key/quest items (the item a keycard or access point checks for) are not synchronized as world pickups: each player keeps their own copy, so both can pick up the key and open the same door. Turn off to make keys single-pickup like every other item."
        ).Value;

        ItemsHostAuthoritative = Config.Bind(
            "Items",
            "HostAuthoritative",
            true,
            "Host-authoritative world items: only the host broadcasts an item's position and visibility, while a client sends just its own actions (pickup, drop, break, drag) as requests. This stops the two machines from fighting over the same object (no more last-writer-wins). Turn off to go back to the old peer-to-peer item sync."
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
            "Host only: register the remote player as an AI target so enemies can notice, chase and shoot at the client. The puppet also gets an inert HealthScript, because the game ignores any target without one. Experimental."
        ).Value;

        AiPerceptionCollider = Config.Bind(
            "Ai",
            "PerceptionCollider",
            true,
            "Host only: add an inert collider on the layer the game's AI uses to detect the player. The combat proxy lives on the player's own (Ignore Raycast) layer, so without this the perception rays pass through the puppet. Requires TargetRemotePlayer."
        ).Value;

        AiDamageRemotePlayer = Config.Bind(
            "Ai",
            "DamageRemotePlayer",
            true,
            "Host only: NPC shots that hit the client deal damage on the client's machine (through its real HealthScript). NPC weapons use the same Shoot component as the player's, so this reuses the player-versus-player hit path. Requires TargetRemotePlayer."
        ).Value;

        AiNpcDamageFromClient = Config.Bind(
            "Ai",
            "NpcDamageFromClient",
            true,
            "Client only: forward damage the client deals to an NPC to the host, which applies it to the real enemy and turns it against the client. Without this the client's hits on enemies are discarded and the NPC never reacts."
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

        StorySyncFlags = Config.Bind(
            "Story",
            "SyncFlags",
            true,
            "Mirror the game's named save flags (FlagsForSaveFiles.SetFlag / SetTrue / SetFalse) between the machines, so both agree on which quests are done. A flag changed during play is set for the other player; a save load does not broadcast."
        ).Value;

        StorySyncQuestEvents = Config.Bind(
            "Story",
            "SyncQuestEvents",
            true,
            "Mirror the intro tutorial quest manager (questmanagerTUTORIAL.triggerevent / blockevents / unblockevents), so both players see the same tutorial steps and their side effects."
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

        WorldSyncElevators = Config.Bind(
            "World",
            "SyncElevators",
            true,
            "Share the world elevators (and the trains/conveyors that use the same component): whoever presses the call button, both players ride the same cabin, and the host's floor keeps a late joiner in step. Turn off to let each player move the lifts independently."
        ).Value;

        BlockClientRollback = Config.Bind(
            "Client",
            "BlockRollback",
            true,
            "Stop a co-op client from loading a save mid-level on its own; that would rewind the client's copy of the shared world and desync it from the host. A rollback is a host decision: the host loads the save and the client loads the same one on request. The initial load and scene transitions are still allowed."
        ).Value;

        AdvertiseEnabled = Config.Bind(
            "Net",
            "Advertise",
            true,
            "Host only: broadcast this machine on the LAN so it appears in the other player's 'Peripeteia Coop' host list. Turn off to stay silent (players can still connect with -coop-connect)."
        ).Value;

        AutoDownloadSave = Config.Bind(
            "Net",
            "AutoDownloadSave",
            true,
            "Client only: on connect, automatically download the host's newest save so both machines start from the same file. Turn off to only download it from the menu button."
        ).Value;

        AutoStartDownloadedSave = Config.Bind(
            "Net",
            "AutoStartDownloadedSave",
            true,
            "Client only: after the host's save has been downloaded, start the run from it automatically instead of waiting for the player to open the game's load menu. Only happens while the client is still on the main menu."
        ).Value;

        SpawnNearHost = Config.Bind(
            "Net",
            "SpawnNearHost",
            true,
            "Client only: on connect (and on entering a new level) put this player next to the host instead of at the save point. The game drops a freshly loaded player at a temporary spawn point high above the map and then lets it fall, so without this the client would start far away from the host. Turn off to always start at the save point."
        ).Value;

        DefaultPlayerName = Config.Bind(
            "Net",
            "PlayerName",
            Environment.MachineName,
            "Name shown to the other player and in the LAN host list."
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

        LogSource.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loading.");
        LogSource.LogInfo($"Game: {Application.productName} {Application.version}; Unity {Application.unityVersion}.");
        LogSource.LogInfo($"Probe enabled: {ProbeEnabled}");
        LogSource.LogInfo(
            $"Coop role={Settings.Role}, port={Settings.Port}, name='{Settings.PlayerName}', autopilot={Settings.AutoPilot}."
        );

        RegisterRuntimeTypes();

        _harmony = new Harmony(PluginInfo.Guid);

        // Always-on patches: probes and client save protection. Each is applied
        // through TryPatch so a single missing game method (for example in the
        // demo build, which lacks SaveHandler.AutosaveOnLevelStart) only skips
        // that one patch instead of aborting the whole plugin.
        TryPatch(typeof(PlayerProbePatch));
        TryPatch(typeof(PlayerHierarchyProbe));
        TryPatch(typeof(ClientBlockSaveRequestPatch));
        TryPatch(typeof(ClientBlockQueuedSavePatch));
        TryPatch(typeof(ClientBlockAutosavePatch));

        // The "Peripeteia Coop" menu button and LAN discovery exist even in a
        // plain single-player launch, so a player can host or join without any
        // command-line arguments.
        AddMenu();

        if (Settings.Role != CoopRole.Offline)
        {
            ApplyCoopPatches();
            EnsureSessionInfrastructure();
        }
        else
        {
            LogSource.LogInfo(
                "Coop networking is off. Open the in-game 'Peripeteia Coop' menu to host or join, " +
                "or use -coop-host / -coop-connect <address>."
            );
        }

        LogSource.LogInfo("Ready. Solo test: launch a second instance; see docs/SOLO_TESTING.md.");
    }

    /// <summary>
    /// Register the managed MonoBehaviours the mod adds at runtime. IL2CPP must
    /// know a type before AddComponent can create it. Safe to call repeatedly.
    /// </summary>
    private static void RegisterRuntimeTypes()
    {
        if (_typesRegistered)
        {
            return;
        }

        _typesRegistered = true;

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<RemoteWeaponIK>();
            ClassInjector.RegisterTypeInIl2Cpp<RemoteHitProxy>();
            ClassInjector.RegisterTypeInIl2Cpp<CoopMenu>();
            LogSource.LogInfo("Runtime components registered (weapon IK, hit proxy, co-op menu).");
        }
        catch (Exception exception)
        {
            LogSource.LogError(
                $"Failed to register runtime components: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void AddMenu()
    {
        try
        {
            _menu = Instance.AddComponent<CoopMenu>();
            LogSource.LogInfo("In-game co-op menu created.");
        }
        catch (Exception exception)
        {
            LogSource.LogError(
                $"Failed to create the co-op menu: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void TryPatch(Type type)
    {
        if (_harmony == null)
        {
            return;
        }

        try
        {
            _harmony.PatchAll(type);
        }
        catch (Exception exception)
        {
            LogSource.LogWarning(
                $"[harmony] patch '{type.Name}' failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// The gameplay patches. Applied at load when the role came from the command
    /// line, or later when the player hosts/joins from the menu. Guarded so it
    /// only ever runs once.
    /// </summary>
    private static void ApplyCoopPatches()
    {
        if (_coopPatchesApplied)
        {
            return;
        }

        _coopPatchesApplied = true;

        TryPatch(typeof(DoorRegisterStartPatch));
        TryPatch(typeof(DoorRegisterOnStartPatch));
        TryPatch(typeof(ElevatorSelectFloorPatch));
        TryPatch(typeof(ElevatorRegisterStartPatch));
        TryPatch(typeof(ElevatorRegisterOnStartPatch));
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
        TryPatch(typeof(NpcShotFireSimplePatch));
        TryPatch(typeof(NpcShotFireAtPointPatch));
        TryPatch(typeof(NpcShotFireAtPointTriggerPatch));
        TryPatch(typeof(CombatFireSimplePatch));
        TryPatch(typeof(CombatFireAtPointPatch));
        TryPatch(typeof(CombatFireAtPointTriggerPatch));
        TryPatch(typeof(EnemyControllerUpdatePatch));
        TryPatch(typeof(EnemyMoverLateUpdatePatch));
        TryPatch(typeof(AiCalculateThreatPatch));
        TryPatch(typeof(SensesClearTargetsPatch));
        TryPatch(typeof(NpcDamageTakePatch));
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
        TryPatch(typeof(StoryFlagSetPatch));
        TryPatch(typeof(StoryFlagSetIndexPatch));
        TryPatch(typeof(StoryFlagSetTruePatch));
        TryPatch(typeof(StoryFlagSetFalsePatch));
        TryPatch(typeof(StoryFlagSetTrueIndexPatch));
        TryPatch(typeof(StoryFlagSetFalseIndexPatch));
        TryPatch(typeof(StoryQuestTriggerEventPatch));
        TryPatch(typeof(StoryQuestBlockEventsPatch));
        TryPatch(typeof(StoryQuestUnblockEventsPatch));
        TryPatch(typeof(DisableTimeControllerPausePatch));
        TryPatch(typeof(DisableFocusPausePatch));
        TryPatch(typeof(MainMenuOpenProbePatch));
        TryPatch(typeof(DeathRespawnPatch));
        TryPatch(typeof(DeathSavePointSavePatch));
        TryPatch(typeof(DeathSavePointQueuedPatch));
        TryPatch(typeof(DeathSavePointAutosavePatch));
        TryPatch(typeof(SaveLoadProcedurePatch));
        TryPatch(typeof(SceneTransitionPatch));

        LogSource.LogInfo(
            "Co-op patches applied: doors, items, weapons, combat, story, death and level transitions."
        );
    }

    /// <summary>
    /// Bring up the network session (host or client) and its heartbeat. Called
    /// at load for a command-line role, or from the menu button at runtime.
    /// </summary>
    internal static void EnsureSessionInfrastructure()
    {
        // Critical for testing two instances on one PC: the unfocused window
        // must keep simulating the game world. Not touched in a solo launch.
        Application.runInBackground = true;

        RegisterRuntimeTypes();

        if (Network == null)
        {
            Network = new CoopNetwork(Settings);
            Network.Start();
        }

        if (_runner == null)
        {
            try
            {
                _runner = Instance.AddComponent<CoopRunner>();
                LogSource.LogInfo("Network tick: CoopRunner MonoBehaviour created.");
            }
            catch (Exception exception)
            {
                LogSource.LogError(
                    $"Failed to create CoopRunner: {exception.GetType().Name}: {exception.Message}"
                );
            }
        }
    }

    /// <summary>Menu action: host a session on the configured port.</summary>
    internal static void StartHostSession(string? playerName)
    {
        if (IsCoopActive)
        {
            LogSource.LogWarning("[menu] a co-op session is already running.");
            return;
        }

        Settings.Configure(
            CoopRole.Host,
            string.IsNullOrEmpty(playerName) ? DefaultPlayerName : playerName!
        );
        ApplyCoopPatches();
        EnsureSessionInfrastructure();
        LogSource.LogInfo($"[menu] hosting as '{Settings.PlayerName}' on UDP {Settings.Port}.");
    }

    /// <summary>Menu action: join the host at the given address.</summary>
    internal static void JoinSession(string address, int port, string? playerName)
    {
        if (IsCoopActive)
        {
            LogSource.LogWarning("[menu] a co-op session is already running.");
            return;
        }

        if (string.IsNullOrEmpty(address))
        {
            LogSource.LogWarning("[menu] no host address to join.");
            return;
        }

        Settings.Configure(
            CoopRole.Client,
            string.IsNullOrEmpty(playerName) ? DefaultPlayerName : playerName!
        );
        Settings.SetAddress(address);
        Settings.SetPort(port);
        ApplyCoopPatches();
        EnsureSessionInfrastructure();
        LogSource.LogInfo($"[menu] joining {address}:{port} as '{Settings.PlayerName}'.");
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
