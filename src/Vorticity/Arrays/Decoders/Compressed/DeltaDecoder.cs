// fastlanes.delta - vortex-fastlanes-0.86.1/src/delta/{vtable/mod.rs,array/delta_decompress.rs}
// and the kernel in fastlanes-0.7.2/src/delta.rs.
//
// DELTA IS A PREFIX SUM ALONG FASTLANES LANES, NOT ALONG ROWS. A 1024-element block is visited in
// the same transposed order bit-packing uses, and each lane carries its own running total seeded
// from a per-block base vector:
//
//     index(row, lane) = FL_ORDER[row / 8] * 16 + (row % 8) * 128 + lane
//
// with `row` over [0, T) and `lane` over [0, 1024 / T), T being the type's bit width. For u32 the
// largest index that produces is 6*16 + 7*128 + 31 = 1023, which is the check that the formula and
// the lane count agree.
//
// SIGNEDNESS IS NOT A CASE. Upstream reinterprets both children as the unsigned type of the same
// width and adds with wrapping, because a wrapping add inverts the encoder's wrapping subtract
// whatever the sign. This decoder works on raw bytes for the same reason.
//
// NOT IN ANY CORE EDITION, which is why the corpus reaches it only through the generator's forced
// path, and why `EncodingRegistry.DescribeUnsupported` used to name it. A default writer upstream
// cannot emit one; that is a statement about writers and has never been a reason not to read.
using System;
using System.Buffers.Binary;

using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>fastlanes.delta</c>: a per-lane prefix sum over 1024-element blocks.</summary>
public sealed class DeltaDecoder : ArrayDecoder
{
    private const string Id = "fastlanes.delta";

    /// <summary>Elements in one FastLanes block.</summary>
    private const int BlockSize = 1024;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DeltaDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "fastlanes.delta"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FastLanesDelta;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, Id);

        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);
        DeltaMetadata metadata = DeltaMetadata.Read(node.Metadata);

        int width = ptype.ByteWidth();
        int lanes = BlockSize / (width * 8);

        if (metadata.DeltasLength > int.MaxValue)
        {
            CompressedThrow.Format($"{Id} declares {metadata.DeltasLength} deltas, which does not fit an int.");
        }

        int deltasLength = (int)metadata.DeltasLength;
        int offset = (int)metadata.Offset;

        // `debug_assert!(remainder.is_empty(), "deltas must be padded to a multiple of 1024")`,
        // promoted to a real check: a short final block would otherwise read a base vector that was
        // never written and produce values rather than an error.
        if (deltasLength % BlockSize != 0)
        {
            CompressedThrow.Format(
                $"{Id} declares {deltasLength} deltas, which is not a multiple of {BlockSize}.");
        }

        // The window [offset, offset + length) must land inside what the blocks produce.
        if ((long)offset + length > deltasLength)
        {
            CompressedThrow.Format(
                $"{Id} takes rows [{offset}, {offset + (long)length}) of {deltasLength} decoded values.");
        }

        int blocks = deltasLength / BlockSize;
        int basesLength = blocks * lanes;

        int basesIndex = context.DecodeChild(in node, 0, dtype, basesLength);
        int deltasIndex = context.DecodeChild(in node, 1, dtype, deltasLength);

        CanonicalNode bases = RequirePrimitive(context, basesIndex, ptype, "bases", basesLength);
        CanonicalNode deltas = RequirePrimitive(context, deltasIndex, ptype, "deltas", deltasLength);

        // Only the window is materialized, not the whole decoded run: a delta node of a million rows
        // asked for one batch has no reason to produce a million values.
        VortexBuffer output = CompressedValues.Allocate(
            context, length * width, width, Id, out Span<byte> destination);

        Undelta(bases.Values.Span, deltas.Values.Span, destination, width, lanes, offset, length);

        return context.Canonical.AddPrimitive(dtype, length, deltas.Validity, ptype, output);
    }

    private static CanonicalNode RequirePrimitive(
        ArrayDecodeContext context, int index, PType ptype, string what, int expected)
    {
        CanonicalNode child = context.Canonical.GetNode(index);
        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, what, child.Kind, "a Primitive");
        }

        if (child.PType != ptype)
        {
            CompressedThrow.Format(
                $"{Id}'s {what} child decoded as {child.PType.Name()}; {ptype.Name()} was required.");
        }

        if (child.Length != expected)
        {
            CompressedThrow.ChildLength(Id, what, child.Length, expected);
        }

        return child;
    }

    /// <summary>Runs the per-lane prefix sum and copies the requested window out of it.</summary>
    /// <remarks>
    /// Blocks outside the window are skipped whole. Inside a block every lane must still be summed
    /// from its base even when only part of the block is wanted, because a row's value depends on
    /// every earlier row of its own lane - which is exactly why this cannot be a per-row take.
    /// </remarks>
    private static void Undelta(
        ReadOnlySpan<byte> bases,
        ReadOnlySpan<byte> deltas,
        Span<byte> destination,
        int width,
        int lanes,
        int offset,
        int length)
    {
        int rowsPerLane = BlockSize / lanes;
        ReadOnlySpan<byte> order = FastLanes.Order;

        Span<byte> block = stackalloc byte[0];
        byte[] rented = new byte[BlockSize * width];
        block = rented;

        int firstBlock = offset / BlockSize;
        int lastBlock = (offset + length - 1) / BlockSize;

        for (int b = firstBlock; b <= lastBlock; b++)
        {
            ReadOnlySpan<byte> deltaBlock = deltas.Slice(b * BlockSize * width, BlockSize * width);
            ReadOnlySpan<byte> baseVector = bases.Slice(b * lanes * width, lanes * width);

            for (int lane = 0; lane < lanes; lane++)
            {
                ulong previous = ReadBits(baseVector.Slice(lane * width, width), width);
                for (int row = 0; row < rowsPerLane; row++)
                {
                    int index = (order[row / 8] * 16) + ((row % 8) * 128) + lane;
                    ulong next = unchecked(previous + ReadBits(deltaBlock.Slice(index * width, width), width));
                    WriteBits(block.Slice(index * width, width), next, width);
                    previous = next;
                }
            }

            // UNTRANSPOSE ON THE WAY OUT. The prefix sum runs in FastLanes' transposed space -
            // that is what the lane iteration means - so `block` is not in row order and upstream
            // calls `Transpose::untranspose` before returning. Doing it per copied element instead
            // of into a second 1024-element buffer keeps the window's cost proportional to the
            // window: a batch that wants 8 rows of a block maps 8 indices, not 1024.
            int blockStart = b * BlockSize;
            int from = Math.Max(offset, blockStart);
            int to = Math.Min(offset + length, blockStart + BlockSize);
            for (int p = from; p < to; p++)
            {
                int transposed = FastLanes.Untranspose(p - blockStart);
                block.Slice(transposed * width, width)
                    .CopyTo(destination.Slice((p - offset) * width, width));
            }
        }
    }

    private static ulong ReadBits(ReadOnlySpan<byte> value, int width) => width switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(value),
    };

    private static void WriteBits(Span<byte> destination, ulong value, int width)
    {
        switch (width)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)value);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value);
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
                break;
        }
    }
}
