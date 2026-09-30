using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

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
internal sealed class DeltaDecoder : ArrayDecoder
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
        return Core(context, in node, dtype, length, 0, length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount == 2 && context.ChildDecodesRange(in node, 0) && context.ChildDecodesRange(in node, 1);
    }

    /// <summary>
    /// Sums the blocks the range touches and no others: a block is seeded from its own bases, so
    /// the bases and deltas of every other block are left unread.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count);
    }

    private static int Core(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
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

        // The rows wanted, in the decoded space the offset is measured in, and the blocks they fall
        // in; the whole node reads every block.
        int first = offset + start;
        bool whole = start == 0 && count == length;
        int firstBlock = whole ? 0 : first / BlockSize;
        int spanned = whole ? blocks : ((first + count - 1) / BlockSize) - firstBlock + 1;
        int basesIndex = whole
            ? context.DecodeChild(in node, 0, dtype, basesLength)
            : context.DecodeChildRange(in node, 0, dtype, basesLength, firstBlock * lanes, spanned * lanes);
        int deltasIndex = whole
            ? context.DecodeChild(in node, 1, dtype, deltasLength)
            : context.DecodeChildRange(in node, 1, dtype, deltasLength, firstBlock * BlockSize, spanned * BlockSize);

        CanonicalNode bases = RequirePrimitive(context, basesIndex, ptype, "bases", spanned * lanes);
        CanonicalNode deltas = RequirePrimitive(context, deltasIndex, ptype, "deltas", spanned * BlockSize);

        // Only the rows asked for are materialized, not the whole decoded run: a delta node of a
        // million rows asked for one batch has no reason to produce a million values.
        //
        // The zero fill stays even though the loop below writes every element of the window. A
        // buffer of this size comes back from the pool with its pages already faulted, so the fill
        // is not a page-fault pass but a sequential prefetch of memory the untranspose loop is about
        // to rewrite, and skipping it trades that for a cold miss per row. Only allocations too
        // large for the pool, which get fresh pages on every scan, are worth leaving uninitialized:
        // the rule is the size, not the encoding.
        VortexBuffer output = CompressedValues.Allocate(
            context, ArrayDecodeContext.CheckedMultiply(count, width, Id), width, Id, out Span<byte> destination);

        int within = first - (firstBlock * BlockSize);
        Undelta(bases.Values.Span, deltas.Values.Span, destination, width, lanes, within, count);

        // The deltas carry the array's validity, one bit per decoded value: the rows asked for are a
        // range of it, which a bitmap is sliced to.
        Validity validity = Layouts.CanonicalSlice.ValidityRange(context.Canonical, deltas.Validity, within, count);
        return context.Canonical.AddPrimitive(dtype, count, validity, ptype, output);
    }

    private static CanonicalNode RequirePrimitive(
        ArrayDecodeContext context, int index, PType ptype, string what, int expected)
    {
        CanonicalNode child = context.Canonical.GetNode(CanonicalSupport.ExpandIfConstant(context, index));
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
                int wholeStart = b * BlockSize;
                if (typeof(T) == typeof(ulong) && Vector128.IsHardwareAccelerated && lanes == 16
                    && wholeStart >= offset && wholeStart + BlockSize <= offset + length)
                {
                    // A whole block of 64-bit values: each lane's sums land in their place in the
                    // output, with no block in between and no pass through the permutation.
                    AccumulateUntransposed(
                        MemoryMarshal.Cast<T, ulong>(baseValues.Slice(b * lanes, lanes)),
                        MemoryMarshal.Cast<T, ulong>(deltaBlock),
                        MemoryMarshal.Cast<T, ulong>(output.Slice(wholeStart - offset, BlockSize)));
                    continue;
                }

                if (typeof(T) == typeof(uint) && Vector128.IsHardwareAccelerated && lanes == 32
                    && wholeStart >= offset && wholeStart + BlockSize <= offset + length)
                {
                    AccumulateUntransposed(
                        MemoryMarshal.Cast<T, uint>(baseValues.Slice(b * lanes, lanes)),
                        MemoryMarshal.Cast<T, uint>(deltaBlock),
                        MemoryMarshal.Cast<T, uint>(output.Slice(wholeStart - offset, BlockSize)));
                    continue;
                }

                if (Vector128.IsHardwareAccelerated && lanes == 8 * Vector128<T>.Count)
                {
                    AccumulateInRegisters(baseValues.Slice(b * lanes, lanes), deltaBlock, block, rowsPerLane);
                }
                else
                {
                    baseValues.Slice(b * lanes, lanes).CopyTo(running);
                    for (int row = 0; row < rowsPerLane; row++)
                    {
                        int at = (order[row >> 3] * 16) + ((row & 7) * 128);
                        Accumulate(running, deltaBlock.Slice(at, lanes), block.Slice(at, lanes));
                    }
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
    /// The block's prefix sums with every lane's running sum held in a register: a block's lanes
    /// are 1 024 bits whatever the width, eight 128-bit vectors, so each row is eight loads, eight
    /// adds and eight stores, where the running sums would otherwise be loaded and stored back
    /// every row.
    /// </summary>
    private static void AccumulateInRegisters<T>(ReadOnlySpan<T> bases, ReadOnlySpan<T> deltaBlock, Span<T> block, int rowsPerLane)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        nuint step = (nuint)Vector128<T>.Count;
        ref T start = ref MemoryMarshal.GetReference(bases);
        Vector128<T> r0 = Vector128.LoadUnsafe(ref start);
        Vector128<T> r1 = Vector128.LoadUnsafe(ref start, step);
        Vector128<T> r2 = Vector128.LoadUnsafe(ref start, 2 * step);
        Vector128<T> r3 = Vector128.LoadUnsafe(ref start, 3 * step);
        Vector128<T> r4 = Vector128.LoadUnsafe(ref start, 4 * step);
        Vector128<T> r5 = Vector128.LoadUnsafe(ref start, 5 * step);
        Vector128<T> r6 = Vector128.LoadUnsafe(ref start, 6 * step);
        Vector128<T> r7 = Vector128.LoadUnsafe(ref start, 7 * step);

        ref T delta = ref MemoryMarshal.GetReference(deltaBlock);
        ref T into = ref MemoryMarshal.GetReference(block);
        ref byte order = ref MemoryMarshal.GetReference(FastLanes.Order);
        for (int row = 0; row < rowsPerLane; row++)
        {
            nuint at = (nuint)((Unsafe.Add(ref order, row >> 3) * 16) + ((row & 7) * 128));
            r0 += Vector128.LoadUnsafe(ref delta, at);
            r0.StoreUnsafe(ref into, at);
            r1 += Vector128.LoadUnsafe(ref delta, at + step);
            r1.StoreUnsafe(ref into, at + step);
            r2 += Vector128.LoadUnsafe(ref delta, at + (2 * step));
            r2.StoreUnsafe(ref into, at + (2 * step));
            r3 += Vector128.LoadUnsafe(ref delta, at + (3 * step));
            r3.StoreUnsafe(ref into, at + (3 * step));
            r4 += Vector128.LoadUnsafe(ref delta, at + (4 * step));
            r4.StoreUnsafe(ref into, at + (4 * step));
            r5 += Vector128.LoadUnsafe(ref delta, at + (5 * step));
            r5.StoreUnsafe(ref into, at + (5 * step));
            r6 += Vector128.LoadUnsafe(ref delta, at + (6 * step));
            r6.StoreUnsafe(ref into, at + (6 * step));
            r7 += Vector128.LoadUnsafe(ref delta, at + (7 * step));
            r7.StoreUnsafe(ref into, at + (7 * step));
        }
    }

    /// <summary>
    /// A whole block of 64-bit values, its prefix sums written where they belong in the output:
    /// lane <c>l</c>'s sum at row <c>r</c> is output value <c>64 * l + r</c>, so each lane's sixty-four
    /// sums are contiguous.
    /// </summary>
    /// <remarks>
    /// Rows go two at a time. A register holds two lanes' sums, and interleaving the register of
    /// row <c>r</c> with that of row <c>r + 1</c> gives each of the two lanes its two consecutive
    /// sums, one store each: the transposition costs two interleaves a register, where the general
    /// path stores the block and then walks the permutation, a table load, a load and a store a value.
    /// </remarks>
    private static void AccumulateUntransposed(ReadOnlySpan<ulong> bases, ReadOnlySpan<ulong> deltaBlock, Span<ulong> output)
    {
        ref ulong start = ref MemoryMarshal.GetReference(bases);
        Vector128<ulong> r0 = Vector128.LoadUnsafe(ref start);
        Vector128<ulong> r1 = Vector128.LoadUnsafe(ref start, 2);
        Vector128<ulong> r2 = Vector128.LoadUnsafe(ref start, 4);
        Vector128<ulong> r3 = Vector128.LoadUnsafe(ref start, 6);
        Vector128<ulong> r4 = Vector128.LoadUnsafe(ref start, 8);
        Vector128<ulong> r5 = Vector128.LoadUnsafe(ref start, 10);
        Vector128<ulong> r6 = Vector128.LoadUnsafe(ref start, 12);
        Vector128<ulong> r7 = Vector128.LoadUnsafe(ref start, 14);

        ref ulong delta = ref MemoryMarshal.GetReference(deltaBlock);
        ref ulong into = ref MemoryMarshal.GetReference(output);
        ref byte order = ref MemoryMarshal.GetReference(FastLanes.Order);
        for (int row = 0; row < 64; row += 2)
        {
            nuint first = (nuint)((Unsafe.Add(ref order, row >> 3) * 16) + ((row & 7) * 128));
            nuint second = (nuint)((Unsafe.Add(ref order, (row + 1) >> 3) * 16) + (((row + 1) & 7) * 128));
            nuint at = (nuint)row;

            Vector128<ulong> a = r0 + Vector128.LoadUnsafe(ref delta, first);
            r0 = a + Vector128.LoadUnsafe(ref delta, second);
            Low(a, r0).StoreUnsafe(ref into, at);
            High(a, r0).StoreUnsafe(ref into, at + 64);

            a = r1 + Vector128.LoadUnsafe(ref delta, first + 2);
            r1 = a + Vector128.LoadUnsafe(ref delta, second + 2);
            Low(a, r1).StoreUnsafe(ref into, at + 128);
            High(a, r1).StoreUnsafe(ref into, at + 192);

            a = r2 + Vector128.LoadUnsafe(ref delta, first + 4);
            r2 = a + Vector128.LoadUnsafe(ref delta, second + 4);
            Low(a, r2).StoreUnsafe(ref into, at + 256);
            High(a, r2).StoreUnsafe(ref into, at + 320);

            a = r3 + Vector128.LoadUnsafe(ref delta, first + 6);
            r3 = a + Vector128.LoadUnsafe(ref delta, second + 6);
            Low(a, r3).StoreUnsafe(ref into, at + 384);
            High(a, r3).StoreUnsafe(ref into, at + 448);

            a = r4 + Vector128.LoadUnsafe(ref delta, first + 8);
            r4 = a + Vector128.LoadUnsafe(ref delta, second + 8);
            Low(a, r4).StoreUnsafe(ref into, at + 512);
            High(a, r4).StoreUnsafe(ref into, at + 576);

            a = r5 + Vector128.LoadUnsafe(ref delta, first + 10);
            r5 = a + Vector128.LoadUnsafe(ref delta, second + 10);
            Low(a, r5).StoreUnsafe(ref into, at + 640);
            High(a, r5).StoreUnsafe(ref into, at + 704);

            a = r6 + Vector128.LoadUnsafe(ref delta, first + 12);
            r6 = a + Vector128.LoadUnsafe(ref delta, second + 12);
            Low(a, r6).StoreUnsafe(ref into, at + 768);
            High(a, r6).StoreUnsafe(ref into, at + 832);

            a = r7 + Vector128.LoadUnsafe(ref delta, first + 14);
            r7 = a + Vector128.LoadUnsafe(ref delta, second + 14);
            Low(a, r7).StoreUnsafe(ref into, at + 896);
            High(a, r7).StoreUnsafe(ref into, at + 960);
        }
    }

    /// <summary>
    /// A whole block of 32-bit values, its prefix sums written where they belong in the output:
    /// lane <c>l</c>'s sum at row <c>r</c> is output value <c>64 * (l % 16) + 32 * (l / 16) + r</c>,
    /// so each lane's thirty-two sums are contiguous, as a 64-bit lane's sixty-four are.
    /// </summary>
    /// <remarks>
    /// Rows go four at a time. A register holds four lanes' sums; the four registers of four
    /// consecutive rows, transposed as a four-by-four tile, give each of the four lanes its four
    /// consecutive sums, one store each. The transposition is eight interleaves for sixteen values,
    /// where the general path stores the block and then walks the permutation, a table load, a
    /// load and a store a value.
    /// </remarks>
    private static void AccumulateUntransposed(ReadOnlySpan<uint> bases, ReadOnlySpan<uint> deltaBlock, Span<uint> output)
    {
        ref uint start = ref MemoryMarshal.GetReference(bases);
        ref uint delta = ref MemoryMarshal.GetReference(deltaBlock);
        ref uint into = ref MemoryMarshal.GetReference(output);
        ref byte order = ref MemoryMarshal.GetReference(FastLanes.Order);

        // One register of four lanes at a time, all 32 rows: its running sums stay in a register,
        // and the eight registers' work is independent.
        for (int k = 0; k < 8; k++)
        {
            Vector128<uint> running = Vector128.LoadUnsafe(ref start, (nuint)(4 * k));
            int lane = 4 * k;
            nuint home = (nuint)((64 * (lane & 15)) + (32 * (lane >> 4)));
            for (int row = 0; row < 32; row += 4)
            {
                // The four rows share a group of eight, so their order entry, and differ in the
                // row within it.
                nuint at = (nuint)((Unsafe.Add(ref order, row >> 3) * 16) + ((row & 7) * 128) + lane);
                Vector128<uint> a = running + Vector128.LoadUnsafe(ref delta, at);
                Vector128<uint> b = a + Vector128.LoadUnsafe(ref delta, at + 128);
                Vector128<uint> c = b + Vector128.LoadUnsafe(ref delta, at + 256);
                running = c + Vector128.LoadUnsafe(ref delta, at + 384);

                Vector128<uint> ab0 = Low(a, b);
                Vector128<uint> ab1 = High(a, b);
                Vector128<uint> cd0 = Low(c, running);
                Vector128<uint> cd1 = High(c, running);
                nuint to = home + (nuint)row;
                Low(ab0.AsUInt64(), cd0.AsUInt64()).AsUInt32().StoreUnsafe(ref into, to);
                High(ab0.AsUInt64(), cd0.AsUInt64()).AsUInt32().StoreUnsafe(ref into, to + 64);
                Low(ab1.AsUInt64(), cd1.AsUInt64()).AsUInt32().StoreUnsafe(ref into, to + 128);
                High(ab1.AsUInt64(), cd1.AsUInt64()).AsUInt32().StoreUnsafe(ref into, to + 192);
            }
        }
    }

    /// <summary>The first two lanes of two registers interleaved.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Low(Vector128<uint> a, Vector128<uint> b) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b)
        : Sse2.IsSupported ? Sse2.UnpackLow(a, b)
        : Vector128.Create(a.GetElement(0), b.GetElement(0), a.GetElement(1), b.GetElement(1));

    /// <summary>The last two lanes of two registers interleaved.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> High(Vector128<uint> a, Vector128<uint> b) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b)
        : Sse2.IsSupported ? Sse2.UnpackHigh(a, b)
        : Vector128.Create(a.GetElement(2), b.GetElement(2), a.GetElement(3), b.GetElement(3));

    /// <summary>The first lanes of two registers side by side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Low(Vector128<ulong> a, Vector128<ulong> b) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b)
        : Sse2.IsSupported ? Sse2.UnpackLow(a, b)
        : Vector128.Create(a.GetElement(0), b.GetElement(0));

    /// <summary>The second lanes of two registers side by side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> High(Vector128<ulong> a, Vector128<ulong> b) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b)
        : Sse2.IsSupported ? Sse2.UnpackHigh(a, b)
        : Vector128.Create(a.GetElement(1), b.GetElement(1));

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
