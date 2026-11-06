using System.Buffers.Binary;
using System.IO.Compression;
using ZstdSharp.Unsafe;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// Thrown when compressed input ends before the stream is complete.
/// </summary>
sealed class TruncatedException : Exception
{
}

/// <summary>
/// Decoders for the compression formats commonly found inside Switch game files.
/// All decoders validate their input and throw on malformed data.
/// </summary>
static class Codecs
{
    public const int MaxOutput = 1 << 30;

    public static byte[] Yaz0(ReadOnlySpan<byte> src)
    {
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(src[4..]);
        if (size <= 0 || size > MaxOutput)
        {
            throw new InvalidDataException();
        }

        byte[] dst = new byte[size];
        int si = 16;
        int di = 0;

        while (di < size)
        {
            if (si >= src.Length)
            {
                throw new TruncatedException();
            }

            byte code = src[si++];

            for (int bit = 0; bit < 8 && di < size; bit++, code <<= 1)
            {
                if ((code & 0x80) != 0)
                {
                    if (si >= src.Length)
                    {
                        throw new TruncatedException();
                    }

                    dst[di++] = src[si++];
                    continue;
                }

                if (si + 1 >= src.Length)
                {
                    throw new TruncatedException();
                }

                int b1 = src[si++];
                int b2 = src[si++];
                int dist = (((b1 & 0xF) << 8) | b2) + 1;
                int n = b1 >> 4;

                if (n == 0)
                {
                    if (si >= src.Length)
                    {
                        throw new TruncatedException();
                    }

                    n = src[si++] + 0x12;
                }
                else
                {
                    n += 2;
                }

                if (dist > di)
                {
                    throw new InvalidDataException();
                }

                int from = di - dist;
                for (int k = 0; k < n && di < size; k++)
                {
                    dst[di++] = dst[from + k];
                }
            }
        }

        return dst;
    }

    public static byte[] Lz4Frame(ReadOnlySpan<byte> src)
    {
        int p = 4;
        if (src.Length < 7)
        {
            throw new TruncatedException();
        }

        byte flg = src[p++];
        byte bd = src[p++];

        if ((flg >> 6) != 1 || (bd & 0x8F) != 0)
        {
            throw new InvalidDataException();
        }

        bool blockChecksum = (flg & 0x10) != 0;
        bool contentSize = (flg & 0x08) != 0;
        bool contentChecksum = (flg & 0x04) != 0;
        bool dictId = (flg & 0x01) != 0;

        if (contentSize)
        {
            p += 8;
        }

        if (dictId)
        {
            p += 4;
        }

        p++; // header checksum

        GrowBuffer output = new();

        while (true)
        {
            if (p + 4 > src.Length)
            {
                throw new TruncatedException();
            }

            uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]);
            p += 4;

            if (blockSize == 0)
            {
                break;
            }

            bool uncompressed = (blockSize & 0x80000000) != 0;
            int size = (int)(blockSize & 0x7FFFFFFF);

            if (size > (4 << 20))
            {
                throw new InvalidDataException();
            }

            if (p + size > src.Length)
            {
                throw new TruncatedException();
            }

            if (uncompressed)
            {
                output.Write(src.Slice(p, size));
            }
            else
            {
                Lz4Block(src.Slice(p, size), output);
            }

            p += size + (blockChecksum ? 4 : 0);

            if (output.Length > MaxOutput)
            {
                throw new InvalidDataException();
            }
        }

        if (contentChecksum)
        {
            p += 4;
        }

        return output.ToArray();
    }

    private sealed class GrowBuffer
    {
        public byte[] Data = new byte[1 << 16];
        public int Length;

        public void Ensure(int extra)
        {
            if (Length + extra > Data.Length)
            {
                long newSize = Math.Max((long)Data.Length * 2, (long)Length + extra);
                if (newSize > MaxOutput)
                {
                    throw new InvalidDataException();
                }

                Array.Resize(ref Data, (int)newSize);
            }
        }

        public void Write(ReadOnlySpan<byte> s)
        {
            Ensure(s.Length);
            s.CopyTo(Data.AsSpan(Length));
            Length += s.Length;
        }

        public byte[] ToArray() => Data.AsSpan(0, Length).ToArray();
    }

    private static void Lz4Block(ReadOnlySpan<byte> src, GrowBuffer output)
    {
        int p = 0;

        while (p < src.Length)
        {
            int token = src[p++];
            int literals = token >> 4;

            if (literals == 15)
            {
                int b;
                do
                {
                    if (p >= src.Length)
                    {
                        throw new InvalidDataException();
                    }

                    b = src[p++];
                    literals += b;
                }
                while (b == 255);
            }

            if (p + literals > src.Length)
            {
                throw new InvalidDataException();
            }

            output.Write(src.Slice(p, literals));
            p += literals;

            if (p >= src.Length)
            {
                break;
            }

            if (p + 2 > src.Length)
            {
                throw new InvalidDataException();
            }

            int offset = src[p] | (src[p + 1] << 8);
            p += 2;
            int match = (token & 15) + 4;

            if ((token & 15) == 15)
            {
                int b;
                do
                {
                    if (p >= src.Length)
                    {
                        throw new InvalidDataException();
                    }

                    b = src[p++];
                    match += b;
                }
                while (b == 255);
            }

            if (offset == 0 || offset > output.Length)
            {
                throw new InvalidDataException();
            }

            output.Ensure(match);
            byte[] data = output.Data;
            int from = output.Length - offset;
            for (int k = 0; k < match; k++)
            {
                data[output.Length + k] = data[from + k];
            }

            output.Length += match;
        }
    }

    /// <summary>
    /// Decodes a single Zstandard frame. Returns null when the frame needs a dictionary.
    /// </summary>
    public static unsafe byte[] ZstdFrame(ReadOnlySpan<byte> src, out int consumed)
    {
        fixed (byte* p = src)
        {
            nuint frameSize = Methods.ZSTD_findFrameCompressedSize(p, (nuint)src.Length);
            if (frameSize > (nuint)src.Length)
            {
                // Error codes are huge values; distinguish truncation from garbage by checking the header.
                ulong headerSize = Methods.ZSTD_getFrameContentSize(p, (nuint)src.Length);
                if (headerSize == unchecked(0UL - 2))
                {
                    throw new InvalidDataException();
                }

                throw new TruncatedException();
            }

            consumed = (int)frameSize;

            if (Methods.ZSTD_getDictID_fromFrame(p, frameSize) != 0)
            {
                return null;
            }

            ulong contentSize = Methods.ZSTD_getFrameContentSize(p, frameSize);

            if (contentSize != unchecked(0UL - 1) && contentSize != unchecked(0UL - 2))
            {
                if (contentSize > MaxOutput)
                {
                    throw new InvalidDataException();
                }

                byte[] dst = new byte[contentSize];
                fixed (byte* d = dst)
                {
                    nuint written = Methods.ZSTD_decompress(d, (nuint)dst.Length, p, frameSize);
                    if (written != (nuint)dst.Length)
                    {
                        throw new InvalidDataException();
                    }
                }

                return dst;
            }

            using MemoryStream input = new(src[..(int)frameSize].ToArray());
            using ZstdSharp.DecompressionStream ds = new(input);
            return ReadCapped(ds);
        }
    }

    public static byte[] Zlib(byte[] buffer, int offset, int length, bool gzip)
    {
        using MemoryStream input = new(buffer, offset, length, false);
        using Stream s = gzip ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
        return ReadCapped(s);
    }

    private static byte[] ReadCapped(Stream s)
    {
        MemoryStream output = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            output.Write(chunk, 0, read);
            if (output.Length > MaxOutput)
            {
                throw new InvalidDataException();
            }
        }

        return output.ToArray();
    }
}
