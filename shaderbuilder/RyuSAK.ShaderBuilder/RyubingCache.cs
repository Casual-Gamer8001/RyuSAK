using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryujinx.Common;

namespace RyuSAK.ShaderBuilder;

// Mirrors of the structures Ryubing writes into its shader disk cache
// (src/Ryujinx.Graphics.Gpu/Shader/DiskCache and ShaderSpecializationState.cs at the commit in ryubing/SOURCE.md).

[InlineArray(32)]
struct Bytes32
{
    private byte _element;
}

[InlineArray(8)]
struct Bytes8
{
    private byte _element;
}

/// <summary>Mirror of GpuChannelGraphicsState (80 bytes).</summary>
struct GfxState
{
    public bool EarlyZForce;
    public int Topology;
    public uint TessMode;
    public bool AlphaToCoverageEnable;
    public bool AlphaToCoverageDitherEnable;
    public bool ViewportTransformDisable;
    public bool DepthMode;
    public bool ProgramPointSizeEnable;
    public float PointSize;
    public bool AlphaTestEnable;
    public int AlphaTestCompare;
    public float AlphaTestReference;
    public Bytes32 AttributeTypes;
    public bool HasConstantBufferDrawParameters;
    public bool HasUnalignedStorageBuffer;
    public Bytes8 FragmentOutputTypes;
    public bool DualSourceBlendEnable;
    public bool YNegateEnabled;
}

/// <summary>Mirror of GpuChannelComputeState.</summary>
struct ComputeState
{
    public int LocalSizeX;
    public int LocalSizeY;
    public int LocalSizeZ;
    public int LocalMemorySize;
    public int SharedMemorySize;
    public bool HasUnalignedStorageBuffer;
}

/// <summary>Mirror of ShaderSpecializationState.TextureKey.</summary>
record struct TextureKey(int StageIndex, int Handle, int CbufSlot);

/// <summary>Mirror of ShaderSpecializationState.TextureSpecializationState.</summary>
struct TexState
{
    public int QueriedFlags;
    public uint Format;
    public bool FormatSrgb;
    public byte TextureTarget;
    public bool CoordNormalized;
}

static class QueriedFlags
{
    // ShaderSpecializationState.QueriedStateFlags
    public const int PrimitiveTopology = 1 << 1;
    public const int TransformFeedback = 1 << 3;
    public const int TextureArrayFromBuffer = 1 << 4;
    public const int TextureArrayFromPool = 1 << 5;

    // ShaderSpecializationState.QueriedTextureStateFlags
    public const int TextureFormat = 1 << 0;
    public const int SamplerType = 1 << 1;
    public const int CoordNormalized = 1 << 2;
}

static class Magic
{
    public static uint Of(string s) => (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

    public static readonly uint Coms = Of("COMS");
    public static readonly uint Gfxs = Of("GFXS");
    public static readonly uint Tfbd = Of("TFBD");
    public static readonly uint Texk = Of("TEXK");
    public static readonly uint Texs = Of("TEXS");
    public static readonly uint Pgps = Of("PGPS");
    public static readonly uint Tocs = Of("TOCS");
    public static readonly uint Tocg = Of("TOCG");
}

/// <summary>
/// Serializable copy of Ryubing's ShaderSpecializationState.
/// </summary>
sealed class SpecState
{
    public int QueriedState;
    public bool Compute;
    public GfxState Gfx;
    public ComputeState Comp;
    public byte CbPerStage;
    public uint[] CbUse = new uint[5];
    public byte[] Pipeline;
    public List<byte[]> TransformFeedback;
    public List<(TextureKey Key, TexState State)> Textures = new();
    public List<(TextureKey Key, int Length)> ArrayFromBuffer = new();
    public List<(bool IsSampler, int Length)> ArrayFromPool = new();

    public SpecState Clone()
    {
        SpecState copy = (SpecState)MemberwiseClone();
        copy.CbUse = (uint[])CbUse.Clone();
        copy.Textures = new(Textures);
        copy.ArrayFromBuffer = new(ArrayFromBuffer);
        copy.ArrayFromPool = new(ArrayFromPool);
        copy.TransformFeedback = TransformFeedback == null ? null : new(TransformFeedback);
        return copy;
    }

    private static T ReadStruct<T>(BinaryReader br, uint magic) where T : unmanaged
    {
        uint actual = br.ReadUInt32();
        int size = br.ReadInt32();
        if (actual != magic || size < 0 || size > Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException("Unexpected structure in the shader cache.");
        }

        byte[] padded = new byte[Unsafe.SizeOf<T>()];
        br.BaseStream.ReadExactly(padded, 0, size);
        return MemoryMarshal.Read<T>(padded);
    }

    private static byte[] ReadBlob(BinaryReader br, uint magic)
    {
        uint actual = br.ReadUInt32();
        int size = br.ReadInt32();
        if (actual != magic || size < 0 || size > 1 << 20)
        {
            throw new InvalidDataException("Unexpected structure in the shader cache.");
        }

        return br.ReadBytes(size);
    }

    private static void WriteStruct<T>(BinaryWriter bw, uint magic, T value) where T : unmanaged
    {
        bw.Write(magic);
        bw.Write(Unsafe.SizeOf<T>());
        bw.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
    }

    private static void WriteBlob(BinaryWriter bw, uint magic, byte[] blob)
    {
        bw.Write(magic);
        bw.Write(blob.Length);
        bw.Write(blob);
    }

    public static SpecState Read(BinaryReader br)
    {
        SpecState s = new()
        {
            QueriedState = br.ReadInt32(),
            Compute = br.ReadBoolean(),
        };

        if (s.Compute)
        {
            s.Comp = ReadStruct<ComputeState>(br, Magic.Coms);
        }
        else
        {
            s.Gfx = ReadStruct<GfxState>(br, Magic.Gfxs);
        }

        s.CbPerStage = br.ReadByte();
        for (int stage = 0; stage < 5; stage++)
        {
            if ((s.CbPerStage & (1 << stage)) != 0)
            {
                s.CbUse[stage] = br.ReadUInt32();
            }
        }

        if (br.ReadBoolean())
        {
            s.Pipeline = ReadBlob(br, Magic.Pgps);
        }

        if ((s.QueriedState & QueriedFlags.TransformFeedback) != 0)
        {
            int count = br.ReadUInt16();
            s.TransformFeedback = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                s.TransformFeedback.Add(ReadBlob(br, Magic.Tfbd));
            }
        }

        int textureCount = br.ReadUInt16();
        for (int i = 0; i < textureCount; i++)
        {
            TextureKey key = ReadStruct<TextureKey>(br, Magic.Texk);
            TexState state = ReadStruct<TexState>(br, Magic.Texs);
            s.Textures.Add((key, state));
        }

        if ((s.QueriedState & QueriedFlags.TextureArrayFromBuffer) != 0)
        {
            int count = br.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                TextureKey key = ReadStruct<TextureKey>(br, Magic.Texk);
                s.ArrayFromBuffer.Add((key, br.ReadInt32()));
            }
        }

        if ((s.QueriedState & QueriedFlags.TextureArrayFromPool) != 0)
        {
            int count = br.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                bool isSampler = ReadStruct<bool>(br, Magic.Texk);
                s.ArrayFromPool.Add((isSampler, br.ReadInt32()));
            }
        }

        return s;
    }

    public void Write(BinaryWriter bw)
    {
        bw.Write(QueriedState);
        bw.Write(Compute);

        if (Compute)
        {
            WriteStruct(bw, Magic.Coms, Comp);
        }
        else
        {
            WriteStruct(bw, Magic.Gfxs, Gfx);
        }

        bw.Write(CbPerStage);
        for (int stage = 0; stage < 5; stage++)
        {
            if ((CbPerStage & (1 << stage)) != 0)
            {
                bw.Write(CbUse[stage]);
            }
        }

        bw.Write(Pipeline != null);
        if (Pipeline != null)
        {
            WriteBlob(bw, Magic.Pgps, Pipeline);
        }

        if ((QueriedState & QueriedFlags.TransformFeedback) != 0)
        {
            bw.Write((ushort)TransformFeedback.Count);
            foreach (byte[] tfb in TransformFeedback)
            {
                WriteBlob(bw, Magic.Tfbd, tfb);
            }
        }

        bw.Write((ushort)Textures.Count);
        foreach ((TextureKey key, TexState state) in Textures)
        {
            WriteStruct(bw, Magic.Texk, key);
            WriteStruct(bw, Magic.Texs, state);
        }

        if ((QueriedState & QueriedFlags.TextureArrayFromBuffer) != 0)
        {
            bw.Write((ushort)ArrayFromBuffer.Count);
            foreach ((TextureKey key, int length) in ArrayFromBuffer)
            {
                WriteStruct(bw, Magic.Texk, key);
                bw.Write(length);
            }
        }

        if ((QueriedState & QueriedFlags.TextureArrayFromPool) != 0)
        {
            bw.Write((ushort)ArrayFromPool.Count);
            foreach ((bool isSampler, int length) in ArrayFromPool)
            {
                WriteStruct(bw, Magic.Texk, isSampler);
                bw.Write(length);
            }
        }
    }

    public string AttributeKey
    {
        get
        {
            ReadOnlySpan<byte> span = Gfx.AttributeTypes;
            return Convert.ToHexString(span);
        }
    }
}

sealed class GuestEntry
{
    public int Index;
    public byte[] Code;
    public byte[] Cb1;
    public ulong Id;
}

sealed class CacheProgram
{
    public int Index;
    public long Offset;
    public bool IsCompute;
    public int[] GuestIndex = { -1, -1, -1, -1, -1, -1 };
    public SpecState State;

    public ulong Fingerprint => RyubingCache.Fingerprint(GuestIndex, State);
}

/// <summary>
/// Reads and appends to Ryubing's shared shader cache (shared.toc/.data and guest.toc/.data).
/// </summary>
sealed class RyubingCache
{
    public const uint SharedFormatVersion = (1u << 16) | 2;
    public const uint GuestVersion = (1u << 16) | 1;
    private const int TocHeaderSize = 32;
    private const int GuestTocEntrySize = 16;

    public string Directory { get; }
    public List<GuestEntry> Guest { get; } = new();
    public List<CacheProgram> Programs { get; } = new();
    public int GuestCount { get; private set; }
    public uint SharedCodeGenVersion { get; private set; }
    public bool Exists => Programs.Count > 0;

    private RyubingCache(string directory)
    {
        Directory = directory;
    }

    public static string SharedTocPath(string dir) => Path.Combine(dir, "shared.toc");

    /// <summary>
    /// Stable identity of a guest shader: the same code and constant data always give the same value.
    /// </summary>
    public static ulong IdOf(ReadOnlySpan<byte> code, ReadOnlySpan<byte> cb1)
    {
        Hash128 a = Hash128.ComputeHash(code);
        Hash128 b = Hash128.ComputeHash(cb1);
        return a.Low ^ BitOperations.RotateLeft(a.High, 17) ^ (b.Low * 0x9E3779B97F4A7C15UL) ^ (ulong)code.Length;
    }

    /// <summary>
    /// The hash Ryubing stores in guest.toc (DiskCacheGuestStorage.CalcHash).
    /// </summary>
    public static uint GuestHash(ReadOnlySpan<byte> code, ReadOnlySpan<byte> cb1)
    {
        return (uint)Hash128.ComputeHash(cb1).Low * 23 ^ (uint)Hash128.ComputeHash(code).Low;
    }

    /// <summary>
    /// Identifies a program RyuSAK added. Ryubing rewrites the whole shared cache after compiling new entries, but it
    /// keeps each program's guest code indexes and its graphics or compute state byte for byte, so this survives the rewrite.
    /// </summary>
    public static ulong Fingerprint(int[] guestIndex, SpecState state)
    {
        Span<byte> buffer = stackalloc byte[1 + 6 * 4 + 128];
        int length = 0;
        buffer[length++] = state.Compute ? (byte)1 : (byte)0;

        for (int stage = 0; stage < 6; stage++)
        {
            BitConverter.TryWriteBytes(buffer[length..], stage < guestIndex.Length ? guestIndex[stage] : -1);
            length += 4;
        }

        ReadOnlySpan<byte> stateBytes = state.Compute
            ? MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref state.Comp, 1))
            : MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref state.Gfx, 1));
        stateBytes.CopyTo(buffer[length..]);
        length += stateBytes.Length;

        return Hash128.ComputeHash(buffer[..length]).Low;
    }

    public static RyubingCache Load(string dir, bool loadGuest = true)
    {
        RyubingCache cache = new(dir);
        string guestToc = Path.Combine(dir, "guest.toc");
        string guestData = Path.Combine(dir, "guest.data");
        string sharedToc = Path.Combine(dir, "shared.toc");
        string sharedData = Path.Combine(dir, "shared.data");

        if (!File.Exists(sharedToc) || !File.Exists(sharedData) || !File.Exists(guestToc) || !File.Exists(guestData)
            || new FileInfo(sharedToc).Length <= TocHeaderSize)
        {
            return cache;
        }

        byte[] gtoc = File.ReadAllBytes(guestToc);

        if (BitConverter.ToUInt32(gtoc, 0) != Magic.Tocg || BitConverter.ToUInt32(gtoc, 4) != GuestVersion)
        {
            throw new BuilderException("CACHE_FORMAT", "This game's guest shader cache uses a format RyuSAK does not support.");
        }

        int guestCount = (gtoc.Length - TocHeaderSize) / GuestTocEntrySize;
        cache.GuestCount = guestCount;
        byte[] gdata = loadGuest ? File.ReadAllBytes(guestData) : null;

        for (int i = 0; loadGuest && i < guestCount; i++)
        {
            int e = TocHeaderSize + i * GuestTocEntrySize;
            int offset = (int)BitConverter.ToUInt32(gtoc, e);
            int codeSize = (int)BitConverter.ToUInt32(gtoc, e + 4);
            int cb1Size = (int)BitConverter.ToUInt32(gtoc, e + 8);

            byte[] cb1 = gdata.AsSpan(offset, cb1Size).ToArray();
            byte[] code = new byte[codeSize];
            ReadCompressed(gdata, offset + cb1Size, code);
            cache.Guest.Add(new GuestEntry { Index = i, Code = code, Cb1 = cb1, Id = IdOf(code, cb1) });
        }

        byte[] toc = File.ReadAllBytes(sharedToc);
        byte[] data = File.ReadAllBytes(sharedData);

        if (BitConverter.ToUInt32(toc, 0) != Magic.Tocs || BitConverter.ToUInt32(toc, 4) != SharedFormatVersion)
        {
            throw new BuilderException("CACHE_FORMAT", "This game's shader cache uses a format RyuSAK does not support. Update RyuSAK or Ryubing.");
        }

        cache.SharedCodeGenVersion = BitConverter.ToUInt32(toc, 8);
        int programCount = (toc.Length - TocHeaderSize) / 8;

        for (int i = 0; i < programCount; i++)
        {
            long offset = (long)BitConverter.ToUInt64(toc, TocHeaderSize + i * 8);
            using Stream s = OpenCompressed(data, (int)offset);
            using BinaryReader br = new(s);

            uint stages = br.ReadUInt32();
            CacheProgram program = new() { Index = i, Offset = offset, IsCompute = stages == 0 };

            if (program.IsCompute)
            {
                program.GuestIndex[0] = br.ReadInt32();
            }
            else
            {
                while (stages != 0)
                {
                    int stage = BitOperations.TrailingZeroCount(stages);
                    program.GuestIndex[stage] = br.ReadInt32();
                    stages &= ~(1u << stage);
                }
            }

            program.State = SpecState.Read(br);
            cache.Programs.Add(program);
        }

        return cache;
    }

    private static Stream OpenCompressed(byte[] data, int offset)
    {
        byte algorithm = data[offset];
        Stream src = new MemoryStream(data, offset + 1, data.Length - offset - 1, false);
        return algorithm switch
        {
            0 => src,
            1 => new DeflateStream(src, CompressionMode.Decompress),
            2 => new BrotliStream(src, CompressionMode.Decompress),
            _ => throw new InvalidDataException("Unknown compression in the shader cache."),
        };
    }

    private static void ReadCompressed(byte[] data, int offset, byte[] output)
    {
        using Stream s = OpenCompressed(data, offset);
        s.ReadExactly(output);
    }

    private static void WriteCompressed(Stream output, Action<BinaryWriter> write)
    {
        output.WriteByte(2); // Brotli, the same algorithm Ryubing uses.
        using BrotliStream brotli = new(output, CompressionLevel.Fastest, leaveOpen: true);
        using BinaryWriter bw = new(brotli);
        write(bw);
    }

    /// <summary>
    /// Appends programs to the cache. Existing programs keep their position, so Ryubing's compiled (host) cache stays valid for them.
    /// Returns the fingerprint of every program written.
    /// </summary>
    public static List<ulong> Append(string dir, IReadOnlyList<NewProgram> programs, Action<int, int> progress)
    {
        System.IO.Directory.CreateDirectory(dir);

        using FileStream guestToc = OpenExclusive(Path.Combine(dir, "guest.toc"));
        using FileStream guestData = OpenExclusive(Path.Combine(dir, "guest.data"));
        using FileStream sharedToc = OpenExclusive(Path.Combine(dir, "shared.toc"));
        using FileStream sharedData = OpenExclusive(Path.Combine(dir, "shared.data"));

        BinaryReader guestTocReader = new(guestToc);
        BinaryWriter guestTocWriter = new(guestToc);
        uint modifications = 0;

        if (guestToc.Length >= TocHeaderSize)
        {
            if (guestTocReader.ReadUInt32() != Magic.Tocg || guestTocReader.ReadUInt32() != GuestVersion)
            {
                throw new BuilderException("CACHE_FORMAT", "This game's guest shader cache uses a format RyuSAK does not support.");
            }

            guestTocReader.ReadUInt32();
            modifications = guestTocReader.ReadUInt32();
        }
        else
        {
            guestToc.SetLength(0);
            guestData.SetLength(0);
            WriteGuestHeader(guestTocWriter, 0);

            // Shared entries point into the guest cache, so they are useless without it.
            sharedToc.SetLength(0);
            sharedData.SetLength(0);
        }

        if (sharedToc.Length >= TocHeaderSize)
        {
            BinaryReader sharedReader = new(sharedToc);
            if (sharedReader.ReadUInt32() != Magic.Tocs || sharedReader.ReadUInt32() != SharedFormatVersion)
            {
                throw new BuilderException("CACHE_FORMAT", "This game's shader cache uses a format RyuSAK does not support. Update RyuSAK or Ryubing.");
            }
        }
        else
        {
            sharedToc.SetLength(0);
            sharedData.SetLength(0);
            BinaryWriter header = new(sharedToc);
            header.Write(Magic.Tocs);
            header.Write(SharedFormatVersion);
            header.Write(0u); // Code generation version: unknown, so Ryubing rebuilds its compiled cache from these entries.
            header.Write(0u);
            header.Write((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            header.Write(0UL);
            header.Flush();
        }

        // Index existing guest entries by hash so identical code is stored once, like Ryubing does.
        Dictionary<uint, List<(int Index, long Offset, int CodeSize, int Cb1Size)>> byHash = new();
        int guestCount = (int)((guestToc.Length - TocHeaderSize) / GuestTocEntrySize);
        guestToc.Seek(TocHeaderSize, SeekOrigin.Begin);
        for (int i = 0; i < guestCount; i++)
        {
            uint offset = guestTocReader.ReadUInt32();
            int codeSize = (int)guestTocReader.ReadUInt32();
            int cb1Size = (int)guestTocReader.ReadUInt32();
            uint hash = guestTocReader.ReadUInt32();
            if (!byHash.TryGetValue(hash, out var list))
            {
                byHash[hash] = list = new();
            }

            list.Add((i, offset, codeSize, cb1Size));
        }

        int AddGuest(byte[] code, byte[] cb1)
        {
            uint hash = GuestHash(code, cb1);
            if (byHash.TryGetValue(hash, out var list))
            {
                foreach (var entry in list)
                {
                    if (entry.CodeSize != code.Length || entry.Cb1Size != cb1.Length)
                    {
                        continue;
                    }

                    byte[] existingCb1 = new byte[entry.Cb1Size];
                    byte[] existingCode = new byte[entry.CodeSize];
                    guestData.Seek(entry.Offset, SeekOrigin.Begin);
                    guestData.ReadExactly(existingCb1);
                    using (Stream s = OpenCompressedStream(guestData))
                    {
                        s.ReadExactly(existingCode);
                    }

                    if (existingCode.AsSpan().SequenceEqual(code) && existingCb1.AsSpan().SequenceEqual(cb1))
                    {
                        return entry.Index;
                    }
                }
            }
            else
            {
                byHash[hash] = list = new();
            }

            guestData.Seek(0, SeekOrigin.End);
            long dataOffset = guestData.Position;
            if (dataOffset > uint.MaxValue)
            {
                throw new BuilderException("CACHE_TOO_LARGE", "The guest shader cache would grow past 4 GB.");
            }

            guestData.Write(cb1);
            WriteCompressed(guestData, bw => bw.Write(code));

            int index = guestCount++;
            guestToc.Seek(0, SeekOrigin.End);
            guestTocWriter.Write((uint)dataOffset);
            guestTocWriter.Write((uint)code.Length);
            guestTocWriter.Write((uint)cb1.Length);
            guestTocWriter.Write(hash);
            modifications++;
            list.Add((index, dataOffset, code.Length, cb1.Length));
            return index;
        }

        BinaryWriter sharedTocWriter = new(sharedToc);
        List<ulong> fingerprints = new(programs.Count);

        foreach (NewProgram program in programs)
        {
            int[] indices = new int[program.Stages.Length];
            uint mask = 0;
            for (int stage = 0; stage < program.Stages.Length; stage++)
            {
                indices[stage] = -1;
                if (program.Stages[stage] != null)
                {
                    indices[stage] = AddGuest(program.Stages[stage].Code, program.Stages[stage].Cb1);
                    if (!program.State.Compute)
                    {
                        mask |= 1u << stage;
                    }
                }
            }

            sharedData.Seek(0, SeekOrigin.End);
            ulong dataOffset = (ulong)sharedData.Position;
            WriteCompressed(sharedData, bw =>
            {
                bw.Write(mask);
                foreach (int index in indices)
                {
                    if (index >= 0)
                    {
                        bw.Write(index);
                    }
                }

                program.State.Write(bw);
            });

            sharedToc.Seek(0, SeekOrigin.End);
            sharedTocWriter.Write(dataOffset);
            fingerprints.Add(Fingerprint(indices, program.State));

            if ((fingerprints.Count & 255) == 0)
            {
                progress?.Invoke(fingerprints.Count, programs.Count);
            }
        }

        guestToc.Seek(0, SeekOrigin.Begin);
        WriteGuestHeader(guestTocWriter, modifications);
        guestTocWriter.Flush();
        sharedTocWriter.Flush();
        progress?.Invoke(fingerprints.Count, programs.Count);
        return fingerprints;
    }

    /// <summary>
    /// Writes shared.toc and shared.data into <paramref name="outputDir"/> without the programs whose fingerprint is
    /// listed. Guest code stays, because programs Ryubing added later may use the same code.
    /// </summary>
    public static (int Removed, int Kept, int FirstRemoved, int MaxKeptGuest) WriteWithout(string dir, string outputDir, HashSet<ulong> fingerprints)
    {
        RyubingCache cache = Load(dir, loadGuest: false);
        byte[] toc = File.ReadAllBytes(Path.Combine(dir, "shared.toc"));
        byte[] data = File.ReadAllBytes(Path.Combine(dir, "shared.data"));
        long[] starts = cache.Programs.Select(p => p.Offset).Distinct().Order().ToArray();

        using FileStream outToc = new(Path.Combine(outputDir, "shared.toc"), FileMode.Create, FileAccess.Write);
        using FileStream outData = new(Path.Combine(outputDir, "shared.data"), FileMode.Create, FileAccess.Write);
        using BinaryWriter tocWriter = new(outToc);
        outToc.Write(toc, 0, TocHeaderSize);

        int removed = 0, kept = 0, firstRemoved = -1, maxKeptGuest = -1;
        foreach (CacheProgram program in cache.Programs)
        {
            if (fingerprints.Contains(program.Fingerprint))
            {
                removed++;
                if (firstRemoved < 0)
                {
                    firstRemoved = program.Index;
                }

                continue;
            }

            int position = Array.BinarySearch(starts, program.Offset);
            long end = position + 1 < starts.Length ? starts[position + 1] : data.Length;
            tocWriter.Write((ulong)outData.Position);
            outData.Write(data, (int)program.Offset, (int)(end - program.Offset));
            maxKeptGuest = Math.Max(maxKeptGuest, program.GuestIndex.Max());
            kept++;
        }

        return (removed, kept, firstRemoved, maxKeptGuest);
    }

    /// <summary>
    /// Cuts guest.toc and guest.data back to their first <paramref name="count"/> entries.
    /// </summary>
    public static bool TruncateGuest(string dir, int count)
    {
        string tocPath = Path.Combine(dir, "guest.toc");
        string dataPath = Path.Combine(dir, "guest.data");
        if (!File.Exists(tocPath) || !File.Exists(dataPath))
        {
            return false;
        }

        using FileStream toc = new(tocPath, FileMode.Open, FileAccess.ReadWrite);
        using FileStream data = new(dataPath, FileMode.Open, FileAccess.ReadWrite);
        long entries = (toc.Length - TocHeaderSize) / GuestTocEntrySize;
        if (count < 0 || count >= entries)
        {
            return false;
        }

        BinaryReader reader = new(toc);
        toc.Seek(TocHeaderSize + (long)count * GuestTocEntrySize, SeekOrigin.Begin);
        uint cutAt = reader.ReadUInt32();
        toc.SetLength(TocHeaderSize + (long)count * GuestTocEntrySize);
        data.SetLength(cutAt);
        return true;
    }

    /// <summary>
    /// Deletes Ryubing's compiled (host) cache files that hold entries past <paramref name="validCount"/> programs,
    /// so Ryubing rebuilds them from the shared cache instead of pairing them with the wrong programs.
    /// </summary>
    public static int ResetHostCache(string dir, int validCount)
    {
        int deleted = 0;
        if (!System.IO.Directory.Exists(dir))
        {
            return 0;
        }

        foreach (string toc in System.IO.Directory.GetFiles(dir, "*.toc"))
        {
            string name = Path.GetFileNameWithoutExtension(toc);
            if (name is "guest" or "shared")
            {
                continue;
            }

            long entries = (new FileInfo(toc).Length - TocHeaderSize) / 16;
            if (validCount >= 0 && entries <= validCount)
            {
                continue;
            }

            File.Delete(toc);
            string data = Path.Combine(dir, name + ".data");
            if (File.Exists(data))
            {
                File.Delete(data);
            }

            deleted++;
        }

        return deleted;
    }

    private static Stream OpenCompressedStream(Stream stream)
    {
        int algorithm = stream.ReadByte();
        return algorithm switch
        {
            0 => new SubStreamPassThrough(stream),
            1 => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true),
            2 => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true),
            _ => throw new InvalidDataException("Unknown compression in the shader cache."),
        };
    }

    private static void WriteGuestHeader(BinaryWriter bw, uint modifications)
    {
        bw.Write(Magic.Tocg);
        bw.Write(GuestVersion);
        bw.Write(0u);
        bw.Write(modifications);
        bw.Write(0UL);
        bw.Write(0UL);
        bw.Flush();
    }

    private static FileStream OpenExclusive(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new BuilderException("CACHE_IN_USE", $"The shader cache is in use. Close Ryubing and try again. ({e.Message})");
        }
    }

    /// <summary>
    /// Reads from the underlying stream without closing it.
    /// </summary>
    private sealed class SubStreamPassThrough(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

sealed class StageCode
{
    public byte[] Code;
    public byte[] Cb1;
}

sealed class NewProgram
{
    /// <summary>Guest code per Ryubing stage index (0 = vertex A, 1 = vertex ... 5 = fragment), or index 0 for compute.</summary>
    public StageCode[] Stages;
    public SpecState State;
}

sealed class BuilderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
