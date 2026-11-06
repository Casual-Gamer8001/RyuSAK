using System.Text.Json;
using System.Text.Json.Nodes;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;

namespace RyuSAK.ShaderBuilder;

sealed class DlcFile
{
    public string Path;
    public List<string> NcaPaths = new();
}

sealed class GameFiles
{
    public string BasePath;
    public string UpdatePath;
    public List<DlcFile> Dlc = new();
}

/// <summary>
/// Finds a game's files the same way Ryubing does: keys in system/, game folders in Config.json,
/// and the chosen update and DLC in games/&lt;title ID&gt;/updates.json and dlc.json.
/// </summary>
static class GameLocator
{
    private static readonly string[] GameExtensions = { ".nsp", ".xci" };

    public static KeySet LoadKeys(string dataDir, string keysDir)
    {
        string dir = keysDir ?? System.IO.Path.Combine(dataDir, "system");
        string prodKeys = System.IO.Path.Combine(dir, "prod.keys");
        string titleKeys = System.IO.Path.Combine(dir, "title.keys");

        if (!File.Exists(prodKeys))
        {
            throw new BuilderException("NO_KEYS", $"prod.keys was not found in {dir}. Set up your keys in Ryubing first.");
        }

        KeySet keySet = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(keySet, prodKeys, File.Exists(titleKeys) ? titleKeys : null, null, null);
        return keySet;
    }

    public static GameFiles Locate(KeySet keySet, string dataDir, ulong titleId, string basePath, string indexPath, Action<string> status)
    {
        GameFiles files = new();
        string gameDir = System.IO.Path.Combine(dataDir, "games", titleId.ToString("x16"));

        string updatesJson = System.IO.Path.Combine(gameDir, "updates.json");
        if (File.Exists(updatesJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(updatesJson));
                if (doc.RootElement.TryGetProperty("selected", out JsonElement selected) && selected.ValueKind == JsonValueKind.String)
                {
                    string path = selected.GetString();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        files.UpdatePath = path;
                    }
                }
            }
            catch (JsonException)
            {
                status?.Invoke("updates.json could not be read, so the update is skipped.");
            }
        }

        string dlcJson = System.IO.Path.Combine(gameDir, "dlc.json");
        if (File.Exists(dlcJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(dlcJson));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement container in doc.RootElement.EnumerateArray())
                    {
                        if (!container.TryGetProperty("path", out JsonElement pathElement) || pathElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        DlcFile dlc = new() { Path = pathElement.GetString() };
                        if (!File.Exists(dlc.Path))
                        {
                            continue;
                        }

                        if (container.TryGetProperty("dlc_nca_list", out JsonElement ncas) && ncas.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement nca in ncas.EnumerateArray())
                            {
                                bool enabled = !nca.TryGetProperty("is_enabled", out JsonElement e) || e.ValueKind != JsonValueKind.False;
                                if (enabled && nca.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                                {
                                    dlc.NcaPaths.Add(p.GetString());
                                }
                            }
                        }

                        if (dlc.NcaPaths.Count > 0)
                        {
                            files.Dlc.Add(dlc);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                status?.Invoke("dlc.json could not be read, so DLC is skipped.");
            }
        }

        files.BasePath = basePath ?? FindBaseGame(keySet, dataDir, titleId, indexPath, status);
        return files;
    }

    private static List<string> ReadGameDirs(string dataDir)
    {
        string config = System.IO.Path.Combine(dataDir, "Config.json");
        List<string> dirs = new();

        if (!File.Exists(config))
        {
            return dirs;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(config));
            if (doc.RootElement.TryGetProperty("game_dirs", out JsonElement gameDirs) && gameDirs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement dir in gameDirs.EnumerateArray())
                {
                    if (dir.ValueKind == JsonValueKind.String && Directory.Exists(dir.GetString()))
                    {
                        dirs.Add(dir.GetString());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable config just means no game folders to search.
        }

        return dirs;
    }

    private static string FindBaseGame(KeySet keySet, string dataDir, ulong titleId, string indexPath, Action<string> status)
    {
        List<string> dirs = ReadGameDirs(dataDir);
        if (dirs.Count == 0)
        {
            throw new BuilderException("NO_GAME_DIRS", "Ryubing has no game folders set up, so RyuSAK can't find this game's files.");
        }

        Dictionary<string, IndexEntry> index = LoadIndex(indexPath);
        string tidText = titleId.ToString("x16");

        List<string> candidates = new();
        foreach (string dir in dirs)
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => GameExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())));
            }
            catch (IOException)
            {
                // Skip folders that went away while searching.
            }
        }

        // Files named after the title ID are almost always the right one, so try them first.
        candidates = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => System.IO.Path.GetFileName(f).Contains(tidText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string found = null;
        int checkedCount = 0;

        foreach (string file in candidates)
        {
            FileInfo info = new(file);
            List<ulong> ids;

            if (index.TryGetValue(file, out IndexEntry cached) && cached.Length == info.Length && cached.LastWrite == info.LastWriteTimeUtc.Ticks)
            {
                ids = cached.TitleIds;
            }
            else
            {
                ids = ReadProgramTitleIds(keySet, file);
                index[file] = new IndexEntry { Length = info.Length, LastWrite = info.LastWriteTimeUtc.Ticks, TitleIds = ids };
                checkedCount++;
                if (checkedCount % 20 == 0)
                {
                    status?.Invoke($"Looking for the game in your game folders ({checkedCount} files checked)");
                }
            }

            if (ids.Contains(titleId))
            {
                found = file;
                break;
            }
        }

        SaveIndex(indexPath, index);

        return found ?? throw new BuilderException("NO_GAME", "This game's file was not found in Ryubing's game folders.");
    }

    /// <summary>
    /// Title IDs of the base programs in an NSP or XCI. Updates and DLC are ignored.
    /// </summary>
    public static List<ulong> ReadProgramTitleIds(KeySet keySet, string path)
    {
        List<ulong> ids = new();
        try
        {
            using LocalStorage storage = new(path, FileAccess.Read);
            IFileSystem pfs = OpenContainer(keySet, storage, importTickets: false);

            foreach (DirectoryEntryEx entry in pfs.EnumerateEntries("/", "*.nca"))
            {
                using UniqueRef<IFile> file = new();
                if (pfs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
                {
                    continue;
                }

                Nca nca = new(keySet, file.Release().AsStorage());
                if (nca.Header.ContentType == NcaContentType.Program && !IsPatch(nca))
                {
                    ids.Add(nca.Header.TitleId);
                }
            }
        }
        catch (Exception)
        {
            // Files that can't be opened (damaged, wrong keys, not a game) just don't match.
        }

        return ids;
    }

    public static IFileSystem OpenContainer(KeySet keySet, IStorage storage, bool importTickets)
    {
        storage.GetSize(out long size).ThrowIfFailure();
        byte[] magic = new byte[4];
        storage.Read(0, magic).ThrowIfFailure();

        if (magic.AsSpan().SequenceEqual("PFS0"u8))
        {
            PartitionFileSystem pfs = new();
            pfs.Initialize(storage).ThrowIfFailure();

            if (importTickets)
            {
                ImportTickets(keySet, pfs);
            }

            return pfs;
        }

        Xci xci = new(keySet, storage);
        return xci.OpenPartition(XciPartitionType.Secure);
    }

    private static void ImportTickets(KeySet keySet, IFileSystem pfs)
    {
        foreach (DirectoryEntryEx entry in pfs.EnumerateEntries("/", "*.tik"))
        {
            try
            {
                using UniqueRef<IFile> tikFile = new();
                pfs.OpenFile(ref tikFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                LibHac.Tools.Es.Ticket ticket = new(tikFile.Get.AsStream());
                byte[] titleKey = ticket.GetTitleKey(keySet);
                if (titleKey != null)
                {
                    keySet.ExternalKeySet.Add(new RightsId(ticket.RightsId), new LibHac.Spl.AccessKey(titleKey));
                }
            }
            catch (Exception)
            {
                // A bad ticket only matters if its content is needed, and that fails later with a clear error.
            }
        }
    }

    public static bool IsPatch(Nca nca)
    {
        try
        {
            return nca.SectionExists(NcaSectionType.Data)
                && nca.GetFsHeader(Nca.GetSectionIndexFromType(NcaSectionType.Data, nca.Header.ContentType)).IsPatchSection();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class IndexEntry
    {
        public long Length { get; set; }
        public long LastWrite { get; set; }
        public List<ulong> TitleIds { get; set; } = new();
    }

    private static Dictionary<string, IndexEntry> LoadIndex(string path)
    {
        Dictionary<string, IndexEntry> index = new(StringComparer.OrdinalIgnoreCase);
        if (path == null || !File.Exists(path))
        {
            return index;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
            {
                foreach ((string file, JsonNode node) in root)
                {
                    index[file] = new IndexEntry
                    {
                        Length = (long)node["Length"],
                        LastWrite = (long)node["LastWrite"],
                        TitleIds = node["TitleIds"].AsArray().Select(id => (ulong)id).ToList(),
                    };
                }
            }
        }
        catch (Exception)
        {
            index.Clear();
        }

        return index;
    }

    private static void SaveIndex(string path, Dictionary<string, IndexEntry> index)
    {
        if (path == null)
        {
            return;
        }

        try
        {
            JsonObject root = new();
            foreach ((string file, IndexEntry entry) in index)
            {
                root[file] = new JsonObject
                {
                    ["Length"] = entry.Length,
                    ["LastWrite"] = entry.LastWrite,
                    ["TitleIds"] = new JsonArray(entry.TitleIds.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                };
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            File.WriteAllText(path, root.ToJsonString());
        }
        catch (Exception)
        {
            // The index only speeds up the next search.
        }
    }
}
