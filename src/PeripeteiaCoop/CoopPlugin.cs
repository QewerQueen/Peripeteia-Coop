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

    internal static bool ItemsSpawnMissing { get; private set; }

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

    internal static CoopSettings Settings { get; private set; } = null!;

    internal static CoopNetwork? Network { get; private set; }

    internal static bool IsCoopActive => Settings.Role != CoopRole.Offline;

    internal static bool ShouldBlockClientSaves =>
        BlockClientSaves && Settings.Role == CoopRole.Client;

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
            false,
            "Experimental: when a player drops an item this side never had, clone a matching template from the scene so it can be picked up. May spawn the wrong object; off by default."
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
            "Share the quest-log effects of a dialogue branch (NPCConversation.SetQuestLog / RemoveQuestLog). Item and reward actions are never replayed."
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
            TryPatch(typeof(DisableTimeControllerPausePatch));
            TryPatch(typeof(DisableFocusPausePatch));
            TryPatch(typeof(MainMenuOpenProbePatch));
        }

        LogSource.LogInfo(
            "Harmony patches applied: probes, network tick, client save protection, " +
            "doors, items, weapons, combat and co-op pause policy."
        );
        LogSource.LogInfo("Ready. Solo test: launch a second instance; see docs/SOLO_TESTING.md.");
    }

    internal static void Tick(PlayerMovement? localPlayer)
    {
        Network?.Tick(localPlayer);
    }

    internal static void LateTick()
    {
        Network?.LateTick();
    }
}
