using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// GZIP, RFC 1952: members of a header, DEFLATE data (<see cref="Inflate"/>) and a trailer of the
/// data's CRC-32 and length, read by this package into a destination of exactly the page's length.
/// </summary>
/// <remarks>
/// A page holds one member or several, each decoding after the one before, and nothing past the
/// last. A member's header is checked as zlib checks it: the magic, the method, no reserved flag,
/// its optional fields inside the page, its own CRC where it carries one. Its trailer is checked
/// against what it decoded: the CRC-32, and the length modulo 2^32. The tables of the dynamic codes
/// are rented from the shared array pool, once a page.
/// </remarks>
internal static class Gzip
{
    private const int HeaderBytes = 10;
    private const int TrailerBytes = 8;
    private const byte HeaderCrc = 1 << 1;
    private const byte Extra = 1 << 2;
    private const byte Name = 1 << 3;
    private const byte Comment = 1 << 4;
    private const byte Reserved = 0xE0;

    /// <summary>Decompresses the members of <paramref name="source"/> into all of <paramref name="destination"/>.</summary>
    internal static void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            // Even an empty member has a header and a trailer; no bytes at all hold no member.
            if (!destination.IsEmpty)
            {
                ThrowFormat("A GZIP page holds no member.");
            }

            return;
        }

        uint[] tables = ArrayPool<uint>.Shared.Rent(Inflate.TableEntries);
        try
        {
            int read = 0;
            int written = 0;
            while (read < source.Length)
            {
                read += Member(source[read..], destination[written..], tables, out int produced);
                written += produced;
            }

            if (written != destination.Length)
            {
                ThrowFormat("A GZIP page decompresses to another length than its header declares.");
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(tables);
        }
    }

    /// <summary>The member at the start of <paramref name="source"/>, decoded into the start of <paramref name="destination"/>; the bytes it takes.</summary>
    private static int Member(ReadOnlySpan<byte> source, Span<byte> destination, uint[] tables, out int produced)
    {
        if (source.Length < HeaderBytes + TrailerBytes || source[0] != 0x1F || source[1] != 0x8B || source[2] != 8)
        {
            ThrowFormat("A GZIP page holds bytes that are not a GZIP member of DEFLATE data.");
        }

        byte flags = source[3];
        if ((flags & Reserved) != 0)
        {
            ThrowFormat("A GZIP member sets a flag RFC 1952 reserves.");
        }

        int at = HeaderBytes;
        if ((flags & Extra) != 0)
        {
            at += sizeof(ushort) + BinaryPrimitives.ReadUInt16LittleEndian(source[at..]);
        }

        if ((flags & Name) != 0)
        {
            at = PastZero(source, at);
        }

        if ((flags & Comment) != 0)
        {
            at = PastZero(source, at);
        }

        if ((flags & HeaderCrc) != 0)
        {
            if (at > source.Length - sizeof(ushort))
            {
                ThrowFormat("A GZIP member's header runs past its page.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(source[at..]) != (ushort)Crc32.HashToUInt32(source[..at]))
            {
                ThrowFormat("A GZIP member's header does not match its CRC.");
            }

            at += sizeof(ushort);
        }

        if (at > source.Length)
        {
            ThrowFormat("A GZIP member's header runs past its page.");
        }

        at += Inflate.Decode(source[at..], destination, tables, out produced);
        if (source.Length - at < TrailerBytes)
        {
            ThrowFormat("A GZIP member ends before its trailer.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(source[(at + sizeof(uint))..]) != (uint)produced
            || BinaryPrimitives.ReadUInt32LittleEndian(source[at..]) != Crc32.HashToUInt32(destination[..produced]))
        {
            ThrowFormat("A GZIP member decodes to bytes its trailer does not describe: its CRC-32 or its length differs.");
        }

        return at + TrailerBytes;
    }

    /// <summary>The position past the zero that ends the field at <paramref name="at"/>.</summary>
    private static int PastZero(ReadOnlySpan<byte> source, int at)
    {
        int zero = at < source.Length ? source[at..].IndexOf((byte)0) : -1;
        if (zero < 0)
        {
            ThrowFormat("A GZIP member's header runs past its page.");
        }

        return at + zero + 1;
    }

    [DoesNotReturn]
    private static void ThrowFormat(string message) => ParquetThrow.Format(message);
}
