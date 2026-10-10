// The format's cost in one engine (docs/design/17-parquet.md §10).
//
// The published report times our reader against the reference's on Vortex files. This asks the
// other question: what the format costs when the engine is the same. One table, the report's, is
// written once as Vortex and once as Parquet by this repository's writers, and the report's actions
// run on both through the one `Scan` the two files hand out, in one process, warm: only the open and
// the write-back differ, each format's own. The ratio informs; it gates nothing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.Parquet;
using Vorticity.Writing;

using Set = Vorticity.Bench.Scenarios.ScenarioSet;

namespace Vorticity.Benchmarks;

/// <summary>The report's actions on the same table as Vortex and as Parquet, read by the same engine.</summary>
internal static class FormatCost
{
    /// <summary>The actions, in the order of the report's table; <c>append</c> is Vortex's alone.</summary>
    private static readonly string[] Actions = ["open", "scan", "project", "filter-narrow", "filter-wide", "take", "write"];

    /// <summary>The batches a scan decodes ahead, when <c>--prefetch</c> sets it; the scan's default otherwise.</summary>
    private static int? Prefetch;

    /// <summary>The column a projection keeps: the report's monotone one, or a given file's first.</summary>
    private static string Projected = Set.Field;

    internal static async Task<int> RunAsync(string[] args)
    {
        int rows = Count(args, "--rows", 1 << 20);
        int runs = Count(args, "--runs", 5);
        bool keep = Array.IndexOf(args, "--keep") >= 0;
        int prefetchAt = Array.IndexOf(args, "--prefetch");
        Prefetch = prefetchAt >= 0 && prefetchAt + 1 < args.Length && int.TryParse(args[prefetchAt + 1], CultureInfo.InvariantCulture, out int ahead) ? ahead : null;
        string directory = Path.Combine(Path.GetTempPath(), $"vx-format-cost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // The report's session: no mapping kept past a close, so every open maps its file anew.
            await using VortexSession session = VortexSession.Create(options => options.MappedFileCacheCount = 0);
            // The report's table, written as Vortex then rewritten as Parquet; or, with --file, a
            // Parquet file of another writer as it is, rewritten as Vortex by ours.
            int fileAt = Array.IndexOf(args, "--file");
            string vortex = Path.Combine(directory, "table.vortex");
            string parquet = fileAt >= 0 && fileAt + 1 < args.Length ? Path.GetFullPath(args[fileAt + 1]) : Path.Combine(directory, "table.parquet");
            string[] actions = Actions;
            if (fileAt >= 0)
            {
                // A loop of the Parquet file alone has no use for its Vortex rewrite, which a trace would count.
                int looping = Array.IndexOf(args, "--loop");
                bool parquetAlone = looping >= 0 && looping + 1 < args.Length && string.Equals(args[looping + 1], "parquet", StringComparison.OrdinalIgnoreCase);
                await using ParquetFile given = await session.OpenParquetAsync(parquet, null, CancellationToken.None).ConfigureAwait(false);
                rows = parquetAlone ? checked((int)given.RowCount) : await RewriteAsVortexAsync(session, parquet, vortex).ConfigureAwait(false);
                Projected = given.Schema[0].Name;
                actions = given.Schema.Any(f => f.Name == Set.Field) ? Actions : [.. Actions.Where(a => !a.StartsWith("filter", StringComparison.Ordinal))];
            }
            else
            {
                await Report.WriteFixtureAsync(vortex, rows).ConfigureAwait(false);
                await RewriteAsync(session, vortex, parquet).ConfigureAwait(false);
            }
            Format[] formats =
            [
                new("Vortex", vortex, async path => new VortexSide(await session.OpenAsync(path, options: null, CancellationToken.None).ConfigureAwait(false))),
                new("Parquet", parquet, async path => new ParquetSide(session, await session.OpenParquetAsync(path, null, CancellationToken.None).ConfigureAwait(false))),
            ];

            // One action of one format in a bare loop, for dotnet-trace: --loop <format> <action> [seconds].
            int loop = Array.IndexOf(args, "--loop");
            if (loop >= 0 && loop + 2 < args.Length)
            {
                Format looped = formats.Single(f => string.Equals(f.Name, args[loop + 1], StringComparison.OrdinalIgnoreCase));
                double seconds = loop + 3 < args.Length && double.TryParse(args[loop + 3], CultureInfo.InvariantCulture, out double given) ? given : 10;
                long calls = 0;
                Stopwatch clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    await RunAsync(looped, args[loop + 2], rows, Count(args, "--degree", 1)).ConfigureAwait(false);
                    calls++;
                }

                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{calls} calls, {clock.Elapsed.TotalMilliseconds / calls:F3} ms a call"));
                return 0;
            }

            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"FORMAT COST, {rows:N0} rows: Vortex {new FileInfo(vortex).Length:N0} bytes, Parquet {new FileInfo(parquet).Length:N0}; median of {runs} runs, warm"));
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{"action",-14} {"cores",-6} {"Vortex ms",10} {"Parquet ms",11} {"ratio",7} {"Vortex alloc",13} {"Parquet alloc",14}"));
            foreach (int degree in (int[])[1, Environment.ProcessorCount])
            {
                foreach (string action in actions)
                {
                    Measure[] measures = new Measure[formats.Length];
                    for (int f = 0; f < formats.Length; f++)
                    {
                        measures[f] = await MeasureAsync(formats[f], action, rows, degree, runs).ConfigureAwait(false);
                    }

                    if (measures.Any(m => m.Rows != measures[0].Rows))
                    {
                        throw new InvalidOperationException(
                            $"{action} returned {string.Join(" and ", measures.Select((m, f) => $"{m.Rows} rows from {formats[f].Name}"))}.");
                    }

                    Line(action, degree, measures);
                }
            }

            // Each column alone, on one core: where a scan's difference lies.
            if (Array.IndexOf(args, "--columns") >= 0)
            {
                VortexSchema schema;
                await using (Side opened = await formats[0].OpenAsync(formats[0].Path).ConfigureAwait(false))
                {
                    schema = opened.Schema;
                }

                foreach (VortexField field in schema)
                {
                    Measure[] measures = new Measure[formats.Length];
                    for (int f = 0; f < formats.Length; f++)
                    {
                        measures[f] = await MeasureAsync(formats[f], "column:" + field.Name, rows, 1, runs).ConfigureAwait(false);
                    }

                    Line(field.Name, 1, measures);
                }
            }

            return 0;
        }
        finally
        {
            if (keep)
            {
                Console.Out.WriteLine($"files kept in {directory}");
            }
            else
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>A line of the table: Vortex's time, Parquet's, their ratio, and what each allocated.</summary>
    private static void Line(string label, int degree, Measure[] measures) =>
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label,-14} {degree,-6} {measures[0].Milliseconds,10:F3} {measures[1].Milliseconds,11:F3} {measures[1].Milliseconds / measures[0].Milliseconds,7:F2} {measures[0].Allocated,13:N0} {measures[1].Allocated,14:N0}"));

    /// <summary>The report's table written again as Parquet, by this repository's writer, with its defaults.</summary>
    private static async Task RewriteAsync(VortexSession session, string vortex, string parquet)
    {
        await using VortexFile source = await session.OpenAsync(vortex, options: null, CancellationToken.None).ConfigureAwait(false);
        await using ParquetFileWriter writer = session.CreateParquetWriter(parquet, source.Schema);
        await foreach (RecordBatch batch in source.Scan().ToBatchesAsync().ConfigureAwait(false))
        {
            using (batch)
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>A Parquet file rewritten as Vortex by this repository's writer, with its defaults; its rows.</summary>
    private static async Task<int> RewriteAsVortexAsync(VortexSession session, string parquet, string vortex)
    {
        await using ParquetFile source = await session.OpenParquetAsync(parquet, null, CancellationToken.None).ConfigureAwait(false);
        await using VortexFileWriter writer = session.CreateWriter(vortex, source.Schema);
        await foreach (RecordBatch batch in source.Scan().ToBatchesAsync().ConfigureAwait(false))
        {
            using (batch)
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
        return checked((int)source.RowCount);
    }

    /// <summary>
    /// The median time and allocation of <paramref name="runs"/> calls, after a second and 32 calls
    /// at least that warm: past the calls after which the JIT recompiles a method optimized, and the
    /// delay it waits before it does, which two calls are not.
    /// </summary>
    private static async Task<Measure> MeasureAsync(Format format, string action, int rows, int degree, int runs)
    {
        long returned = 0;
        Stopwatch warming = Stopwatch.StartNew();
        for (int warm = 0; warm < 32 || warming.ElapsedMilliseconds < 1_000; warm++)
        {
            returned = await RunAsync(format, action, rows, degree).ConfigureAwait(false);
        }

        double[] times = new double[runs];
        long[] allocated = new long[runs];
        for (int run = 0; run < runs; run++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            returned = await RunAsync(format, action, rows, degree).ConfigureAwait(false);
            times[run] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[run] = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        Array.Sort(times);
        Array.Sort(allocated);
        return new Measure(returned, times[runs / 2], allocated[runs / 2]);
    }

    /// <summary>One call of <paramref name="action"/> on the format's file, from its open to its last row.</summary>
    private static async Task<long> RunAsync(Format format, string action, int rows, int degree)
    {
        ScanOptions options = Prefetch is { } prefetch ? new() { DegreeOfParallelism = degree, Prefetch = prefetch } : new() { DegreeOfParallelism = degree };
        await using Side file = await format.OpenAsync(format.Path).ConfigureAwait(false);
        return action switch
        {
            "open" => file.RowCount,
            "scan" => await CountAsync(file.Scan().With(options)).ConfigureAwait(false),
            "project" => await CountAsync(file.Scan(Projected).With(options)).ConfigureAwait(false),
            "filter-narrow" => await CountAsync(file.Scan().Where(Band(Set.BandLow, rows / 100)).With(options)).ConfigureAwait(false),
            "filter-wide" => await CountAsync(file.Scan().Where(Band(Set.BandLow, rows / 2)).With(options)).ConfigureAwait(false),
            "take" => await CountAsync(file.Scan().Rows(Spread(file.RowCount, Set.ReportTakeCount, rows / Set.ReportTakeCount)).With(options)).ConfigureAwait(false),
            "write" => await file.WriteBackAsync(options, degree).ConfigureAwait(false),
            _ when action.StartsWith("column:", StringComparison.Ordinal) => await CountAsync(file.Scan(action["column:".Length..]).With(options)).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not one of the report's actions."),
        };
    }

    private static async Task<long> CountAsync(Scan scan)
    {
        long rows = 0;
        await foreach (BatchView batch in scan)
        {
            rows += batch.Selection.Count;
        }

        return rows;
    }

    /// <summary>The half-open band on the monotone column the report's filters ask for.</summary>
    private static VortexExpr Band(long low, long width) => Expr.And(
        Expr.Ge(Expr.Field(Set.Field), Expr.Literal(FilterLiteral.From(low))),
        Expr.Lt(Expr.Field(Set.Field), Expr.Literal(FilterLiteral.From(low + width))));

    /// <summary>The report's take: the middle row of each of <paramref name="count"/> gaps of <paramref name="stride"/>.</summary>
    private static long[] Spread(long rowCount, long count, long stride)
    {
        List<long> indices = [];
        for (long i = 0; i < count; i++)
        {
            long row = (i * stride) + (stride / 2);
            if (row < rowCount)
            {
                indices.Add(row);
            }
        }

        return [.. indices];
    }

    private static int Count(string[] args, string flag, int fallback)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : fallback;
    }

    private sealed record Measure(long Rows, double Milliseconds, long Allocated);

    private sealed record Format(string Name, string Path, Func<string, Task<Side>> OpenAsync);

    /// <summary>An open file of either format, seen through what the actions ask of it.</summary>
    private abstract class Side : IAsyncDisposable
    {
        internal abstract long RowCount { get; }

        internal abstract VortexSchema Schema { get; }

        internal abstract Scan Scan(params ReadOnlySpan<string> columns);

        /// <summary>Every row read and written by the format's own writer to a sink that keeps nothing.</summary>
        internal abstract Task<long> WriteBackAsync(ScanOptions options, int degree);

        public abstract ValueTask DisposeAsync();
    }

    private sealed class VortexSide(VortexFile file) : Side
    {
        internal override long RowCount => file.RowCount;

        internal override VortexSchema Schema => file.Schema;

        internal override Scan Scan(params ReadOnlySpan<string> columns) => file.Scan(columns);

        internal override async Task<long> WriteBackAsync(ScanOptions options, int degree)
        {
            await using VortexFileWriter writer = VortexFileWriter.Create(
                new Set.DiscardSink(), file.DType, new VortexWriteOptions { DegreeOfParallelism = degree });
            long rows = 0;
            await foreach (RecordBatch batch in file.Scan().With(options).ToBatchesAsync().ConfigureAwait(false))
            {
                using (batch)
                {
                    rows += batch.RowCount;
                    await writer.WriteAsync(batch).ConfigureAwait(false);
                }
            }

            await writer.CompleteAsync().ConfigureAwait(false);
            return rows;
        }

        public override ValueTask DisposeAsync() => file.DisposeAsync();
    }

    private sealed class ParquetSide(VortexSession session, ParquetFile file) : Side
    {
        internal override long RowCount => file.RowCount;

        internal override VortexSchema Schema => file.Schema;

        internal override Scan Scan(params ReadOnlySpan<string> columns) => file.Scan(columns);

        internal override async Task<long> WriteBackAsync(ScanOptions options, int degree)
        {
            await using ParquetFileWriter writer = session.CreateParquetWriter(
                new DiscardingPipe(), file.Schema, new ParquetWriteOptions { DegreeOfParallelism = degree });
            long rows = 0;
            await foreach (RecordBatch batch in file.Scan().With(options).ToBatchesAsync().ConfigureAwait(false))
            {
                using (batch)
                {
                    rows += batch.RowCount;
                    await writer.WriteAsync(batch).ConfigureAwait(false);
                }
            }

            await writer.CompleteAsync().ConfigureAwait(false);
            return rows;
        }

        public override ValueTask DisposeAsync() => file.DisposeAsync();
    }
}
