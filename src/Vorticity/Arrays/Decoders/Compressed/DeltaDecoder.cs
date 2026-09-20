using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vorticity.Arrays.Decoders.Canonical;

using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>fastlanes.delta</c>: a per-lane prefix sum over 1024-element blocks. The sum runs
/// along FastLanes lanes rather than along rows, so a block is visited in the same transposed order
/// bit-packing uses, with <c>index(row, lane) = order[row / 8] * 16 + (row % 8) * 128 + lane</c> and
/// each lane seeded from a per-block base vector. Signedness is not a case: both children are read
/// as the unsigned type of the same width, because a wrapping add inverts the encoder's wrapping
/// subtract whatever the sign.
/// </summary>
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

        // Deltas are padded to a whole number of blocks: a short final block would read a base
        // vector that was never written and produce values rather than an error.
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
        //
        // The zero fill stays even though the loop below writes every element of the window. A
        // buffer of this size comes back from the pool with its pages already faulted, so the fill
        // is not a page-fault pass but a sequential prefetch of memory the untranspose loop is about
        // to rewrite, and skipping it trades that for a cold miss per row. Only allocations too
        // large for the pool, which get fresh pages on every scan, are worth leaving uninitialized:
        // the rule is the size, not the encoding.
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
        switch (width)
        {
            case 1:
                Undelta<byte>(bases, deltas, destination, lanes, offset, length);
                break;
            case 2:
                Undelta<ushort>(bases, deltas, destination, lanes, offset, length);
                break;
            case 4:
                Undelta<uint>(bases, deltas, destination, lanes, offset, length);
                break;
            default:
                Undelta<ulong>(bases, deltas, destination, lanes, offset, length);
                break;
        }
    }

    /// <summary>
    /// The prefix sum, with the element type resolved once and the lanes walked together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lanes are the vector, not the rows. The index formula is contiguous in lane, so one row's
    /// whole lane vector is a contiguous run of the delta block, and each lane's accumulator is
    /// independent of every other -- which is exactly the shape a prefix sum normally is not. The
    /// per-row step is therefore a handful of vector adds rather than a scalar add per element.
    /// </para>
    /// <para>
    /// The 1024-element block is rented rather than allocated per decode, so a file full of delta
    /// nodes does not put one array on the heap per node.
    /// </para>
    /// </remarks>
    private static void Undelta<T>(
        ReadOnlySpan<byte> bases,
        ReadOnlySpan<byte> deltas,
        Span<byte> destination,
        int lanes,
        int offset,
        int length)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> baseValues = MemoryMarshal.Cast<byte, T>(bases);
        ReadOnlySpan<T> deltaValues = MemoryMarshal.Cast<byte, T>(deltas);
        Span<T> output = MemoryMarshal.Cast<byte, T>(destination);
        int rowsPerLane = BlockSize / lanes;
        ReadOnlySpan<byte> order = FastLanes.Order;

        Scratch<T> blockScratch = new Scratch<T>(BlockSize, default);
        Scratch<T> laneScratch = new Scratch<T>(lanes, default);
        try
        {
            Span<T> block = blockScratch.Span;
            Span<T> running = laneScratch.Span;

            int firstBlock = offset / BlockSize;
            int lastBlock = (offset + length - 1) / BlockSize;

            for (int b = firstBlock; b <= lastBlock; b++)
            {
                ReadOnlySpan<T> deltaBlock = deltaValues.Slice(b * BlockSize, BlockSize);
                baseValues.Slice(b * lanes, lanes).CopyTo(running);

                for (int row = 0; row < rowsPerLane; row++)
                {
                    int at = (order[row >> 3] * 16) + ((row & 7) * 128);
                    Accumulate(running, deltaBlock.Slice(at, lanes), block.Slice(at, lanes));
                }

                // Untranspose on the way out: the prefix sum runs in FastLanes' transposed space, so
                // `block` is not in row order. Mapping each copied element, rather than
                // untransposing the whole block into a second buffer, keeps the cost proportional
                // to the window: a batch that wants eight rows of a block maps eight indices.
                int blockStart = b * BlockSize;
                int from = Math.Max(offset, blockStart);
                int to = Math.Min(offset + length, blockStart + BlockSize);

                // Addressed by reference because this loop runs once per decoded value, and the
                // bounds checks an indexed form would repeat cannot fail: `p - blockStart` is in
                // [0, 1024) by the clamps above and the table has exactly 1024 entries.
                ref int table = ref MemoryMarshal.GetReference(FastLanes.UntransposeTable);
                ref T blockRef = ref MemoryMarshal.GetReference(block);
                ref T outputRef = ref MemoryMarshal.GetReference(output);
                for (int p = from; p < to; p++)
                {
                    int at = Unsafe.Add(ref table, p - blockStart);
                    Unsafe.Add(ref outputRef, p - offset) = Unsafe.Add(ref blockRef, at);
                }
            }
        }
        finally
        {
            laneScratch.Dispose();
            blockScratch.Dispose();
        }
    }

    /// <summary>
    /// <c>running[l] += delta[l]</c> for every lane, publishing each sum into the block.
    /// </summary>
    private static void Accumulate<T>(Span<T> running, ReadOnlySpan<T> delta, Span<T> into)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        // Indexing the spans would cost a bounds check per vector iteration, on the innermost loop
        // of the encoding. The three spans are the same length -- the caller slices all three to
        // `lanes` -- so one set of hoisted references serves all of them.
        int count = running.Length;
        ref T runningRef = ref MemoryMarshal.GetReference(running);
        ref T deltaRef = ref MemoryMarshal.GetReference(delta);
        ref T intoRef = ref MemoryMarshal.GetReference(into);

        int i = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<T>.Count)
        {
            int width = Vector<T>.Count;
            for (; i <= count - width; i += width)
            {
                Vector<T> sum = Vector.LoadUnsafe(ref runningRef, (nuint)i)
                    + Vector.LoadUnsafe(ref deltaRef, (nuint)i);
                sum.StoreUnsafe(ref runningRef, (nuint)i);
                sum.StoreUnsafe(ref intoRef, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            T sum = unchecked(Unsafe.Add(ref runningRef, i) + Unsafe.Add(ref deltaRef, i));
            Unsafe.Add(ref runningRef, i) = sum;
            Unsafe.Add(ref intoRef, i) = sum;
        }
    }
}
