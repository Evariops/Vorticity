using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// A zstd-compressed frame over a column's valid values, with its priced size. The stream holds
/// only the valid values, nulls excluded, so the decoder scatters them back across the null slots.
/// </summary>
internal sealed class ZstdPlan
{
    private ZstdPlan(byte[] frame, int frameLength, int uncompressedSize, int valueCount)
    {
        Frame = frame;
        FrameLength = frameLength;
        UncompressedSize = uncompressedSize;
        ValueCount = valueCount;
    }

    /// <summary>The compressed frame, exactly as it goes into the buffer.</summary>
    internal byte[] Frame { get; }

    /// <summary>
    /// Bytes of <see cref="Frame"/> that are the frame; the rest is slack in a pooled rental sized
    /// for the worst case. Ownership of the array passes to whoever writes the plan, or back to the
    /// pool through <see cref="Release"/>; after either, <see cref="Frame"/> must not be read.
    /// </summary>
    internal int FrameLength { get; }

    /// <summary>Bytes the frame decompresses to, which the decoder allocates against a cap.</summary>
    internal int UncompressedSize { get; }

    /// <summary>Values stored in the frame: the column's valid rows, nulls excluded.</summary>
    internal int ValueCount { get; }

    /// <summary>Hands <see cref="Frame"/> back to the pool, for a plan nobody wrote.</summary>
    internal void Release() => ArrayPool<byte>.Shared.Return(Frame);

    /// <summary>
    /// Compresses a <c>VarBinView</c> or <c>Primitive</c> node's valid values, returning
    /// <see langword="null"/> unless the frame beats <paramref name="canonicalSize"/> by a margin
    /// wide enough to justify the decompression pass every read then pays.
    /// </summary>
    internal static ZstdPlan? TryBuild(CanonicalArena arena, int nodeIndex, long canonicalSize)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind == CanonicalKind.Primitive)
        {
            return TryBuildPrimitive(arena, node, canonicalSize);
        }

        if (node.Kind != CanonicalKind.VarBinView)
        {
            return null;
        }

        int rows = node.Length;
        long streamBytes = 0;
        int valueCount = 0;

        // The validity kind is a property of the node, so it is resolved once rather than switched
        // on per row.
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        for (int i = 0; i < rows; i++)
        {
            if (!valid.IsValid(i))
            {
                continue;
            }

            valueCount++;
            streamBytes += sizeof(uint) + ValueOf(node, i).Length;
            if (streamBytes > int.MaxValue)
            {
                return null;
            }
        }

        if (valueCount == 0)
        {
            return null;
        }

        // Rented, not allocated: pricing zstd needs two buffers the size of the column for a
        // candidate that may lose.
        byte[] stream = ArrayPool<byte>.Shared.Rent((int)streamBytes);
        byte[] destination = ArrayPool<byte>.Shared.Rent(
            checked((int)ZstandardEncoder.GetMaxCompressedLength((int)streamBytes)));
        bool kept = false;
        try
        {
        // The copy is the encoding: the wire form is `u32 length` then the bytes, value after
        // value, and the column's views point at bytes that are neither contiguous nor
        // length-prefixed, so there is nothing to compress in place.
        int offset = 0;
        for (int i = 0; i < rows; i++)
        {
            if (!valid.IsValid(i))
            {
                continue;
            }

            ReadOnlySpan<byte> value = ValueOf(node, i);
            BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(offset, sizeof(uint)), (uint)value.Length);
            offset += sizeof(uint);
            value.CopyTo(stream.AsSpan(offset));
            offset += value.Length;
        }

        // The one-shot builds a native compression context per call; that is one per column per
        // chunk, each followed by compressing a whole column, so pooling the context would buy
        // nothing here.
        if (!ZstandardEncoder.TryCompress(
                stream.AsSpan(0, (int)streamBytes), destination, out int written) || written <= 0)
        {
            return null;
        }

        // The frame's own bytes are the whole cost: the metadata is two varints and the validity
        // child is written either way.
        if (written * (long)MarginDenominator >= canonicalSize * (long)MarginNumerator)
        {
            return null;
        }

        // The plan takes the rental on the success path; `kept` is what tells the `finally` not to
        // return it.
        kept = true;
        return new ZstdPlan(destination, written, (int)streamBytes, valueCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stream);
            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(destination);
            }
        }
    }

    /// <summary>
    /// A primitive column: the stored stream is the valid values back to back, with no length
    /// prefixes, since the decoder scatters them using the fixed width alone.
    /// </summary>
    private static ZstdPlan? TryBuildPrimitive(CanonicalArena arena, CanonicalNode node, long canonicalSize)
    {
        int width = node.PType.ByteWidth();
        if (width == 0)
        {
            return null;
        }

        int rows = node.Length;
        ReadOnlySpan<byte> source = node.Values.Span;

        // With no null row the compacting loop would copy the source to itself, so the source is
        // compressed as it stands. It has to be sliced at `rows * width`: the arena's block can be
        // longer than the rows this node owns, and a longer input is a different frame.
        bool allValid = node.Validity.IsAllValid;
        int valueCount = allValid ? rows : 0;
        byte[]? stream = allValid ? null : ArrayPool<byte>.Shared.Rent(rows * width);
        byte[] destination = ArrayPool<byte>.Shared.Rent(
            checked((int)ZstandardEncoder.GetMaxCompressedLength(rows * width)));
        bool kept = false;
        try
        {
            if (stream is not null)
            {
                ValidityReader valid = ValidityReader.Of(arena, node.Validity);
                for (int i = 0; i < rows; i++)
                {
                    if (!valid.IsValid(i))
                    {
                        continue;
                    }

                    source.Slice(i * width, width).CopyTo(stream.AsSpan(valueCount * width, width));
                    valueCount++;
                }
            }

            if (valueCount == 0)
            {
                return null;
            }

            int streamBytes = valueCount * width;
            ReadOnlySpan<byte> input = stream is null
                ? source[..streamBytes]
                : stream.AsSpan(0, streamBytes);
            if (!ZstandardEncoder.TryCompress(input, destination, out int written) || written <= 0)
            {
                return null;
            }

            // A much wider margin than varbin, because it is a read-time decision: a primitive
            // column's alternative is bit-packing, which decodes several times faster than zstd, so
            // only the large size wins are worth taking and the marginal ones are left to it.
            if (written * (long)PrimitiveMarginDenominator >= canonicalSize * (long)PrimitiveMarginNumerator)
            {
                return null;
            }

            kept = true;
            return new ZstdPlan(destination, written, streamBytes, valueCount);
        }
        finally
        {
            if (stream is not null)
            {
                ArrayPool<byte>.Shared.Return(stream);
            }

            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(destination);
            }
        }
    }

    /// <summary>On a primitive column, keep zstd only when it saves at least a quarter.</summary>
    private const int PrimitiveMarginNumerator = 3;

    private const int PrimitiveMarginDenominator = 4;

    /// <summary>Keep zstd only when it saves at least a tenth of the plain form.</summary>
    private const int MarginNumerator = 9;

    private const int MarginDenominator = 10;

    private static bool IsValid(CanonicalArena arena, CanonicalNode node, int row) => FsstPlan.IsValid(arena, node, row);

    private static ReadOnlySpan<byte> ValueOf(CanonicalNode node, int row) => FsstPlan.ValueOf(node, row);
}
