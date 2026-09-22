using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One column encoded with ALP: the exponents, the integers, and the exceptions. A float with few
/// significant digits scales to a small integer that bit-packs like any other, and the encoder's
/// whole job is choosing the pair of exponents; a value is encoded only when decoding its integer
/// reproduces the original bit pattern exactly, and every other value is carried verbatim as a
/// patch, which is what turns a lossy transform into a lossless encoding. The comparison is bitwise
/// on purpose: <c>-0.0</c> must not round-trip to <c>+0.0</c> and a NaN must not round-trip to a
/// different NaN, or the writer would silently change values that compare equal without being the
/// same.
/// </summary>
internal sealed class AlpPlan
{
    private byte[] _encoded;
    private int[] _patchIndices;
    private byte[] _patchValues;
    private readonly int _patchWidth;

    private AlpPlan(
        byte exponentE, byte exponentF, byte[] encoded, int encodedLength, PType encodedPType,
        int[] patchIndices, byte[] patchValues, int patchCount, int patchWidth, long encodedSize)
    {
        ExponentE = exponentE;
        ExponentF = exponentF;
        _encoded = encoded;
        EncodedLength = encodedLength;
        EncodedPType = encodedPType;
        _patchIndices = patchIndices;
        _patchValues = patchValues;
        PatchCount = patchCount;
        _patchWidth = patchWidth;
        EncodedSize = encodedSize;
    }

    /// <summary>The <c>10^e</c> exponent, applied at encode and undone at decode.</summary>
    internal byte ExponentE { get; }

    /// <summary>The <c>10^-f</c> exponent.</summary>
    internal byte ExponentF { get; }

    /// <summary>The encoded integers, little-endian, one per row.</summary>
    internal ReadOnlySpan<byte> Encoded => _encoded.AsSpan(0, EncodedLength);

    /// <summary>How many bytes of the rental the integers occupy.</summary>
    internal int EncodedLength { get; private set; }

    /// <summary>Their physical type: <c>i32</c> for f32, <c>i64</c> for f64.</summary>
    internal PType EncodedPType { get; }

    /// <summary>The rows that could not be represented, ascending.</summary>
    internal ReadOnlySpan<int> PatchIndices => _patchIndices.AsSpan(0, PatchCount);

    /// <summary>
    /// Their original values, at the column's own float width; empty once
    /// <see cref="TakePatchValues"/> has handed them on.
    /// </summary>
    internal ReadOnlySpan<byte> PatchValues =>
        _patchValues.Length == 0 ? [] : _patchValues.AsSpan(0, PatchCount * _patchWidth);

    /// <summary>How many rows are patches.</summary>
    internal int PatchCount { get; private set; }

    /// <summary>The estimated size of this encoding once the integers are bit-packed.</summary>
    internal long EncodedSize { get; }

    /// <summary>Hands the integers back to the pool, once they live somewhere else.</summary>
    /// <remarks>
    /// Called by the encoder as soon as it has laid the integers in the arena, before it writes
    /// them, so that the pool can serve the rentals that writing makes. Safe twice, and a plan
    /// that has released reads as empty rather than as whatever the next renter wrote.
    /// </remarks>
    internal void ReleaseEncoded()
    {
        byte[] encoded = _encoded;
        _encoded = [];
        EncodedLength = 0;
        if (encoded.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    /// <summary>
    /// The patch values' rental and the bytes of it that are values. They pass to the caller: the
    /// plan forgets them, and <see cref="Release"/> no longer hands them back.
    /// </summary>
    internal (byte[] Values, int Length) TakePatchValues()
    {
        byte[] values = _patchValues;
        int length = values.Length == 0 ? 0 : PatchCount * _patchWidth;
        _patchValues = [];
        return (values, length);
    }

    /// <summary>Hands back every rental the plan still holds: the integers, the patches.</summary>
    /// <remarks>
    /// Called where the plan stops being needed: by the encoder once it has written the node, and
    /// by the chooser for a plan nothing will write. Safe twice.
    /// </remarks>
    internal void Release()
    {
        ReleaseEncoded();
        int[] indices = _patchIndices;
        byte[] values = _patchValues;
        _patchIndices = [];
        _patchValues = [];
        PatchCount = 0;
        if (indices.Length > 0)
        {
            ArrayPool<int>.Shared.Return(indices);
        }

        if (values.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(values);
        }
    }

    /// <summary>The largest <c>e</c> each width searches: 10^18 is the last power of ten an i64 holds.</summary>
    private const int MaxExponentDouble = 18;

    /// <summary>The same for f32, bounded by i32.</summary>
    private const int MaxExponentSingle = 10;

    /// <summary>Values the exponent search looks at, in runs, however long the column is.</summary>
    private const int SampleSize = 64;

    /// <summary>The length of each run, so the sample sees locality rather than a stride.</summary>
    private const int SampleBlock = 8;

    /// <summary>What an ALP node costs beyond its data: the node, its metadata, two patch children.</summary>
    private const int Overhead = 512;

    /// <summary>
    /// Encodes a float column, or returns null when ALP does not pay.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">A canonical Primitive node of F32 or F64.</param>
    /// <param name="plain">What the column costs written as it is.</param>
    /// <returns>The plan, or null.</returns>
    internal static AlpPlan? TryBuild(CanonicalArena arena, int nodeIndex, long plain)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        return node.PType switch
        {
            PType.F64 => BuildDouble(arena, node, plain),
            PType.F32 => BuildSingle(arena, node, plain),
            _ => null,
        };
    }

    private static AlpPlan? BuildDouble(CanonicalArena arena, CanonicalNode node, long plain)
    {
        int rows = node.Length;
        ReadOnlySpan<double> values = MemoryMarshal.Cast<byte, double>(node.Values.Span)[..rows];
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        (int e, int f) = BestExponentsDouble(values, mask, rows);
        double scale = AlpTables.F10Double[e];
        double inverse = AlpTables.If10Double[f];
        double back = AlpTables.F10Double[f];
        double backInverse = AlpTables.If10Double[e];

        // The integers go straight into the bytes the plan will carry, so a candidate never builds
        // the column twice. Leaving the rental uninitialized is safe because every slot is written
        // before it is read: the loop writes the valid unpatched rows and `FillGaps` writes the
        // invalid and the patched ones, which is what `FillGaps` is for. It is rented for the same
        // reason as the patch buffers below: every float column is priced with ALP whether or not
        // ALP wins, and these integers are the column itself, so a losing candidate would otherwise
        // hand the heap a whole copy of it. The rental goes back when the plan is refused here, or
        // when the writer has laid the integers in the arena.
        int encodedLength = rows * sizeof(long);
        byte[] encodedBytes = ArrayPool<byte>.Shared.Rent(Math.Max(encodedLength, 1));
        Span<long> encoded = MemoryMarshal.Cast<byte, long>(encodedBytes.AsSpan(0, encodedLength));

        // The patch buffers are rented and sized for the worst case, which is every row a patch. A
        // `List` that grows by doubling is cheap when ALP fits and ruinous when it does not: a
        // column built to defeat ALP turns every row into a patch, and the two lists reallocating
        // their way up dominate the write. Rented, so the worst case costs the pool and not the
        // heap.
        int[] indices = ArrayPool<int>.Shared.Rent(rows);
        double[] patches = ArrayPool<double>.Shared.Rent(rows);
        int patchCount = 0;
        bool kept = false;
        try
        {
        long? fill = null;
        int done = mask.AllValid && Vector128.IsHardwareAccelerated
            ? LanesDouble(values, encoded, e, f, indices, patches, ref patchCount, ref fill)
            : 0;

        for (int i = done; i < rows; i++)
        {
            if (!mask.IsValid(i))
            {
                // A null row's float is unspecified and its decoded value is masked out anyway, so
                // patching it would spend bytes on something no reader can observe.
                encoded[i] = 0;
                continue;
            }

            double value = values[i];
            long integer = ToInt64(FastRound(value * scale * inverse));
            if (BitConverter.DoubleToInt64Bits(integer * back * backInverse)
                == BitConverter.DoubleToInt64Bits(value))
            {
                encoded[i] = integer;
                fill ??= integer;
                continue;
            }

            indices[patchCount] = i;
            patches[patchCount] = value;
            patchCount++;
        }

        // Patched and null slots take a real encoded value rather than zero: the integers are
        // bit-packed downstream over their own range, and a stray zero among values near 10^9
        // widens that range for nothing.
        long filler = fill ?? 0;
        FillGaps(encoded, indices.AsSpan(0, patchCount), mask, rows, filler);

        long size = Estimate<long>(encoded, patchCount, sizeof(double), sizeof(long));
        if (size + Overhead >= plain)
        {
            return null;
        }

        AlpPlan plan = new AlpPlan(
            (byte)e, (byte)f, encodedBytes, encodedLength, PType.I64,
            RentedCopy(indices.AsSpan(0, patchCount)),
            RentedCopy(MemoryMarshal.AsBytes(patches.AsSpan(0, patchCount))),
            patchCount, sizeof(double), size);
        kept = true;
        return plan;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
            ArrayPool<double>.Shared.Return(patches);
            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(encodedBytes);
            }
        }
    }

    private static AlpPlan? BuildSingle(CanonicalArena arena, CanonicalNode node, long plain)
    {
        int rows = node.Length;
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(node.Values.Span)[..rows];
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        (int e, int f) = BestExponentsSingle(values, mask, rows);
        float scale = AlpTables.F10Single[e];
        float inverse = AlpTables.If10Single[f];
        float back = AlpTables.F10Single[f];
        float backInverse = AlpTables.If10Single[e];

        int encodedLength = rows * sizeof(int);
        byte[] encodedBytes = ArrayPool<byte>.Shared.Rent(Math.Max(encodedLength, 1));
        Span<int> encoded = MemoryMarshal.Cast<byte, int>(encodedBytes.AsSpan(0, encodedLength));
        int[] indices = ArrayPool<int>.Shared.Rent(rows);
        float[] patches = ArrayPool<float>.Shared.Rent(rows);
        int patchCount = 0;
        bool kept = false;
        try
        {
        int? fill = null;
        int done = mask.AllValid && Vector128.IsHardwareAccelerated
            ? LanesSingle(values, encoded, e, f, indices, patches, ref patchCount, ref fill)
            : 0;

        for (int i = done; i < rows; i++)
        {
            if (!mask.IsValid(i))
            {
                encoded[i] = 0;
                continue;
            }

            float value = values[i];
            int integer = ToInt32(FastRound(value * scale * inverse));
            if (BitConverter.SingleToInt32Bits(integer * back * backInverse)
                == BitConverter.SingleToInt32Bits(value))
            {
                encoded[i] = integer;
                fill ??= integer;
                continue;
            }

            indices[patchCount] = i;
            patches[patchCount] = value;
            patchCount++;
        }

        int filler = fill ?? 0;
        FillGaps(encoded, indices.AsSpan(0, patchCount), mask, rows, filler);

        long size = Estimate<int>(encoded, patchCount, sizeof(float), sizeof(int));
        if (size + Overhead >= plain)
        {
            return null;
        }

        AlpPlan plan = new AlpPlan(
            (byte)e, (byte)f, encodedBytes, encodedLength, PType.I32,
            RentedCopy(indices.AsSpan(0, patchCount)),
            RentedCopy(MemoryMarshal.AsBytes(patches.AsSpan(0, patchCount))),
            patchCount, sizeof(float), size);
        kept = true;
        return plan;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
            ArrayPool<float>.Shared.Return(patches);
            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(encodedBytes);
            }
        }
    }

    /// <summary>
    /// The patches a plan keeps, copied out of the worst-case rentals into rentals of their own
    /// size, so that a plan holds what it has rather than a row's worth per row until it is
    /// written; the empty array when there are none.
    /// </summary>
    private static T[] RentedCopy<T>(ReadOnlySpan<T> patches)
    {
        if (patches.IsEmpty)
        {
            return [];
        }

        T[] copy = ArrayPool<T>.Shared.Rent(patches.Length);
        patches.CopyTo(copy);
        return copy;
    }

    /// <summary>
    /// The encode loop of an all-valid column, two doubles per step: scale, round, convert,
    /// convert back, and compare the bit patterns, as the scalar loop does per row.
    /// </summary>
    /// <remarks>
    /// The same arithmetic in the same order, element-wise, so every lane rounds as the scalar row
    /// would: the products are left to right, and <see cref="Vector128.ConvertToInt64(Vector128{double})"/>
    /// saturates and sends a NaN to zero, which is <see cref="ToInt64(double)"/>. A lane that does
    /// not come back is a patch; its integer is stored anyway and <see cref="FillGaps"/> overwrites it.
    /// </remarks>
    /// <returns>The rows handled: the largest multiple of two.</returns>
    private static int LanesDouble(
        ReadOnlySpan<double> values, Span<long> encoded, int e, int f,
        int[] indices, double[] patches, ref int patchCount, ref long? fill)
    {
        int length = values.Length & ~1;
        Vector128<double> scale = Vector128.Create(AlpTables.F10Double[e]);
        Vector128<double> inverse = Vector128.Create(AlpTables.If10Double[f]);
        Vector128<double> back = Vector128.Create(AlpTables.F10Double[f]);
        Vector128<double> backInverse = Vector128.Create(AlpTables.If10Double[e]);
        Vector128<double> sweet = Vector128.Create((double)((1UL << 52) + (1UL << 51)));
        ref double source = ref MemoryMarshal.GetReference(values);
        ref long destination = ref MemoryMarshal.GetReference(encoded);
        int patched = patchCount;
        for (int i = 0; i < length; i += 2)
        {
            Vector128<double> value = Vector128.LoadUnsafe(ref source, (nuint)i);
            Vector128<long> integer = Vector128.ConvertToInt64((((value * scale) * inverse) + sweet) - sweet);
            Vector128<double> decoded = (Vector128.ConvertToDouble(integer) * back) * backInverse;
            integer.StoreUnsafe(ref destination, (nuint)i);
            uint same = Vector128.Equals(decoded.AsInt64(), value.AsInt64()).ExtractMostSignificantBits();
            if (same == 0b11 && fill.HasValue)
            {
                continue;
            }

            for (int lane = 0; lane < 2; lane++)
            {
                if ((same & (1u << lane)) != 0)
                {
                    fill ??= integer.GetElement(lane);
                    continue;
                }

                indices[patched] = i + lane;
                patches[patched] = value.GetElement(lane);
                patched++;
            }
        }

        patchCount = patched;
        return length;
    }

    /// <summary>The single-precision form of <see cref="LanesDouble"/>, four floats per step.</summary>
    /// <returns>The rows handled: the largest multiple of four.</returns>
    private static int LanesSingle(
        ReadOnlySpan<float> values, Span<int> encoded, int e, int f,
        int[] indices, float[] patches, ref int patchCount, ref int? fill)
    {
        int length = values.Length & ~3;
        Vector128<float> scale = Vector128.Create(AlpTables.F10Single[e]);
        Vector128<float> inverse = Vector128.Create(AlpTables.If10Single[f]);
        Vector128<float> back = Vector128.Create(AlpTables.F10Single[f]);
        Vector128<float> backInverse = Vector128.Create(AlpTables.If10Single[e]);
        Vector128<float> sweet = Vector128.Create((float)((1 << 23) + (1 << 22)));
        ref float source = ref MemoryMarshal.GetReference(values);
        ref int destination = ref MemoryMarshal.GetReference(encoded);
        int patched = patchCount;
        for (int i = 0; i < length; i += 4)
        {
            Vector128<float> value = Vector128.LoadUnsafe(ref source, (nuint)i);
            Vector128<int> integer = Vector128.ConvertToInt32((((value * scale) * inverse) + sweet) - sweet);
            Vector128<float> decoded = (Vector128.ConvertToSingle(integer) * back) * backInverse;
            integer.StoreUnsafe(ref destination, (nuint)i);
            uint same = Vector128.Equals(decoded.AsInt32(), value.AsInt32()).ExtractMostSignificantBits();
            if (same == 0b1111 && fill.HasValue)
            {
                continue;
            }

            for (int lane = 0; lane < 4; lane++)
            {
                if ((same & (1u << lane)) != 0)
                {
                    fill ??= integer.GetElement(lane);
                    continue;
                }

                indices[patched] = i + lane;
                patches[patched] = value.GetElement(lane);
                patched++;
            }
        }

        patchCount = patched;
        return length;
    }

    /// <summary>Branchless round-to-nearest: add the sweet spot and take it away.</summary>
    /// <remarks>
    /// Adding 1.5 * 2^52 forces every fractional bit out of the mantissa under the hardware's own
    /// rounding mode, and subtracting it leaves the rounded value. It is deliberately not
    /// <c>Math.Round</c>, which disagrees on halfway cases: the integers have to be the ones every
    /// other writer of this encoding produces, or a file would not decode to the same floats.
    /// </remarks>
    private static double FastRound(double value)
    {
        const double Sweet = (1UL << 52) + (1UL << 51);
        return (value + Sweet) - Sweet;
    }

    private static float FastRound(float value)
    {
        const float Sweet = (1 << 23) + (1 << 22);
        return (value + Sweet) - Sweet;
    }

    /// <summary>Saturating cast, so an out-of-range value is deterministic rather than undefined.</summary>
    private static long ToInt64(double value) => value switch
    {
        >= 9.2233720368547758E18 => long.MaxValue,
        <= -9.2233720368547758E18 => long.MinValue,
        _ => double.IsNaN(value) ? 0 : (long)value,
    };

    private static int ToInt32(float value) => value switch
    {
        >= 2147483648f => int.MaxValue,
        <= -2147483648f => int.MinValue,
        _ => float.IsNaN(value) ? 0 : (int)value,
    };

    private static void FillGaps<T>(Span<T> encoded, ReadOnlySpan<int> indices, ValidityMask mask, int rows, T filler)
    {
        foreach (int index in indices)
        {
            encoded[index] = filler;
        }

        for (int i = 0; i < rows; i++)
        {
            if (!mask.IsValid(i))
            {
                encoded[i] = filler;
            }
        }
    }

    /// <summary>
    /// The bytes this encoding will occupy, assuming the integers get a frame of reference and are
    /// bit-packed - which they will, because the writer compresses the encoded child like any other
    /// column.
    /// </summary>
    private static long Estimate<T>(ReadOnlySpan<T> encoded, int patchCount, int floatWidth, int intWidth)
        where T : unmanaged, IComparable<T>
    {
        ulong span = Span(encoded, intWidth);
        int bits = span == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(span);
        long encodedBytes = (((long)encoded.Length * bits) + 7) / 8;

        // A patch costs its value and its index; the index is a u32 in the shape we write.
        return encodedBytes + ((long)patchCount * (floatWidth + sizeof(uint)));
    }

    private static ulong Span<T>(ReadOnlySpan<T> encoded, int intWidth)
        where T : unmanaged, IComparable<T>
    {
        if (encoded.Length == 0)
        {
            return 0;
        }

        if (intWidth == sizeof(long))
        {
            ReadOnlySpan<long> values = MemoryMarshal.Cast<T, long>(encoded);
            long min = long.MaxValue;
            long max = long.MinValue;
            foreach (long value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            return unchecked((ulong)max) - unchecked((ulong)min);
        }

        ReadOnlySpan<int> ints = MemoryMarshal.Cast<T, int>(encoded);
        int lo = int.MaxValue;
        int hi = int.MinValue;
        foreach (int value in ints)
        {
            lo = Math.Min(lo, value);
            hi = Math.Max(hi, value);
        }

        return unchecked((ulong)(uint)hi) - unchecked((ulong)(uint)lo);
    }

    // ------------------------------------------------------------------------- exponent search

    /// <summary>
    /// Picks (e, f) by estimating the encoded size of a sample under every candidate pair.
    /// </summary>
    /// <remarks>
    /// The search runs e downward and f up to e, and that order decides the tie-break: among pairs
    /// of equal estimated size the smaller `e - f` wins, because that is the one that scales least
    /// and therefore rounds least.
    /// </remarks>
    private static (int E, int F) BestExponentsDouble(
        ReadOnlySpan<double> values, ValidityMask mask, int rows)
    {
        Span<double> taken = stackalloc double[SampleSize];
        ReadOnlySpan<double> sample = taken[..Sample(values, mask, rows, taken)];
        int bestE = 0;
        int bestF = 0;
        long best = EstimateSampleDouble(sample, 0, 0);

        for (int e = MaxExponentDouble - 1; e >= 0; e--)
        {
            for (int f = 0; f <= e; f++)
            {
                long size = EstimateSampleDouble(sample, e, f);
                if (size < best || (size == best && e - f < bestE - bestF))
                {
                    best = size;
                    bestE = e;
                    bestF = f;
                }
            }
        }

        return (bestE, bestF);
    }

    private static (int E, int F) BestExponentsSingle(
        ReadOnlySpan<float> values, ValidityMask mask, int rows)
    {
        Span<float> taken = stackalloc float[SampleSize];
        ReadOnlySpan<float> sample = taken[..Sample(values, mask, rows, taken)];
        int bestE = 0;
        int bestF = 0;
        long best = EstimateSampleSingle(sample, 0, 0);

        for (int e = MaxExponentSingle - 1; e >= 0; e--)
        {
            for (int f = 0; f <= e; f++)
            {
                long size = EstimateSampleSingle(sample, e, f);
                if (size < best || (size == best && e - f < bestE - bestF))
                {
                    best = size;
                    bestE = e;
                    bestF = f;
                }
            }
        }

        return (bestE, bestF);
    }

    private static long EstimateSampleDouble(ReadOnlySpan<double> sample, int e, int f)
    {
        if (sample.Length == 0)
        {
            return 0;
        }

        double scale = AlpTables.F10Double[e];
        double inverse = AlpTables.If10Double[f];
        double back = AlpTables.F10Double[f];
        double backInverse = AlpTables.If10Double[e];

        long min = long.MaxValue;
        long max = long.MinValue;
        int patches = 0;
        foreach (double value in sample)
        {
            long integer = ToInt64(FastRound(value * scale * inverse));
            if (BitConverter.DoubleToInt64Bits(integer * back * backInverse)
                != BitConverter.DoubleToInt64Bits(value))
            {
                patches++;
                continue;
            }

            min = Math.Min(min, integer);
            max = Math.Max(max, integer);
        }

        return SampleCost(sample.Length, patches, min, max, sizeof(double));
    }

    private static long EstimateSampleSingle(ReadOnlySpan<float> sample, int e, int f)
    {
        if (sample.Length == 0)
        {
            return 0;
        }

        float scale = AlpTables.F10Single[e];
        float inverse = AlpTables.If10Single[f];
        float back = AlpTables.F10Single[f];
        float backInverse = AlpTables.If10Single[e];

        long min = long.MaxValue;
        long max = long.MinValue;
        int patches = 0;
        foreach (float value in sample)
        {
            int integer = ToInt32(FastRound(value * scale * inverse));
            if (BitConverter.SingleToInt32Bits(integer * back * backInverse)
                != BitConverter.SingleToInt32Bits(value))
            {
                patches++;
                continue;
            }

            min = Math.Min(min, integer);
            max = Math.Max(max, integer);
        }

        return SampleCost(sample.Length, patches, min, max, sizeof(float));
    }

    private static long SampleCost(int count, int patches, long min, long max, int floatWidth)
    {
        // Every value patched: the estimate is the patches alone, and the bit width is irrelevant.
        int bits = min > max
            ? 0
            : Bits(unchecked((ulong)max) - unchecked((ulong)min));
        return ((((long)count * bits) + 7) / 8) + ((long)patches * (floatWidth + sizeof(ushort)));
    }

    private static int Bits(ulong span) =>
        span == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(span);

    /// <summary>
    /// Samples in runs rather than at a stride: adjacent values share a magnitude and a number of
    /// significant digits, and a stride would see one value from each run and miss that entirely.
    /// </summary>
    /// <returns>The valid values taken, at the head of <paramref name="sample"/>.</returns>
    private static int Sample<T>(ReadOnlySpan<T> values, ValidityMask mask, int rows, Span<T> sample)
    {
        int taken = 0;
        if (rows <= SampleSize)
        {
            for (int i = 0; i < rows; i++)
            {
                if (mask.IsValid(i))
                {
                    sample[taken++] = values[i];
                }
            }

            return taken;
        }

        int blocks = SampleSize / SampleBlock;
        for (int b = 0; b < blocks; b++)
        {
            int start = (int)((long)b * (rows - SampleBlock) / Math.Max(blocks - 1, 1));
            for (int i = start; i < start + SampleBlock; i++)
            {
                if (mask.IsValid(i))
                {
                    sample[taken++] = values[i];
                }
            }
        }

        return taken;
    }
}
