using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
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

    internal static CoopSettings Settings { get; private set; } = null!;

    internal static CoopNetwork? Network { get; private set; }

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

        LogSource.LogInfo(
            "Harmony patches applied: probes, network tick, client save protection."
        );
        LogSource.LogInfo("Ready. Solo test: launch a second instance; see docs/SOLO_TESTING.md.");
    }

    internal static void Tick(PlayerMovement? localPlayer)
    {
        Network?.Tick(localPlayer);
    }
}
