using System.Collections.Concurrent;
using System.Diagnostics;
using LibHac.Common.Keys;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// RyuSAK shader builder: fills Ryubing's shader cache for a game from the game's own files.
///
///   status   --data DIR --title TID   What RyuSAK has added to the cache, quickly (no game scan)
///   inspect  --data DIR --title TID   Count what would be added, without changing anything
///   build    --data DIR --title TID   Back up the cache, then add the new entries
///   undo     --data DIR --title TID   Remove the entries RyuSAK added, keeping the ones Ryubing compiled
///   restore  --data DIR --title TID   Put back the newest backup (fallback if undo is not possible)
///   export   --data DIR --title TID --out DIR   Copy the shared cache without RyuSAK's entries (for sharing)
///   backups  --data DIR --title TID   List backups
///   evaluate --data DIR --title TID --learn N   Test mode: learn from the first N cached programs and
///                                               measure how many later ones the new entries would cover
///
/// Options: --keys DIR, --base FILE, --update FILE, --no-dlc, --index FILE, --max N|all (default: what the RAM allows),
///          --unaligned auto|yes|no, --pipeline all|learned|none, --force (even while Ryubing runs), --json (one JSON object per line).
/// </summary>
static class Program
{
    private const int ChunkSize = 128 << 20;
    private const int ChunkOverlap = 4 << 20;
    private const int MinLearnedPrograms = 10;

    /// <summary>
    /// Ryubing prepares every cached program when the game starts and keeps it in memory: measured on an RTX 3090,
    /// about 0.3 MB of RAM per program (Odyssey: 7.0 GB with 14,040 programs, 2.2 GB with 204). So by default the
    /// whole cache is kept to what the computer's RAM allows, and the best candidates go in first.
    /// </summary>
    private static int DefaultMaxPrograms(int programsInCache)
    {
        double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30);
        int budget = Math.Clamp((int)((ramGb - 6) * 3000), 5000, 40000);
        return Math.Max(0, budget - programsInCache);
    }

    private sealed record WorkItem(byte[] Data, string Name, int ScanLimit, Func<int, byte[]> Reread);

    private sealed class Context
    {
        public string DataDir;
        public ulong TitleId;
        public GameFiles Files;
        public GameReader Reader;
        public ShaderFinder Finder;
        public long FilesScanned;
        public long BytesScanned;
        public TimeSpan ScanTime;
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("Usage: RyuSAK.ShaderBuilder <status|inspect|build|undo|restore|export|backups|evaluate> --data <Ryubing data folder> --title <title ID> [options]");
            return args.Length == 0 ? 1 : 0;
        }

        Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());
        Output.Json = options.ContainsKey("json");

        try
        {
            return args[0] switch
            {
                "status" => Status(options),
                "inspect" => Build(options, write: false),
                "build" => Build(options, write: true),
                "undo" => Undo(options),
                "restore" => Restore(options),
                "export" => Export(options),
                "backups" => ListBackups(options),
                "evaluate" => Evaluate(options),
                _ => throw new BuilderException("USAGE", $"Unknown command \"{args[0]}\"."),
            };
        }
        catch (BuilderException e)
        {
            Output.Error(e.Code, e.Message);
            return 2;
        }
        catch (Exception e)
        {
            Output.Error("UNEXPECTED", e.Message);
            Console.Error.WriteLine(e);
            return 3;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
            {
                throw new BuilderException("USAGE", $"Unexpected argument \"{args[i]}\".");
            }

            string name = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            options[name] = hasValue ? args[++i] : "true";
        }

        return options;
    }

    private static string Require(Dictionary<string, string> options, string name)
    {
        return options.TryGetValue(name, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new BuilderException("USAGE", $"--{name} is required.");
    }

    private static (string DataDir, ulong TitleId) Target(Dictionary<string, string> options)
    {
        string dataDir = Path.GetFullPath(Require(options, "data"));
        string title = Require(options, "title");

        if (title.Length != 16 || !ulong.TryParse(title, System.Globalization.NumberStyles.HexNumber, null, out ulong titleId))
        {
            throw new BuilderException("USAGE", $"\"{title}\" is not a title ID.");
        }

        if (!Directory.Exists(dataDir))
        {
            throw new BuilderException("NO_DATA_DIR", $"The Ryubing data folder {dataDir} does not exist.");
        }

        return (dataDir, titleId);
    }

    private static Context Prepare(Dictionary<string, string> options)
    {
        (string dataDir, ulong titleId) = Target(options);
        Context context = new() { DataDir = dataDir, TitleId = titleId, Finder = new ShaderFinder() };

        Output.Progress("locate", 0, 0, "Reading your keys");
        KeySet keySet = GameLocator.LoadKeys(dataDir, options.GetValueOrDefault("keys"));

        Output.Progress("locate", 0, 0, "Finding the game files");
        context.Files = GameLocator.Locate(keySet, dataDir, titleId, options.GetValueOrDefault("base"), options.GetValueOrDefault("index"), Output.Status);

        if (options.TryGetValue("update", out string update))
        {
            context.Files.UpdatePath = update == "none" ? null : update;
        }

        if (options.ContainsKey("no-dlc"))
        {
            context.Files.Dlc.Clear();
        }

        context.Reader = GameReader.Open(keySet, context.Files, titleId);
        Scan(context);
        return context;
    }

    private static void Scan(Context context)
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<GameFile> files = context.Reader.EnumerateFiles().ToList();
        long total = files.Sum(f => f.Size);
        long read = 0;
        long lastReport = 0;

        using BlockingCollection<WorkItem> queue = new(8);
        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            foreach (WorkItem item in queue.GetConsumingEnumerable())
            {
                try
                {
                    context.Finder.Process(item.Data, item.Name, 0, item.ScanLimit, item.Reread);
                }
                catch (Exception)
                {
                    context.Finder.Count("files that could not be scanned");
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        try
        {
            foreach (GameFile file in files)
            {
                if (file.Size <= ChunkSize + ChunkOverlap)
                {
                    byte[] data = file.Read(0, (int)file.Size);
                    queue.Add(new WorkItem(data, file.Name, data.Length, null));
                }
                else
                {
                    for (long start = 0; start < file.Size; start += ChunkSize)
                    {
                        int length = (int)Math.Min(ChunkSize + ChunkOverlap, file.Size - start);
                        byte[] data = file.Read(start, length);
                        long chunkStart = start;
                        GameFile source = file;
                        Func<int, byte[]> reread = relative =>
                        {
                            long absolute = chunkStart + relative;
                            return source.Read(absolute, (int)Math.Min(256L << 20, source.Size - absolute));
                        };

                        int scanLimit = start + ChunkSize >= file.Size ? length : ChunkSize;
                        queue.Add(new WorkItem(data, $"{file.Name}@{start:x}", scanLimit, reread));
                    }
                }

                read += file.Size;
                context.FilesScanned++;

                if (watch.ElapsedMilliseconds - lastReport > 1000)
                {
                    lastReport = watch.ElapsedMilliseconds;
                    Output.Progress("scan", read >> 20, total >> 20, "Reading the game files (MB)");
                }
            }
        }
        finally
        {
            queue.CompleteAdding();
            Task.WaitAll(workers);
        }

        context.BytesScanned = total;
        context.ScanTime = watch.Elapsed;
        Output.Progress("scan", total >> 20, total >> 20, "Reading the game files (MB)");
    }

    private static uint UsedAttributes(RyubingCache cache, int guestIndex)
    {
        byte[] code = cache.Guest[guestIndex].Code;
        return code.Length >= 0x28 ? Prober.UsedAttributesFromHeader(code) : 0;
    }

    private static PlanOptions PlanOptionsFrom(Dictionary<string, string> options, int defaultMax)
    {
        PlanOptions planOptions = new() { MaxPrograms = defaultMax };
        if (options.TryGetValue("max", out string max))
        {
            if (max == "all")
            {
                planOptions.MaxPrograms = int.MaxValue;
            }
            else if (int.TryParse(max, out int maxPrograms) && maxPrograms > 0)
            {
                planOptions.MaxPrograms = maxPrograms;
            }
            else
            {
                throw new BuilderException("USAGE", "--max needs a positive number or \"all\".");
            }
        }

        planOptions.Unaligned = options.GetValueOrDefault("unaligned", "auto") switch
        {
            "yes" => true,
            "no" => false,
            _ => null,
        };

        planOptions.Pipeline = options.GetValueOrDefault("pipeline", planOptions.Pipeline);
        if (planOptions.Pipeline is not ("all" or "learned" or "none"))
        {
            throw new BuilderException("USAGE", "--pipeline needs all, learned or none.");
        }

        return planOptions;
    }

    private static int Status(Dictionary<string, string> options)
    {
        (string dataDir, ulong titleId) = Target(options);
        RyubingCache cache = RyubingCache.Load(Backups.ShaderDir(dataDir, titleId), loadGuest: false);
        HashSet<ulong> built = BuiltRegistry.Load(dataDir, titleId);
        int builtPresent = built.Count == 0 ? 0 : cache.Programs.Count(p => built.Contains(p.Fingerprint));
        List<string> backups = Backups.List(dataDir, titleId);

        Output.Result(new Dictionary<string, object>
        {
            ["titleId"] = titleId.ToString("x16"),
            ["cachedPrograms"] = cache.Programs.Count,
            ["builtPrograms"] = builtPresent,
            ["backups"] = backups.Count,
            ["ryubingRunning"] = Backups.EmulatorRunning(),
        }, $"{cache.Programs.Count} programs in the cache, {builtPresent} of them added by RyuSAK; {backups.Count} backups.");
        return 0;
    }

    private static int Build(Dictionary<string, string> options, bool write)
    {
        Context context = Prepare(options);
        string shaderDir = Backups.ShaderDir(context.DataDir, context.TitleId);
        RyubingCache cache = RyubingCache.Load(shaderDir);
        HashSet<ulong> built = BuiltRegistry.Load(context.DataDir, context.TitleId);
        Profile profile = Profile.Learn(cache, index => UsedAttributes(cache, index), built: built);
        bool needsLaunch = profile.GraphicsPrograms < MinLearnedPrograms;

        Dictionary<string, object> result = new()
        {
            ["titleId"] = context.TitleId.ToString("x16"),
            ["baseFile"] = context.Files.BasePath,
            ["updateFile"] = context.Files.UpdatePath,
            ["dlcFiles"] = context.Reader.DlcCount,
            ["filesScanned"] = context.FilesScanned,
            ["megabytesScanned"] = context.BytesScanned >> 20,
            ["scanSeconds"] = Math.Round(context.ScanTime.TotalSeconds, 1),
            ["cachedPrograms"] = cache.Programs.Count,
            ["builtPrograms"] = profile.BuiltPrograms,
            ["learnedPrograms"] = profile.GraphicsPrograms + profile.ComputePrograms,
            ["needsLaunch"] = needsLaunch,
        };

        if (needsLaunch)
        {
            result["programsFound"] = context.Finder.Programs.Count;
            if (write)
            {
                throw new BuilderException("LAUNCH_FIRST", "Start the game in Ryubing once (until the title screen), close it, then try again. RyuSAK learns the game's graphics settings from that first run.");
            }

            Output.Result(result, $"Found {context.Finder.Programs.Count} shader programs. Start the game once in Ryubing so RyuSAK can learn its settings.");
            return 0;
        }

        Dictionary<ulong, StageProbe> probes = Planner.ProbeAll(context.Finder, profile, (done, total) => Output.Progress("probe", done, total, "Checking shaders"));
        PlanOptions planOptions = PlanOptionsFrom(options, DefaultMaxPrograms(cache.Programs.Count));
        Plan plan = Planner.Build(context.Finder, probes, profile, planOptions);

        result["programsFound"] = plan.ProgramsFound;
        result["alreadyCached"] = plan.AlreadyCached;
        result["newPrograms"] = plan.Programs.Count;
        result["limit"] = planOptions.MaxPrograms == int.MaxValue ? -1 : planOptions.MaxPrograms;
        result["cappedOut"] = plan.CappedOut;
        result["skipped"] = plan.Skipped;
        result["unalignedCopies"] = plan.Programs.Count(p => p.Unaligned);

        if (!write)
        {
            Output.Result(result, $"Found {plan.ProgramsFound} shader programs in the game files. {plan.Programs.Count} can be added to the cache ({plan.AlreadyCached} already there, {plan.Skipped.Values.Sum()} skipped, {plan.CappedOut} over the limit).");
            return 0;
        }

        if (plan.Programs.Count == 0)
        {
            result["added"] = 0;
            Output.Result(result, "Nothing to add: the cache already has every shader RyuSAK can build for this game.");
            return 0;
        }

        if (!options.ContainsKey("force") && Backups.EmulatorRunning())
        {
            throw new BuilderException("RYUBING_RUNNING", "Close Ryubing before building shaders.");
        }

        Output.Progress("write", 0, plan.Programs.Count, "Backing up the current shader cache");
        string backup = Backups.Create(context.DataDir, context.TitleId, cache.Programs.Count, "before adding RyuSAK shaders");
        int written = Backups.WriteThroughStaging(context.DataDir, context.TitleId, plan.Programs.Select(p => p.Entry).ToList(),
            (done, total) => Output.Progress("write", done, total, "Adding shaders to the cache"));

        result["added"] = written;
        result["builtPrograms"] = profile.BuiltPrograms + written;
        result["backup"] = backup;
        Output.Result(result, $"Added {written} shader programs to the cache. Ryubing compiles them the next time the game starts. Backup: {backup}");
        return 0;
    }

    private static int Undo(Dictionary<string, string> options)
    {
        (string dataDir, ulong titleId) = Target(options);
        if (!options.ContainsKey("force") && Backups.EmulatorRunning())
        {
            throw new BuilderException("RYUBING_RUNNING", "Close Ryubing before removing shaders.");
        }

        (int removed, int kept, bool hostReset) = Backups.RemoveBuilt(dataDir, titleId);
        Output.Result(new Dictionary<string, object>
        {
            ["removed"] = removed,
            ["kept"] = kept,
            ["hostCacheReset"] = hostReset,
        }, removed == 0
            ? "None of the shaders RyuSAK added are in the cache any more."
            : $"Removed {removed} shader programs RyuSAK added and kept {kept}." + (hostReset ? " Ryubing will recompile the kept ones once, the next time the game starts." : ""));
        return 0;
    }

    private static int Export(Dictionary<string, string> options)
    {
        (string dataDir, ulong titleId) = Target(options);
        string outputDir = Path.GetFullPath(Require(options, "out"));
        int programs = Backups.ExportWithoutBuilt(dataDir, titleId, outputDir);
        Output.Result(new Dictionary<string, object>
        {
            ["out"] = outputDir,
            ["programs"] = programs,
        }, $"Wrote {programs} shader programs (without the ones RyuSAK added) to {outputDir}.");
        return 0;
    }

    private static int Restore(Dictionary<string, string> options)
    {
        (string dataDir, ulong titleId) = Target(options);
        if (!options.ContainsKey("force") && Backups.EmulatorRunning())
        {
            throw new BuilderException("RYUBING_RUNNING", "Close Ryubing before restoring the shader cache.");
        }

        string restored = Backups.RestoreLatest(dataDir, titleId);
        Output.Result(new Dictionary<string, object>
        {
            ["restored"] = restored,
            ["remainingBackups"] = Backups.List(dataDir, titleId).Count,
        }, $"Restored the shader cache from {restored}.");
        return 0;
    }

    private static int ListBackups(Dictionary<string, string> options)
    {
        (string dataDir, ulong titleId) = Target(options);
        List<string> backups = Backups.List(dataDir, titleId);
        Output.Result(new Dictionary<string, object> { ["backups"] = backups }, backups.Count == 0 ? "No backups." : string.Join(Environment.NewLine, backups));
        return 0;
    }

    private static int Evaluate(Dictionary<string, string> options)
    {
        Context context = Prepare(options);
        string cacheDir = options.GetValueOrDefault("cache") ?? Backups.ShaderDir(context.DataDir, context.TitleId);
        RyubingCache full = RyubingCache.Load(cacheDir);
        int learn = int.Parse(options.GetValueOrDefault("learn", "200"));

        if (options.ContainsKey("trust-compute"))
        {
            ComputeControl.Trusted = true;
        }

        Profile profile = Profile.Learn(full, index => UsedAttributes(full, index), learn);
        Dictionary<ulong, StageProbe> probes = Planner.ProbeAll(context.Finder, profile, (done, total) => Output.Progress("probe", done, total, "Checking shaders"));
        Plan plan = Planner.Build(context.Finder, probes, profile, PlanOptionsFrom(options, int.MaxValue));

        string report = $"Scanned {context.FilesScanned} files ({context.BytesScanned >> 20} MB) in {context.ScanTime.TotalSeconds:F0}s; update={context.Reader.HasUpdate}, DLC={context.Reader.DlcCount}"
            + Environment.NewLine + string.Join(", ", context.Finder.Counters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
            + Environment.NewLine + Evaluator.Run(full, learn, context.Finder, probes, profile, plan);

        Output.Result(new Dictionary<string, object> { ["report"] = report }, report);
        return 0;
    }
}
