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

    internal static CoopSettings Settings { get; private set; } = null!;

    internal static CoopNetwork? Network { get; private set; }

    internal static bool IsCoopActive => Settings.Role != CoopRole.Offline;

    internal static bool ShouldBlockClientSaves =>
        BlockClientSaves && Settings.Role == CoopRole.Client;

    public override void Load()
    {
        Instance = this;
        LogSource = Log;

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

        Settings = CoopSettings.Parse(Environment.GetCommandLineArgs());

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
                LogSource.LogInfo("Remote weapon IK component registered.");
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
        harmony.PatchAll(typeof(PlayerProbePatch));
        harmony.PatchAll(typeof(PlayerHierarchyProbe));
        harmony.PatchAll(typeof(ClientBlockSaveRequestPatch));
        harmony.PatchAll(typeof(ClientBlockQueuedSavePatch));
        harmony.PatchAll(typeof(ClientBlockAutosavePatch));

        if (Settings.Role != CoopRole.Offline)
        {
            harmony.PatchAll(typeof(DoorRegisterStartPatch));
            harmony.PatchAll(typeof(DoorRegisterOnStartPatch));
            harmony.PatchAll(typeof(ItemRegisterStartPatch));
            harmony.PatchAll(typeof(ItemRegisterOnStartPatch));
            harmony.PatchAll(typeof(ItemRegisterOnLoadPatch));
            harmony.PatchAll(typeof(ItemRegisterDroppedPatch));
            harmony.PatchAll(typeof(ItemRegisterDroppedDetailedPatch));
            harmony.PatchAll(typeof(WeaponFireSimplePatch));
            harmony.PatchAll(typeof(WeaponFireAtPointPatch));
            harmony.PatchAll(typeof(WeaponFireAtPointTriggerPatch));
            harmony.PatchAll(typeof(DisableTimeControllerPausePatch));
            harmony.PatchAll(typeof(DisableFocusPausePatch));
            harmony.PatchAll(typeof(MainMenuOpenProbePatch));
        }

        LogSource.LogInfo(
            "Harmony patches applied: probes, network tick, client save protection, " +
            "doors, items, weapons and co-op pause policy."
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
