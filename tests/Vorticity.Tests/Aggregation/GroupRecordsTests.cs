using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The states of fixed size of a partition's groups in a record a group (PLAN-HIGH-CARDINALITY.md,
/// H1): the layout of a record against its slots, the records grown, seeded and kept, and a caller's
/// state that holds a reference, which keeps an array of its own beside them.
/// </summary>
public sealed partial class GroupRecordsTests
{
    [Fact]
    public void ARecordHoldsEveryStateTheWidestFirst()
    {
        AggregateSlot[] slots =
        [
            new CountSlot(),
            new FixedSlot<long, SumState<long>, NarrowSignedSum<long>, long>(StorageKind.Primitive, static s => s.Sum),
            new ExistsSlot(false),
            new BoolSlot<bool?>(BoolFlags.Min),
            new RowSlot(last: false),
            new ChosenBySlot<double>(max: true, StorageKind.Primitive),
            new FixedDistinctSlot<int>(StorageKind.Primitive),
        ];

        RecordLayout layout = RecordLayout.Of(slots)!;

        // 16, 16, 8, 8, 1 and 1 bytes, the equal ones in the slots' order; 50 bytes round to 64.
        Assert.Equal([32, 0, 48, 49, 40, 16, -1], layout.Offsets);
        Assert.Equal(8, layout.Stride);

        // Every state starts at its seed: a row not chosen yet is -1, the rest are zero.
        Span<byte> seed = MemoryMarshal.AsBytes(layout.Seed.AsSpan());
        Assert.Equal(-1L, MemoryMarshal.Read<long>(seed[40..]));
        Assert.Equal(-1L, MemoryMarshal.Read<long>(seed[16..]));
        Assert.All(seed[..16].ToArray(), b => Assert.Equal(0, b));
        Assert.All(seed[32..40].ToArray(), b => Assert.Equal(0, b));
        Assert.All(seed[48..].ToArray(), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(24, 32)]
    [InlineData(40, 40)]
    [InlineData(48, 64)]
    [InlineData(56, 64)]
    [InlineData(80, 80)]
    public void ARecordRoundsToAPowerOfTwoWhenThatAddsAThirdAtMost(int bytes, int stride)
    {
        AggregateSlot[] slots = [.. Enumerable.Range(0, bytes / 8).Select(_ => (AggregateSlot)new CountSlot())];
        Assert.Equal(stride / 8, RecordLayout.Of(slots)!.Stride);
    }

    [Fact]
    public void RecordsGrowSeededAndKeepTheGroupsKept()
    {
        AggregateSlot[] slots = [new CountSlot(), new RowSlot(last: false)];
        RecordLayout layout = RecordLayout.Of(slots)!;
        GroupRecords records = new GroupRecords(layout);
        int count = layout.Offsets[0];
        int row = layout.Offsets[1];

        records.EnsureGroups(3);
        for (int g = 0; g < 3; g++)
        {
            Assert.Equal(0L, records.View<long>(count)[g]);
            Assert.Equal(-1L, records.View<long>(row)[g]);
            records.View<long>(count)[g] = 10 + g;
            records.View<long>(row)[g] = 100 + g;
        }

        // A growth past the capacity moves the records, and seeds only the new ones.
        records.EnsureGroups(40);
        Assert.Equal(12L, records.View<long>(count)[2]);
        Assert.Equal(-1L, records.View<long>(row)[39]);

        records.Keep([0, 2]);
        Assert.Equal(2, records.Groups);
        Assert.Equal(10L, records.View<long>(count)[0]);
        Assert.Equal(12L, records.View<long>(count)[1]);
        Assert.Equal(102L, records.View<long>(row)[1]);

        // The groups past the kept ones are seeded again when they are made.
        records.EnsureGroups(3);
        Assert.Equal(0L, records.View<long>(count)[2]);
        Assert.Equal(-1L, records.View<long>(row)[2]);
    }

    /// <summary>
    /// A sum keeps the count a mean divides by only when a mean of its column reads its slot: its
    /// total alone otherwise, of the width the statistics prove, signed or not; a mean of another
    /// column reads its own.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASumNoMeanReadsKeepsItsTotalAlone(bool shared)
    {
        Row[] rows = Rows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 2);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = file.Scan<Row>()
                .GroupBy(r => r.Key)
                .Select(g => (
                    g.Key, g.Count(), g.Sum(r => r.Value), g.Sum(r => r.Big), g.Sum(r => r.Small), g.Sum(r => r.Huge),
                    shared ? g.Average(r => r.Value) : g.Average(r => r.Real)));
            int[] bytes = [];
            grouped.Plan.Watch = partitions => bytes = [.. partitions[0].Slots.Select(slot => slot.StateBytes)];
            Dictionary<int, Sums> read = [];
            await foreach (Sums group in grouped.As<Sums>().ToRecordsAsync(Ct))
            {
                read.Add(group.Key, group);
            }

            // The count, then the sums of a long proven narrow, of a long and an unsigned long the
            // statistics prove nothing of, and of an unsigned int proven narrow: the first keeps its
            // count beside its total when the mean of its column reads it.
            Assert.Equal([8, shared ? 16 : 8, 16, 8, 16], bytes[..5]);
            Assert.Equal(5_000, read.Count);
            foreach (IGrouping<int, Row> group in rows.GroupBy(r => r.Key))
            {
                Sums sums = read[group.Key];
                Assert.Equal(group.Count(), sums.Count);
                Assert.Equal(group.Sum(r => r.Value), sums.Values);
                Assert.Equal(group.Sum(r => r.Big), sums.Bigs);
                Assert.Equal(group.Aggregate(0UL, (total, r) => total + r.Small), sums.Smalls);
                Assert.Equal(group.Aggregate(0UL, (total, r) => total + r.Huge), sums.Huges);
                Assert.Equal(shared ? group.Average(r => r.Value) : group.Average(r => r.Real), sums.Mean!.Value, 9);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AStateHoldingAReferenceKeepsAnArrayBesideTheRecords(int degree)
    {
        Row[] rows = Rows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, (long Count, long Sum, Largest Largest)> read = [];
            await foreach (KeyLargest group in file.Scan<Row>()
                .GroupBy(r => r.Key)
                .Select(g => (g.Key, g.Count(), g.Sum(r => r.Value), g.Aggregate<double, LargestAsText, Largest>(r => r.Real)))
                .As<KeyLargest>()
                .ToRecordsAsync(Ct))
            {
                read.Add(group.Key, (group.Count, group.Sum, group.Largest));
            }

            foreach (IGrouping<int, Row> group in rows.GroupBy(r => r.Key))
            {
                (long count, long sum, Largest largest) = read[group.Key];
                Assert.Equal(group.Count(), count);
                Assert.Equal(group.Sum(r => r.Value), sum);
                Assert.Equal(group.Max(r => r.Real).ToString(System.Globalization.CultureInfo.InvariantCulture), largest.Text);
            }

            Assert.Equal(5_000, read.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Five thousand keys in no order, twenty rows each; a long and an unsigned int whose rows times
    /// largest value fit 64 bits, and a long and an unsigned long whose do not, though every group's
    /// sum does.
    /// </summary>
    private static Row[] Rows() =>
        [.. Enumerable.Range(0, 100_000).Select(r => new Row(
            r * 7_919 % 5_000, r % 97, r % 89 * 1.5, (r % 3 - 1) * (long.MaxValue / 1_000), (uint)r, (ulong)(r % 5) * (ulong.MaxValue / 1_000)))];

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-records");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int Key, long Value, double Real, long Big, uint Small, ulong Huge);

    [VortexRecord]
    public partial record struct KeyLargest(int Key, long Count, long Sum, Largest Largest);

    [VortexRecord]
    public partial record struct Sums(int Key, long Count, long Values, long Bigs, ulong Smalls, ulong Huges, double? Mean);
}

/// <summary>The largest value seen, and its text: a state that holds a reference.</summary>
[VortexRecord]
public partial struct Largest
{
    public double Value;
    public string? Text;
}

/// <summary>Keeps the largest value and writes it as text when it changes: a state a record of bytes cannot hold.</summary>
public readonly struct LargestAsText : IAggregator<double, Largest>
{
    public static Largest Seed() => new Largest { Value = double.NegativeInfinity };

    public static void Step(ref Largest state, ReadOnlySpan<double> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int i in rows)
        {
            if ((validity.IsEmpty || ((validity[i >> 6] >> (i & 63)) & 1) != 0) && values[i] > state.Value)
            {
                state.Value = values[i];
                state.Text = values[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    public static void Merge(ref Largest into, in Largest other)
    {
        if (other.Value > into.Value)
        {
            into = other;
        }
    }
}
