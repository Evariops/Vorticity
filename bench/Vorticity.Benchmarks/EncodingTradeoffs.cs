using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// What each compression profile and hint costs on one column of a given nature, cardinality and
/// volume: the bytes written, the write, a full decode and a scattered take.
/// </summary>
/// <remarks>
/// <para>
/// Every column is generated from its row number alone, so a volume costs no memory to hold, and
/// written in batches of <see cref="BatchRows"/> rows to a file that is then opened mapped. Times are
/// medians of <see cref="Passes"/> passes after <see cref="Warmups"/> thrown away; the write's is net
/// of generating the rows, which is timed on its own.
/// </para>
/// <para>
/// A smaller file that decodes slower reads faster end to end when the storage is slow enough: for
/// each configuration against <c>Auto</c> the table gives the throughput at which the two cross,
/// the bytes saved over the decode time lost.
/// </para>
/// </remarks>
internal static class EncodingTradeoffs
{
    private const int BatchRows = 65_536;
    private const int Warmups = 1;
    private const int Passes = 3;
    private const int TakeRows = 1_000;
    private const string Field = "v";

    private static readonly string[] Cities =
    [
        "Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Montpellier",
        "Bordeaux", "Lille", "Rennes", "Reims", "Saint-Etienne", "Le Havre", "Toulon", "Grenoble",
    ];

    /// <summary>Runs every column whose name contains one of <paramref name="only"/>, or all of them.</summary>
    /// <param name="rows">Rows a column.</param>
    /// <param name="only">Words that pick columns by name; none for all.</param>
    /// <param name="page">The benchmark page to write the <c>tradeoffs</c> section to, after a run of every column; null for none.</param>
    internal static async Task<int> RunAsync(long rows, string[] only, string? page)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vorticity-tradeoffs");
        Directory.CreateDirectory(directory);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"# Encoding trade-offs, {rows:N0} rows a column, median of {Passes} after {Warmups} warm-up\n"));

        List<(Column Column, List<Result> Results)> measured = [];
        foreach (Column column in Columns())
        {
            if (only.Length > 0 && !only.Any(o => column.Name.Contains(o, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            measured.Add((column, await MeasureAsync(column, rows, directory).ConfigureAwait(false)));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"checksum {s_checksum}"));
        if (page is not null)
        {
            if (only.Length > 0)
            {
                Console.Error.WriteLine("The page takes a run of every column; it is not written by this run.");
            }
            else
            {
                await ResultsPage.WriteAsync(page, [("tradeoffs", Section(rows, measured))]).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>
    /// The advice on every column whose name contains one of <paramref name="only"/>, or on all of
    /// them, under the goals the tables above tell apart: whole scans from a local drive, from an
    /// object store and from the page cache, reads by row, and the bytes alone.
    /// </summary>
    /// <param name="rows">Rows a column.</param>
    /// <param name="only">Words that pick columns by name; none for all.</param>
    /// <param name="page">The benchmark page to write the <c>advice</c> section to, after a run of every column; null for none.</param>
    internal static async Task<int> AdviseAsync(long rows, string[] only, string? page)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vorticity-tradeoffs");
        Directory.CreateDirectory(directory);
        (string Name, EncodingGoal Goal)[] goals =
        [
            ("scans at 2 GB/s", EncodingGoal.Default),
            ("scans at 100 MB/s", new EncodingGoal { StorageBytesPerSecond = 100_000_000 }),
            ("scans at 10 GB/s", new EncodingGoal { StorageBytesPerSecond = 10_000_000_000 }),
            ("a row in 1 000 read by row", new EncodingGoal { LookupsPerScan = rows / 1_000.0 }),
            ("size", EncodingGoal.Smallest),
        ];

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# Encoding advice, {rows:N0} rows a column\n"));
        List<(Column Column, string[] Advised)> advised = [];
        foreach (Column column in Columns())
        {
            if (only.Length > 0 && !only.Any(o => column.Name.Contains(o, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string[] cells = new string[goals.Length];

            string path = Path.Combine(directory, "column.vortex");
            await WriteAsync(column, rows, path, new VortexWriteOptions { Compression = CompressionProfile.None }).ConfigureAwait(false);
            StringBuilder text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"## {column.Name}\n\n");
            text.Append("| goal | advised | written as | chunk | B/value | scan ns | lookup µs | seconds | reason |\n");
            text.Append("|---|---|---|---:|---:|---:|---:|---:|---|\n");
            await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None).ConfigureAwait(false))
            {
                for (int g = 0; g < goals.Length; g++)
                {
                    (string name, EncodingGoal goal) = goals[g];
                    long start = Stopwatch.GetTimestamp();
                    EncodingAdvice advice = await VortexSession.Default.AdviseAsync(file, goal).ConfigureAwait(false);
                    double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    ColumnEncodingAdvice answer = advice.Columns[0];
                    EncodingCandidate chosen = answer.Recommended;
                    text.Append(CultureInfo.InvariantCulture,
                        $"| {name} | {chosen.Hint} | {string.Join(", ", chosen.WrittenAs)} | {chosen.ChunkTargetBytes >> 20} MiB | {chosen.BytesPerValue:F2} | {chosen.ScanNanosecondsPerValue:F2} | {chosen.LookupMicroseconds:F1} | {seconds:F1} | {answer.Reason} |\n");
                    cells[g] = Departure(chosen);
                }
            }

            System.IO.File.Delete(path);
            Console.WriteLine(text.ToString());
            advised.Add((column, cells));
        }

        if (page is not null)
        {
            if (only.Length > 0)
            {
                Console.Error.WriteLine("The page takes a run of every column; it is not written by this run.");
            }
            else
            {
                await ResultsPage.WriteAsync(page, [("advice", AdviceSection(rows, [.. goals.Select(g => g.Name)], advised))]).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>How a recommendation departs from the writer's own choice, for a cell; empty when it does not.</summary>
    private static string Departure(EncodingCandidate chosen)
    {
        List<string> parts = [];
        if (chosen.Hint != EncodingHint.Auto)
        {
            parts.Add($"`{chosen.Hint}`");
        }

        if (chosen.ChunkTargetBytes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{chosen.ChunkTargetBytes >> 20} MiB chunks"));
        }

        return string.Join(", ", parts);
    }

    private static async Task<List<Result>> MeasureAsync(Column column, long rows, string directory)
    {
        string path = Path.Combine(directory, "column.vortex");

        // Every configuration once at a small volume first, so that what this column's encoders and
        // decoders compile is compiled before the first measured pass rather than inside it.
        long warmRows = Math.Min(rows, 4 * BatchRows);
        foreach ((_, VortexWriteOptions options) in Configurations(column))
        {
            await WriteAsync(column, warmRows, path, options).ConfigureAwait(false);
            await ScanAsync(path, warmRows).ConfigureAwait(false);
            await TakeAsync(path, [0, warmRows / 2, warmRows - 1]).ConfigureAwait(false);
        }

        double generate = Median(Enumerable.Range(0, Warmups + Passes).Select(_ => Time(() => Generate(column, rows))).Skip(Warmups));

        List<Result> results = [];
        foreach ((string name, VortexWriteOptions options) in Configurations(column))
        {
            string encodings = "";
            List<double> writes = [];
            for (int pass = 0; pass < Warmups + Passes; pass++)
            {
                long start = Stopwatch.GetTimestamp();
                encodings = await WriteAsync(column, rows, path, options).ConfigureAwait(false);
                writes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            long bytes = new FileInfo(path).Length;
            List<double> scans = [];
            List<double> takes = [];
            long[] indices = [.. Enumerable.Range(0, TakeRows).Select(i => (i * (rows / TakeRows)) + 7)];
            for (int pass = 0; pass < Warmups + Passes; pass++)
            {
                scans.Add(await TimeAsync(() => ScanAsync(path, rows)).ConfigureAwait(false));
                takes.Add(await TimeAsync(() => TakeAsync(path, indices)).ConfigureAwait(false));
            }

            results.Add(new Result(
                name, encodings, bytes,
                Math.Max(0, Median(writes.Skip(Warmups)) - generate),
                Median(scans.Skip(Warmups)),
                Median(takes.Skip(Warmups))));
            System.IO.File.Delete(path);
        }

        Console.WriteLine($"## {column.Name}\n\n{Table(rows, results)}");
        return results;
    }

    /// <summary>Every configuration of one column, against <c>Auto</c>, its first.</summary>
    private static string Table(long rows, List<Result> results)
    {
        Result auto = results[0];
        StringBuilder text = new StringBuilder();
        text.Append("| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |\n");
        text.Append("|---|---|---:|---:|---:|---:|---:|---|\n");
        foreach (Result r in results)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.Encodings} | {r.Bytes:N0} | {(double)r.Bytes / rows:F2} | {r.WriteMs:F0} | {r.ScanMs:F1} | {r.TakeMs:F2} | {Against(auto, r)} |\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// The twenty columns by kind, what <c>Auto</c> writes and what it costs, the configurations
    /// worth knowing beside it, and every configuration folded under each; for the page's
    /// <c>tradeoffs</c> section.
    /// </summary>
    private static string Section(long rows, List<(Column Column, List<Result> Results)> measured)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("## Encodings, column by column");
        text.AppendLine();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"One column of {rows:N0} rows per file, written under each compression profile and each hint that"));
        text.AppendLine("applies to it, then opened and read. A **scan** decodes every value to its plain form and reads it");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"once; a **take** reads {TakeRows:N0} rows spread over the file. Each figure is the median of {Passes} passes after"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{Warmups} warm-up, the file in the page cache and mapped anew by each pass, so that a larger file pays for its"));
        text.AppendLine("pages on every read; a write is net of generating its rows. **Crosses at** is the");
        text.AppendLine("storage throughput at which a configuration and `Auto` read the column whole in the same time,");
        text.AppendLine("counting its bytes at that throughput and then its scan: below it the smaller file reads faster");
        text.AppendLine("end to end, above it the faster decode does. What the figures mean for a choice is in");
        text.AppendLine("[choose-encodings.md](choose-encodings.md).");
        text.AppendLine();
        text.AppendLine("**Worth knowing** is picked by rule, against `Auto`, among the configurations that write something");
        text.AppendLine("else: the smallest, when it saves 5 % of the bytes; of those a fifth faster to scan, the one that");
        text.AppendLine("overtakes `Auto` on the slowest storage, when that storage is no faster than the page cache, 10");
        text.AppendLine("GB/s; and the fastest take, when it halves `Auto`'s.");
        text.AppendLine();
        foreach (string kind in (string[])["Integers", "Floating point", "Text", "Booleans"])
        {
            List<(Column Column, List<Result> Results)> of = [.. measured.Where(m => Kind(m.Column) == kind)];
            if (of.Count == 0)
            {
                continue;
            }

            text.AppendLine($"### {kind}");
            text.AppendLine();
            text.AppendLine("| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |");
            text.AppendLine("|---|---|---:|---:|---:|---:|---|");
            foreach ((Column column, List<Result> results) in of)
            {
                Result auto = results[0];
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {Shape(column)} | {auto.Encodings} | {(double)auto.Bytes / rows:F2} | {auto.WriteMs:F0} | {auto.ScanMs:F1} | " +
                    $"{auto.TakeMs:F2} | {string.Join("; ", WorthKnowing(rows, results))} |"));
            }

            text.AppendLine();
            foreach ((Column column, List<Result> results) in of)
            {
                text.AppendLine($"<details><summary>{Shape(column)}: every configuration</summary>");
                text.AppendLine();
                text.Append(Table(rows, results));
                text.AppendLine();
                text.AppendLine("</details>");
                text.AppendLine();
            }
        }

        text.Append(Profiles(rows, measured));
        text.AppendLine(ResultsPage.Provenance());
        return text.ToString();
    }

    /// <summary>The fastest storage a column is read from, the page cache, in MB/s: past it a crossing is not one a reader meets.</summary>
    private const double PageCacheMegabytesPerSecond = 10_000;

    /// <summary>The configurations beside <c>Auto</c> a reader of one column's row should know of, by the rule the section states.</summary>
    private static List<string> WorthKnowing(long rows, List<Result> results)
    {
        Result auto = results[0];
        List<Result> others = [.. results.Skip(1).Where(r => r.Encodings != auto.Encodings)];
        List<Result> picked = [];
        if (others.MinBy(r => r.Bytes) is { } smallest && smallest.Bytes <= 0.95 * auto.Bytes)
        {
            picked.Add(smallest);
        }

        // Of those a fifth faster to scan, the one that overtakes `Auto` on the slowest storage:
        // no bytes more, or the fewest bytes more for each millisecond saved.
        if (others.Where(r => r.ScanMs <= 0.8 * auto.ScanMs && Overtakes(auto, r) <= PageCacheMegabytesPerSecond)
                .MinBy(r => Overtakes(auto, r)) is { } faster && !picked.Contains(faster))
        {
            picked.Add(faster);
        }

        if (others.Where(r => r.TakeMs <= 0.5 * auto.TakeMs && Against(auto, r) != "reads slower at any throughput")
                .MinBy(r => r.TakeMs) is { } nearest && !picked.Contains(nearest))
        {
            picked.Add(nearest);
        }

        return
        [
            .. picked.Select(r => string.Create(CultureInfo.InvariantCulture,
                $"{r.Name}: {r.Encodings}, {(double)r.Bytes / rows:F2} B/value, scan {r.ScanMs:F1} ms, take {r.TakeMs:F2} ms; {Against(auto, r)}")),
        ];
    }

    /// <summary>
    /// The storage throughput, in MB/s, above which <paramref name="r"/>, faster to scan than
    /// <paramref name="auto"/>, reads the column faster end to end: zero when it is no larger.
    /// </summary>
    private static double Overtakes(Result auto, Result r) =>
        r.Bytes <= auto.Bytes ? 0 : (r.Bytes - auto.Bytes) / (auto.ScanMs - r.ScanMs) / 1_000;

    /// <summary>What the profiles chose over every column, computed from the tables.</summary>
    private static string Profiles(long rows, List<(Column Column, List<Result> Results)> measured)
    {
        int fastestSame = 0;
        int noneLargest = 0;
        List<string> smallestOther = [];
        (double Ratio, Result Smallest, Result Auto, Column Column)? slowest = null;
        foreach ((Column column, List<Result> results) in measured)
        {
            Result auto = results[0];
            Result? fastest = results.FirstOrDefault(r => r.Name == "Fastest");
            Result? smallest = results.FirstOrDefault(r => r.Name == "Smallest");
            Result? none = results.FirstOrDefault(r => r.Name == "None");
            fastestSame += fastest is not null && fastest.Encodings == auto.Encodings ? 1 : 0;
            noneLargest += none is not null && results.All(r => r.Bytes <= none.Bytes) ? 1 : 0;
            if (smallest is not null && smallest.Encodings != auto.Encodings)
            {
                smallestOther.Add(column.Name);
            }

            if (smallest is not null && auto.WriteMs > 0 && smallest.WriteMs / auto.WriteMs > (slowest?.Ratio ?? 0))
            {
                slowest = (smallest.WriteMs / auto.WriteMs, smallest, auto, column);
            }
        }

        StringBuilder text = new StringBuilder();
        text.AppendLine("### The profiles side by side");
        text.AppendLine();
        string written = slowest is { } slow
            ? string.Create(CultureInfo.InvariantCulture,
                $", and took up to {slow.Ratio:F1} times `Auto`'s write, {slow.Smallest.WriteMs:N0} ms against {slow.Auto.WriteMs:N0} ms on *{slow.Column.Name}*, since it tries every scheme on every chunk.")
            : ".";
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"`Fastest` wrote what `Auto` wrote on {fastestSame} of {measured.Count} columns. `Smallest` wrote something else on " +
            $"{(smallestOther.Count == 0 ? "none" : string.Join(", ", smallestOther.Select(s => $"*{s}*")))}{written}"));

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"`None`, the plain form, made the largest file on {noneLargest} of {measured.Count} columns."));
        text.AppendLine();
        return text.ToString();
    }

    /// <summary>
    /// The columns on which the advice departs from the writer's own choice under at least one of
    /// the goals, a cell a goal's departure; for the page's <c>advice</c> section.
    /// </summary>
    private static string AdviceSection(long rows, string[] goals, List<(Column Column, string[] Advised)> advised)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("## What the advice picks");
        text.AppendLine();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"`VortexSession.AdviseAsync` on the same columns of {rows:N0} rows, under five goals: whole scans with the"));
        text.AppendLine("bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.");
        text.AppendLine("A cell is what the advice takes where it departs from the writer's own choice, empty where it");
        text.AppendLine("does not; a column it never departs on is left out.");
        text.AppendLine();
        text.AppendLine("| column | " + string.Join(" | ", goals) + " |");
        text.AppendLine("|---|" + string.Concat(goals.Select(_ => "---|")));
        List<string> alike = [];
        foreach ((Column column, string[] cells) in advised)
        {
            if (cells.All(c => c.Length == 0))
            {
                alike.Add(column.Name);
                continue;
            }

            text.AppendLine($"| {column.Name} | {string.Join(" | ", cells)} |");
        }

        text.AppendLine();
        if (alike.Count > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"It keeps the writer's choice under every goal on the {alike.Count} other columns."));
            text.AppendLine();
        }

        text.AppendLine(ResultsPage.Provenance());
        return text.ToString();
    }

    /// <summary>The kind a column's table sits under.</summary>
    private static string Kind(Column column) => column switch
    {
        Longs => "Integers",
        Doubles => "Floating point",
        Strings => "Text",
        _ => "Booleans",
    };

    /// <summary>A column's shape, its name without the physical type the table's kind already says.</summary>
    private static string Shape(Column column)
    {
        int comma = column.Name.IndexOf(", ", StringComparison.Ordinal);
        return comma < 0 ? column.Name : column.Name[(comma + 2)..];
    }

    /// <summary>Where a configuration stands against <c>Auto</c> for a full read, storage included.</summary>
    private static string Against(Result auto, Result r)
    {
        if (ReferenceEquals(auto, r))
        {
            return "";
        }

        // Within a percent of the bytes and five of the time, two configurations are told apart by
        // noise alone.
        double saved = auto.Bytes - r.Bytes;
        double lost = r.ScanMs - auto.ScanMs;
        bool sameBytes = Math.Abs(saved) <= 0.01 * auto.Bytes;
        bool sameTime = Math.Abs(lost) <= 0.05 * auto.ScanMs;
        if (sameBytes && sameTime)
        {
            return "same";
        }

        if (saved >= 0 && lost <= 0 || sameBytes && lost <= 0 || sameTime && saved >= 0)
        {
            return "reads faster at any throughput";
        }

        if (saved <= 0 && lost >= 0 || sameBytes && lost >= 0 || sameTime && saved <= 0)
        {
            return "reads slower at any throughput";
        }

        // Bytes over milliseconds is bytes per millisecond; a thousandth of that is MB/s.
        double crossing = saved / lost / 1_000;
        return saved > 0
            ? string.Create(CultureInfo.InvariantCulture, $"reads faster below {crossing:N0} MB/s")
            : string.Create(CultureInfo.InvariantCulture, $"reads faster above {crossing:N0} MB/s");
    }

    private static IEnumerable<(string Name, VortexWriteOptions Options)> Configurations(Column column)
    {
        yield return ("Auto", new VortexWriteOptions());
        yield return ("Fastest", new VortexWriteOptions { Compression = CompressionProfile.Fastest });
        yield return ("Smallest", new VortexWriteOptions { Compression = CompressionProfile.Smallest });
        yield return ("None", new VortexWriteOptions { Compression = CompressionProfile.None });
        foreach (EncodingHint hint in column.Hints)
        {
            yield return (
                $"hint {hint}",
                new VortexWriteOptions { Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(Field, hint) });
        }

        // A dictionary pays its entries once a chunk, so on a column whose values repeat across
        // the file more than within a default chunk the chunk's size is what decides it.
        if (column.Name.Contains("distinct", StringComparison.Ordinal))
        {
            yield return ("Auto, 16 MiB chunks", new VortexWriteOptions { ChunkTargetBytes = 16 << 20 });
        }
    }

    private static async Task<string> WriteAsync(Column column, long rows, string path, VortexWriteOptions options)
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct([Field], [column.Type(types)], Nullability.NonNullable);
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, options))
        {
            CanonicalArena arena = new CanonicalArena();
            for (long start = 0; start < rows; start += BatchRows)
            {
                int count = (int)Math.Min(BatchRows, rows - start);
                int values = column.Build(arena, types, start, count);
                int root = arena.AddStruct(schema, count, Validity.NonNullable, [values]);
                using (RecordBatch batch = new RecordBatch(arena, root, start))
                {
                    await writer.WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
                }

                arena.Reset();
            }

            report = await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return Summarize(report.Columns[0].Encodings);
    }

    private static void Generate(Column column, long rows)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        for (long start = 0; start < rows; start += BatchRows)
        {
            column.Build(arena, types, start, (int)Math.Min(BatchRows, rows - start));
            arena.Reset();
        }
    }

    private static string Summarize(ImmutableArray<string> encodings) =>
        string.Join(", ", encodings
            .GroupBy(e => e, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} x{g.Count()}")));

    private static long s_checksum;

    private static async Task ScanAsync(string path, long rows)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path).ConfigureAwait(false);
        long seen = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().ConfigureAwait(false))
        {
            seen += batch.RowCount;
            s_checksum += Consume(batch);
        }

        if (seen != rows)
        {
            throw new InvalidOperationException($"read {seen} rows of {rows}");
        }
    }

    private static async Task TakeAsync(string path, long[] indices)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path).ConfigureAwait(false);
        long seen = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Take(indices).ExecuteAsync().ConfigureAwait(false))
        {
            seen += batch.RowCount;
            s_checksum += Consume(batch);
        }

        if (seen != indices.Length)
        {
            throw new InvalidOperationException($"took {seen} rows of {indices.Length}");
        }
    }

    /// <summary>
    /// Reads every byte of the batch's decoded column once, as a consumer of the values does: a
    /// column left in its plain form is otherwise a view of the mapped file that no one touches.
    /// </summary>
    private static long Consume(RecordBatch batch)
    {
        CanonicalArena arena = batch.Arena;
        CanonicalNode node = arena.GetNode(arena.GetNode(batch.RootIndex).GetFieldIndex(0));
        long sum = node.Kind switch
        {
            CanonicalKind.Primitive => Sum(node.Values.Span),
            CanonicalKind.Bool => Sum(node.Bits.Span),
            CanonicalKind.VarBinView => SumText(node),
            _ => throw new InvalidOperationException($"a scan delivered a {node.Kind} column"),
        };

        if (node.Validity.Kind == ValidityKind.Bitmap)
        {
            sum += Sum(arena.GetNode(node.Validity.CanonicalNodeIndex).Bits.Span);
        }

        return sum;
    }

    private static long SumText(CanonicalNode node)
    {
        long sum = Sum(node.Views.Span);
        for (int i = 0; i < node.DataBufferCount; i++)
        {
            sum += Sum(node.GetDataBuffer(i).Span);
        }

        return sum;
    }

    private static long Sum(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes);
        Vector<ulong> total = Vector<ulong>.Zero;
        int i = 0;
        for (; i <= words.Length - Vector<ulong>.Count; i += Vector<ulong>.Count)
        {
            total += new Vector<ulong>(words[i..]);
        }

        ulong sum = Vector.Sum(total);
        for (; i < words.Length; i++)
        {
            sum += words[i];
        }

        for (int b = words.Length * sizeof(ulong); b < bytes.Length; b++)
        {
            sum += bytes[b];
        }

        return (long)sum;
    }

    private static double Time(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static async Task<double> TimeAsync(Func<Task> action)
    {
        long start = Stopwatch.GetTimestamp();
        await action().ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }

    private sealed record Result(string Name, string Encodings, long Bytes, double WriteMs, double ScanMs, double TakeMs);

    /// <summary>A column of one nature: its type, how a batch of it is built, and the hints worth trying.</summary>
    private abstract class Column(string name, EncodingHint[] hints)
    {
        internal string Name { get; } = name;

        internal EncodingHint[] Hints { get; } = hints;

        internal abstract DType Type(DTypeArena types);

        internal abstract int Build(CanonicalArena arena, DTypeArena types, long start, int count);
    }

    private sealed class Longs(string name, Func<long, long> value, int nullEvery = 0)
        : Column(name, [EncodingHint.Dictionary, EncodingHint.BitPacked, EncodingHint.RunEnd, EncodingHint.Zstd])
    {
        internal override DType Type(DTypeArena types) =>
            types.Primitive(PType.I64, nullEvery > 0 ? Nullability.Nullable : Nullability.NonNullable);

        internal override int Build(CanonicalArena arena, DTypeArena types, long start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), 64, out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(Type(types), count, Nulls(arena, types, start, count, nullEvery), PType.I64, buffer);
        }
    }

    private sealed class Doubles(string name, Func<long, double> value)
        : Column(name, [EncodingHint.Dictionary, EncodingHint.Alp, EncodingHint.Zstd])
    {
        internal override DType Type(DTypeArena types) => types.Primitive(PType.F64, Nullability.NonNullable);

        internal override int Build(CanonicalArena arena, DTypeArena types, long start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(double), 64, out Span<byte> bytes);
            Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(Type(types), count, Validity.NonNullable, PType.F64, buffer);
        }
    }

    private sealed class Bools(string name, Func<long, bool> value)
        : Column(name, [EncodingHint.RunEnd])
    {
        internal override DType Type(DTypeArena types) => types.Bool(Nullability.NonNullable);

        internal override int Build(CanonicalArena arena, DTypeArena types, long start, int count)
        {
            VortexBuffer buffer = arena.Allocate((count + 7) / 8, 64, out Span<byte> bits);
            bits.Clear();
            for (int i = 0; i < count; i++)
            {
                if (value(start + i))
                {
                    bits[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return arena.AddBool(Type(types), count, Validity.NonNullable, buffer, 0);
        }
    }

    /// <summary>Text, written as UTF-8 into <paramref name="write"/>'s span, which is at most <c>MaxBytes</c>.</summary>
    private sealed class Strings(string name, int maxBytes, Func<long, Span<byte>, int> write, int nullEvery = 0)
        : Column(name, [EncodingHint.Dictionary, EncodingHint.Fsst, EncodingHint.Zstd])
    {
        internal override DType Type(DTypeArena types) =>
            types.Utf8(nullEvery > 0 ? Nullability.Nullable : Nullability.NonNullable);

        internal override int Build(CanonicalArena arena, DTypeArena types, long start, int count)
        {
            VortexBuffer heapBuffer = arena.Allocate(count * maxBytes, 64, out Span<byte> heap);
            VortexBuffer viewBuffer = arena.Allocate(count * 16, 64, out Span<byte> views);
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                int length = write(start + i, heap[used..]);
                Span<byte> view = views.Slice(i * 16, 16);
                view.Clear();
                MemoryMarshal.Write(view, length);
                ReadOnlySpan<byte> text = heap.Slice(used, length);
                if (length <= 12)
                {
                    text.CopyTo(view[4..]);
                }
                else
                {
                    text[..4].CopyTo(view[4..]);
                    MemoryMarshal.Write(view[8..], 0);
                    MemoryMarshal.Write(view[12..], used);
                    used += length;
                }
            }

            return arena.AddVarBinView(
                Type(types), count, Nulls(arena, types, start, count, nullEvery), viewBuffer, [heapBuffer.Slice(0, Math.Max(used, 1))]);
        }
    }

    private static Validity Nulls(CanonicalArena arena, DTypeArena types, long start, int count, int nullEvery)
    {
        if (nullEvery == 0)
        {
            return Validity.NonNullable;
        }

        VortexBuffer buffer = arena.Allocate((count + 7) / 8, 64, out Span<byte> bits);
        bits.Clear();
        for (int i = 0; i < count; i++)
        {
            if (Mix(start + i) % (ulong)nullEvery != 0)
            {
                bits[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, buffer, 0));
    }

    /// <summary>A row's own random number: splitmix64 of the row.</summary>
    private static ulong Mix(long row)
    {
        ulong z = (ulong)row + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static int Ascii(ReadOnlySpan<char> text, Span<byte> destination) => Encoding.ASCII.GetBytes(text, destination);

    private static IEnumerable<Column> Columns()
    {
        long[] sixteen = [.. Enumerable.Range(0, 16).Select(i => (long)Mix(-1 - i))];
        double[] sixteenDoubles = [.. Enumerable.Range(0, 16).Select(i => (Mix(-100 - i) >> 11) * (1.0 / (1UL << 53)) * 1000)];
        double[] thousand = [.. Enumerable.Range(0, 1000).Select(i => Math.Round((Mix(-1000 - i) % 1_000_000) / 100.0, 2))];

        yield return new Longs("i64, a sequence", row => row);
        yield return new Longs("i64, sorted runs of 1 000", row => row / 1000);
        yield return new Longs("i64, timestamps (ms, increasing, jittered)", row => 1_700_000_000_000L + (row * 1000) + (long)(Mix(row) % 1000));
        yield return new Longs("i64, random in 0..999", row => (long)(Mix(row) % 1000));
        yield return new Longs("i64, 16 distinct, random order", row => sixteen[Mix(row) % 16]);
        yield return new Longs("i64, 100 003 distinct, repeating", row => (row * 7919 % 100_003) * 1_000_003L);
        yield return new Longs("i64, uniform 64-bit", row => (long)Mix(row));
        yield return new Longs("i64, random in 0..999, 10 % null", row => (long)(Mix(row) % 1000), nullEvery: 10);
        yield return new Doubles("f64, prices (2 decimals)", row => (Mix(row) % 1_000_000) / 100.0);
        yield return new Doubles("f64, 16 distinct, random order", row => sixteenDoubles[Mix(row) % 16]);
        yield return new Doubles("f64, 1 000 distinct prices", row => thousand[Mix(row) % 1000]);
        yield return new Doubles("f64, 100 003 distinct, repeating", row => (row * 7919 % 100_003) / 7.0);
        yield return new Doubles("f64, uniform in [0, 1)", row => (Mix(row) >> 11) * (1.0 / (1UL << 53)));
        yield return new Strings("utf8, 16 cities, random order", 16, (row, span) => Ascii(Cities[Mix(row) % 16], span));
        yield return new Strings("utf8, 10 000 distinct ids", 16, (row, span) => Ascii(string.Create(CultureInfo.InvariantCulture, $"customer-{Mix(row) % 10_000:D5}"), span));
        yield return new Strings("utf8, 10 000 distinct ids, 10 % null", 16, (row, span) => Ascii(string.Create(CultureInfo.InvariantCulture, $"customer-{Mix(row) % 10_000:D5}"), span), nullEvery: 10);
        yield return new Strings("utf8, unique UUIDs", 36, (row, span) => Uuid(row, span));
        yield return new Strings("utf8, log lines (~100 B)", 160, (row, span) => LogLine(row, span));
        yield return new Bools("bool, half true", row => (Mix(row) & 1) != 0);
        yield return new Bools("bool, 1 % true", row => Mix(row) % 100 == 0);
    }

    /// <summary>A unique UUID a row, as ASCII text; also the kernel benchmarks' text of that shape.</summary>
    internal static int Uuid(long row, Span<byte> destination)
    {
        Span<byte> raw = stackalloc byte[16];
        MemoryMarshal.Write(raw, Mix(row));
        MemoryMarshal.Write(raw[8..], Mix(~row));
        Guid guid = new Guid(raw);
        Span<char> text = stackalloc char[36];
        guid.TryFormat(text, out _, "D");
        return Ascii(text, destination);
    }

    /// <summary>A log line of about a hundred bytes a row; also the kernel benchmarks' text of that shape.</summary>
    internal static int LogLine(long row, Span<byte> destination)
    {
        ulong r = Mix(row);
        string level = (r % 50) switch { 0 => "ERROR", < 5 => "WARN", _ => "INFO" };
        string[] paths = ["/api/orders", "/api/users", "/api/search", "/health", "/api/cart/items"];
        int status = ((r >> 16) % 20) switch { 0 => 500, < 3 => 404, _ => 200 };
        long millis = 1_700_000_000_000L + (row * 7);
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.FromUnixTimeMilliseconds(millis):yyyy-MM-ddTHH:mm:ss.fffZ} {level} GET {paths[(r >> 8) % 5]} {status} in {(r >> 24) % 900} ms req={r >> 40:x6}");
        return Ascii(line, destination);
    }
}
