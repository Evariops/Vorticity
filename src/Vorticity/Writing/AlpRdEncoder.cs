using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One float column encoded with ALP-RD, "real doubles": each value's bits cut in two at a width
/// chosen for the column, the high part coded against a dictionary of at most eight recurring
/// patterns and the low part kept as it is. It is for the floats ALP refuses, those with no short
/// decimal form, whose high bits still recur: the sign, the exponent and the first bits of the
/// mantissa of values of one magnitude. A high part the dictionary misses is an exception, carried
/// as a patch.
/// </summary>
/// <remarks>
/// <para>
/// The cut and the dictionary are chosen as the reference chooses them (vortex-alp over the
/// <c>alp</c> crate): the leading sixteen bits of a sample of the column are counted once, and every
/// cut from sixteen bits down to one is priced by the patterns its eight most frequent groups cover,
/// merging each pair of sibling groups on the way out; the cut with the fewest estimated bits per
/// value wins, the shallowest on a tie. The dictionary is written in that order, most frequent
/// first, ties in pattern order, so a column encodes the same way here and there.
/// </para>
/// <para>
/// A null row takes code 0 and a right part of 0, and is never an exception: its bits are not read.
/// The sample skips nulls for the same reason, where the reference counts whatever the slot holds.
/// </para>
/// <para>
/// The split is made while pricing and kept, as ALP keeps its integers: the codes, the right parts
/// and the exceptions are the pool's, and go back once the writer has them in the arena, or with
/// <see cref="Release"/> for a plan nothing writes.
/// </para>
/// </remarks>
internal sealed class AlpRdPlan
{
    /// <summary>The most bits a cut takes from the top of a value.</summary>
    internal const int CutLimit = 16;

    /// <summary>The most patterns the dictionary holds, which a code of three bits indexes.</summary>
    internal const int MaxDictionarySize = 8;

    /// <summary>Values the cut search reads, however long the column.</summary>
    private const int MaxSample = 4096;

    /// <summary>The runs the sample is taken in, so that it sees neighbours rather than a stride.</summary>
    private const int SampleBlock = 64;

    /// <summary>What the node costs beyond its buffers: four children, their specs, the metadata.</summary>
    private const int Overhead = 512;

    private DictionaryPatterns _dictionary;
    private ushort[] _codes;
    private byte[] _right;
    private int[] _exceptionRows;
    private byte[] _exceptionValues;

    private AlpRdPlan(
        in DictionaryPatterns dictionary, int dictionaryLength, int rightBitWidth, int rows, int width,
        ushort[] codes, byte[] right, int[] exceptionRows, byte[] exceptionValues, int exceptions,
        long encodedSize)
    {
        _dictionary = dictionary;
        DictionaryLength = dictionaryLength;
        RightBitWidth = rightBitWidth;
        Rows = rows;
        Width = width;
        _codes = codes;
        _right = right;
        _exceptionRows = exceptionRows;
        _exceptionValues = exceptionValues;
        ExceptionCount = exceptions;
        EncodedSize = encodedSize;
    }

    /// <summary>The high-bit patterns, indexed by code; <see cref="DictionaryLength"/> of them are live.</summary>
    [InlineArray(MaxDictionarySize)]
    internal struct DictionaryPatterns
    {
        private ushort _element;
    }

    /// <summary>The dictionary's patterns, most frequent first.</summary>
    internal ReadOnlySpan<ushort> Dictionary => ((ReadOnlySpan<ushort>)_dictionary)[..DictionaryLength];

    /// <summary>How many patterns the dictionary holds.</summary>
    internal int DictionaryLength { get; }

    /// <summary>Bits of each value below the cut: the right part.</summary>
    internal int RightBitWidth { get; }

    /// <summary>Rows of the column.</summary>
    internal int Rows { get; }

    /// <summary>Bytes of one value: 8 for f64, 4 for f32, and so of one right part.</summary>
    internal int Width { get; }

    /// <summary>The dictionary code of each row's high bits; 0 at a null and at an exception.</summary>
    internal ReadOnlySpan<ushort> Codes => _codes.Length == 0 ? [] : _codes.AsSpan(0, Rows);

    /// <summary>Each row's low bits, little-endian at <see cref="Width"/>.</summary>
    internal ReadOnlySpan<byte> Right => _right.Length == 0 ? [] : _right.AsSpan(0, Rows * Width);

    /// <summary>The rows whose high bits are not in the dictionary, ascending.</summary>
    internal ReadOnlySpan<int> ExceptionRows => _exceptionRows.AsSpan(0, ExceptionCount);

    /// <summary>How many rows are exceptions.</summary>
    internal int ExceptionCount { get; private set; }

    /// <summary>The estimated size of this encoding once its children are bit-packed.</summary>
    internal long EncodedSize { get; }

    /// <summary>The same estimate without the node's own framing: the bytes its buffers will hold.</summary>
    internal long BufferBytes => EncodedSize - Overhead;

    /// <summary>
    /// What the search chose for a column -- the dictionary and the width below the cut -- and the
    /// bits per value it estimates they cost, from the sample alone.
    /// </summary>
    /// <remarks>
    /// Separate from the split so that a candidate that beats ALP-RD, zstd on values that repeat,
    /// is priced against the estimate and the split is never paid for: the search reads a few
    /// thousand values, the split reads them all.
    /// </remarks>
    internal readonly struct Cut
    {
        internal Cut(in DictionaryPatterns dictionary, int length, int rightBitWidth, double bitsPerValue)
        {
            Dictionary = dictionary;
            Length = length;
            RightBitWidth = rightBitWidth;
            BitsPerValue = bitsPerValue;
        }

        /// <summary>The patterns, most frequent first; <see cref="Length"/> of them are live.</summary>
        internal DictionaryPatterns Dictionary { get; }

        /// <summary>How many patterns the dictionary holds.</summary>
        internal int Length { get; }

        /// <summary>Bits of each value below the cut.</summary>
        internal int RightBitWidth { get; }

        /// <summary>The reference's estimate: both widths, and 32 bits per exception spread over the sample.</summary>
        internal double BitsPerValue { get; }

        /// <summary>What the estimate puts on <paramref name="rows"/> rows, the node's framing included.</summary>
        internal long EstimatedBytes(int rows) => Overhead + (long)Math.Ceiling(rows * BitsPerValue / 8);
    }

    /// <summary>The search alone, over a sample of the column: null for a column with no valid value, or not of floats.</summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">A canonical primitive node of F32 or F64.</param>
    internal static Cut? Search(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;
        if (rows == 0)
        {
            return null;
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        Cut cut = node.PType switch
        {
            PType.F64 => Choose(MemoryMarshal.Cast<byte, ulong>(node.Values.Span)[..rows], mask, rows),
            PType.F32 => Choose(MemoryMarshal.Cast<byte, uint>(node.Values.Span)[..rows], mask, rows),
            _ => default,
        };
        return cut.Length == 0 ? null : cut;
    }

    /// <summary>
    /// Encodes a float column, or returns null when ALP-RD does not save a tenth of
    /// <paramref name="plain"/>: below that, a decode step on every read is not worth the bytes.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">A canonical primitive node of F32 or F64.</param>
    /// <param name="plain">What the column costs written as it is.</param>
    internal static AlpRdPlan? TryBuild(CanonicalArena arena, int nodeIndex, long plain) =>
        Search(arena, nodeIndex) is { } cut ? TryBuild(arena, nodeIndex, plain, in cut) : null;

    /// <summary>Splits the column at a cut the search already chose, and prices the split exactly.</summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">The node <see cref="Search"/> was given.</param>
    /// <param name="plain">What the column costs written as it is.</param>
    /// <param name="cut">The search's choice.</param>
    internal static AlpRdPlan? TryBuild(CanonicalArena arena, int nodeIndex, long plain, in Cut cut)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        return node.PType switch
        {
            PType.F64 => Build<ulong>(arena, node, plain, in cut),
            PType.F32 => Build<uint>(arena, node, plain, in cut),
            _ => null,
        };
    }

    /// <summary>
    /// The cut and the dictionary the search picks for a column, whatever the encoding would then
    /// cost: what the tests hold to the reference's own cases, which are too short to be worth
    /// encoding.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">A canonical primitive node of F64.</param>
    internal static (int RightBitWidth, ushort[] Dictionary) SearchForTests(CanonicalArena arena, int nodeIndex)
    {
        Cut cut = Search(arena, nodeIndex) ?? default;
        DictionaryPatterns dictionary = cut.Dictionary;
        return (cut.RightBitWidth, ((ReadOnlySpan<ushort>)dictionary)[..cut.Length].ToArray());
    }

    /// <summary>
    /// Hands the codes and the right parts back, once the writer has laid them in the arena and
    /// before it writes them, so that the pool serves the rentals writing makes. Safe twice.
    /// </summary>
    internal void ReleaseParts()
    {
        ushort[] codes = _codes;
        byte[] right = _right;
        _codes = [];
        _right = [];
        if (codes.Length > 0)
        {
            ArrayPool<ushort>.Shared.Return(codes);
        }

        if (right.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(right);
        }
    }

    /// <summary>
    /// The exceptions' high bits, little-endian u16, and the bytes of the rental that hold them.
    /// They pass to the caller: the plan forgets them, and <see cref="Release"/> no longer hands them back.
    /// </summary>
    internal (byte[] Values, int Length) TakeExceptionValues()
    {
        byte[] values = _exceptionValues;
        _exceptionValues = [];
        return (values, values.Length == 0 ? 0 : ExceptionCount * sizeof(ushort));
    }

    /// <summary>Hands back every rental the plan still holds. Safe twice.</summary>
    internal void Release()
    {
        ReleaseParts();
        int[] rows = _exceptionRows;
        byte[] values = _exceptionValues;
        _exceptionRows = [];
        _exceptionValues = [];
        ExceptionCount = 0;
        if (rows.Length > 0)
        {
            ArrayPool<int>.Shared.Return(rows);
        }

        if (values.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(values);
        }
    }

    private static AlpRdPlan? Build<TBits>(CanonicalArena arena, CanonicalNode node, long plain, in Cut cut)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        int rows = node.Length;
        int width = Unsafe.SizeOf<TBits>();
        ReadOnlySpan<TBits> values = MemoryMarshal.Cast<byte, TBits>(node.Values.Span)[..rows];
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        DictionaryPatterns dictionary = cut.Dictionary;
        int dictionaryLength = cut.Length;
        int rightBitWidth = cut.RightBitWidth;

        ushort[] codes = ArrayPool<ushort>.Shared.Rent(rows);
        byte[] right = ArrayPool<byte>.Shared.Rent(rows * width);
        int[] exceptionRows = ArrayPool<int>.Shared.Rent(Math.Max(rows / 64, 16));
        ushort[] exceptionPatterns = ArrayPool<ushort>.Shared.Rent(exceptionRows.Length);
        bool kept = false;
        try
        {
            int exceptions = Split(
                values, mask, rightBitWidth, dictionary, dictionaryLength, codes.AsSpan(0, rows),
                MemoryMarshal.Cast<byte, TBits>(right.AsSpan(0, rows * width)),
                ref exceptionRows, ref exceptionPatterns);

            // Both parts are packed a whole block at a time, as the writer packs them.
            int leftBitWidth = LeftBitWidth(dictionaryLength);
            int indexWidth = FsstPlan.IndexPType(rows).ByteWidth();
            long blocks = (rows + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
            long size = Overhead
                + (blocks * FastLanes.BlockByteLength(leftBitWidth))
                + (blocks * FastLanes.BlockByteLength(rightBitWidth))
                + ((long)exceptions * (indexWidth + sizeof(ushort)));
            if (size * 10 >= plain * 9)
            {
                return null;
            }

            // The exceptions are copied out of their growing rentals into rentals of their own
            // size, so that a plan holds what it has until it is written; their high bits go out
            // as the little-endian u16 the patch values are.
            int[] keptRows = exceptions == 0 ? [] : ArrayPool<int>.Shared.Rent(exceptions);
            byte[] keptValues = exceptions == 0 ? [] : ArrayPool<byte>.Shared.Rent(exceptions * sizeof(ushort));
            exceptionRows.AsSpan(0, exceptions).CopyTo(keptRows);
            MemoryMarshal.AsBytes(exceptionPatterns.AsSpan(0, exceptions)).CopyTo(keptValues);
            if (!BitConverter.IsLittleEndian)
            {
                throw new PlatformNotSupportedException("The writer lays values out little-endian.");
            }

            AlpRdPlan plan = new AlpRdPlan(
                in dictionary, dictionaryLength, rightBitWidth, rows, width, codes, right,
                keptRows, keptValues, exceptions, size);
            kept = true;
            return plan;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(exceptionRows);
            ArrayPool<ushort>.Shared.Return(exceptionPatterns);
            if (!kept)
            {
                ArrayPool<ushort>.Shared.Return(codes);
                ArrayPool<byte>.Shared.Return(right);
            }
        }
    }

    /// <summary>The bits a code needs: one at least, three for a full dictionary.</summary>
    internal static int LeftBitWidth(int dictionaryLength) =>
        dictionaryLength <= 1 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)(dictionaryLength - 1));

    /// <summary>
    /// Cuts every row in two and codes its high part, and returns the exceptions: the rows whose
    /// high part the dictionary does not hold, with that high part.
    /// </summary>
    /// <remarks>
    /// The dictionary is one vector of eight lanes, so finding a row's code is one compare against
    /// all of them and a count of trailing zeros, whatever the dictionary holds; lanes past its
    /// length are masked out rather than filled with a value that could never match, since at a
    /// sixteen-bit cut every value can.
    /// </remarks>
    private static int Split<TBits>(
        ReadOnlySpan<TBits> values, ValidityMask mask, int rightBitWidth,
        in DictionaryPatterns dictionary, int dictionaryLength, Span<ushort> codes, Span<TBits> right,
        ref int[] exceptionRows, ref ushort[] exceptionPatterns)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        TBits rightMask = (TBits.One << rightBitWidth) - TBits.One;
        Vector128<ushort> patterns = Vector128.Create((ReadOnlySpan<ushort>)dictionary);
        uint live = (1u << dictionaryLength) - 1;
        bool allValid = mask.AllValid;
        int exceptions = 0;
        int done = Compute.WordBytes.IsAccelerated && (typeof(TBits) == typeof(ulong) || typeof(TBits) == typeof(uint))
            ? SplitLanes(values, mask, rightBitWidth, in dictionary, dictionaryLength, codes, right,
                ref exceptionRows, ref exceptionPatterns, ref exceptions)
            : 0;
        for (int row = done; row < values.Length; row++)
        {
            if (!allValid && !mask.IsValid(row))
            {
                codes[row] = 0;
                right[row] = TBits.Zero;
                continue;
            }

            TBits bits = values[row];
            right[row] = bits & rightMask;
            ushort pattern = ushort.CreateTruncating(bits >> rightBitWidth);
            uint hits = Vector128.Equals(patterns, Vector128.Create(pattern)).ExtractMostSignificantBits() & live;
            if (hits != 0)
            {
                codes[row] = (ushort)BitOperations.TrailingZeroCount(hits);
                continue;
            }

            codes[row] = 0;
            if (exceptions == exceptionRows.Length || exceptions == exceptionPatterns.Length)
            {
                Grow(ref exceptionRows, exceptions);
                Grow(ref exceptionPatterns, exceptions);
            }

            exceptionRows[exceptions] = row;
            exceptionPatterns[exceptions] = pattern;
            exceptions++;
        }

        return exceptions;
    }

    /// <summary>
    /// <see cref="Split"/> of whole blocks of 64 rows where there are 512-bit vectors, a vector of
    /// rows at a time rather than a row: eight doubles' bits or sixteen singles' to a register.
    /// </summary>
    /// <returns>The rows split, a multiple of 64; the rest are the caller's.</returns>
    /// <remarks>
    /// The right parts are one and, the patterns one shift; each of the dictionary's few patterns
    /// is compared with the whole register, and a lane that matches takes that pattern's index as
    /// its code. The patterns are distinct, so at most one matches a lane, the one the scalar
    /// search's trailing-zero count finds. The codes are narrowed to sixteen bits by one move. A
    /// null row is a lane the validity word clears, its code and right part zero; a valid lane no
    /// pattern matched is an exception, taken lane by lane in row order, which is rare, since the
    /// dictionary was chosen to cover the column.
    /// </remarks>
    private static int SplitLanes<TBits>(
        ReadOnlySpan<TBits> values, ValidityMask mask, int rightBitWidth,
        in DictionaryPatterns dictionary, int dictionaryLength, Span<ushort> codes, Span<TBits> right,
        ref int[] exceptionRows, ref ushort[] exceptionPatterns, ref int exceptions)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        int lanes = Vector512<TBits>.Count;
        Vector512<TBits> rightMask = Vector512.Create((TBits.One << rightBitWidth) - TBits.One);
        Span<Vector512<TBits>> keys = stackalloc Vector512<TBits>[MaxDictionarySize];
        for (int k = 0; k < dictionaryLength; k++)
        {
            keys[k] = Vector512.Create(TBits.CreateTruncating(dictionary[k]));
        }

        Compute.WordBytes spread = Compute.WordBytes.Create();
        bool allValid = mask.AllValid;
        ref TBits from = ref MemoryMarshal.GetReference(values);
        ref TBits into = ref MemoryMarshal.GetReference(right);
        ref ushort coded = ref MemoryMarshal.GetReference(codes);
        int row = 0;
        for (; row <= values.Length - 64; row += 64)
        {
            ulong valid = allValid ? ulong.MaxValue : BitWords.Load(mask.Bits, mask.BitOffset + row);
            for (int group = 0; group < 64 / lanes; group++)
            {
                int at = row + (group * lanes);
                Vector512<TBits> value = Vector512.LoadUnsafe(ref from, (nuint)at);
                Vector512<TBits> kept = allValid ? Vector512<TBits>.AllBitsSet : spread.Lanes<TBits>(valid, group);
                Vector512<TBits> pattern = value >>> rightBitWidth;
                Vector512<TBits> code = Vector512<TBits>.Zero;
                Vector512<TBits> matched = Vector512<TBits>.Zero;
                for (int k = 0; k < dictionaryLength; k++)
                {
                    Vector512<TBits> hit = Vector512.Equals(pattern, keys[k]);
                    code |= hit & Vector512.Create(TBits.CreateTruncating(k));
                    matched |= hit;
                }

                (value & rightMask & kept).StoreUnsafe(ref into, (nuint)at);
                code &= kept;
                if (typeof(TBits) == typeof(ulong))
                {
                    Avx512F.ConvertToVector128UInt16(code.AsUInt64()).StoreUnsafe(ref coded, (nuint)at);
                }
                else
                {
                    Avx512F.ConvertToVector256UInt16(code.AsUInt32()).StoreUnsafe(ref coded, (nuint)at);
                }

                ulong missing = Vector512.AndNot(kept, matched).ExtractMostSignificantBits();
                while (missing != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(missing);
                    missing &= missing - 1;
                    if (exceptions == exceptionRows.Length || exceptions == exceptionPatterns.Length)
                    {
                        Grow(ref exceptionRows, exceptions);
                        Grow(ref exceptionPatterns, exceptions);
                    }

                    exceptionRows[exceptions] = at + lane;
                    exceptionPatterns[exceptions] = ushort.CreateTruncating(values[at + lane] >> rightBitWidth);
                    exceptions++;
                }
            }
        }

        return row;
    }

    /// <summary>Doubles a rental, keeping its first <paramref name="used"/> entries.</summary>
    private static void Grow<T>(ref T[] rental, int used)
    {
        T[] grown = ArrayPool<T>.Shared.Rent(rental.Length * 2);
        rental.AsSpan(0, used).CopyTo(grown);
        ArrayPool<T>.Shared.Return(rental);
        rental = grown;
    }

    /// <summary>
    /// The reference's search: the leading sixteen bits of a sample, counted per pattern, and every
    /// cut priced from those counts by merging sibling groups one bit at a time.
    /// </summary>
    /// <returns>The cut, whose dictionary is empty when no value was valid.</returns>
    private static Cut Choose<TBits>(ReadOnlySpan<TBits> values, ValidityMask mask, int rows)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        int bits = Unsafe.SizeOf<TBits>() * 8;
        ushort[] sample = ArrayPool<ushort>.Shared.Rent(Math.Min(rows, MaxSample));
        ushort[] groupPatterns = ArrayPool<ushort>.Shared.Rent(Math.Min(rows, MaxSample));
        uint[] groupCounts = ArrayPool<uint>.Shared.Rent(Math.Min(rows, MaxSample));
        try
        {
            int count = Sample(values, mask, rows, bits, sample);
            if (count == 0)
            {
                return default;
            }

            // The group arrays are free until the run lengths are taken, so one of them is the
            // sort's scratch.
            Span<ushort> taken = RadixSort(sample.AsSpan(0, count), groupPatterns.AsSpan(0, count));
            int groups = RunLengths(taken, groupPatterns, groupCounts);

            DictionaryPatterns best = default;
            int bestLength = 0;
            int bestRight = 0;
            double bestSize = double.MaxValue;
            for (int cut = CutLimit; cut >= 1; cut--)
            {
                int right = bits - cut;
                (DictionaryPatterns dictionary, int length, long encodable) =
                    Select(groupPatterns.AsSpan(0, groups), groupCounts.AsSpan(0, groups));
                double size = EstimatedBits(right, LeftBitWidth(length), count - encodable, count);

                // Cuts are visited deepest first, so accepting a tie leaves the shallowest of the
                // equally good cuts holding the title, as the reference's own search does.
                if (size <= bestSize)
                {
                    bestSize = size;
                    best = dictionary;
                    bestLength = length;
                    bestRight = right;
                }

                groups = MergeSiblings(groupPatterns.AsSpan(0, groups), groupCounts.AsSpan(0, groups));
            }

            return new Cut(in best, bestLength, bestRight, bestSize);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(sample);
            ArrayPool<ushort>.Shared.Return(groupPatterns);
            ArrayPool<uint>.Shared.Return(groupCounts);
        }
    }

    /// <summary>
    /// The leading sixteen bits of the valid values of at most <see cref="MaxSample"/> rows: all of
    /// them in a short column, else runs of <see cref="SampleBlock"/> evenly spread over it.
    /// </summary>
    /// <returns>The patterns taken, at the head of <paramref name="sample"/>.</returns>
    private static int Sample<TBits>(
        ReadOnlySpan<TBits> values, ValidityMask mask, int rows, int bits, Span<ushort> sample)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        int shift = bits - CutLimit;
        if (rows <= MaxSample)
        {
            return Take(values, mask, 0, rows, shift, sample, 0);
        }

        const int Blocks = MaxSample / SampleBlock;
        int spacing = (rows - SampleBlock) / (Blocks - 1);
        int taken = 0;
        for (int block = 0; block < Blocks; block++)
        {
            taken = Take(values, mask, block * spacing, SampleBlock, shift, sample, taken);
        }

        return taken;
    }

    /// <summary>The leading bits of the valid rows of one run, appended to the sample; the validity is asked only when there is a null.</summary>
    private static int Take<TBits>(
        ReadOnlySpan<TBits> values, ValidityMask mask, int start, int count, int shift, Span<ushort> sample, int taken)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        if (mask.AllValid)
        {
            for (int row = start; row < start + count; row++)
            {
                sample[taken++] = ushort.CreateTruncating(values[row] >> shift);
            }

            return taken;
        }

        for (int row = start; row < start + count; row++)
        {
            if (mask.IsValid(row))
            {
                sample[taken++] = ushort.CreateTruncating(values[row] >> shift);
            }
        }

        return taken;
    }

    /// <summary>
    /// Sorts sixteen-bit keys with two passes of an eight-bit radix, as the reference does, and
    /// returns whichever of the two spans ends up holding them.
    /// </summary>
    /// <remarks>
    /// A comparison sort of the same keys costs several times this, and more still here, where the
    /// generic comparer it goes through is not devirtualized. A digit every key shares cannot
    /// reorder anything, so its scatter is skipped, which is most of the sort for the data ALP-RD is
    /// for: values of a few magnitudes, whose leading bits agree.
    /// </remarks>
    private static Span<ushort> RadixSort(Span<ushort> keys, Span<ushort> scratch)
    {
        Span<int> offsets = stackalloc int[256];
        for (int shift = 0; shift <= 8; shift += 8)
        {
            offsets.Clear();
            for (int i = 0; i < keys.Length; i++)
            {
                offsets[(keys[i] >> shift) & 0xFF]++;
            }

            if (offsets.Contains(keys.Length))
            {
                continue;
            }

            int start = 0;
            for (int digit = 0; digit < offsets.Length; digit++)
            {
                int count = offsets[digit];
                offsets[digit] = start;
                start += count;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                int digit = (keys[i] >> shift) & 0xFF;
                scratch[offsets[digit]++] = keys[i];
            }

            Span<ushort> swap = keys;
            keys = scratch;
            scratch = swap;
        }

        return keys;
    }

    /// <summary>The distinct patterns of a sorted sample, ascending, with how many times each occurs.</summary>
    /// <remarks>
    /// <paramref name="sorted"/> may be <paramref name="patterns"/> itself, which the sort may have
    /// left its keys in: a pattern is written at or before the key it came from is read.
    /// </remarks>
    private static int RunLengths(ReadOnlySpan<ushort> sorted, Span<ushort> patterns, Span<uint> counts)
    {
        int groups = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (groups > 0 && patterns[groups - 1] == sorted[i])
            {
                counts[groups - 1]++;
                continue;
            }

            patterns[groups] = sorted[i];
            counts[groups] = 1;
            groups++;
        }

        return groups;
    }

    /// <summary>
    /// Rewrites the groups as groups of patterns one bit shorter, summing each pair of siblings;
    /// both ascend, so the siblings are neighbours and one pass does it in place.
    /// </summary>
    private static int MergeSiblings(Span<ushort> patterns, Span<uint> counts)
    {
        int merged = 0;
        int read = 0;
        while (read < patterns.Length)
        {
            ushort pattern = (ushort)(patterns[read] >> 1);
            uint count = counts[read];
            read++;
            if (read < patterns.Length && patterns[read] >> 1 == pattern)
            {
                count += counts[read];
                read++;
            }

            patterns[merged] = pattern;
            counts[merged] = count;
            merged++;
        }

        return merged;
    }

    /// <summary>
    /// The <see cref="MaxDictionarySize"/> most frequent patterns, most frequent first; a group that
    /// ties with one already held goes after it, and the groups arrive in pattern order, so the
    /// dictionary is decided by the data.
    /// </summary>
    private static (DictionaryPatterns Dictionary, int Length, long Encodable) Select(
        ReadOnlySpan<ushort> patterns, ReadOnlySpan<uint> counts)
    {
        DictionaryPatterns chosen = default;
        Span<ushort> held = chosen;
        Span<uint> heldCounts = stackalloc uint[MaxDictionarySize];
        int length = 0;
        for (int g = 0; g < patterns.Length; g++)
        {
            uint count = counts[g];
            int at = 0;
            while (at < length && count <= heldCounts[at])
            {
                at++;
            }

            if (at == length)
            {
                if (length < MaxDictionarySize)
                {
                    held[length] = patterns[g];
                    heldCounts[length] = count;
                    length++;
                }

                continue;
            }

            length = Math.Min(length + 1, MaxDictionarySize);
            for (int i = length - 1; i > at; i--)
            {
                held[i] = held[i - 1];
                heldCounts[i] = heldCounts[i - 1];
            }

            held[at] = patterns[g];
            heldCounts[at] = count;
        }

        long encodable = 0;
        for (int i = 0; i < length; i++)
        {
            encodable += heldCounts[i];
        }

        return (chosen, length, encodable);
    }

    /// <summary>The reference's estimate of bits per value: the two widths, and 32 bits per exception spread over the sample.</summary>
    private static double EstimatedBits(int rightBitWidth, int leftBitWidth, long exceptions, int sampled) =>
        rightBitWidth + leftBitWidth + ((double)(exceptions * 32) / sampled);
}
