using System;
using System.Buffers;
using ManagedCompressions.Zstd;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// A region of an index held as one zstd frame behind a version byte and its body's length, as the variant columns' path
/// directories, path maps and block statistics are, each at a version of its own: what they write and read alike.
/// </summary>
internal static class ZstdRegion
{
    /// <summary>The region of <paramref name="body"/> at <paramref name="version"/>.</summary>
    /// <exception cref="InvalidOperationException">The body did not compress into the bound zstd gives it.</exception>
    internal static byte[] Encode(ulong version, ReadOnlySpan<byte> body)
    {
        // The frame compressed into a rented block at zstd's bound, then the region made once at its length: a region of
        // each shredded key of each object of a consolidation made both new.
        Block<byte> frame = Blocks.Rent<byte>(ZstdCompressor.GetMaxCompressedLength(body.Length));
        try
        {
            ZstdCompressor encoder = Writing.ZstdEncoders.Rent();
            int written;
            try
            {
                if (encoder.Compress(body, frame, out _, out written) != OperationStatus.Done)
                {
                    throw new InvalidOperationException("A region did not compress into the bound zstd gives it.");
                }
            }
            finally
            {
                Writing.ZstdEncoders.Return(encoder);
            }

            byte[] region = new byte[Serialization.Varint.Size(version) + Serialization.Varint.Size((ulong)body.Length) + written];
            int at = Serialization.Varint.Write(region, version);
            at += Serialization.Varint.Write(region.AsSpan(at), (ulong)body.Length);
            frame.AsSpan(0, written).CopyTo(region.AsSpan(at));
            return region;
        }
        finally
        {
            Blocks.Return(frame);
        }
    }

    /// <summary>
    /// The body of region <paramref name="bytes"/> in an array of its own, of its exact length, which a reader keeps and
    /// slices; null as <see cref="Rent"/> says.
    /// </summary>
    internal static byte[]? Decode(ReadOnlySpan<byte> bytes, ulong version)
    {
        if (Rent(bytes, version) is not { } rented)
        {
            return null;
        }

        try
        {
            return rented.AsSpan().ToArray();
        }
        finally
        {
            Blocks.Return(rented);
        }
    }

    /// <summary>
    /// The body of region <paramref name="bytes"/> in a block of the pool, which the caller gives back; null when the region
    /// is at another version than <paramref name="version"/>, claims a body longer than zstd's most expansion of its frame
    /// (<see cref="ZstdDecoders.MostExpansion"/>), which no frame decodes to, or does not decode.
    /// </summary>
    internal static Block<byte>? Rent(ReadOnlySpan<byte> bytes, ulong version)
    {
        try
        {
            ProtoReader header = new ProtoReader(bytes);
            if (header.ReadVarint() != version)
            {
                return null;
            }

            ulong declared = header.ReadVarint();
            ReadOnlySpan<byte> frame = bytes[(bytes.Length - header.Remaining)..];
            if (declared > (ulong)frame.Length * ZstdDecoders.MostExpansion || declared > int.MaxValue)
            {
                return null;
            }

            int size = (int)declared;
            Block<byte> body = Blocks.Rent<byte>(size);
            ZstdDecompressor decoder = ZstdDecoders.Rent();
            try
            {
                if (decoder.Decompress(frame, body, out _, out int produced) != OperationStatus.Done || produced != size)
                {
                    Blocks.Return(body);
                    return null;
                }
            }
            finally
            {
                ZstdDecoders.Return(decoder);
            }

            return body;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }
}
