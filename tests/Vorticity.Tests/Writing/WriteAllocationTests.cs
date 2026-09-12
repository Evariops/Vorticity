// What writing a file allocates. Nothing measured this before.
//
// docs/05-benchmarks.md reports the read paths and PathAllocationTests pins six of them. The write
// path had neither: no benchmark axis, no ceiling, no figure anywhere in the repository. The one
// write-side number that exists is WrittenSizeTests' output ratio, which is about the BYTES ON DISK
// and says nothing about what producing them costs in managed memory.
//
// WHAT "AUDITED" MEANS HERE, because a literal zero would be the wrong target and would be learned
// as noise within a week. A writer builds buffers; that is its job. The target is **no
// UNINTENTIONAL allocation**: every remaining one named, justified and bounded by something other
// than the input size where possible. So this file does two things a benchmark cannot:
//
//   * it pins the total against a ceiling, the way WrittenSizeTests pins the size ratio;
//   * it pins the total PER ROW, which is the shape question. A writer that allocates a constant
//     per batch is bounded; one that allocates per row is not, and the two are indistinguishable
//     from a single total. Two files of very different row counts separate them.
//
// The sink discards. The measurement is of the writer, not of a FileStream or of a MemoryStream's
// doubling - both are the caller's choice of destination, and neither is what this audits.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What the write path allocates, held against a ratchet.</summary>
/// <remarks>
/// Shares <see cref="Vorticity.Tests.Scan.AllocationCollection"/> for the reason that class
/// records: <c>ArrayPool&lt;T&gt;.Shared</c> is process-global, so a neighbouring test class
/// draining it turns a rent that would have been free into an allocation charged here.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class WriteAllocationTests
{
    /// <summary>
    /// Runs discarded before measuring: JIT, statics, and the encoding registry.
    /// </summary>
    /// <remarks>
    /// Five rather than three, and the reason is the counter. This measures process-wide allocation
    /// (see <see cref="Rewrite"/>), so tiered JIT promoting a writer method on its call-count
    /// threshold lands INSIDE a measured run rather than beside it. That tripped a ceiling once on
    /// the first suite run after this file was added and never again in seven. Warming longer fixes
    /// the cause; widening the ceiling would only have hidden it.
    /// </remarks>
    private const int Warmup = 5;

    /// <summary>Runs measured, of which the minimum is the answer.</summary>
    private const int Runs = 6;

    /// <summary>
    /// The files written, and what each is here to expose.
    /// </summary>
    /// <remarks>
    /// Chosen to differ in the dimension that matters rather than to be representative: the same
    /// schema at two row counts would answer the per-row question, and different schemas answer
    /// which ENCODER is expensive. Both questions are worth one axis each.
    /// </remarks>
    private static readonly (string Id, long Ceiling)[] Files =
    [
        ("containers/zoned_many_zones_nulls", 60_100_000),
        ("distributions/high_cardinality_i64_r8193", 1_435_000),
        ("encodings/fsst", 2_180_000),
        ("encodings/onpair", 610_000),
        ("types/utf8_nullable_r1025", 787_000),
    ];

    /// <summary>
    /// A shape guard, in bytes per row, over and above each file's own ceiling.
    /// </summary>
    /// <remarks>
    /// The per-file ceilings catch a regression on these five files. This catches the thing they
    /// cannot: a cost that scales with the DATA rather than the schema would pass every per-file
    /// ceiling the day it was set and fail on the first larger file anyone wrote. Set well above
    /// the worst current figure rather than near it, because it is not the tight bound - it is the
    /// bound that says "still roughly proportional to what it was".
    /// </remarks>
    private const double PerRowCeiling = 1_000.0;

    [Fact]
    public async Task WritingAllocatesWithinItsPerRowCeiling()
    {
        Decoders.EnsureRegistered();

        StringBuilder report = new StringBuilder("WRITE ALLOCATIONS: floor of ")
            .Append(Runs.ToString(CultureInfo.InvariantCulture))
            .Append(" runs after ")
            .Append(Warmup.ToString(CultureInfo.InvariantCulture))
            .Append(" warm-ups\n");

        List<string> over = [];
        foreach ((string id, long ceiling) in Files)
        {
            string path = Corpus.Path(id);
            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            (long floor, long rows) = await Measure(path);
            double perRow = rows == 0 ? 0 : (double)floor / rows;
            report.Append("    ")
                .Append(id.PadRight(42))
                .Append(rows.ToString(CultureInfo.InvariantCulture).PadLeft(7))
                .Append(" rows  ")
                .Append(floor.ToString(CultureInfo.InvariantCulture).PadLeft(10))
                .Append(" B  ")
                .Append(perRow.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7))
                .Append(" B/row   ceiling ")
                .Append(ceiling.ToString(CultureInfo.InvariantCulture).PadLeft(10))
                .Append('\n');

            if (floor > ceiling)
            {
                over.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{id} allocated {floor} B against a ceiling of {ceiling}"));
            }

            if (perRow > PerRowCeiling)
            {
                over.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{id} allocated {perRow:F1} B/row against the shape guard of {PerRowCeiling:F1}"));
            }
        }

        Console.Out.Write(report.ToString());
        Assert.True(over.Count == 0, string.Join("\n", over) + "\n" + report);
    }

    /// <summary>Rewrites one file through a discarding sink and returns the floor and its rows.</summary>
    private static async Task<(long Floor, long Rows)> Measure(string path)
    {
        long rows = 0;
        for (int i = 0; i < Warmup; i++)
        {
            rows = await Rewrite(path);
        }

        long floor = long.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            rows = await Rewrite(path);
            floor = Math.Min(floor, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return (floor, rows);
    }

    /// <summary>
    /// Reads every batch of a file and writes it back out, which is the writer's whole job.
    /// </summary>
    /// <remarks>
    /// <see cref="GC.GetTotalAllocatedBytes"/> rather than the per-thread counter
    /// <see cref="Vorticity.Tests.Scan.PathAllocationTests"/> uses: writing goes through
    /// <c>WriteAsync</c>, and a sink is entitled to complete asynchronously on another thread even
    /// when this one does not. Process-wide is the safe direction to be wrong in - it can only
    /// over-count - and the class runs alone, so there is nothing else to over-count.
    ///
    /// The READ half is inside the measurement and cannot be subtracted without a second harness.
    /// PathAllocationTests prices it: 190 672 B for a full scan of the largest file here. The
    /// figures below are therefore an upper bound on what writing costs, which is the honest
    /// direction for a ceiling.
    /// </remarks>
    private static async Task<long> Rewrite(string path)
    {
        long rows = 0;
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(new NullSink(), source.Schema);

        await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return rows;
    }

    /// <summary>A sink that counts and keeps nothing, so the writer is what gets measured.</summary>
    private sealed class NullSink : ISegmentSink
    {
        public long Position { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
