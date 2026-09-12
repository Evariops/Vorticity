// ALP encoding: a float stored as the integer it scales to, plus the values that would not survive
// the trip.
//
// "Adaptive Lossless floating-Point". Most real-world doubles are decimals with few significant
// digits - prices, measurements, coordinates - and `round(v * 10^e / 10^f)` turns those into small
// integers that bit-pack like any other. The encoder's whole job is choosing (e, f), and its
// correctness rests on one check: a value is only encoded if decoding the integer reproduces its
// EXACT BIT PATTERN. Everything else is carried verbatim as a patch, which is what makes a lossy
// transform into a lossless encoding.
//
// Transcribed from alp-0.0.4/src/alp/mod.rs (`find_best_exponents`, `encode_chunk_unchecked`), the
// crate vortex-alp uses.
//
// THE BITWISE COMPARISON IS LOAD-BEARING. `-0.0` must not round-trip to `+0.0` and a NaN must not
// round-trip to a different NaN, because a writer that let either through would silently change
// values that compare equal but are not the same - the same rule the row encoder follows for the
// same reason (docs/08-semantics.md §5).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>One column encoded with ALP: the exponents, the integers, and the exceptions.</summary>
internal sealed class AlpPlan
{
    private AlpPlan(
        byte exponentE, byte exponentF, byte[] encoded, PType encodedPType,
        int[] patchIndices, byte[] patchValues, long encodedSize)
    {
        ExponentE = exponentE;
        ExponentF = exponentF;
        Encoded = encoded;
        EncodedPType = encodedPType;
        PatchIndices = patchIndices;
        PatchValues = patchValues;
        EncodedSize = encodedSize;
    }

    /// <summary>The <c>10^e</c> exponent, applied at encode and undone at decode.</summary>
    internal byte ExponentE { get; }

    /// <summary>The <c>10^-f</c> exponent.</summary>
    internal byte ExponentF { get; }

    /// <summary>The encoded integers, little-endian, one per row.</summary>
    internal byte[] Encoded { get; }

    /// <summary>Their physical type: <c>i32</c> for f32, <c>i64</c> for f64.</summary>
    internal PType EncodedPType { get; }

    /// <summary>The rows that could not be represented, ascending.</summary>
    internal int[] PatchIndices { get; }

    /// <summary>Their original values, at the column's own float width.</summary>
    internal byte[] PatchValues { get; }

    /// <summary>The estimated size of this encoding once the integers are bit-packed.</summary>
    internal long EncodedSize { get; }

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

        long[] encoded = new long[rows];
        List<int> indices = [];
        List<double> patches = [];
        long? fill = null;

        for (int i = 0; i < rows; i++)
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

            indices.Add(i);
            patches.Add(value);
        }

        // Patched and null slots take a real encoded value rather than zero: the integers are
        // bit-packed downstream over their own range, and a stray zero among values near 10^9
        // widens that range for nothing.
        long filler = fill ?? 0;
        FillGaps(encoded, indices, mask, rows, filler);

        long size = Estimate(encoded, indices.Count, sizeof(double), sizeof(long));
        if (size + Overhead >= plain)
        {
            return null;
        }

        byte[] encodedBytes = new byte[rows * sizeof(long)];
        MemoryMarshal.Cast<long, byte>(encoded).CopyTo(encodedBytes);
        byte[] patchBytes = new byte[patches.Count * sizeof(double)];
        MemoryMarshal.Cast<double, byte>(CollectionsMarshal.AsSpan(patches)).CopyTo(patchBytes);

        return new AlpPlan(
            (byte)e, (byte)f, encodedBytes, PType.I64, [.. indices], patchBytes, size);
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

        int[] encoded = new int[rows];
        List<int> indices = [];
        List<float> patches = [];
        int? fill = null;

        for (int i = 0; i < rows; i++)
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

            indices.Add(i);
            patches.Add(value);
        }

        int filler = fill ?? 0;
        FillGaps(encoded, indices, mask, rows, filler);

        long size = Estimate(encoded, indices.Count, sizeof(float), sizeof(int));
        if (size + Overhead >= plain)
        {
            return null;
        }

        byte[] encodedBytes = new byte[rows * sizeof(int)];
        MemoryMarshal.Cast<int, byte>(encoded).CopyTo(encodedBytes);
        byte[] patchBytes = new byte[patches.Count * sizeof(float)];
        MemoryMarshal.Cast<float, byte>(CollectionsMarshal.AsSpan(patches)).CopyTo(patchBytes);

        return new AlpPlan(
            (byte)e, (byte)f, encodedBytes, PType.I32, [.. indices], patchBytes, size);
    }

    /// <summary>The branchless round-to-nearest of the reference: add the sweet spot and take it away.</summary>
    /// <remarks>
    /// Adding 1.5 * 2^52 forces every fractional bit out of the mantissa under the FPU's own
    /// rounding mode, and subtracting it leaves the rounded value. It is not `Math.Round`: it is
    /// what the reference does, and the two disagree on halfway cases.
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

    private static void FillGaps<T>(T[] encoded, List<int> indices, ValidityMask mask, int rows, T filler)
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
    private static long Estimate<T>(T[] encoded, int patchCount, int floatWidth, int intWidth)
        where T : unmanaged, IComparable<T>
    {
        ulong span = Span(encoded, intWidth);
        int bits = span == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(span);
        long encodedBytes = (((long)encoded.Length * bits) + 7) / 8;

        // A patch costs its value and its index; the index is a u32 in the shape we write.
        return encodedBytes + ((long)patchCount * (floatWidth + sizeof(uint)));
    }

    private static ulong Span<T>(T[] encoded, int intWidth)
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
    /// The search runs e downward and f up to e, which is the reference's order and matters for
    /// the tie-break: among pairs of equal estimated size the smaller `e - f` wins, because that
    /// is the one that scales least and therefore rounds least.
    /// </remarks>
    private static (int E, int F) BestExponentsDouble(
        ReadOnlySpan<double> values, ValidityMask mask, int rows)
    {
        double[] sample = SampleDouble(values, mask, rows);
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
        float[] sample = SampleSingle(values, mask, rows);
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

    private static long EstimateSampleDouble(double[] sample, int e, int f)
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

    private static long EstimateSampleSingle(float[] sample, int e, int f)
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
    /// Samples in RUNS rather than at a stride: adjacent values share a magnitude and a number of
    /// significant digits, and a stride would see one value from each run and miss that entirely.
    /// </summary>
    private static double[] SampleDouble(ReadOnlySpan<double> values, ValidityMask mask, int rows)
    {
        List<double> sample = new List<double>(SampleSize);
        foreach (int index in SampleIndices(rows))
        {
            if (mask.IsValid(index))
            {
                sample.Add(values[index]);
            }
        }

        return [.. sample];
    }

    private static float[] SampleSingle(ReadOnlySpan<float> values, ValidityMask mask, int rows)
    {
        List<float> sample = new List<float>(SampleSize);
        foreach (int index in SampleIndices(rows))
        {
            if (mask.IsValid(index))
            {
                sample.Add(values[index]);
            }
        }

        return [.. sample];
    }

    private static IEnumerable<int> SampleIndices(int rows)
    {
        if (rows <= SampleSize)
        {
            for (int i = 0; i < rows; i++)
            {
                yield return i;
            }

            yield break;
        }

        int blocks = SampleSize / SampleBlock;
        for (int b = 0; b < blocks; b++)
        {
            long start = (long)b * (rows - SampleBlock) / Math.Max(blocks - 1, 1);
            for (int i = 0; i < SampleBlock; i++)
            {
                yield return (int)start + i;
            }
        }
    }
}
