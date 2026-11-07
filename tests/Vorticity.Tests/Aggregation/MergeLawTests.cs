// The merge of every state the parts of a question carry, held to the laws that make a question's bits the same
// whatever its cut: commutative and associative, to the bit.
//
// THE STATES are built from values drawn from a pool that holds the edges: both zeros, NaN, both infinities, the
// largest and the smallest doubles, subnormals, and ordinary values. Each state is built again from its values for each
// merge, since a merge changes the state it merges into, and the laws compare the states' bytes, or a sketch's
// encoding: a merge that rounds differently in another order, or keeps the first of two equal values, shows there.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>A row of one double, for the extremes of zeros.</summary>
[VortexRecord]
public partial record struct ZeroRow(double Value);

public sealed class MergeLawTests
{
    private const int Trials = 300;

    private static readonly double[] Edges =
    [
        0.0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, -double.MaxValue,
        double.Epsilon, -double.Epsilon, 2.2250738585072014E-308, 1.0, -1.0, 0.1, 1e300, -1e-300, 3.0, 1e16, -1e16,
    ];

    [Fact]
    public void AnIndexedSumMergesTheSameWhateverTheOrder() =>
        Laws(values => { IndexedSum sum = default; foreach (double value in values) { sum.Add(value); } return sum; },
            (ref IndexedSum into, in IndexedSum other) => into.Merge(in other), Bytes);

    [Fact]
    public void AWideIndexedSumMergesTheSameWhateverTheOrder() =>
        Laws(values => { WideIndexedSum sum = default; foreach (double value in values) { sum.Add(value); } return sum; },
            (ref WideIndexedSum into, in WideIndexedSum other) => into.Merge(in other), Bytes);

    [Fact]
    public void TheLeastOfDoublesMergesTheSameWhateverTheOrder() =>
        Laws(values => { ExtremeState<double> state = default; MinOp<double> op = default; foreach (double value in values) { op.Add(ref state, value); } return state; },
            (ref ExtremeState<double> into, in ExtremeState<double> other) => default(MinOp<double>).Merge(ref into, in other), Bytes);

    [Fact]
    public void TheGreatestOfDoublesMergesTheSameWhateverTheOrder() =>
        Laws(values => { ExtremeState<double> state = default; MaxOp<double> op = default; foreach (double value in values) { op.Add(ref state, value); } return state; },
            (ref ExtremeState<double> into, in ExtremeState<double> other) => default(MaxOp<double>).Merge(ref into, in other), Bytes);

    [Fact]
    public void ASeededLeastOfDoublesMergesTheSameWhateverTheOrder() =>
        Laws(values => { double state = double.NaN; SeededMinOp<double> op = default; foreach (double value in values) { op.Add(ref state, value); } return state; },
            (ref double into, in double other) => default(SeededMinOp<double>).Merge(ref into, in other), Bytes);

    [Fact]
    public void ASeededGreatestOfDoublesMergesTheSameWhateverTheOrder() =>
        Laws(values => { double state = double.NaN; SeededMaxOp<double> op = default; foreach (double value in values) { op.Add(ref state, value); } return state; },
            (ref double into, in double other) => default(SeededMaxOp<double>).Merge(ref into, in other), Bytes);

    [Fact]
    public void ACardinalitySketchMergesTheSameWhateverTheOrder() =>
        Laws(values =>
            {
                CardinalitySketch sketch = new CardinalitySketch();
                foreach (double value in values)
                {
                    sketch.Add(System.IO.Hashing.XxHash3.HashToUInt64(BitConverter.GetBytes(value)));
                }

                return sketch;
            },
            (ref CardinalitySketch into, in CardinalitySketch other) => into.Merge(other),
            sketch => Encoded(sketch.Encode),
            wide: true);

    [Fact]
    public void AQuantileSketchMergesTheSameWhateverTheOrder() =>
        Laws(values =>
            {
                QuantileSketch sketch = new QuantileSketch(7);
                foreach (double value in values)
                {
                    sketch.Add(value);
                }

                return sketch;
            },
            (ref QuantileSketch into, in QuantileSketch other) => into.Merge(other),
            sketch => Encoded(sketch.Encode));

    [Fact]
    public void TheExtremeOfZerosIsTheSameZeroWhereverTheyLie()
    {
        // The span kernels, in vector lanes and in their scalar tail: the greatest of zeros is +0.0 as soon as one is
        // there, the least -0.0, whatever the place of each, around NaN, over every length up to three vectors.
        for (int length = 1; length <= 3 * System.Numerics.Vector<double>.Count + 1; length++)
        {
            for (int mask = 0; mask < 1 << Math.Min(length, 12); mask++)
            {
                double[] values = new double[length];
                for (int i = 0; i < length; i++)
                {
                    values[i] = i < 12 && (mask & (1 << i)) != 0 ? 0.0 : -0.0;
                }

                if (length > 2)
                {
                    values[length / 2] = double.NaN;
                }

                bool positive = values.Any(value => value == 0 && !double.IsNegative(value));
                bool negative = values.Any(value => value == 0 && double.IsNegative(value));
                Assert.True(ExtremeKernels.TryMax<double>(values, out double max));
                Assert.True(ExtremeKernels.TryMin<double>(values, out double min));
                Assert.Equal(!positive, double.IsNegative(max));
                Assert.Equal(negative, double.IsNegative(min));
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async System.Threading.Tasks.Task AFilesGreatestZeroIsPositiveAndItsLeastNegativeWhateverTheirBlocks(bool negativeFirst, bool withinBlocks)
    {
        // Blocks of 64 zeros, of one sign each and alternating, or of both signs row after row: the greatest of the file is
        // +0.0 and its least -0.0, through the terminal scan, which decides from the blocks' bounds where it can, through the
        // terminal scan made to read the rows, and through the aggregate.
        System.Threading.CancellationToken ct = TestContext.Current.CancellationToken;
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-zeros-{Guid.NewGuid():N}.vortex");
        try
        {
            ZeroRow[] rows = [.. Enumerable.Range(0, 64 * 5).Select(i => new ZeroRow(((withinBlocks ? i : i / 64) % 2 == 0) == negativeFirst ? -0.0 : 0.0))];
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(path, ZeroRow.Schema, new VortexWriteOptions { RowBlockSize = 64 }))
            {
                await writer.WriteAsync<ZeroRow>(rows, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.False(double.IsNegative((await file.ScanBuilder().MaxAsync(nameof(ZeroRow.Value), ct)).FloatValue));
            Assert.True(double.IsNegative((await file.ScanBuilder().MinAsync(nameof(ZeroRow.Value), ct)).FloatValue));
            Assert.False(double.IsNegative(await file.Scan<ZeroRow>().MaxAsync(r => r.Value, ct)));
            Assert.True(double.IsNegative(await file.Scan<ZeroRow>().MinAsync(r => r.Value, ct)));
            ScanOptions rowsOnly = new() { UseStatistics = false };
            Assert.False(double.IsNegative(await file.Scan<ZeroRow>().With(rowsOnly).MaxAsync(r => r.Value, ct)));
            Assert.True(double.IsNegative(await file.Scan<ZeroRow>().With(rowsOnly).MinAsync(r => r.Value, ct)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private delegate void Merger<T>(ref T into, in T other);

    /// <summary>
    /// For states built from drawn values, a merge of two in either order, and of three grouped either way, gives the same
    /// bytes. A sketch of distinct values draws up to a few hundred of them, past its exact count.
    /// </summary>
    private static void Laws<T>(Func<double[], T> build, Merger<T> merge, Func<T, byte[]> bytes, bool wide = false)
    {
        // Every pair and triple of states of one edge value each, then states drawn at random.
        List<(double[] A, double[] B, double[] C)> cases = [];
        foreach (double x in Edges)
        {
            foreach (double y in Edges)
            {
                foreach (double z in (double[])[0.0, -0.0, double.NaN, 1.0])
                {
                    cases.Add(([x], [y], [z]));
                }
            }
        }

        Random random = new Random(0x1A3);
        for (int trial = 0; trial < Trials; trial++)
        {
            cases.Add((Draw(random, wide), Draw(random, wide), Draw(random, wide)));
        }

        for (int trial = 0; trial < cases.Count; trial++)
        {
            (double[] a, double[] b, double[] c) = cases[trial];

            T ab = build(a);
            merge(ref ab, build(b));
            T ba = build(b);
            merge(ref ba, build(a));
            Assert.True(
                bytes(ab).AsSpan().SequenceEqual(bytes(ba)),
                $"trial {trial}: merging [{Show(a)}] with [{Show(b)}] gives other bytes in the other order");

            T left = build(a);
            merge(ref left, build(b));
            merge(ref left, build(c));
            T right = build(b);
            merge(ref right, build(c));
            T grouped = build(a);
            merge(ref grouped, right);
            Assert.True(
                bytes(left).AsSpan().SequenceEqual(bytes(grouped)),
                $"trial {trial}: merging [{Show(a)}], [{Show(b)}] and [{Show(c)}] gives other bytes grouped the other way");
        }
    }

    private static double[] Draw(Random random, bool wide)
    {
        int count = random.Next(0, wide && random.Next(4) == 0 ? 300 : 6);
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = random.Next(3) == 0 ? (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-20, 20)) : Edges[random.Next(Edges.Length)];
        }

        return values;
    }

    private static string Show(double[] values) =>
        string.Join(", ", values.Select(value => value == 0 && double.IsNegative(value) ? "-0.0" : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    private static byte[] Bytes<T>(T state)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref state)).ToArray();

    private static byte[] Encoded(Action<IBufferWriter<byte>> encode)
    {
        ArrayBufferWriter<byte> into = new ArrayBufferWriter<byte>();
        encode(into);
        return into.WrittenSpan.ToArray();
    }
}
