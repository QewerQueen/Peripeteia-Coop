using System;

namespace PeripeteiaCoop;

internal enum CoopRole
{
    Offline,
    Host,
    Client,
}

/// <summary>
/// Parsed command-line settings. Defaults keep the mod inactive during normal
/// single-player launches.
/// </summary>
internal sealed class CoopSettings
{
    public CoopRole Role { get; private set; } = CoopRole.Offline;

    public string Address { get; private set; } = "127.0.0.1";

    public int Port { get; private set; } = 7777;

    public string PlayerName { get; private set; } = "Player";

    public bool AutoPilot { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: instead of circling, the autopilot walks the
    /// local player towards the nearest enemy so a host can observe whether its
    /// AI reacts to the client. Never enabled unless -coop-autopilot-hunt is
    /// passed.
    /// </summary>
    public bool AutoPilotHunt { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: the client periodically deals damage to the
    /// nearest NPC so the host can prove that client damage is forwarded and the
    /// enemy turns against the client. Never enabled unless -coop-autopilot-shoot
    /// is passed.
    /// </summary>
    public bool AutoPilotShoot { get; private set; }

    public bool DoorTest { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: the host automatically calls the first
    /// multi-floor elevator every few seconds, so the elevator sync (a client
    /// call reaches the host, and the host's floor reaches the client) can be
    /// retested on one machine. Never set unless -coop-elevator-test is passed.
    /// </summary>
    public bool ElevatorTest { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: the host automatically grabs a dead teammate's
    /// body and carries it around, so a retest can prove that a corpse drag is
    /// shared with the body's owner without having to die and hold a key on two
    /// machines. Never set unless -coop-corpse-test is passed.
    /// </summary>
    public bool CorpseTest { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: a scene name to jump to once the session is up,
    /// so a retest can prove that a level transition is shared with the other
    /// machine without having to find a level exit. Never set unless
    /// -coop-scene-test is passed.
    /// </summary>
    public string SceneTest { get; private set; } = string.Empty;

    public bool GameAnimator { get; private set; } = true;

    public string LoadSaveName { get; private set; } = string.Empty;

    /// <summary>
    /// Debug-only solo test mode: dump the story components (VIDE_Assign,
    /// keycard, questmanagerTUTORIAL) present in the loaded scene, so a retest
    /// can see what the mod has to work with. Never enabled unless
    /// -coop-story-scan is passed.
    /// </summary>
    public bool StoryScan { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: force one game save once in a level, so a
    /// retest can prove that a co-op client is allowed to write its own
    /// 'Coop - &lt;name&gt;' save (and thus keep its personal inventory). Never
    /// enabled unless -coop-save-test is passed.
    /// </summary>
    public bool SaveTest { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: force the local player to die once the level
    /// has streamed in, so the co-op death respawn (respawn at the save point,
    /// world kept, no reload) can be retested on one machine. Never enabled
    /// unless -coop-death-test is passed.
    /// </summary>
    public bool DeathTest { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: the name of a key/quest item to pick up once in
    /// a level, so a retest can prove that a key picked up by one player stays in
    /// the other player's world instead of vanishing. Never set unless
    /// -coop-key-test is passed.
    /// </summary>
    public string KeyTestName { get; private set; } = string.Empty;

    /// <summary>
    /// Debug-only solo test mode: the host force-kills the nearest NPC every few
    /// seconds, so a retest can prove that a death the client never dealt itself
    /// still produces a corpse on the client (the "NPC corpses do not appear"
    /// report). Never set unless -coop-npc-death-test is passed.
    /// </summary>
    public bool NpcDeathTest { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: dump every loot container in the loaded scene
    /// (corpses, crates, shops) with its item list, so the container sync can be
    /// designed against the real object graph. Never set unless
    /// -coop-container-scan is passed.
    /// </summary>
    public bool ContainerScan { get; private set; }

    /// <summary>
    /// Debug-only solo test mode: the host periodically takes one item out of a
    /// corpse's container so a retest can prove the other machine loses the same
    /// item. Never set unless -coop-loot-test is passed.
    /// </summary>
    public bool LootTest { get; private set; }

    /// <summary>Debug-only: same as -coop-loot-test but driven by the client.</summary>
    public bool LootTestClient { get; private set; }

    /// <summary>
    /// Reconfigure the session at runtime, for the in-game "Peripeteia Coop"
    /// menu. The command line still works: it just calls these before the first
    /// frame. A session cannot change role once it is running.
    /// </summary>
    public void Configure(CoopRole role, string playerName)
    {
        Role = role;
        if (!string.IsNullOrEmpty(playerName))
        {
            PlayerName = playerName;
        }
    }

    public void SetAddress(string address)
    {
        if (!string.IsNullOrEmpty(address))
        {
            Address = address;
        }
    }

    public void SetPort(int port)
    {
        if (port > 0 && port <= 65535)
        {
            Port = port;
        }
    }

    public static CoopSettings Parse(string[] args)
    {
        var settings = new CoopSettings();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();

            switch (arg)
            {
                case "-coop-host":
                case "--coop-host":
                    settings.Role = CoopRole.Host;
                    break;

                case "-coop-connect":
                case "--coop-connect":
                    settings.Role = CoopRole.Client;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        settings.Address = args[++i];
                    }
                    break;

                case "-coop-port":
                case "--coop-port":
                    if (i + 1 < args.Length &&
                        int.TryParse(args[++i], out int port) &&
                        port > 0 &&
                        port <= 65535)
                    {
                        settings.Port = port;
                    }
                    break;

                case "-coop-name":
                case "--coop-name":
                    if (i + 1 < args.Length)
                    {
                        settings.PlayerName = args[++i];
                    }
                    break;

                case "-coop-autopilot":
                case "--coop-autopilot":
                    settings.AutoPilot = true;
                    break;

                case "-coop-autopilot-hunt":
                case "--coop-autopilot-hunt":
                    settings.AutoPilot = true;
                    settings.AutoPilotHunt = true;
                    break;

                case "-coop-autopilot-shoot":
                case "--coop-autopilot-shoot":
                    settings.AutoPilot = true;
                    settings.AutoPilotShoot = true;
                    break;

                case "-coop-door-test":
                case "--coop-door-test":
                    settings.DoorTest = true;
                    break;

                case "-coop-elevator-test":
                case "--coop-elevator-test":
                    settings.ElevatorTest = true;
                    break;

                case "-coop-corpse-test":
                case "--coop-corpse-test":
                    settings.CorpseTest = true;
                    break;

                case "-coop-scene-test":
                case "--coop-scene-test":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        settings.SceneTest = args[++i];
                    }
                    break;

                case "-coop-game-animator":
                case "--coop-game-animator":
                    settings.GameAnimator = true;
                    break;

                case "-coop-procedural-animation":
                case "--coop-procedural-animation":
                    settings.GameAnimator = false;
                    break;

                case "-coop-load":
                case "--coop-load":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        settings.LoadSaveName = args[++i];
                    }
                    break;

                case "-coop-story-scan":
                case "--coop-story-scan":
                    settings.StoryScan = true;
                    break;

                case "-coop-save-test":
                case "--coop-save-test":
                    settings.SaveTest = true;
                    break;

                case "-coop-death-test":
                case "--coop-death-test":
                    settings.DeathTest = true;
                    break;

                case "-coop-key-test":
                case "--coop-key-test":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        settings.KeyTestName = args[++i];
                    }
                    break;

                case "-coop-npc-death-test":
                case "--coop-npc-death-test":
                    settings.NpcDeathTest = true;
                    break;

                case "-coop-container-scan":
                case "--coop-container-scan":
                    settings.ContainerScan = true;
                    break;

                case "-coop-loot-test":
                case "--coop-loot-test":
                    settings.LootTest = true;
                    break;

                case "-coop-loot-test-client":
                case "--coop-loot-test-client":
                    settings.LootTestClient = true;
                    break;
            }
        }

        return settings;
    }
}
