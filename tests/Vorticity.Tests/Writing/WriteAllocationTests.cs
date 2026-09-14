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
    /// <remarks>
    /// EIGHT OF THESE TEN CAME DOWN WITH W-1, by the exact bytes it saved, so the headroom is the
    /// same and the ratchet is tighter: interning a component id no longer decodes the wire bytes
    /// to a `string` on every node just to look one up. `encodings/fsst` and `encodings/zstd` did
    /// not move at all, which is its own small fact -- their write is dominated by the compressor
    /// rather than by the node count.
    ///
    /// FIVE CAME DOWN AGAIN WITH W-3, the same way: a metadata scalar store is now sized for the two
    /// integers it holds instead of for a file's statistics. The files that moved are the ones whose
    /// columns elect `fastlanes.for` or `vortex.sequence` -- `zoned_many_zones_nulls` -5 712 B,
    /// `map` -2 304, `delta`, `pco` and `zstd` -576 each -- and the five that did not are the ones
    /// that elect neither.
    /// </remarks>
    private static readonly (string Id, long Ceiling)[] Files =
    [
        ("containers/zoned_many_zones_nulls", 7_891_000),
        ("distributions/high_cardinality_i64_r8193", 469_800),
        ("encodings/fsst", 310_000),
        ("encodings/onpair", 369_900),
        ("types/utf8_nullable_r1025", 304_900),

        // THE LATE COMPONENTS, on the write side, for PERF-AUDIT-v2.md F2's reason: `fastlanes.delta`,
        // `vortex.pco`, `vortex.zstd`, `vortex.map` and `vortex.variant` were watched by no
        // allocation ratchet on either side. Note that what is written here is the CANONICAL form
        // of each file -- our compressor picks the encoding, it does not preserve the source's --
        // so these axes measure "what does writing this SHAPE of data cost", which is the question
        // a ratchet can answer. Whether our writer re-elects the same encoding is a different
        // question and `bench/crosscheck.sh` is where it is asked.
        ("encodings/fastlanes_delta", 63_300),
        ("encodings/pco", 65_300),
        ("encodings/zstd", 363_400),
        ("encodings/map", 453_100),
        ("encodings/variant", 64_680),
    ];

    // FOUR OF THESE FIVE CAME DOWN AGAIN WHEN FSST STOPPED ALLOCATING WHAT IT THROWS AWAY.
    // Pricing FSST means training a table and compressing the whole column, and on a column it
    // loses -- which is the common case, because it is priced against zstd and against the plain
    // form -- the heap, the row table and the code stream are all garbage the moment it returns
    // null. Rented instead of allocated, with a row as two ints rather than a
    // `ReadOnlyMemory<byte>` in a `List`:
    //
    //     containers/zoned_many_zones_nulls   12 061 328 B -> 7 866 272 B   -35%
    //     encodings/fsst                       1 148 096 B ->   303 624 B   -74%
    //     types/utf8_nullable_r1025              490 944 B ->   298 624 B   -39%
    //     encodings/onpair                       495 408 B ->   365 392 B   -26%
    //
    // The corpus still rewrites to 9 942 348 bytes, unchanged to the byte: the sampler draws the
    // same lines and the trainer reaches the same tables.

    // FOUR OF THESE FIVE WENT UP WHEN REPARTITIONING LANDED, and that is a trade rather than a
    // regression, so it is written down rather than rounded over. A writer that buffers rows needs
    // an arena to buffer them in, and the rows it buffers are materialized into it -- a fixed cost
    // per FILE, plus a second materialization when several batches are concatenated into one chunk.
    // On a file whose rows fit in one block that cost is all there is, and it is worth 3% to 20%:
    //
    //     containers/zoned_many_zones_nulls   32 172 440 B -> 12 059 208 B   -62%
    //     distributions/high_cardinality         381 512 B ->    463 704 B   +22%
    //     encodings/fsst                       1 129 768 B ->  1 148 096 B    +2%
    //     encodings/onpair                       477 008 B ->    495 408 B    +4%
    //     types/utf8_nullable_r1025              472 544 B ->    490 944 B    +4%
    //
    // What it buys, on the file large enough to have chunks to save: 64 chunks become 3, which is
    // 20 MB of write allocation and -76% of the allocation a SCAN of that file costs
    // (RewrittenComparison: 147 510 B -> 35 437 B). Setting `RowBlockSize = null` restores the old
    // figures exactly, for a caller whose batches are already its chunking.

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
    private const double PerRowCeiling = 600.0;

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
