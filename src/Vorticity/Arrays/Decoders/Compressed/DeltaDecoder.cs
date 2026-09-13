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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vorticity.Arrays.Decoders.Canonical;

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
    /// The prefix sum, with the element type resolved once and the LANES walked together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE LANES ARE THE VECTOR, not the rows. `index(row, lane) = FL_ORDER[row / 8] * 16 +
    /// (row % 8) * 128 + lane` is contiguous in LANE, so for one row the whole lane vector is one
    /// contiguous run of the delta block -- and each lane's accumulator is independent of every
    /// other, which is exactly the shape a prefix sum normally is not. There are 16 lanes for a
    /// 64-bit element and 128 for an 8-bit one, so the per-row step is a handful of vector adds
    /// where it used to be one scalar add per element through two physical-type switches.
    /// </para>
    /// <para>
    /// The 1024-element block is rented rather than `new byte[BlockSize * width]` per decode, which
    /// is PERF-AUDIT §3.2's heap allocation per delta node.
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

                // UNTRANSPOSE ON THE WAY OUT. The prefix sum runs in FastLanes' transposed space -
                // that is what the lane iteration means - so `block` is not in row order and
                // upstream calls `Transpose::untranspose` before returning. Doing it per copied
                // element instead of into a second 1024-element buffer keeps the window's cost
                // proportional to the window: a batch that wants 8 rows of a block maps 8 indices,
                // not 1024.
                int blockStart = b * BlockSize;
                int from = Math.Max(offset, blockStart);
                int to = Math.Min(offset + length, blockStart + BlockSize);

                // ADDRESSED BY REFERENCE, because this loop runs once per DECODED VALUE and the
                // by-index form paid four checks for each: two `ArgumentOutOfRangeException` tests
                // inside `FastLanes.Untranspose`, then the table's own bounds check, then two span
                // indexers. `p - blockStart` is in [0, 1024) by the clamps above and the table has
                // exactly 1024 entries, so the argument checks can only ever pass.
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
        // `in running[i]` is a BOUNDS CHECK PER VECTOR ITERATION, on the innermost loop of the
        // encoding. The three spans are the same length -- the caller slices all three to `lanes`
        // -- so one set of hoisted references serves all of them.
        int count = running.Length;
        ref T runningRef = ref MemoryMarshal.GetReference(running);
        ref T deltaRef = ref MemoryMarshal.GetReference(delta);
        ref T intoRef = ref MemoryMarshal.GetReference(into);

        int i = 0;
        if (Vector<T>.IsSupported && count >= Vector<T>.Count)
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
