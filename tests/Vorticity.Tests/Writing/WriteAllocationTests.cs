// What writing a file allocates.
//
// PathAllocationTests pins what the read paths allocate. On the write side, WrittenSizeTests'
// output ratio is about the BYTES ON DISK and says nothing about what producing them costs in
// managed memory.
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
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
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
    /// threshold lands INSIDE a measured run rather than beside it. Warming longer fixes the cause;
    /// widening the ceiling would only hide it.
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
    /// Each ceiling sits just above its measurement and comes down with every change that saves
    /// bytes on its file, so the headroom stays the same and the ratchet tightens. A ceiling goes
    /// up only with its cost written down beside what that cost buys, because a ratchet whose
    /// floors drift upward unremarked is how the next one gets excused.
    /// <para>
    /// The files do not all answer to the same costs: `encodings/fsst` and `encodings/zstd` are
    /// dominated by their compressor rather than by the node count, and `delta` and `pco` are the
    /// two whose columns the sequence detector claims before the run scan ever runs.
    /// </para>
    /// <para>
    /// `encodings/variant` pays for the column writers being a TREE: a variant's canonical form is
    /// a two-field struct, so that file keeps three summarizing nodes where a flat column keeps
    /// one, and in exchange its leaves are written as columns of their own. The cost is per COLUMN
    /// and not per row, which is the distinction this file exists to make.
    /// </para>
    /// </remarks>
    // `Auto` is the default, and the ceilings below carry it: the index writer and its per-column
    // arrays, a Bloom builder per column it indexes (two hash sets whose tables come from the
    // pool, a queue, two lists), the reason for an abandon, the report entries, and for a
    // dictionary file the directory. Per file and per column, never per row, so the per-row guard
    // does not carry it. The filters themselves are not built for a column `Auto` will abandon --
    // the abandon is decided at the first block, on the raw bytes -- or a high-cardinality column
    // would pay for filters it then throws away.
    private static readonly (string Id, long Ceiling)[] Files =
    [
        // The ALP integers and the dictionary codes come from the pool and go back to it: each is
        // a whole column's worth, eight bytes a row for the first and four for the second, and
        // both are built while PRICING, so the candidate that loses would pay for them too.
        ("containers/zoned_many_zones_nulls", 709_800),   // 709 368 measured
        ("distributions/high_cardinality_i64_r8193", 73_700),   // 73 256 measured, including the `Auto` index and the file statistics segment -- a FlatBufferBuilder, a ScalarStore, the bounds in protobuf -- per file, not per row
        ("encodings/fsst", 235_800),   // 235 320 measured, including the `Auto` index
        ("encodings/onpair", 69_200),   // 68 768 measured
        ("types/utf8_nullable_r1025", 215_000),   // 214 568 measured

        // THE REMAINING COMPONENTS, on the write side, so that each has an allocation ratchet:
        // `fastlanes.delta`, `vortex.pco`, `vortex.zstd`, `vortex.map` and `vortex.variant`. Note
        // that what is written here is the CANONICAL form of each file -- our compressor picks the
        // encoding, it does not preserve the source's -- so these axes measure "what does writing
        // this SHAPE of data cost", which is the question a ratchet can answer. Whether our writer
        // re-elects the same encoding is a different question and `bench/crosscheck.sh` is where
        // it is asked.
        ("encodings/fastlanes_delta", 66_100),   // 65 632 measured, including the `Auto` index and the file statistics segment: per file, not per row
        ("encodings/pco", 67_600),   // 67 440 measured alone and in the suite under DOTNET_TieredPGO=0, 67 512 in the suite with dynamic PGO: the measurement is process-wide, so the gap follows the JIT's instrumentation, not the writer
        // The read half of this axis keeps a `ZstandardDecoder` per node, so a change on the zstd
        // read path can move this ceiling while the write path stays put.
        ("encodings/zstd", 209_100),   // 208 624 measured
        ("encodings/map", 89_100),   // 88 616 measured, including the three nodes the column tree keeps under a map -- the entries, the key, the value -- each with its block lists, its previous row and the map's window cursor: per column, not per row
        ("encodings/variant", 68_200),   // 67 792 measured, including the file statistics segment, the `Auto` index and the two transit ScanContexts the writer creates, whose read-side fields it never uses: per file, not per row (a context reduced to the arena is the fix if that ever matters)

        // THE TWO ALP SHAPES, so that the ALP write path is watched on both of its cases:
        // `alp` is a column ALP fits, `alprd` is one built to defeat it so that every row becomes a
        // patch. The second is the case that made the patch buffers worth renting, and a ratchet
        // that only held the easy shape would have said nothing about it.
        ("encodings/alp", 89_500),   // 89 064 measured
        ("encodings/alprd", 67_300),   // 66 800 measured
    ];

    // Pricing FSST means training a table and compressing the whole column, and on a column it
    // loses -- which is the common case, because it is priced against zstd and against the plain
    // form -- the heap, the row table and the code stream are all garbage the moment it returns
    // null. So they are rented rather than allocated, with a row as two ints rather than a
    // `ReadOnlyMemory<byte>` in a `List`, and the ceilings above count on it.

    // Repartitioning costs allocation, and that is a trade rather than a regression. A writer that
    // buffers rows needs an arena to buffer them in, and the rows it buffers are materialized into
    // it -- a fixed cost per FILE, plus a second materialization when several batches are
    // concatenated into one chunk. On a file whose rows fit in one block that cost is all there
    // is; on a file large enough to have chunks to save, it buys far fewer chunks, less write
    // allocation, and a scan of the file written that allocates far less. Setting
    // `RowBlockSize = null` writes one chunk per batch and drops that cost, for a caller whose
    // batches are already its chunking.

    /// <summary>
    /// A shape guard, in bytes per row, over and above each file's own ceiling.
    /// </summary>
    /// <remarks>
    /// The per-file ceilings catch a regression on these files. This catches the thing they
    /// cannot: a cost that scales with the DATA rather than the schema would pass every per-file
    /// ceiling the day it was set and fail on the first larger file anyone wrote. Set well above
    /// the worst current figure rather than near it, because it is not the tight bound - it is the
    /// bound that says "still roughly proportional to what it was".
    /// </remarks>
    private const double PerRowCeiling = 600.0;

    [Fact]
    public async Task WritingAllocatesWithinItsPerRowCeiling()
    {
        ReleaseOnlyCeilings.Require();
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

    /// <summary>Columns of the narrow and the wide schema of the axis below.</summary>
    private const int NarrowColumns = 100;

    private const int WideColumns = 1_000;

    /// <summary>Rows each column of that axis holds, small so that state outweighs data.</summary>
    private const int WideRows = 64;

    /// <summary>
    /// What one more column adds, in bytes: the ceiling that says the cost is a STATE and not a
    /// scratch. The writer's block scratch is ~130 KiB and measured here is **19 484 B**, a
    /// seventh of it, so the claim holds with room; the ceiling is set just above the measurement
    /// as a ratchet, not as a target.
    /// </summary>
    /// <remarks>
    /// THE BREAKDOWN, measured the same way at 64 and at 512 rows a column, because a
    /// number this size deserves to be named rather than merely bounded:
    /// <list type="bullet">
    /// <item>13 366 B is the writer's own per-column state, and it does not move with the rows —
    /// identical at 64 and at 512, which is the shape claim this axis exists to make;</item>
    /// <item>+5 806 B when `Auto` is on: the index writer's per-column arrays and the Bloom builder
    /// it abandons at the first block;</item>
    /// <item>+312 B for the compressor.</item>
    /// </list>
    /// A thousand columns therefore cost about 19 MB to write once, against 130 KiB of scratch.
    /// </remarks>
    private const double PerColumnCeiling = 22_000.0;

    /// <summary>The wide schema's own ratchet, in bytes. Measured at 19 003 288 B.</summary>
    private const long WideCeiling = 20_000_000;

    /// <summary>
    /// The schema axis: a schema of a thousand columns costs a thousand small states and one
    /// scratch, not a thousand scratches.
    /// </summary>
    /// <remarks>
    /// MARGINAL, NOT TOTAL, because the claim is about the shape of the cost rather than its size.
    /// A per-file ceiling on a thousand columns would pass the day it was set whatever the shape,
    /// so this measures a hundred columns and a thousand of the same rows and divides the
    /// difference by the nine hundred: that number is what one column costs, and it is compared to
    /// what one scratch costs. Sixty-four rows a column, so that a column's data (512 bytes) does
    /// not drown its state.
    /// <para>
    /// `Auto` is on, as everywhere else in this file, so each column also carries the index
    /// writer's per-column state and the Bloom builder it abandons at the first block. That is part
    /// of what a column costs and belongs inside the ceiling.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AThousandColumnsCostAThousandStatesAndOneScratch()
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();

        long narrow = await MeasureWide(NarrowColumns);
        long wide = await MeasureWide(WideColumns);
        double perColumn = (double)(wide - narrow) / (WideColumns - NarrowColumns);
        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"WIDE SCHEMA: {NarrowColumns} columns {narrow} B, {WideColumns} columns {wide} B, " +
            $"{perColumn:F0} B per column ({WideRows} rows each)\n");
        Console.Out.Write(report);

        Assert.True(
            perColumn <= PerColumnCeiling,
            string.Create(
                CultureInfo.InvariantCulture,
                $"one column costs {perColumn:F0} B against a ceiling of {PerColumnCeiling:F0}\n{report}"));
        Assert.True(
            wide <= WideCeiling,
            string.Create(CultureInfo.InvariantCulture, $"{wide} B against a ceiling of {WideCeiling}\n{report}"));
    }

    /// <summary>The floor of several writes of a schema of <paramref name="columns"/> i64 columns.</summary>
    /// <param name="columns">The columns.</param>
    private static async Task<long> MeasureWide(int columns)
    {
        for (int i = 0; i < Warmup; i++)
        {
            await WriteWide(columns);
        }

        long floor = long.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            await WriteWide(columns);
            floor = Math.Min(floor, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return floor;
    }

    /// <summary>Writes one batch of a schema of <paramref name="columns"/> i64 columns.</summary>
    /// <param name="columns">The columns.</param>
    /// <remarks>
    /// The arena and its buffers are built OUTSIDE the measured region: this axis is about the
    /// writer, and materializing a thousand canonical columns is the caller's cost. What is
    /// measured is the write of an already-built batch.
    /// </remarks>
    private static async Task WriteWide(int columns)
    {
        (DType schema, CanonicalArena arena, int root) = Wide(columns);
        await using VortexFileWriter writer = VortexFileWriter.Create(new NullSink(), schema);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    /// <summary>A struct of <paramref name="columns"/> i64 columns of <see cref="WideRows"/> rows.</summary>
    /// <param name="columns">The columns.</param>
    private static (DType Schema, CanonicalArena Arena, int Root) Wide(int columns)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[columns];
        DType[] fields = new DType[columns];
        for (int column = 0; column < columns; column++)
        {
            names[column] = string.Create(CultureInfo.InvariantCulture, $"c{column:D4}");
            fields[column] = i64;
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        int[] children = new int[columns];
        for (int column = 0; column < columns; column++)
        {
            VortexBuffer buffer = arena.Allocate(WideRows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < WideRows; row++)
            {
                // Values a chooser cannot fold away: a constant column would cost nothing to write
                // and the axis would measure an empty writer.
                values[row] = ((long)column * 7_919L) + (row * 104_729L);
            }

            children[column] = arena.AddPrimitive(i64, WideRows, Validity.NonNullable, PType.I64, buffer);
        }

        return (schema, arena, arena.AddStruct(schema, WideRows, Validity.NonNullable, children));
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
        await using VortexFileWriter writer = VortexFileWriter.Create(new NullSink(), source.DType);

        await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync()
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
