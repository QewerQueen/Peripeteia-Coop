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
    /// Debug-only solo test mode: a scene name to jump to once the session is up,
    /// so a retest can prove that a level transition is shared with the other
    /// machine without having to find a level exit. Never set unless
    /// -coop-scene-test is passed.
    /// </summary>
    public string SceneTest { get; private set; } = string.Empty;

    public bool GameAnimator { get; private set; } = true;

    public string LoadSaveName { get; private set; } = string.Empty;

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
            }
        }

        return settings;
    }
}
