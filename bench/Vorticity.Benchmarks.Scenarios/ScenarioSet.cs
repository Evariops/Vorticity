// The scenarios themselves, in their own assembly so that two builds of them can meet.
//
// The repository's rule is "one process, one clock", and its only realisation was copying an old
// loop into the bench by hand -- impossible for a change that spans a layout reader or the arena,
// so those were judged across two runs, which is exactly what the rule forbids.
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
using Vorticity.Scanning;
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

    /// <summary>Rows an append adds: a tenth of the report's larger table, as its append adds.</summary>
    public const long AppendRows = 1_048_576;

    /// <summary>The scenario names this assembly answers to.</summary>
    public static string[] Names =>
        ["fullscan", "projected", "projected-wide", "take", "filtered", "filtered-pruned", "write", "write-smallest", "write-bloom", "write-postings", "write-sorted-runs", "lookup-sorted-runs", "append"];

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
        "write-smallest" => p => ReadAndWrite(p, new VortexWriteOptions { Compression = CompressionProfile.Smallest }),
        "write-bloom" => p => ReadAndWriteIndexed(p, IndexSpec.Bloom()),
        "write-postings" => p => ReadAndWriteIndexed(p, IndexSpec.Postings),
        "write-sorted-runs" => p => ReadAndWriteIndexed(p, IndexSpec.SortedRuns),
        "lookup-sorted-runs" => LookupSortedRuns,
        "prune-in" => PruneIn,
        "append" => p => Append(p, AppendRows),
        _ => null,
    };

    /// <summary>Rows the published report's scattered take asks for.</summary>
    public const long ReportTakeCount = 1_000;

    /// <summary>The scenario names the published report runs, in the order of its table.</summary>
    public static string[] ReportNames =>
        ["open", "scan", "project", "filter-narrow", "filter-wide", "take", "write", "append"];

    /// <summary>
    /// The scenario <paramref name="name"/> names in the published report, on a fixture of
    /// <paramref name="rows"/> rows.
    /// </summary>
    /// <param name="name">One of <see cref="ReportNames"/>.</param>
    /// <param name="rows">The fixture's row count, which sizes the bands, the take and the append.</param>
    /// <returns>The scenario, or <see langword="null"/> when the name is unknown.</returns>
    /// <remarks>
    /// One table for the two processes the report times on our side, the Native AOT runner and the
    /// framework-dependent host: a scenario defined in one of them and copied into the other would
    /// be two scenarios with one name.
    /// </remarks>
    public static Func<string, Task<long>>? ForReport(string name, long rows) => name switch
    {
        "open" => FooterOnly,
        "scan" => ScanAll,
        "project" => p => ScanProjectedField(p, Field),
        "filter-narrow" => p => FilteredScan(p, BandLow, rows / 100),
        "filter-wide" => p => FilteredScan(p, BandLow, rows / 2),
        "take" => p => ScatteredTake(p, ReportTakeCount, rows / ReportTakeCount),
        "write" => ReadAndWrite,
        "append" => p => Append(p, rows / 10),
        _ => null,
    };

    /// <summary>
    /// Takes <c>--threads &lt;n&gt;</c> or <c>--threads all</c> out of <paramref name="args"/> and
    /// gives every scan that many lanes, one per processor for <c>all</c>: the reader's counterpart
    /// of the reference's multi-threaded runtime, and the write-back's writer as many threads.
    /// Absent, both keep the library's default of one.
    /// </summary>
    /// <param name="args">The arguments, the option anywhere among them.</param>
    /// <returns>The arguments without the option, and the lanes every scan now has.</returns>
    /// <exception cref="ArgumentException">The option names no positive count.</exception>
    public static (string[] Remaining, int Threads) TakeThreads(string[] args)
    {
        int at = Array.IndexOf(args, "--threads");
        if (at < 0)
        {
            return (args, ScanBuilder.DefaultDegreeOfParallelism);
        }

        string? value = at + 1 < args.Length ? args[at + 1] : null;
        int threads = value == "all"
            ? Environment.ProcessorCount
            : int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int count) && count > 0
                ? count
                : throw new ArgumentException($"--threads: '{value}' is neither a positive count nor 'all'.", nameof(args));
        ScanBuilder.DefaultDegreeOfParallelism = threads;
        return ([.. args[..at], .. args[(at + 2)..]], threads);
    }

    /// <summary>The session every scenario opens a file from a path through; null for the library's default.</summary>
    /// <remarks>
    /// <para>
    /// EVERY HARNESS THAT COMPARES SETS ONE THAT KEEPS NO MAPPING, `MappedFileCacheCount = 0`, and
    /// the comparisons against Rust depend on it. The default session keeps the mapping of a file it
    /// closed and hands it to the next open of the same file, pages already mapped. Every instrument
    /// opens its file anew on every round, and the reference maps it anew each time, so a kept
    /// mapping made each round after the first cheaper on our side alone: three and a half times
    /// on the million-row table, measured the day it was found.
    /// </para>
    /// <para>
    /// Not set here, because the option is younger than these scenarios and `bench/ab.sh` builds
    /// this assembly against older libraries: there both sides open through the default, alike.
    /// </para>
    /// </remarks>
    public static VortexSession? Session { get; set; }

    /// <summary>Opens <paramref name="path"/> through <see cref="Session"/>.</summary>
    /// <param name="path">The file.</param>
    public static ValueTask<VortexFile> OpenAsync(string path) =>
        Session is { } session
            ? session.OpenAsync(path, options: null, CancellationToken.None)
            : VortexFile.OpenAsync(path, CancellationToken.None);

    /// <summary>What an open costs on its own: the footer, and no row read.</summary>
    /// <param name="path">The file.</param>
    public static async Task<long> FooterOnly(string path)
    {
        await using VortexFile file = await OpenAsync(path);
        return file.RowCount;
    }

    /// <summary>
    /// Appends the first <paramref name="rows"/> rows of the file to a copy of it, so the scenario
    /// can run again on the same fixture.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="rows">How many of its rows to append.</param>
    public static async Task<long> Append(string path, long rows)
    {
        string copy = path + ".append";
        System.IO.File.Copy(path, copy, overwrite: true);
        try
        {
            await using VortexFileWriter appender =
                await VortexFileWriter.AppendAsync(copy, options: null, CancellationToken.None);
            long start = appender.RowCount;
            await using VortexFile source = await OpenAsync(path);
            long written = 0;
            await foreach (RecordBatch batch in source.ScanBuilder()
                .Rows(new RowRange(0, rows)).ExecuteAsync().WithCancellation(CancellationToken.None))
            {
                using (batch)
                {
                    await appender.WriteAsync(batch, CancellationToken.None);
                    written += batch.RowCount;
                }
            }

            WriteReport report = await appender.CompleteAsync(CancellationToken.None);
            return report.RowCount - start == 0 ? written : report.RowCount;
        }
        finally
        {
            System.IO.File.Delete(copy);
        }
    }

    /// <summary>
    /// Plans an <c>IN</c> of <see cref="LookupProbes"/> keys of the column against that same file:
    /// what the locating index costs to ANSWER a filter, where `lookup-sorted-runs` measures what
    /// it costs to seek one key at a time.
    /// </summary>
    /// <param name="path">A tabular file.</param>
    /// <returns>The blocks the plan leaves live.</returns>
    /// <remarks>
    /// THE QUESTION IS ASKED PER BLOCK, and there are as many literals as probes, so this is the
    /// scenario that catches a per-block cost that grows with the filter -- one was once found
    /// worth half the plan. The probes are keys the column holds, so the index cannot prune them
    /// away and every block is asked about every literal.
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
        ScanExplanation plan = await file.ScanBuilder()
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
            found += await cursor.SeekAsync(probe, SeekMode.Exact, CancellationToken.None) ? 1 : 0;
        }

        return found;
    }

    /// <exception cref="NotSupportedException">The file's root is not a struct of columns.</exception>
    private static async Task<(byte[] Bytes, string Column, FilterLiteral[] Probes)> PrepareLookupAsync(string path)
    {
        await using VortexFile source = await OpenAsync(path);
        if (source.DType.Kind != DTypeKind.Struct)
        {
            throw new NotSupportedException(
                $"lookup-sorted-runs keys a named column, and this file's root is {source.DType.Kind}");
        }

        string column = string.Empty;
        for (int i = 0; i < source.DType.FieldCount && column.Length == 0; i++)
        {
            string name = source.DType.GetFieldName(i);
            if (name == "measure" || name == Field)
            {
                column = name;
            }
        }

        if (column.Length == 0)
        {
            column = source.DType.GetFieldName(0);
        }

        System.IO.MemoryStream written = new System.IO.MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions
        {
            WritePolicy = WritePolicy.None.For(column, IndexSpec.SortedRuns),
            IndexBudgetPerMille = 1_000_000,
            DataBlockTargetBytes = 64 << 10,
        };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(written), source.DType, options))
        {
            await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
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
        await using VortexFile file = await OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
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
    /// `WithDegreeOfParallelism` existed and nothing measured it. A lane is a
    /// split decoded on the thread pool while another is being consumed, so the interesting number
    /// is not the speed-up alone but where it stops.
    /// </remarks>
    public static async Task<long> ScanAllLanes(string path, int degree)
    {
        await using VortexFile file = await OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().WithDegreeOfParallelism(degree)
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
        await using VortexFile file = await OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project(Field).ExecuteAsync()
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
        await using VortexFile file = await OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project(field).ExecuteAsync()
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
    /// THE INDICES PAST THE END ARE DROPPED, NOT SQUEEZED IN. The stride assumed every file in the
    /// 1M corpus had a million rows -- `table_wide` has 50 000, so the fourth index landed at
    /// 54 687, `RowSelection.Create` threw, and an UNCAUGHT throw ends the process: every encoding
    /// after it went unmeasured, and nothing said so.
    /// <para>
    /// Clamping the stride to `rows / count` looks like the fix. It would have been wrong, and the
    /// reference says why: `vxbench_take` builds the SAME strided indices and filters
    /// `row &lt; rows_in_file` (tools/vxbench-rs/src/lib.rs). On `table_wide` it therefore takes
    /// THREE rows, not sixty-four. Clamping would have had this side take sixty-four spread over
    /// 50 000 rows while the reference took three -- a ratio between two different amounts of work,
    /// which is worse than the exception because it would have looked like a number.
    /// </para>
    /// </remarks>
    public static async Task<long> ScatteredTake(string path, long count, long stride)
    {
        await using VortexFile file = await OpenAsync(path);

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
        await foreach (RecordBatch batch in file.ScanBuilder()
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

        await using VortexFile file = await OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(band).ExecuteAsync()
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
        await foreach (RecordBatch batch in file.ScanBuilder().Where(Expr.In(Expr.Field(PrunedField), PrunedNeedles))
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
        Dictionary<string, EncodingHint> hints = new Dictionary<string, EncodingHint>();
        for (int c = 0; c < PrunedColumns; c++)
        {
            names[c] = c == 0 ? PrunedField : "p" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[c] = i64;

            // Zstd because its decoder cannot take rows without decoding the node: the chunk is
            // decoded once, retained, and the batch's rows come out of it. An encoding that selects
            // for itself -- bit-packing, which is what the cascade picks for a monotone column left
            // to itself -- never reaches that path and would measure the wrong branch.
            hints[names[c]] = EncodingHint.Zstd;
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
    internal static Task<long> ReadAndWriteIndexed(string path, IndexSpec index) =>
        ReadAndWrite(
            path,
            new VortexWriteOptions { WritePolicy = WritePolicy.None.WithDefault(index), IndexBudgetPerMille = 1_000_000 });

    private static Task<long> ReadAndWrite(string path, VortexWriteOptions? options) =>
        ReadAndWrite(path, options, new DiscardSink());

    /// <summary>The bytes the write-back of <paramref name="path"/> produces: what its time bought.</summary>
    /// <param name="path">The file.</param>
    /// <remarks>
    /// Not a timed scenario. A writer can be fast by compressing less, and a ratio of write times
    /// read alone would not say so; the per-encoding write table prints these beside its times.
    /// </remarks>
    public static async Task<long> WrittenBytes(string path)
    {
        DiscardSink sink = new DiscardSink();
        await ReadAndWrite(path, null, sink);
        return sink.Position;
    }

    private static async Task<long> ReadAndWrite(string path, VortexWriteOptions? options, DiscardSink sink)
    {
        // The writer compresses on as many threads as the scan reads on.
        await using VortexFile source = await OpenAsync(path);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            sink,
            source.DType,
            (options ?? new VortexWriteOptions()) with { DegreeOfParallelism = ScanBuilder.DefaultDegreeOfParallelism });

        long rows = 0;
        await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync()
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
    /// A write benchmark that measures where the bytes land measures the filesystem, or the
    /// allocator. `vxbench_write` hands the reference's writer the same sink, its own `DiscardSink`:
    /// it used to write into a `Vec&lt;u8&gt;` that grew to hold the whole file, a cost charged to
    /// the reference alone.
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
