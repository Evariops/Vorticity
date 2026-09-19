// The scenarios themselves, in their own assembly so that two builds of them can meet.
//
// BENCH-AUDIT.md C1: the repository's rule is "one process, one clock", and its only realisation
// was copying an old loop into the bench by hand -- impossible for a change that spans a layout
// reader or the arena, so those were judged across two runs, which is what v2 §1.1 forbids.
// The A/B mode loads THIS assembly twice: once from the build tree and once from a worktree of an older
// commit, each bound to its own `Vorticity.dll` in its own `AssemblyLoadContext`.
//
// IT DEPENDS ON THE LIBRARY AND ON NOTHING ELSE. Whatever it references has to exist, and compile,
// in every commit anyone ever wants to compare against -- so no FFI reader, no BenchmarkDotNet, no
// corpus helper. The only types crossing the load-context boundary are `string`, `Task<long>` and
// `Func<,>`, which live in the shared runtime and are therefore the same type on both sides; a
// scenario returning anything of the library's own would be two incompatible types with one name.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Bench.Scenarios;

/// <summary>What a scan, a projection, a take, a filter and a write-back ARE.</summary>
public static class ScenarioSet
{
    /// <summary>The column the projection keeps and the filter tests.</summary>
    public const string Field = "monotone";

    /// <summary>Rows a scattered take asks for.</summary>
    public const long TakeCount = 64;

    /// <summary>The gap between taken rows on the 65 536-row file.</summary>
    public const long TakeStride = 1024;

    /// <summary>The low edge of the filter band.</summary>
    public const long BandLow = 1_000_000;

    /// <summary>A band that keeps about one row in a hundred.</summary>
    public const long NarrowBand = 656;

    /// <summary>A band that keeps about half the rows.</summary>
    public const long WideBand = 32_768;

    /// <summary>The column a projection out of a fifty-column file keeps.</summary>
    public const string WideField = "c07";

    /// <summary>The scenario names this assembly answers to.</summary>
    public static string[] Names =>
        ["fullscan", "projected", "projected-wide", "take", "filtered", "filtered-pruned", "write", "write-bloom", "write-postings", "write-sorted-runs", "lookup-sorted-runs"];

    /// <summary>
    /// The scenario <paramref name="name"/> names, as a delegate of shared-runtime types only.
    /// </summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <returns>The scenario, or <see langword="null"/> when the name is unknown.</returns>
    /// <remarks>
    /// THE ENTRY POINT `--ab` REFLECTS ON. It is deliberately the whole contract: one static method
    /// taking a string and returning a `Func&lt;string, Task&lt;long&gt;&gt;`, so an old build only
    /// has to have THIS shape for the comparison to work, whatever else changed inside it.
    /// </remarks>
    public static Func<string, Task<long>>? Resolve(string name) => name switch
    {
        "fullscan" => ScanAll,
        "projected" => ScanProjected,
        "projected-wide" => p => ScanProjectedField(p, WideField),
        "take" => p => ScatteredTake(p, TakeCount, TakeStride),
        "filtered" => p => FilteredScan(p, BandLow, NarrowBand),
        "filtered-pruned" => FilteredPruned,
        "write" => ReadAndWrite,
        "write-bloom" => p => ReadAndWriteIndexed(p, IndexPolicy.Bloom()),
        "write-postings" => p => ReadAndWriteIndexed(p, IndexPolicy.Postings),
        "write-sorted-runs" => p => ReadAndWriteIndexed(p, IndexPolicy.SortedRuns),
        "lookup-sorted-runs" => LookupSortedRuns,
        "prune-in" => PruneIn,
        _ => null,
    };

    /// <summary>
    /// Plans an <c>IN</c> of <see cref="LookupProbes"/> keys of the column against that same file:
    /// what the locating index costs to ANSWER a filter, where `lookup-sorted-runs` measures what
    /// it costs to seek one key at a time.
    /// </summary>
    /// <param name="path">A tabular file.</param>
    /// <returns>The blocks the plan leaves live.</returns>
    /// <remarks>
    /// THE QUESTION IS ASKED PER BLOCK, and there are as many literals as probes, so this is the
    /// scenario that catches a per-block cost that grows with the filter -- step 33 found one worth
    /// half the plan (docs/12-index-reads.md §13). The probes are keys the column holds, so the
    /// index cannot prune them away and every block is asked about every literal.
    /// </remarks>
    public static async Task<long> PruneIn(string path)
    {
        if (!Lookups.TryGetValue(path, out (byte[] Bytes, string Column, FilterLiteral[] Probes) prepared))
        {
            prepared = await PrepareLookupAsync(path);
            Lookups.TryAdd(path, prepared);
        }

        await using VortexFile file = await VortexFile.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(prepared.Bytes), new VortexOpenOptions(), CancellationToken.None);
        ScanPlan plan = await file.Scan()
            .Where(Expr.In(Expr.Field(prepared.Column), prepared.Probes))
            .ExplainAsync(CancellationToken.None);
        return plan.LiveBlocks;
    }

    /// <summary>The written file, its keyed column and its probes, per input: built on the first call.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Bytes, string Column, FilterLiteral[] Probes)> Lookups = new();

    /// <summary>Probes per call.</summary>
    public const int LookupProbes = 200;

    /// <summary>
    /// Opens the input rewritten with sorted runs on its first keyable column, in chunks of 64 KiB,
    /// and seeks <see cref="LookupProbes"/> keys: what a point lookup costs on a file of many chunks.
    /// </summary>
    /// <param name="path">A tabular file.</param>
    /// <remarks>
    /// The rewrite is done once per input and per build, on the first call, which the harness's
    /// warm-up rounds absorb; every call after it opens the file cold and seeks, so the run cache is
    /// empty each time -- the lookup a fresh reader pays.
    /// </remarks>
    public static async Task<long> LookupSortedRuns(string path)
    {
        if (!Lookups.TryGetValue(path, out (byte[] Bytes, string Column, FilterLiteral[] Probes) prepared))
        {
            prepared = await PrepareLookupAsync(path);
            Lookups.TryAdd(path, prepared);
        }

        await using VortexFile file = await VortexFile.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(prepared.Bytes), new VortexOpenOptions(), CancellationToken.None);
        await using KeyCursor cursor = await file.Keys(prepared.Column)
            .WithSource(KeySourceKind.SortedRuns).OpenAsync(CancellationToken.None);
        long found = 0;
        foreach (FilterLiteral probe in prepared.Probes)
        {
            found += await cursor.SeekAsync(probe, SeekOp.Exact, CancellationToken.None) ? 1 : 0;
        }

        return found;
    }

    /// <exception cref="NotSupportedException">The file's root is not a struct of columns.</exception>
    private static async Task<(byte[] Bytes, string Column, FilterLiteral[] Probes)> PrepareLookupAsync(string path)
    {
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        if (source.Schema.Kind != DTypeKind.Struct)
        {
            throw new NotSupportedException(
                $"lookup-sorted-runs keys a named column, and this file's root is {source.Schema.Kind}");
        }

        string column = string.Empty;
        for (int i = 0; i < source.Schema.FieldCount && column.Length == 0; i++)
        {
            string name = source.Schema.GetFieldName(i);
            if (name == "measure" || name == Field)
            {
                column = name;
            }
        }

        if (column.Length == 0)
        {
            column = source.Schema.GetFieldName(0);
        }

        System.IO.MemoryStream written = new System.IO.MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions
        {
            Indexes = WritePolicy.None.For(column, IndexPolicy.SortedRuns),
            IndexBudgetPerMille = 1_000_000,
            DataBlockTargetBytes = 64 << 10,
        };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(written), source.Schema, options))
        {
            await foreach (RecordBatch batch in source.Scan().ExecuteAsync().WithCancellation(CancellationToken.None))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        byte[] bytes = written.ToArray();
        await using VortexFile file = await VortexFile.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(bytes), new VortexOpenOptions(), CancellationToken.None);
        await using KeyCursor cursor = await file.Keys(column)
            .WithSource(KeySourceKind.SortedRuns).OpenAsync(CancellationToken.None);
        long entries = cursor.EntryCount ?? file.RowCount;
        long stride = Math.Max(1, entries / LookupProbes);
        FilterLiteral[] probes = new FilterLiteral[LookupProbes];
        for (int i = 0; i < LookupProbes; i++)
        {
            await cursor.SeekRankAsync(Math.Min(entries - 1, i * stride), CancellationToken.None);
            probes[i] = cursor.Key;
        }

        return (bytes, column, probes);
    }

    /// <summary>Every row of every column, canonicalized.</summary>
    /// <param name="path">The file.</param>
    public static async Task<long> ScanAll(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Every row, canonicalized, with <paramref name="degree"/> lanes in flight.</summary>
    /// <param name="path">The file.</param>
    /// <param name="degree">Lanes; 1 is the default scan.</param>
    /// <remarks>
    /// `WithDegreeOfParallelism` existed and nothing measured it (BENCH-AUDIT.md D2). A lane is a
    /// split decoded on the thread pool while another is being consumed, so the interesting number
    /// is not the speed-up alone but where it stops.
    /// </remarks>
    public static async Task<long> ScanAllLanes(string path, int degree)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().WithDegreeOfParallelism(degree)
            .ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>One column of five.</summary>
    /// <param name="path">The file.</param>
    public static async Task<long> ScanProjected(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project(Field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>One named column, whichever file it is in.</summary>
    /// <param name="path">The file.</param>
    /// <param name="field">The column to keep.</param>
    public static async Task<long> ScanProjectedField(string path, string field)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project(field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Rows spread through the file, one every <paramref name="stride"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="stride">The gap between them; the row taken is the middle of each gap.</param>
    /// <remarks>
    /// THE INDICES PAST THE END ARE DROPPED, NOT SQUEEZED IN, and BENCH-AUDIT.md B16 is why. The
    /// stride assumed every file in the 1M corpus had a million rows -- `table_wide` has 50 000, so
    /// the fourth index landed at 54 687, `RowSelection.Create` threw, and an UNCAUGHT throw ends
    /// the process: every encoding after it went unmeasured, and nothing said so.
    /// <para>
    /// B16 proposed clamping the stride to `rows / count`. That would have been wrong, and the
    /// reference says why: `vxbench_take` builds the SAME strided indices and filters
    /// `row &lt; rows_in_file` (tools/vxbench-rs/src/lib.rs). On `table_wide` it therefore takes
    /// THREE rows, not sixty-four. Clamping would have had this side take sixty-four spread over
    /// 50 000 rows while the reference took three -- a ratio between two different amounts of work,
    /// which is worse than the exception because it would have looked like a number.
    /// </para>
    /// </remarks>
    public static async Task<long> ScatteredTake(string path, long count, long stride)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

        int wanted = 0;
        long[] indices = new long[count];
        for (int i = 0; i < count; i++)
        {
            long row = (i * stride) + (stride / 2);
            if (row < file.RowCount)
            {
                indices[wanted++] = row;
            }
        }

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Take(indices.AsSpan(0, wanted).ToArray()).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>A half-open band on <see cref="Field"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="low">The band's low edge, inclusive.</param>
    /// <param name="width">Its width.</param>
    public static async Task<long> FilteredScan(string path, long low, long width)
    {
        VortexExpr band = Expr.And(
            Expr.Ge(Expr.Field(Field), Expr.Literal(FilterLiteral.From(low))),
            Expr.Lt(Expr.Field(Field), Expr.Literal(FilterLiteral.From(low + width))));

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Where(band).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>
    /// One live block in every chunk of a million-row file: the scan shape in which a chunk holds
    /// dead blocks and is read through the selection path.
    /// </summary>
    /// <param name="path">Only the key under which the file is kept; the file itself is built here.</param>
    /// <remarks>
    /// <para>
    /// The only scenario that builds its own input, because no corpus file has the shape and the
    /// path it exercises is otherwise unmeasured: a chunk with dead blocks in it goes through
    /// <c>ChunkedLayoutReader.ExecuteChunkLive</c>, and neither the throughput axis, nor the take
    /// axis, nor any other scenario here reaches that branch. Passing a different file changes
    /// nothing, which is why the argument is named for what it does.
    /// </para>
    /// <para>
    /// The predicate is a membership of one value per chunk rather than a band, and that is the
    /// whole design. A band leaves exactly two chunks partly live however wide it is -- the two it
    /// starts and ends in -- so it exercises the path twice per scan whatever the file's size. One
    /// value per chunk leaves every chunk partly live, which is the shape that shows what the path
    /// costs.
    /// </para>
    /// <para>
    /// The column is zstd so its decoder cannot take rows for itself, and the blocks are
    /// <see cref="PrunedBlock"/> rows in chunks of <see cref="PrunedBlocksPerChunk"/>, so each
    /// touched chunk decodes eight blocks and keeps one.
    /// </para>
    /// </remarks>
    public static async Task<long> FilteredPruned(string path)
    {
        if (!PrunedFiles.TryGetValue(path, out byte[]? bytes))
        {
            bytes = await WritePrunedAsync();
            PrunedFiles.TryAdd(path, bytes);
        }

        await using VortexFile file = await VortexFile.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(bytes), new VortexOpenOptions(), CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Where(Expr.In(Expr.Field(PrunedField), PrunedNeedles))
            .WithPruning(true).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>The built file, per key: written on the first call and kept for the process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> PrunedFiles = new();

    /// <summary>The column <see cref="FilteredPruned"/> writes and filters.</summary>
    public const string PrunedField = "v";

    /// <summary>Rows of the file <see cref="FilteredPruned"/> builds.</summary>
    public const int PrunedRows = 1_000_000;

    /// <summary>Rows per block, and so per zone and per split, in that file.</summary>
    public const int PrunedBlock = 1_024;

    /// <summary>Blocks per chunk in that file.</summary>
    public const int PrunedBlocksPerChunk = 8;

    /// <summary>
    /// Columns of that file: one the membership tests and nine of payload, because what a take out
    /// of a retained chunk copies is every column of the rows it names, not just the filtered one.
    /// </summary>
    public const int PrunedColumns = 10;

    /// <summary>One value per chunk, each in that chunk's first block: what the membership asks for.</summary>
    private static readonly FilterLiteral[] PrunedNeedles = BuildNeedles();

    private static FilterLiteral[] BuildNeedles()
    {
        const int chunk = PrunedBlock * PrunedBlocksPerChunk;
        int chunks = (PrunedRows + chunk - 1) / chunk;
        FilterLiteral[] needles = new FilterLiteral[chunks];
        for (int i = 0; i < chunks; i++)
        {
            long row = ((long)i * chunk) + (PrunedBlock / 2);
            needles[i] = FilterLiteral.From(row + (row % 3));
        }

        return needles;
    }

    private static async Task<byte[]> WritePrunedAsync()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[PrunedColumns];
        DType[] fields = new DType[PrunedColumns];
        Dictionary<string, VortexEncodingHint> hints = new Dictionary<string, VortexEncodingHint>();
        for (int c = 0; c < PrunedColumns; c++)
        {
            names[c] = c == 0 ? PrunedField : "p" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[c] = i64;

            // Zstd because its decoder cannot take rows without decoding the node: the chunk is
            // decoded once, retained, and the batch's rows come out of it. An encoding that selects
            // for itself -- bit-packing, which is what the cascade picks for a monotone column left
            // to itself -- never reaches that path and would measure the wrong branch.
            hints[names[c]] = VortexEncodingHint.Zstd;
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        System.IO.MemoryStream written = new System.IO.MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = PrunedBlock,
            DataBlockTargetBytes = (long)PrunedBlock * PrunedBlocksPerChunk * PrunedColumns * sizeof(long),
            EncodingHints = hints,
        };

        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(written), schema, options))
        {
            const int batch = PrunedBlock * PrunedBlocksPerChunk;
            for (int start = 0; start < PrunedRows; start += batch)
            {
                int rows = Math.Min(batch, PrunedRows - start);
                Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
                int[] columns = new int[PrunedColumns];
                for (int c = 0; c < PrunedColumns; c++)
                {
                    Vorticity.Buffers.VortexBuffer values =
                        arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> destination);
                    Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
                    for (int i = 0; i < rows; i++)
                    {
                        // Monotone but not a progression, so every zone map holds a range the
                        // membership can be answered against; the payload columns carry the same
                        // shape so the decode they cost is the decode a real table costs.
                        long row = start + i;
                        longs[i] = row + (row % 3);
                    }

                    columns[c] = arena.AddPrimitive(
                        i64, rows, Vorticity.Arrays.Validity.NonNullable, PType.I64, values);
                }

                int root = arena.AddStruct(
                    schema, rows, Vorticity.Arrays.Validity.NonNullable, columns);
                using RecordBatch record = new RecordBatch(arena, root, start);
                await writer.WriteAsync(record, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        return written.ToArray();
    }

    /// <summary>The file read back out to a sink that keeps nothing.</summary>
    /// <param name="path">The file.</param>
    /// <remarks>
    /// The read is inside the measurement on both sides and is therefore common-mode, but it is not
    /// small: subtract the scan axis before reading the quotient as a statement about writers.
    /// </remarks>
    public static Task<long> ReadAndWrite(string path) => ReadAndWrite(path, null);

    /// <summary>
    /// The write-back with one index kind asked for on every column, which no default does: the
    /// builders' own cost, above <see cref="ReadAndWrite(string)"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="index">The index every column gets.</param>
    /// <remarks>The budget is lifted: the axis times the builder, not whether its index pays.</remarks>
    public static Task<long> ReadAndWriteIndexed(string path, IndexPolicy index) =>
        ReadAndWrite(
            path,
            new VortexWriteOptions { Indexes = WritePolicy.None.WithDefault(index), IndexBudgetPerMille = 1_000_000 });

    private static async Task<long> ReadAndWrite(string path, VortexWriteOptions? options)
    {
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        await using VortexFileWriter writer = options is null
            ? VortexFileWriter.Create(new DiscardSink(), source.Schema)
            : VortexFileWriter.Create(new DiscardSink(), source.Schema, options);

        long rows = 0;
        await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return rows;
    }

    /// <summary>A sink that counts bytes and keeps none of them.</summary>
    /// <remarks>
    /// A write benchmark that measures the filesystem measures the filesystem, and `vxbench_write`
    /// writes into a `Vec&lt;u8&gt;` for the same reason.
    /// </remarks>
    public sealed class DiscardSink : ISegmentSink
    {
        /// <summary>Bytes written so far.</summary>
        public long Position { get; private set; }

        /// <inheritdoc/>
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
