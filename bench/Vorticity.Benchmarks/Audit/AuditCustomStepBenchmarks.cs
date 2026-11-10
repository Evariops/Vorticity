// Audit AG-06: what a caller's aggregator costs a row when the rows of a batch go to many groups, each row stepped on
// its own, against the engine's own sum.
//
// 4,194,304 rows of a key among 64 groups and a 64-bit value, from memory, at one lane: the keys drawn at random row by
// row, or in blocks of 1,024 rows of one key, the blocks in random order (the key is not sorted, so the rows go to their
// groups one by one either way). The groups' sums are read.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A caller's sum and the engine's, per group.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditCustomStepBenchmarks
{
    private const int Rows = 1 << 22;

    private VortexFile _file = null!;

    /// <summary>Whether a key holds blocks of rows rather than rows drawn one by one.</summary>
    [Params(false, true)]
    public bool Blocks { get; set; }

    /// <summary>A row: its group and its value.</summary>
    [VortexRecord]
    public partial record struct Row(int Group, long Value);

    /// <summary>A group's sum.</summary>
    [VortexRecord]
    public partial record struct Total(int Group, long Sum);

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-custom-{Guid.NewGuid():N}.vortex");
        Random random = new Random(61);
        Row[] rows = new Row[Rows];
        int group = 0;
        for (int i = 0; i < Rows; i++)
        {
            if (!Blocks || (i & 1023) == 0)
            {
                group = random.Next(64);
            }

            rows[i] = new Row(group, random.NextInt64(1L << 30));
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        if (Custom() != Engine())
        {
            throw new InvalidOperationException("The caller's sum and the engine's differ.");
        }
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The caller's sum of every group, summed.</summary>
    [Benchmark]
    public long Custom() => SumAsync(custom: true).GetAwaiter().GetResult();

    /// <summary>The engine's sum of every group, summed.</summary>
    [Benchmark]
    public long Engine() => SumAsync(custom: false).GetAwaiter().GetResult();

    private async Task<long> SumAsync(bool custom)
    {
        Scan<Row> scan = _file.Scan<Row>().With(new ScanOptions { DegreeOfParallelism = 1 });
        Aggregation sums = custom
            ? scan.GroupBy(r => r.Group).Select(g => (g.Key, g.Aggregate<long, LongSum, long>(r => r.Value)))
            : scan.GroupBy(r => r.Group).Select(g => (g.Key, g.Sum(r => r.Value)));
        long sum = 0;
        await foreach (Columns<Total> batch in sums.As<Total>())
        {
            foreach (long value in batch.Sum.Values)
            {
                sum += value;
            }
        }

        return sum;
    }

    private static async Task WriteAsync(string path, Row[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>A caller's sum of 64-bit values.</summary>
    public readonly struct LongSum : IAggregator<long, long>
    {
        /// <inheritdoc/>
        public static long Seed() => 0;

        /// <inheritdoc/>
        public static void Step(ref long state, ReadOnlySpan<long> values, ReadOnlySpan<ulong> validity, Selection rows)
        {
            foreach (int i in rows)
            {
                if (validity.IsEmpty || ((validity[i >> 6] >> (i & 63)) & 1) != 0)
                {
                    state += values[i];
                }
            }
        }

        /// <inheritdoc/>
        public static void Merge(ref long into, in long other) => into += other;
    }
}
