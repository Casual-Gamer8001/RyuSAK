using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// One compiled shader stage from a BNSH container: NVN control section and code (byte code) section.
/// </summary>
sealed class StageBlob
{
    public ulong Id;
    public int BnshStage;
    public byte[] Control;
    public byte[] ByteCode;
}

record struct ProgramKey(ulong Vertex, ulong TessControl, ulong TessEval, ulong Geometry, ulong Fragment, ulong Compute)
{
    public ulong this[int bnshStage] => bnshStage switch
    {
        0 => Vertex,
        1 => TessControl,
        2 => TessEval,
        3 => Geometry,
        4 => Fragment,
        _ => Compute,
    };

    public static ProgramKey From(ulong[] ids) => new(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);
}

sealed class FoundProgram
{
    public ProgramKey Key;
    public ConcurrentDictionary<ulong, string> Containers { get; } = new();
    public int Occurrences;

    public IEnumerable<ulong> ContainerIds => Containers.Keys;

    public bool SharesContainerWith(HashSet<ulong> ids) => ContainerIds.Any(ids.Contains);
}

/// <summary>
/// Unpacks game files (Yaz0, Zstandard, LZ4, zlib and gzip, nested any number of times) and collects
/// every program listed in the BNSH shader containers it finds.
/// </summary>
sealed class ShaderFinder
{
    private const int MaxDepth = 6;

    public ConcurrentDictionary<ulong, StageBlob> Blobs { get; } = new();
    public ConcurrentDictionary<ProgramKey, FoundProgram> Programs { get; } = new();
    public ConcurrentDictionary<string, long> Counters { get; } = new();
    public ConcurrentBag<ShadingModelInfo> ShadingModels { get; } = new();
    public ConcurrentBag<MaterialInfo> Materials { get; } = new();

    /// <summary>Also read model materials and shader archives, to tell which programs materials can select.</summary>
    public bool ReadMaterials { get; init; }

    public void Count(string name, long value = 1) => Counters.AddOrUpdate(name, value, (_, v) => v + value);

    public void Process(byte[] buf, string name, int depth, int scanLimit, Func<int, byte[]> reread)
    {
        Count("bytes scanned", Math.Min(scanLimit, buf.Length));
        ParseBnsh(buf, name, scanLimit);

        if (ReadMaterials)
        {
            MaterialReader.Scan(this, buf, scanLimit);
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        FindStreams(buf, name, depth, scanLimit, reread, "Yaz0"u8.ToArray(), "yaz0");
        FindStreams(buf, name, depth, scanLimit, reread, new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "zstd");
        FindStreams(buf, name, depth, scanLimit, reread, new byte[] { 0x04, 0x22, 0x4D, 0x18 }, "lz4");
        FindStreams(buf, name, depth, scanLimit, reread, new byte[] { 0x1F, 0x8B, 0x08 }, "gzip");
        FindZlib(buf, name, depth, scanLimit);
    }

    private void FindStreams(byte[] buf, string name, int depth, int scanLimit, Func<int, byte[]> reread, byte[] magic, string kind)
    {
        int pos = 0;
        int limit = Math.Min(scanLimit, buf.Length);

        while (pos < limit)
        {
            int found = buf.AsSpan(pos, limit - pos).IndexOf(magic);
            if (found < 0)
            {
                break;
            }

            int at = pos + found;
            pos = at + 1;

            byte[] child = null;
            int consumed = 0;

            try
            {
                child = Decode(buf, at, kind, out consumed);
            }
            catch (TruncatedException)
            {
                byte[] bigger = reread?.Invoke(at);
                if (bigger != null)
                {
                    try
                    {
                        child = Decode(bigger, 0, kind, out consumed);
                    }
                    catch
                    {
                        Count("truncated " + kind);
                    }
                }
            }
            catch
            {
                // Most magic matches inside other data are not real streams.
            }

            if (child == null)
            {
                continue;
            }

            if (child.Length == 0)
            {
                Count(kind + " needing a dictionary");
                continue;
            }

            Count(kind + " streams");

            if (kind == "zstd" && consumed > 0)
            {
                pos = at + consumed;
            }

            Process(child, $"{name}>{kind}@{at:x}", depth + 1, child.Length, null);
        }
    }

    private static byte[] Decode(byte[] buf, int offset, string kind, out int consumed)
    {
        consumed = 0;
        return kind switch
        {
            "yaz0" => Codecs.Yaz0(buf.AsSpan(offset)),
            "zstd" => Codecs.ZstdFrame(buf.AsSpan(offset), out consumed) ?? Array.Empty<byte>(),
            "lz4" => Codecs.Lz4Frame(buf.AsSpan(offset)),
            "gzip" => Codecs.Zlib(buf, offset, buf.Length - offset, gzip: true),
            _ => throw new InvalidOperationException(),
        };
    }

    private void FindZlib(byte[] buf, string name, int depth, int scanLimit)
    {
        int limit = Math.Min(scanLimit, buf.Length - 2);
        int pos = 0;

        while (pos < limit)
        {
            int found = buf.AsSpan(pos, limit - pos).IndexOf((byte)0x78);
            if (found < 0)
            {
                break;
            }

            int at = pos + found;
            pos = at + 1;
            byte flg = buf[at + 1];

            if (flg != 0x9C && flg != 0xDA && flg != 0x01 && flg != 0x5E)
            {
                continue;
            }

            byte[] child;
            try
            {
                child = Codecs.Zlib(buf, at, buf.Length - at, gzip: false);
            }
            catch
            {
                continue;
            }

            if (child.Length < 256)
            {
                continue;
            }

            Count("zlib streams");
            Process(child, $"{name}>zlib@{at:x}", depth + 1, child.Length, null);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong U64(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint U32(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);

    /// <summary>
    /// Lists the programs (variations) of every BNSH container in the buffer.
    /// </summary>
    private void ParseBnsh(byte[] buffer, string name, int scanLimit)
    {
        ReadOnlySpan<byte> s = buffer;
        int pos = 0;
        int limit = Math.Min(scanLimit, s.Length);

        while (pos < limit)
        {
            int f = s[pos..limit].IndexOf("BNSH\0\0\0\0"u8);
            if (f < 0)
            {
                break;
            }

            int b = pos + f;
            pos = b + 8;

            try
            {
                int grsc = b + BinaryPrimitives.ReadUInt16LittleEndian(s[(b + 0x16)..]);
                if (!s.Slice(grsc, 4).SequenceEqual("grsc"u8))
                {
                    continue;
                }

                int count = (int)U32(s, grsc + 0x1C);
                long variationStart = (long)U64(s, grsc + 0x20);
                uint fileSize = U32(s, b + 0x1C);
                if (count <= 0 || count > 1_000_000)
                {
                    continue;
                }

                ulong containerId = RyubingCache.IdOf(s.Slice(b, Math.Min(4096, s.Length - b)), BitConverter.GetBytes(fileSize));
                string label = $"{name}@{b:x}";

                for (int i = 0; i < count; i++)
                {
                    int v = checked(b + (int)variationStart + i * 64);
                    int program = checked(b + (int)U64(s, v + 0x10));
                    ProgramKey key = ReadProgram(buffer, b, program, register: true) ?? throw new InvalidDataException();
                    FoundProgram found = Programs.GetOrAdd(key, k => new FoundProgram { Key = k });
                    found.Containers.TryAdd(containerId, label);
                    Interlocked.Increment(ref found.Occurrences);
                }

                Count("bnsh containers");
                Count("bnsh programs listed", count);
            }
            catch (Exception)
            {
                Count("bnsh containers that could not be read");
            }
        }
    }

    /// <summary>
    /// Identifies the stages of the BNSH program at <paramref name="program"/> (offsets inside are relative to the
    /// BNSH container at <paramref name="b"/>). Returns null if the program doesn't look valid.
    /// </summary>
    public ProgramKey? ReadProgram(byte[] buffer, int b, int program, bool register)
    {
        ReadOnlySpan<byte> s = buffer;
        ulong[] ids = new ulong[6];

        try
        {
            for (int stage = 0; stage < 6; stage++)
            {
                long codeOffset = (long)U64(s, program + 0x08 + stage * 8);
                if (codeOffset == 0)
                {
                    continue;
                }

                int c = checked(b + (int)codeOffset);
                int control = checked(b + (int)U64(s, c + 0x08));
                int byteCode = checked(b + (int)U64(s, c + 0x10));
                int byteCodeSize = (int)U32(s, c + 0x18);
                int controlSize = (int)U32(s, c + 0x1C);

                if (byteCodeSize < 0x40 || controlSize < 0x700 || byteCode + byteCodeSize > s.Length || control + controlSize > s.Length)
                {
                    return null;
                }

                ReadOnlySpan<byte> controlBytes = s.Slice(control, controlSize);
                ReadOnlySpan<byte> codeBytes = s.Slice(byteCode, byteCodeSize);
                ulong id = RyubingCache.IdOf(codeBytes, controlBytes);
                ids[stage] = id;

                if (register && !Blobs.ContainsKey(id))
                {
                    Blobs.TryAdd(id, new StageBlob { Id = id, BnshStage = stage, Control = controlBytes.ToArray(), ByteCode = codeBytes.ToArray() });
                }
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }

        return ProgramKey.From(ids);
    }
}

/// <summary>
/// Values from an NVN shader control section (NVNshaderControl).
/// </summary>
readonly struct NvnControl
{
    /// <summary>NVNshaderControl.early_fragment_tests (fragment shaders).</summary>
    public const int EarlyFragmentTestsOffset = 0x718;

    public readonly uint ConstBufSize;
    public readonly uint ConstBufOffset;

    public NvnControl(ReadOnlySpan<byte> control)
    {
        ConstBufSize = BinaryPrimitives.ReadUInt32LittleEndian(control[0x6FC..]);
        ConstBufOffset = BinaryPrimitives.ReadUInt32LittleEndian(control[0x700..]);
    }

    public static bool EarlyFragmentTests(byte[] control) => control.Length > EarlyFragmentTestsOffset && control[EarlyFragmentTestsOffset] != 0;

    public static uint ReadU32(ReadOnlySpan<byte> control, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(control[offset..]);
}

static class SpanExtensions
{
    public static ReadOnlySpan<ulong> AsUlongs(this byte[] data, int offset)
    {
        int length = (data.Length - offset) & ~7;
        return MemoryMarshal.Cast<byte, ulong>(data.AsSpan(offset, Math.Max(0, length)));
    }
}
