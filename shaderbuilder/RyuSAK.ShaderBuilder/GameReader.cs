using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// A file (or NSO segment) to scan. Reads are thread-safe.
/// </summary>
sealed class GameFile
{
    public string Name;
    public long Size;
    public Func<long, int, byte[]> Read;
}

/// <summary>
/// Opens a game with its update and DLC and lists every file in them.
/// </summary>
sealed class GameReader
{
    private readonly object _io = new();
    private IFileSystem _romFs;
    private IFileSystem _exeFs;
    private readonly List<(string Name, IFileSystem Fs)> _dlc = new();

    public ulong TitleId { get; private set; }
    public bool HasUpdate { get; private set; }
    public int DlcCount => _dlc.Count;

    public static GameReader Open(KeySet keySet, GameFiles files, ulong titleId)
    {
        GameReader reader = new() { TitleId = titleId };

        (Nca baseNca, Nca bundledPatch) = FindProgram(keySet, files.BasePath, titleId, wantPatch: false);
        if (baseNca == null)
        {
            throw new BuilderException("NO_GAME", $"{System.IO.Path.GetFileName(files.BasePath)} does not contain this game.");
        }

        Nca patchNca = bundledPatch;
        if (files.UpdatePath != null)
        {
            (_, Nca update) = FindProgram(keySet, files.UpdatePath, titleId, wantPatch: true);
            patchNca = update ?? patchNca;
        }

        try
        {
            reader._romFs = patchNca != null
                ? baseNca.OpenFileSystemWithPatch(patchNca, NcaSectionType.Data, IntegrityCheckLevel.None)
                : baseNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
            reader._exeFs = (patchNca ?? baseNca).OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.None);
        }
        catch (Exception e) when (e is not BuilderException)
        {
            throw new BuilderException("GAME_UNREADABLE", $"The game files could not be decrypted. Check that your keys are up to date. ({e.Message})");
        }

        reader.HasUpdate = patchNca != null;

        foreach (DlcFile dlc in files.Dlc)
        {
            try
            {
                LocalStorage storage = new(dlc.Path, FileAccess.Read);
                IFileSystem pfs = GameLocator.OpenContainer(keySet, storage, importTickets: true);

                foreach (string ncaPath in dlc.NcaPaths)
                {
                    using UniqueRef<IFile> file = new();
                    if (pfs.OpenFile(ref file.Ref, ncaPath.ToU8Span(), OpenMode.Read).IsFailure())
                    {
                        continue;
                    }

                    Nca nca = new(keySet, file.Release().AsStorage());
                    if (nca.SectionExists(NcaSectionType.Data))
                    {
                        reader._dlc.Add(($"dlc:{System.IO.Path.GetFileName(dlc.Path)}{ncaPath}", nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None)));
                    }
                }
            }
            catch (Exception)
            {
                // DLC that can't be read only means its shaders are left out.
            }
        }

        return reader;
    }

    private static (Nca Base, Nca Patch) FindProgram(KeySet keySet, string path, ulong titleId, bool wantPatch)
    {
        LocalStorage storage = new(path, FileAccess.Read);
        IFileSystem pfs = GameLocator.OpenContainer(keySet, storage, importTickets: true);
        Nca baseNca = null;
        Nca patchNca = null;
        ulong patchId = titleId | 0x800;

        foreach (DirectoryEntryEx entry in pfs.EnumerateEntries("/", "*.nca"))
        {
            using UniqueRef<IFile> file = new();
            if (pfs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
            {
                continue;
            }

            Nca nca = new(keySet, file.Release().AsStorage());
            if (nca.Header.ContentType != NcaContentType.Program)
            {
                continue;
            }

            bool isPatch = GameLocator.IsPatch(nca);
            if (!isPatch && nca.Header.TitleId == titleId)
            {
                baseNca = nca;
            }
            else if (isPatch && (nca.Header.TitleId == patchId || nca.Header.TitleId == titleId))
            {
                patchNca = nca;
            }
        }

        if (wantPatch && patchNca == null)
        {
            return (null, null);
        }

        return (baseNca, patchNca);
    }

    private byte[] ReadFile(IFileSystem fs, string path, long offset, int length)
    {
        byte[] data = new byte[length];
        lock (_io)
        {
            using UniqueRef<IFile> file = new();
            fs.OpenFile(ref file.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            file.Get.Read(out long read, offset, data).ThrowIfFailure();
            if (read < length)
            {
                Array.Resize(ref data, (int)read);
            }
        }

        return data;
    }

    /// <summary>
    /// Lists the code segments of every executable and every file in the RomFS of the game, its update and its DLC.
    /// </summary>
    public IEnumerable<GameFile> EnumerateFiles()
    {
        List<GameFile> exe = new();
        foreach (DirectoryEntryEx entry in _exeFs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories))
        {
            if (entry.Type != DirectoryEntryType.File || entry.Name.EndsWith(".npdm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<(string, byte[])> segments = new();
            lock (_io)
            {
                using UniqueRef<IFile> file = new();
                if (_exeFs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
                {
                    continue;
                }

                NsoReader nso = new();
                if (nso.Initialize(file.Get).IsFailure())
                {
                    continue;
                }

                foreach (NsoReader.SegmentType segment in new[] { NsoReader.SegmentType.Text, NsoReader.SegmentType.Ro, NsoReader.SegmentType.Data })
                {
                    if (nso.GetSegmentSize(segment, out uint size).IsFailure() || size == 0)
                    {
                        continue;
                    }

                    byte[] data = new byte[size];
                    if (nso.ReadSegment(segment, data).IsSuccess())
                    {
                        segments.Add(($"exefs:{entry.FullPath}.{segment}", data));
                    }
                }
            }

            foreach ((string name, byte[] data) in segments)
            {
                exe.Add(new GameFile { Name = name, Size = data.Length, Read = (offset, length) => data.AsSpan((int)offset, length).ToArray() });
            }
        }

        foreach (GameFile file in exe)
        {
            yield return file;
        }

        List<(string Prefix, IFileSystem Fs)> roots = new() { ("romfs:", _romFs) };
        roots.AddRange(_dlc);

        foreach ((string prefix, IFileSystem fs) in roots)
        {
            foreach (DirectoryEntryEx entry in fs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories))
            {
                if (entry.Type != DirectoryEntryType.File || entry.Size == 0)
                {
                    continue;
                }

                string path = entry.FullPath;
                IFileSystem captured = fs;
                yield return new GameFile
                {
                    Name = prefix + path,
                    Size = entry.Size,
                    Read = (offset, length) => ReadFile(captured, path, offset, length),
                };
            }
        }
    }
}
