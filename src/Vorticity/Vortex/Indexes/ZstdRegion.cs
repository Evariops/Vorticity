using System;
using System.Buffers;
using ManagedCompressions.Zstd;
using Vorticity.Arrays;
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
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(body.Length)];
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

        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(version);
            writer.WriteVarint((ulong)body.Length);
            writer.WriteRawBytes(frame.AsSpan(0, written));
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The body of region <paramref name="bytes"/>, its first <paramref name="length"/> bytes of an array of the shared pool,
    /// which the caller gives back; null when the region is at another version than <paramref name="version"/>, claims a
    /// body longer than zstd's most expansion of its frame (<see cref="ZstdDecoders.MostExpansion"/>), which no frame
    /// decodes to, or does not decode.
    /// </summary>
    internal static byte[]? Rent(ReadOnlySpan<byte> bytes, ulong version, out int length)
    {
        length = 0;
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
            byte[] body = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
            ZstdDecompressor decoder = ZstdDecoders.Rent();
            try
            {
                if (decoder.Decompress(frame, body.AsSpan(0, size), out _, out int produced) != OperationStatus.Done || produced != size)
                {
                    ArrayPool<byte>.Shared.Return(body);
                    return null;
                }
            }
            finally
            {
                ZstdDecoders.Return(decoder);
            }

            length = size;
            return body;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }
}
