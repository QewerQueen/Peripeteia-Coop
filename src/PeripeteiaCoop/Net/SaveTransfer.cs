using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Copies the host's save to a joining player from inside the "Peripeteia Coop"
/// menu, so both machines start the run from the same file without the player
/// having to find the save folder by hand.
///
/// The host picks its most recently written save, sends a manifest (save name +
/// file list) and then streams the files in chunks over the reliable channel.
/// The client reassembles them under its own <c>EASaves</c> folder. Everything
/// runs on the main thread through <see cref="TickHost"/>, rate-limited so a
/// 1 MB save does not stall a frame.
/// </summary>
internal static class SaveTransfer
{
    private const int ChunkSize = 16 * 1024;
    private const int BudgetPerTick = 96 * 1024;
    private const string TempFolder = "temp";

    /// <summary>
    /// Prefix for a save downloaded from another player. Keeping the host's save
    /// under its own local name means it can never overwrite a save this player
    /// made alone, so solo play keeps working (Client.KeepPersonalInventory).
    /// </summary>
    private const string CoopSavePrefix = "Coop - ";

    /// <summary>Local save name a downloaded host save is written under.</summary>
    public static string LocalNameFor(string hostName)
    {
        return CoopSavePrefix + hostName;
    }

    /// <summary>
    /// True when a save name belongs to this machine's own co-op save (a save it
    /// downloaded from a host, or wrote itself under that name). Such saves live
    /// beside the player's solo saves and never touch the host's files, so a
    /// client is allowed to write them.
    /// </summary>
    public static bool IsCoopLocalSave(string? saveName)
    {
        return !string.IsNullOrEmpty(saveName) &&
            saveName!.StartsWith(CoopSavePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OutFile
    {
        public string Rel = string.Empty;
        public byte[] Data = Array.Empty<byte>();
    }

    private sealed class InFile
    {
        public string Rel = string.Empty;
        public byte[] Data = Array.Empty<byte>();
        public int Received;
    }

    private static readonly List<OutFile> OutFiles = new();
    private static string _outName = string.Empty;
    private static int _outIndex;
    private static int _outOffset;
    private static bool _sending;

    private static string _inName = string.Empty;
    private static List<InFile>? _inFiles;
    private static bool _receiving;
    private static float _nextProgressLog;

    private static int _sentCount;
    private static int _receivedCount;

    public static int SentCount => _sentCount;

    public static int ReceivedCount => _receivedCount;

    public static bool IsReceiving => _receiving;

    public static string LastReceivedName { get; private set; } = string.Empty;

    /// <summary>Folder that holds every save, next to the game's config.</summary>
    public static string SaveRoot => Path.Combine(Application.persistentDataPath, "EASaves");

    /// <summary>How many saves are on disk. Advertised to the host list.</summary>
    public static int CountSaves()
    {
        try
        {
            return Directory.Exists(SaveRoot) ? Directory.GetDirectories(SaveRoot).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // Host
    // ------------------------------------------------------------------

    /// <summary>A client asked for the save; start streaming the newest one.</summary>
    public static void OnSaveRequested()
    {
        OutFiles.Clear();
        _sending = false;

        string? directory = FindLatestSaveDirectory();
        if (directory == null)
        {
            CoopPlugin.LogSource.LogWarning("[save] host has no save to send.");
            CoopPlugin.Network?.SendSaveManifest(string.Empty, Array.Empty<string>(), Array.Empty<int>());
            return;
        }

        _outName = Path.GetFileName(directory);
        try
        {
            foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (relative.StartsWith(TempFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                OutFiles.Add(new OutFile { Rel = relative, Data = File.ReadAllBytes(path) });
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[save] could not read '{directory}': {exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        var rels = new string[OutFiles.Count];
        var sizes = new int[OutFiles.Count];
        int total = 0;
        for (int i = 0; i < OutFiles.Count; i++)
        {
            rels[i] = OutFiles[i].Rel;
            sizes[i] = OutFiles[i].Data.Length;
            total += OutFiles[i].Data.Length;
        }

        _outIndex = 0;
        _outOffset = 0;
        _sending = OutFiles.Count > 0;
        _sentCount++;

        CoopPlugin.LogSource.LogInfo(
            $"[save] sending save '{_outName}' to the client ({OutFiles.Count} files, {total} bytes)."
        );
        CoopPlugin.Network?.SendSaveManifest(_outName, rels, sizes);
    }

    /// <summary>Host pump: push the next slice of the save.</summary>
    public static void TickHost()
    {
        if (!_sending)
        {
            return;
        }

        int budget = BudgetPerTick;
        while (budget > 0 && _outIndex < OutFiles.Count)
        {
            OutFile file = OutFiles[_outIndex];
            if (_outOffset >= file.Data.Length)
            {
                _outIndex++;
                _outOffset = 0;
                continue;
            }

            int length = Math.Min(ChunkSize, file.Data.Length - _outOffset);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(file.Data, _outOffset, chunk, 0, length);
            CoopPlugin.Network?.SendSaveChunk(_outIndex, _outOffset, chunk);
            _outOffset += length;
            budget -= length;
        }

        if (_outIndex >= OutFiles.Count)
        {
            _sending = false;
            CoopPlugin.Network?.SendSaveDone();
            CoopPlugin.LogSource.LogInfo($"[save] finished sending save '{_outName}'.");
        }
    }

    // ------------------------------------------------------------------
    // Client
    // ------------------------------------------------------------------

    public static void OnManifest(string name, string[] rels, int[] sizes)
    {
        if (string.IsNullOrEmpty(name) || rels == null || rels.Length == 0)
        {
            CoopPlugin.LogSource.LogWarning("[save] host has no save to download.");
            return;
        }

        _inName = name;
        _inFiles = new List<InFile>(rels.Length);
        int total = 0;
        for (int i = 0; i < rels.Length; i++)
        {
            int size = i < sizes.Length ? Math.Max(0, sizes[i]) : 0;
            _inFiles.Add(new InFile { Rel = rels[i], Data = new byte[size] });
            total += size;
        }

        _receiving = true;
        CoopPlugin.LogSource.LogInfo(
            $"[save] downloading save '{name}' from the host ({rels.Length} files, {total} bytes)."
        );
    }

    public static void OnChunk(int index, int offset, byte[] data)
    {
        if (!_receiving || _inFiles == null || index < 0 || index >= _inFiles.Count)
        {
            return;
        }

        InFile file = _inFiles[index];
        if (offset < 0 || data == null || offset + data.Length > file.Data.Length)
        {
            return;
        }

        Buffer.BlockCopy(data, 0, file.Data, offset, data.Length);
        file.Received += data.Length;

        if (Time.unscaledTime >= _nextProgressLog)
        {
            _nextProgressLog = Time.unscaledTime + 2.0f;
            int done = 0;
            int total = 0;
            foreach (InFile f in _inFiles)
            {
                done += f.Received;
                total += f.Data.Length;
            }

            CoopPlugin.LogSource.LogInfo(
                $"[save] download progress {done}/{total} bytes."
            );
        }
    }

    public static void OnDone()
    {
        if (!_receiving || _inFiles == null)
        {
            return;
        }

        _receiving = false;
        List<InFile> files = _inFiles;
        _inFiles = null;

        // The host save is written under a local name so it can never overwrite a
        // save this player made on their own; that keeps solo play intact.
        string localName = LocalNameFor(_inName);
        string directory = Path.Combine(SaveRoot, localName);
        try
        {
            Directory.CreateDirectory(directory);
            int written = 0;
            int keptPersonal = 0;
            foreach (InFile file in files)
            {
                string target = Path.Combine(
                    directory,
                    file.Rel.Replace('/', Path.DirectorySeparatorChar)
                );
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // inv.sfile is this player's own inventory / money / health scene
                // save. Keep the local copy so a rejoin never resets the kit to the
                // host's; only a first join (no local copy yet) takes the host's.
                if (CoopPlugin.ClientKeepPersonalInventory &&
                    IsPersonalFile(file.Rel) &&
                    File.Exists(target))
                {
                    keptPersonal++;
                    continue;
                }

                File.WriteAllBytes(target, file.Data);
                written++;
            }

            LastReceivedName = localName;
            _receivedCount++;
            CoopPlugin.LogSource.LogInfo(
                $"[save] wrote host save '{_inName}' as '{localName}' ({written} files) " +
                $"to '{directory}'" +
                (keptPersonal > 0 ? ", kept the local personal inventory save." : ".")
            );

            if (CoopPlugin.Settings.Role == CoopRole.Client && CoopPlugin.AutoStartDownloadedSave)
            {
                SaveLoadSync.QueueStartFromMainMenu(localName);
                CoopPlugin.LogSource.LogInfo(
                    $"[save] Will start the run from '{localName}' once the main menu is ready."
                );
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[save] could not write the host save: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// The per-player inventory-scene file inside a save. Everything else in a
    /// save is the shared world; this one file is the player's own inventory,
    /// money and health.
    /// </summary>
    private static bool IsPersonalFile(string relative)
    {
        return string.Equals(relative, "inv.sfile", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindLatestSaveDirectory()
    {
        try
        {
            if (!Directory.Exists(SaveRoot))
            {
                return null;
            }

            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string directory in Directory.GetDirectories(SaveRoot))
            {
                // Never send back a save this machine downloaded from another
                // host: those live under the 'Coop - ' prefix.
                if (Path.GetFileName(directory).StartsWith(CoopSavePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime time = Directory.GetLastWriteTimeUtc(directory);
                if (time > bestTime)
                {
                    bestTime = time;
                    best = directory;
                }
            }

            return best;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[save] could not list saves: {exception.GetType().Name}: {exception.Message}"
            );
            return null;
        }
    }

    internal static string FormatBytes(int bytes)
    {
        return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
    }
}
