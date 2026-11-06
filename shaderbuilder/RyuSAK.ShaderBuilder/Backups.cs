using System.Diagnostics;
using System.Text.Json;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// Backs up a game's shared shader cache before RyuSAK changes it, and restores it on request.
/// Backups live next to the cache in games/&lt;title ID&gt;/cache/ryusak-shader-backups, which Ryubing ignores.
/// Only the shared and guest files are copied: RyuSAK never writes Ryubing's compiled (host) cache, and when a
/// restore makes it stale it is deleted so Ryubing rebuilds it.
/// </summary>
static class Backups
{
    private const int Keep = 3;
    private const string ManifestName = "ryusak-backup.json";

    // Data files come before their index, so if a swap is interrupted an index never points past its data.
    private static readonly string[] SharedFiles = { "guest.data", "guest.toc", "shared.data", "shared.toc" };

    public static string ShaderDir(string dataDir, ulong titleId) =>
        Path.Combine(dataDir, "games", titleId.ToString("x16"), "cache", "shader");

    private static string BackupRoot(string dataDir, ulong titleId) =>
        Path.Combine(dataDir, "games", titleId.ToString("x16"), "cache", "ryusak-shader-backups");

    public static List<string> List(string dataDir, ulong titleId)
    {
        string root = BackupRoot(dataDir, titleId);
        if (!Directory.Exists(root))
        {
            return new List<string>();
        }

        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, ManifestName)))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    public static string Create(string dataDir, ulong titleId, int programsBefore, string reason)
    {
        string source = ShaderDir(dataDir, titleId);
        string target = Path.Combine(BackupRoot(dataDir, titleId), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(target);

        List<string> files = new();
        foreach (string name in SharedFiles)
        {
            string file = Path.Combine(source, name);
            if (File.Exists(file))
            {
                File.Copy(file, Path.Combine(target, name), overwrite: true);
                files.Add(name);
            }
        }

        File.WriteAllText(Path.Combine(target, ManifestName), Output.ToNode(new Dictionary<string, object>
        {
            ["created"] = DateTime.UtcNow.ToString("o"),
            ["reason"] = reason,
            ["programsBefore"] = programsBefore,
            ["files"] = files,
        }).ToJsonString());

        List<string> all = List(dataDir, titleId);
        foreach (string old in all.Take(Math.Max(0, all.Count - Keep)))
        {
            Directory.Delete(old, recursive: true);
        }

        return target;
    }

    /// <summary>
    /// Puts back the newest backup and deletes it. Programs Ryubing compiled since then are lost, which is why
    /// removing RyuSAK's shaders (undo) is the normal way back and this is only a fallback.
    /// </summary>
    public static string RestoreLatest(string dataDir, ulong titleId)
    {
        List<string> all = List(dataDir, titleId);
        if (all.Count == 0)
        {
            throw new BuilderException("NO_BACKUP", "There is no RyuSAK backup of this game's shader cache to restore.");
        }

        string backup = all[^1];
        string shaderDir = ShaderDir(dataDir, titleId);
        Directory.CreateDirectory(shaderDir);

        int programsBefore = -1;
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(backup, ManifestName))))
        {
            if (manifest.RootElement.TryGetProperty("programsBefore", out JsonElement value) && value.TryGetInt32(out int count))
            {
                programsBefore = count;
            }
        }

        foreach (string name in SharedFiles)
        {
            string current = Path.Combine(shaderDir, name);
            string saved = Path.Combine(backup, name);
            if (File.Exists(saved))
            {
                File.Copy(saved, current, overwrite: true);
            }
            else if (File.Exists(current))
            {
                File.Delete(current);
            }
        }

        RyubingCache.ResetHostCache(shaderDir, programsBefore);
        BuiltRegistry.Clear(dataDir, titleId);
        Directory.Delete(backup, recursive: true);
        return backup;
    }

    /// <summary>
    /// Ryubing must not be running while its cache files are replaced.
    /// </summary>
    public static bool EmulatorRunning()
    {
        foreach (string name in new[] { "Ryujinx", "Ryubing" })
        {
            Process[] processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (Process process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends entries to a copy of the cache, then swaps the copy in, so a failure never leaves a half-written cache.
    /// The new entries are recorded so they can be told apart from the game's own later.
    /// </summary>
    public static int WriteThroughStaging(string dataDir, ulong titleId, IReadOnlyList<NewProgram> programs, Action<int, int> progress)
    {
        string shaderDir = ShaderDir(dataDir, titleId);
        string staging = StagingDir(dataDir, titleId);

        PrepareStaging(staging, shaderDir);

        try
        {
            int guestBefore = GuestCount(staging);
            List<ulong> fingerprints = RyubingCache.Append(staging, programs, progress);
            BuiltRegistry.Add(dataDir, titleId, fingerprints, guestBefore);
            SwapIn(staging, shaderDir);
            return fingerprints.Count;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// Removes every program RyuSAK added, keeping the ones Ryubing compiled while the game ran.
    /// </summary>
    public static (int Removed, int Kept, bool HostReset) RemoveBuilt(string dataDir, ulong titleId)
    {
        (HashSet<ulong> built, int guestBefore) = BuiltRegistry.LoadWithGuestCount(dataDir, titleId);
        string shaderDir = ShaderDir(dataDir, titleId);
        string staging = StagingDir(dataDir, titleId);

        if (built.Count == 0)
        {
            throw new BuilderException("NOTHING_BUILT", "RyuSAK has not added shaders to this game's cache.");
        }

        PrepareStaging(staging, shaderDir);

        try
        {
            (int removed, int kept, int firstRemoved, int maxKeptGuest) = RyubingCache.WriteWithout(shaderDir, staging, built);
            if (removed == 0)
            {
                BuiltRegistry.Clear(dataDir, titleId);
                return (0, kept, false);
            }

            // Guest code RyuSAK added sits after the code that was there before. When no remaining program uses
            // anything from that point on, it can go too.
            if (guestBefore >= 0 && maxKeptGuest < guestBefore)
            {
                RyubingCache.TruncateGuest(staging, guestBefore);
            }

            Create(dataDir, titleId, removed + kept, "before removing RyuSAK shaders");
            SwapIn(staging, shaderDir);
            bool hostReset = RyubingCache.ResetHostCache(shaderDir, firstRemoved) > 0;
            BuiltRegistry.Clear(dataDir, titleId);
            return (removed, kept, hostReset);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static int GuestCount(string dir)
    {
        string toc = Path.Combine(dir, "guest.toc");
        return File.Exists(toc) && new FileInfo(toc).Length >= 32 ? (int)((new FileInfo(toc).Length - 32) / 16) : 0;
    }

    /// <summary>
    /// Writes a copy of the shared cache without the programs RyuSAK added, for sharing with other players.
    /// </summary>
    public static int ExportWithoutBuilt(string dataDir, ulong titleId, string outputDir)
    {
        (HashSet<ulong> built, int guestBefore) = BuiltRegistry.LoadWithGuestCount(dataDir, titleId);
        string shaderDir = ShaderDir(dataDir, titleId);
        Directory.CreateDirectory(outputDir);

        foreach (string name in SharedFiles)
        {
            string source = Path.Combine(shaderDir, name);
            if (!File.Exists(source))
            {
                throw new BuilderException("NO_CACHE", "This game has no shader cache yet.");
            }

            if (built.Count == 0 || name.StartsWith("guest"))
            {
                File.Copy(source, Path.Combine(outputDir, name), overwrite: true);
            }
        }

        if (built.Count == 0)
        {
            return RyubingCache.Load(shaderDir, loadGuest: false).Programs.Count;
        }

        (_, int kept, _, int maxKeptGuest) = RyubingCache.WriteWithout(shaderDir, outputDir, built);
        if (guestBefore >= 0 && maxKeptGuest < guestBefore)
        {
            RyubingCache.TruncateGuest(outputDir, guestBefore);
        }

        return kept;
    }

    private static string StagingDir(string dataDir, ulong titleId) =>
        Path.Combine(dataDir, "games", titleId.ToString("x16"), "cache", "ryusak-shader-staging");

    private static void PrepareStaging(string staging, string shaderDir)
    {
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(shaderDir);

        foreach (string name in SharedFiles)
        {
            string source = Path.Combine(shaderDir, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(staging, name));
            }
        }
    }

    private static void SwapIn(string staging, string shaderDir)
    {
        foreach (string name in SharedFiles)
        {
            string staged = Path.Combine(staging, name);
            if (File.Exists(staged))
            {
                File.Move(staged, Path.Combine(shaderDir, name), overwrite: true);
            }
        }
    }
}
