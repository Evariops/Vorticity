using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

using Set = Vorticity.Bench.Scenarios.ScenarioSet;

namespace Vorticity.Benchmarks;

/// <summary>
/// The published comparison: high-level scenarios, each side in its own process, measured by wall
/// time, peak resident memory and processor time, on one core and on all of them.
/// </summary>
/// <remarks>
/// <para>
/// Why processes rather than the in-process ratios the rest of the bench uses: those three figures
/// belong to a process. A loop that shares one with the other implementation can report neither a
/// peak nor a total, and a reader deciding between two libraries wants what a run of each costs.
/// The price is that the fixed cost of starting a runtime is inside every figure, which is why the
/// table carries it as its own scenario rather than subtracting it silently.
/// </para>
/// <para>
/// The reference is built and run as upstream's own benchmarks are, which `tools/vxbench-rs`
/// states: mimalloc, `-C target-cpu=native`, one codegen unit, no LTO; on one core its
/// single-threaded runtime, on all of them a multi-threaded Tokio runtime of one worker per
/// processor. Our side is the Native AOT runner built for this machine's instruction set, with one
/// lane per scan or one per processor. Both sides read the same two files: the one our writer
/// makes, and the one the reference's writer makes from the same rows.
/// </para>
/// <para>
/// Our side also runs as this framework-dependent host on one core, which is what a process that
/// starts the runtime and compiles the scan as it goes costs. The two run the same scenario code;
/// only the build differs.
/// </para>
/// </remarks>
internal static class Report
{
    /// <summary>
    /// The fixture sizes: 2^20 rows and ten times that, so that every row block of 8 192 is full. A
    /// round million leaves a partial block at the end of the file, which the writer encodes on its
    /// own terms, and the two sizes would no longer hold the same shapes.
    /// </summary>
    private static readonly int[] Sizes = [1 << 20, 10 << 20];

    /// <summary>Rows per batch the fixture is written in: 64 full row blocks.</summary>
    private const int FixtureBatch = 64 * 8192;

    /// <summary>One scenario: what the reference is asked, when it can be asked.</summary>
    /// <param name="Name">The scenario, as <see cref="Set.ForReport"/> knows it.</param>
    /// <param name="What">What it does, for the table.</param>
    /// <param name="Reference">The reference binary's arguments, or null when it has no such entry point.</param>
    /// <param name="OursOnly">Whether it runs on our own file alone: a feature of this library, asked of no other file.</param>
    private sealed record Scenario(string Name, string What, Func<string, int, string[]>? Reference, bool OursOnly = false);

    private static readonly Scenario[] Scenarios =
    [
        new("open", "open the file and read no rows", (path, rows) => ["open", path]),
        new("scan", "read every column of every row", (path, rows) => ["scan", path]),
        new("project", "read one column of four", (path, rows) => ["project", path, Set.Field]),
        new("filter-narrow", "read the rows of a band holding about one in a hundred",
            (path, rows) => ["filter", path, Set.Field, Set.BandLow.ToString(CultureInfo.InvariantCulture),
                (rows / 100).ToString(CultureInfo.InvariantCulture)]),
        new("filter-wide", "read the rows of a band holding about half",
            (path, rows) => ["filter", path, Set.Field, Set.BandLow.ToString(CultureInfo.InvariantCulture),
                (rows / 2).ToString(CultureInfo.InvariantCulture)]),
        new("take", "take a thousand rows spread across the file",
            (path, rows) => ["take", path, Set.ReportTakeCount.ToString(CultureInfo.InvariantCulture),
                (rows / Set.ReportTakeCount).ToString(CultureInfo.InvariantCulture)]),
        new("write", "read the file and encode it back out", (path, rows) => ["write", path]),
        new("append", "append a tenth of the rows to a copy of the file", null, OursOnly: true),
    ];

    /// <summary>How many cores a run may use.</summary>
    private enum Cores
    {
        /// <summary>One: the reference's single-threaded runtime, our scans at one lane.</summary>
        One,

        /// <summary>All of them: a Tokio worker per processor, our scans at a lane per processor.</summary>
        All,
    }

    /// <summary>A file both sides read.</summary>
    /// <param name="Rows">Its rows.</param>
    /// <param name="Writer">Which writer made it.</param>
    /// <param name="Path">Where it is.</param>
    /// <param name="Bytes">Its size.</param>
    /// <param name="Columns">Each column's name and how the writer laid it out.</param>
    /// <param name="Plain">Each column's plain size (<see cref="PlainSize"/>), the same for both files of a size.</param>
    private sealed record Fixture(
        int Rows, string Writer, string Path, long Bytes, (string Name, string Layout)[] Columns, long[] Plain);

    /// <summary>Rounds a process of the per-encoding comparison runs, and the first ones it does not count.</summary>
    private const int KernelRounds = 12;

    private const int KernelColdRounds = 2;

    /// <summary>Processes per side and per encoding file, taking turns.</summary>
    private const int KernelProcesses = 3;

    /// <summary>
    /// Rounds the process that measures our allocations runs, and the first ones it does not count:
    /// the others are counted once the pools, the caches and the thread pool have met the scenario.
    /// Their median is the figure, because on several lanes a call now and then allocates ten times
    /// what the others do, and a single round can be that one.
    /// </summary>
    private const int AllocationRounds = 8;

    private const int AllocationColdRounds = 2;

    /// <summary>The writer name of our own files.</summary>
    private const string Ours = "Vorticity";

    /// <summary>The writer name of the reference's files.</summary>
    private const string Theirs = "Vortex Rust";

    /// <summary>Runs one scenario in this process and reports what it cost.</summary>
    internal static async Task<int> ScenarioAsync(string[] args)
    {
        int threads;
        try
        {
            (args, threads) = Set.TakeThreads(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: --scenario <name> <file.vortex> <rows> [--threads <n>|all]");
            return 2;
        }

        if (!long.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rows))
        {
            Console.Error.WriteLine($"'{args[3]}' is not a row count");
            return 2;
        }

        Func<string, Task<long>>? scenario = Set.ForReport(args[1], rows);
        if (scenario is null)
        {
            Console.Error.WriteLine($"no scenario named '{args[1]}'");
            return 2;
        }

        // The action is timed from inside the process, once it is up, as the runner and the
        // reference time theirs.
        long started = Stopwatch.GetTimestamp();
        long delivered = await scenario(args[2]).ConfigureAwait(false);
        long workMicros = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000);
        (long cpuMs, long rssBytes) = Vorticity.Bench.Scenarios.ProcessCost.Read();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rows={delivered} work_us={workMicros} cpu_ms={cpuMs} rss_bytes={rssBytes} threads={threads}"));
        return 0;
    }

    /// <summary>Runs every scenario on both sides, on both files and at both core counts, and prints the table.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        int runs = Count(args, "--runs", 5);
        bool markdown = Array.IndexOf(args, "--markdown") >= 0;
        string? reference = Locate(ReferencePath);
        if (reference is null)
        {
            Console.Error.WriteLine(
                $"The reference binary is missing: {ReferencePath}. " +
                "Build it with: cd tools/vxbench-rs && cargo build --release");
            return 1;
        }

        string? runner = Locate(RunnerPath);
        if (runner is null)
        {
            Console.Error.WriteLine(
                $"Our native runner is missing: {RunnerPath}. " +
                "Build it with: dotnet publish -c Release bench/Vorticity.Benchmarks.Runner");
            return 1;
        }

        string directory = Path.Combine(Path.GetTempPath(), "vorticity-report");
        Directory.CreateDirectory(directory);
        List<Row> table = [];
        bool disagreed = false;

        foreach (int rows in Sizes)
        {
            // Written again on every run: a file left from an earlier commit would be measured as
            // this commit's writer's.
            string ours = Path.Combine(directory, $"mixed-{rows}.vortex");
            Console.Error.WriteLine($"writing the {rows:N0}-row fixture...");
            await WriteFixtureAsync(ours, rows).ConfigureAwait(false);
            string theirs = Path.Combine(directory, $"mixed-{rows}-rust.vortex");
            string? written = await RewriteAsync(reference, ours, theirs).ConfigureAwait(false);
            if (written is not null)
            {
                Console.Error.WriteLine($"The reference could not write the {rows:N0}-row fixture: {written}");
                return 1;
            }

            // The rows are the same in both files, and so is what they weigh flat.
            (_, long[] plain) = await PlainSize.OfFileAsync(ours).ConfigureAwait(false);
            Fixture[] fixtures =
            [
                new Fixture(rows, Ours, ours, new FileInfo(ours).Length, await ColumnsAsync(ours).ConfigureAwait(false), plain),
                new Fixture(rows, Theirs, theirs, new FileInfo(theirs).Length, await ColumnsAsync(theirs).ConfigureAwait(false), plain),
            ];

            foreach (Fixture fixture in fixtures)
            {
                Console.Error.WriteLine($"{rows:N0} rows written by {fixture.Writer}, {fixture.Bytes:N0} bytes");
                foreach (Cores cores in (Cores[])[Cores.One, Cores.All])
                {
                    foreach (Scenario scenario in Scenarios)
                    {
                        if (scenario.OursOnly && fixture.Writer != Ours)
                        {
                            continue;
                        }

                        Row row = await MeasureRowAsync(fixture, cores, scenario, runner, reference, runs).ConfigureAwait(false);
                        disagreed |= Disagrees(row);
                        table.Add(row);
                        Console.Error.WriteLine($"  {Label(cores)}, {scenario.Name}: " +
                            (row.Aot is null ? "refused" : $"{row.Aot.WorkMs.Median:F1} ms native") +
                            (cores == Cores.One ? ", " + (row.Jit is null ? "refused" : $"{row.Jit.WorkMs.Median:F1} ms on the JIT") : string.Empty) +
                            " against " +
                            (row.Theirs is not null ? $"{row.Theirs.WorkMs.Median:F1} ms"
                                : scenario.Reference is null ? "no reference" : "a refusal"));
                    }
                }
            }
        }

        List<KernelRow>? kernels = null;
        if (Array.IndexOf(args, "--no-kernels") < 0)
        {
            kernels = await KernelsAsync(runner, reference).ConfigureAwait(false);
            disagreed |= kernels.Any(k => k.Problem is { } problem && problem.StartsWith(DisagreementPrefix, StringComparison.Ordinal));
        }

        int outAt = Array.IndexOf(args, "--out");
        string? page = outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : null;
        if (markdown)
        {
            List<(string Name, string Markdown)> sections = [("scenarios", ScenariosSection(table, runs))];
            if (kernels is { Count: > 0 })
            {
                sections.Add(("decoding", DecodingSection(kernels)));
            }

            if (page is not null)
            {
                await ResultsPage.WriteAsync(page, sections).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(string.Join("\n", sections.Select(s => s.Markdown)));
            }
        }
        else if (page is not null)
        {
            await System.IO.File.WriteAllTextAsync(page, Text(table, kernels, runs)).ConfigureAwait(false);
            Console.Error.WriteLine($"written to {page}");
        }
        else
        {
            Console.WriteLine(Text(table, kernels, runs));
        }

        return disagreed ? 1 : 0;
    }

    /// <summary>One scenario on one file at one core count, every side's runs interleaved.</summary>
    private static async Task<Row> MeasureRowAsync(
        Fixture fixture, Cores cores, Scenario scenario, string runner, string reference, int runs)
    {
        string[] threads = cores == Cores.All ? ["--threads", "all"] : [];
        string[] ours = ["--scenario", scenario.Name, fixture.Path, fixture.Rows.ToString(CultureInfo.InvariantCulture), .. threads];

        // On one core the reference runs under each of its splits and its figure is the faster
        // (`vxbench_set_split`); on all of them it keeps its default, whose splits spread the work.
        List<(string Exe, string[] Args)?> commands =
        [
            (runner, ours),
            cores == Cores.One ? OurCommand(ours) : null,
            scenario.Reference is null ? null : (reference, [.. scenario.Reference(fixture.Path, fixture.Rows), .. threads]),
            scenario.Reference is null || cores != Cores.One ? null
                : (reference, [.. scenario.Reference(fixture.Path, fixture.Rows), "--split", "per-chunk"]),
        ];

        List<(Measurement? Measurement, string? Refusal)> measured = await MeasureAsync(commands, runs).ConfigureAwait(false);
        long allocated = measured[0].Measurement is null
            ? -1
            : await AllocatedAsync(runner, ours).ConfigureAwait(false);
        (Measurement? theirs, string? refusal) = measured[2];
        bool perChunk = measured[3].Measurement is { } chunked
            && (theirs is null || chunked.WorkMs.Median < theirs.WorkMs.Median);
        if (perChunk)
        {
            theirs = measured[3].Measurement;
        }

        if (measured[3].Measurement is { } other && measured[2].Measurement is { } first && other.Rows != first.Rows)
        {
            Console.Error.WriteLine(
                $"{scenario.Name} at {fixture.Rows:N0} rows: the reference's two splits rendered {first.Rows} and {other.Rows} rows.");
        }

        return new Row(
            fixture, cores, scenario, measured[0].Measurement, measured[1].Measurement, theirs,
            refusal, allocated, perChunk);
    }

    /// <summary>
    /// What our runner allocates for the action once warm: the median of the rounds past the cold
    /// ones, the cost of a call in a process that stays up, where the first calls also fill the pools.
    /// </summary>
    /// <returns>The bytes, or -1 when the runner did not say.</returns>
    private static async Task<long> AllocatedAsync(string runner, string[] arguments)
    {
        (string output, _, int exit, _) = await RunAsync(
            (runner, [.. arguments, "--repeat", AllocationRounds.ToString(CultureInfo.InvariantCulture)])).ConfigureAwait(false);
        List<double> warm = WarmRounds(output, "allocated_bytes=", AllocationColdRounds);
        return exit == 0 && warm.Count > 0 ? (long)Median(warm) : -1;
    }

    /// <summary>
    /// Every file of the per-encoding corpus scanned by both sides, each in processes of its own
    /// that run the scan <see cref="KernelRounds"/> times: the decoders' cost once warm, on one core.
    /// </summary>
    /// <returns>A row per file; none when the corpus has not been generated.</returns>
    private static async Task<List<KernelRow>> KernelsAsync(string runner, string reference)
    {
        string root = ThroughputCheck.Root;
        string[] files = Directory.Exists(root) ? Directory.GetFiles(root, "*.vortex") : [];
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"no per-encoding corpus at {root}: bench/gen-throughput.sh writes it; the section is left out");
            return [];
        }

        string rounds = KernelRounds.ToString(CultureInfo.InvariantCulture);
        List<KernelRow> kernels = [];
        foreach (string path in files)
        {
            string encoding = Path.GetFileNameWithoutExtension(path);
            (long rows, long[] columns) = await PlainSize.OfFileAsync(path).ConfigureAwait(false);
            // The reference twice, under each of its splits: on one core neither is its faster on
            // every file, and its figure is the faster of the two (`vxbench_set_split`).
            (string Exe, string[] Args)[] sides =
            [
                (runner, ["--scenario", "scan", path, rows.ToString(CultureInfo.InvariantCulture), "--repeat", rounds]),
                (reference, ["scan", path, "--repeat", rounds]),
                (reference, ["scan", path, "--repeat", rounds, "--split", "per-chunk"]),
            ];

            List<double>[] warm = [[], [], []];
            long[] rendered = [-1, -1, -1];
            string? problem = null;
            for (int run = 0; run < KernelProcesses && problem is null; run++)
            {
                for (int turn = 0; turn < sides.Length && problem is null; turn++)
                {
                    int side = (run + turn) % sides.Length;
                    (string output, string error, int exit, _) = await RunAsync(sides[side]).ConfigureAwait(false);
                    if (exit != 0)
                    {
                        problem = $"{(side == 0 ? Ours : Theirs)} refused: {First(error).Replace(path, Path.GetFileName(path), StringComparison.Ordinal)}";
                        continue;
                    }

                    warm[side].Add(Median(WarmRounds(output, "work_us=", KernelColdRounds)));
                    rendered[side] = Value(output, "rows=");
                }
            }

            if (problem is null && (rendered[0] != rendered[1] || rendered[0] != rendered[2]))
            {
                problem = string.Create(CultureInfo.InvariantCulture,
                    $"{DisagreementPrefix} {rendered[0]} rows on one side and {rendered[1]} and {rendered[2]} on the other");
                Console.Error.WriteLine($"{encoding}: {problem}");
            }

            double byDefault = warm[1].Count == 0 ? 0 : Median(warm[1]);
            double perChunk = warm[2].Count == 0 ? 0 : Median(warm[2]);
            bool chunked = perChunk > 0 && perChunk < byDefault;
            KernelRow row = new KernelRow(
                encoding, rows, columns.Sum(),
                warm[0].Count == 0 ? 0 : Median(warm[0]), chunked ? perChunk : byDefault, problem, chunked);
            kernels.Add(row);
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {encoding}: {row.OursUs:F0} us against {row.TheirsUs:F0} us{(problem is null ? string.Empty : ", " + problem)}"));
        }

        return kernels;
    }

    /// <summary>The prefix of a problem that is two sides returning different rows, which fails the run.</summary>
    private const string DisagreementPrefix = "different rows:";

    /// <summary>One figure of each round a <c>--repeat</c> run printed, past the cold ones.</summary>
    /// <param name="output">The run's standard output.</param>
    /// <param name="key">The figure, as the round line names it: <c>work_us=</c> or <c>allocated_bytes=</c>.</param>
    /// <param name="cold">The first rounds, which are not counted.</param>
    private static List<double> WarmRounds(string output, string key, int cold)
    {
        List<double> values = [];
        foreach (string line in output.Split('\n'))
        {
            if (!line.StartsWith("round=", StringComparison.Ordinal))
            {
                continue;
            }

            if (Value(line, "round=") >= cold && Value(line, key) is >= 0 and long value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        List<double> sorted = [.. values];
        sorted.Sort();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>Whether the sides rendered different rows, which the run then fails on.</summary>
    private static bool Disagrees(Row row)
    {
        long? rendered = row.Aot?.Rows ?? row.Jit?.Rows;
        bool disagreed = false;
        foreach (Measurement? other in new[] { row.Jit, row.Theirs })
        {
            if (rendered is { } mine && other is not null && other.Rows != mine)
            {
                disagreed = true;
                Console.Error.WriteLine(
                    $"{row.Scenario.Name} at {row.Fixture.Rows:N0} rows, {row.Fixture.Writer}'s file, {Label(row.Cores)}: " +
                    $"{mine} rows on one side and {other.Rows} on another. A ratio between two different answers is not a ratio.");
            }
        }

        return disagreed;
    }

    /// <summary>Each column of the file at <paramref name="path"/>, by name, and how it is laid out.</summary>
    private static async Task<(string Name, string Layout)[]> ColumnsAsync(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, System.Threading.CancellationToken.None).ConfigureAwait(false);
        VortexLayout root = await file.GetLayoutAsync().ConfigureAwait(false);
        (string Name, string Layout)[] columns = new (string, string)[root.Children.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = (file.Schema[i].Name, Describe(root.Children[i]));
        }

        return columns;
    }

    /// <summary>
    /// A layout in a few words: its chunks and the outermost array encoding of each, which is what
    /// decides what a read of the column costs. Zone maps are left out.
    /// </summary>
    private static string Describe(VortexLayout node) => node.Encoding switch
    {
        "vortex.zoned" => Describe(node.Children[0]),
        "vortex.chunked" => string.Create(CultureInfo.InvariantCulture,
            $"{node.Children.Length:N0} chunks of {Outermost(node.Children)}"),
        "vortex.flat" => $"one chunk of {Outermost([node])}",
        "vortex.dict" => $"a dictionary layout, its values in {Describe(node.Children[0])} and its codes in {Describe(node.Children[1])}",
        _ => $"`{node.Encoding}`",
    };

    /// <summary>The outermost array encodings of <paramref name="chunks"/>, each named once.</summary>
    private static string Outermost(IEnumerable<VortexLayout> chunks) =>
        string.Join(" and ", chunks
            .Select(chunk => chunk.ArrayEncoding is { } encoding
                ? $"`{(encoding.IndexOf('(', StringComparison.Ordinal) is var open and >= 0 ? encoding[..open] : encoding)}`"
                : Describe(chunk))
            .Distinct());

    /// <summary>Has the reference write <paramref name="source"/>'s rows to <paramref name="destination"/>.</summary>
    /// <returns>Null, or what the reference said when it refused.</returns>
    private static async Task<string?> RewriteAsync(string reference, string source, string destination)
    {
        ProcessStartInfo start = new ProcessStartInfo(reference) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in (string[])["rewrite", source, destination])
        {
            start.ArgumentList.Add(argument);
        }

        using Process child = Process.Start(start) ?? throw new InvalidOperationException($"could not start {reference}");
        _ = await child.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string error = await child.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode == 0 ? null : First(error);
    }

    /// <summary>
    /// Runs every side's process in turn, run after run, and says what each cost or why it declined.
    /// </summary>
    /// <param name="commands">The sides, null for one that is not asked.</param>
    /// <param name="runs">Timed runs, after one discarded.</param>
    /// <returns>Per side, the measurement, or null with the first line the process wrote to its error stream.</returns>
    /// <remarks>
    /// Interleaved rather than one side's runs and then the other's: a machine that drifts over the
    /// minutes a scenario takes drifts under both sides alike. The side that goes first changes from
    /// run to run, so that none always follows the same one.
    /// </remarks>
    private static async Task<List<(Measurement? Measurement, string? Refusal)>> MeasureAsync(
        List<(string Exe, string[] Args)?> commands, int runs)
    {
        int sides = commands.Count;
        List<double>[] wall = [.. Enumerable.Range(0, sides).Select(_ => new List<double>())];
        List<double>[] work = [.. Enumerable.Range(0, sides).Select(_ => new List<double>())];
        List<double>[] cpu = [.. Enumerable.Range(0, sides).Select(_ => new List<double>())];
        List<double>[] rss = [.. Enumerable.Range(0, sides).Select(_ => new List<double>())];
        long[] rows = [.. Enumerable.Repeat(-1L, sides)];
        string?[] refused = new string?[sides];

        // One run is thrown away first: the page cache, the JIT and the dynamic loader all charge
        // their setup to whoever goes first, and that is a property of the machine, not of either
        // implementation.
        for (int run = 0; run <= runs; run++)
        {
            for (int turn = 0; turn < sides; turn++)
            {
                int side = (run + turn) % sides;
                if (commands[side] is not { } command || refused[side] is not null)
                {
                    continue;
                }

                (string output, string error, int exit, double elapsed) = await RunAsync(command).ConfigureAwait(false);
                if (exit != 0)
                {
                    // A side that refuses a scenario is reported, not thrown: a table with one hole
                    // and the reason beside it is worth more than no table. The shim prefixes its
                    // message with its own name and the file it was given; both are this machine's,
                    // and the published page quotes what is left.
                    string reason = First(error);
                    foreach (string argument in command.Args)
                    {
                        reason = reason.Replace(argument + ": ", string.Empty, StringComparison.Ordinal);
                    }

                    refused[side] = reason.Replace(
                        Path.GetFileName(command.Exe) + ": ", string.Empty, StringComparison.Ordinal);
                    Console.Error.WriteLine(
                        $"  refused: {Path.GetFileName(command.Exe)} {string.Join(' ', command.Args.Take(2))} " +
                        $"exited {exit}: {refused[side]}");
                    continue;
                }

                if (run == 0)
                {
                    continue;
                }

                long workMicros = Value(output, "work_us=");
                if (workMicros < 0)
                {
                    // A side that does not time its own action cannot be compared on it, and a wall
                    // clock in its place would be a different measurement under the same heading.
                    refused[side] = "the process reported no time for its action";
                    continue;
                }

                wall[side].Add(elapsed);
                work[side].Add(workMicros / 1000.0);
                rows[side] = Value(output, "rows=");
                cpu[side].Add(Value(output, "cpu_ms="));
                rss[side].Add(Value(output, "rss_bytes="));
            }
        }

        List<(Measurement?, string?)> measured = [];
        for (int side = 0; side < sides; side++)
        {
            measured.Add(commands[side] is null || refused[side] is not null || work[side].Count == 0
                ? (null, refused[side])
                : (new Measurement(rows[side], Spread.Of(wall[side]), Spread.Of(work[side]), Spread.Of(cpu[side]), Spread.Of(rss[side])), null));
        }

        return measured;
    }

    /// <summary>One process: what it printed, its exit code and its wall time from this clock.</summary>
    private static async Task<(string Output, string Error, int Exit, double ElapsedMs)> RunAsync((string Exe, string[] Args) command)
    {
        ProcessStartInfo start = new ProcessStartInfo(command.Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in command.Args)
        {
            start.ArgumentList.Add(argument);
        }

        Stopwatch watch = Stopwatch.StartNew();
        using Process child = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {command.Exe}");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().ConfigureAwait(false);
        watch.Stop();
        return (await output.ConfigureAwait(false), await error.ConfigureAwait(false), child.ExitCode, watch.Elapsed.TotalMilliseconds);
    }

    private static long Value(string output, string key)
    {
        int at = output.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return -1;
        }

        ReadOnlySpan<char> rest = output.AsSpan(at + key.Length);
        int end = 0;
        while (end < rest.Length && (char.IsAsciiDigit(rest[end]) || rest[end] == '-'))
        {
            end++;
        }

        return long.TryParse(rest[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : -1;
    }

    /// <summary>This host, framework-dependent, running one scenario: the JIT column.</summary>
    private static (string Exe, string[] Args) OurCommand(string[] arguments)
    {
        string host = Environment.ProcessPath ?? "dotnet";

        // Under `dotnet run` the host is the muxer and the assembly has to be named; a published
        // executable takes the arguments directly.
        return Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (host, [Assembly.GetExecutingAssembly().Location, .. arguments])
            : (host, arguments);
    }

    /// <summary>The reference binary, relative to the repository root.</summary>
    private static string ReferencePath => Path.Combine(
        "tools", "vxbench-rs", "target", "release", Executable("vxbench"));

    /// <summary>Our Native AOT runner, where `dotnet publish -c Release` puts it, relative to the repository root.</summary>
    private static string RunnerPath => Path.Combine(
        "bench", "Vorticity.Benchmarks.Runner", "bin", "Release", "net11.0",
        RuntimeInformation.RuntimeIdentifier, "publish", Executable("Vorticity.Benchmarks.Runner"));

    private static string Executable(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

    /// <summary>The file at <paramref name="relative"/> under the nearest ancestor of this build that has it.</summary>
    private static string? Locate(string relative)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static int Count(string[] args, string flag, int fallback)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length
            && int.TryParse(args[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value > 0
                ? value
                : fallback;
    }

    /// <summary>
    /// A mixed table: a monotone i64 the filter and the projection use, a double, a short string
    /// and a boolean with nulls. Written by us, because the comparison needs a million rows and ten
    /// million, and the conformance corpus holds neither; the reference's writer then writes the
    /// same rows as the other file.
    /// </summary>
    internal static async Task WriteFixtureAsync(string path, int rows)
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            [Set.Field, "value", "label", "flag"],
            [
                types.Primitive(PType.I64, Nullability.NonNullable),
                types.Primitive(PType.F64, Nullability.NonNullable),
                types.Utf8(Nullability.NonNullable),
                types.Bool(Nullability.Nullable),
            ],
            Nullability.NonNullable);

        string[] labels = ["alpha", "beta", "gamma", "delta", "epsilon"];
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema);
        for (int start = 0; start < rows; start += FixtureBatch)
        {
            int count = Math.Min(FixtureBatch, rows - start);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                VortexBuffer keys = arena.Allocate(count * sizeof(long), 8, out Span<byte> keyBytes);
                VortexBuffer values = arena.Allocate(count * sizeof(double), 8, out Span<byte> valueBytes);
                Span<long> monotone = MemoryMarshal.Cast<byte, long>(keyBytes);
                Span<double> value = MemoryMarshal.Cast<byte, double>(valueBytes);
                VortexBuffer views = arena.Allocate(count * 16, 8, out Span<byte> viewBytes);
                VortexBuffer bits = arena.Allocate((count + 7) / 8, 8, out Span<byte> flagBits);
                VortexBuffer validity = arena.Allocate((count + 7) / 8, 8, out Span<byte> validBits);
                flagBits.Clear();
                validBits.Fill(0xFF);

                for (int i = 0; i < count; i++)
                {
                    int row = start + i;
                    monotone[i] = Set.BandLow + row;
                    value[i] = (row * 7919L % 100_003) / 7.0;
                    Span<byte> view = viewBytes.Slice(i * 16, 16);
                    int length = Encoding.UTF8.GetBytes(labels[row % labels.Length], view[4..]);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, length);
                    if ((row & 1) == 0)
                    {
                        flagBits[i >> 3] |= (byte)(1 << (i & 7));
                    }

                    if (row % 1_000 == 0)
                    {
                        validBits[i >> 3] &= (byte)~(1 << (i & 7));
                    }
                }

                int keyNode = arena.AddPrimitive(schema.GetField(0), count, Validity.NonNullable, PType.I64, keys);
                int valueNode = arena.AddPrimitive(schema.GetField(1), count, Validity.NonNullable, PType.F64, values);
                int labelNode = arena.AddVarBinView(
                    schema.GetField(2), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
                int validityNode = arena.AddBool(
                    types.Bool(Nullability.NonNullable), count, Validity.NonNullable, validity, 0);
                int flagNode = arena.AddBool(
                    schema.GetField(3), count, Validity.Bitmap(validityNode), bits, 0);
                int root = arena.AddStruct(
                    schema, count, Validity.NonNullable, [keyNode, valueNode, labelNode, flagNode]);
                using RecordBatch record = new RecordBatch(arena, root, start);
                await writer.WriteAsync(record).ConfigureAwait(false);
            }
            finally
            {
                arena.Reset();
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private sealed record Spread(double Median, double Low, double High)
    {
        internal static Spread Of(List<double> values)
        {
            if (values.Count == 0)
            {
                return new Spread(0, 0, 0);
            }

            List<double> sorted = [.. values];
            sorted.Sort();
            return new Spread(sorted[sorted.Count / 2], sorted[0], sorted[^1]);
        }
    }

    /// <param name="Rows">Rows the process rendered.</param>
    /// <param name="WallMs">The whole process, from the parent's clock.</param>
    /// <param name="WorkMs">The action alone, from the process's own clock, once it was up.</param>
    /// <param name="CpuMs">Processor time of the process.</param>
    /// <param name="RssBytes">Peak resident set of the process.</param>
    private sealed record Measurement(long Rows, Spread WallMs, Spread WorkMs, Spread CpuMs, Spread RssBytes);

    /// <param name="Fixture">The file both sides read.</param>
    /// <param name="Cores">How many cores the run could use.</param>
    /// <param name="Scenario">What was asked of both sides.</param>
    /// <param name="Aot">Our Native AOT runner's measurement, or null when it refused.</param>
    /// <param name="Jit">This framework-dependent host's measurement, on one core; null otherwise or when it refused.</param>
    /// <param name="Theirs">The reference's, or null when it refused or was not asked.</param>
    /// <param name="Refusal">What the reference said when it refused, so the page can quote it.</param>
    /// <param name="Allocated">Bytes our runner allocated for the action once warm, or -1 when not measured.</param>
    /// <param name="TheirsPerChunk">Whether the reference's figure is its time under one split per chunk, the faster of its two splits here.</param>
    private sealed record Row(
        Fixture Fixture, Cores Cores, Scenario Scenario, Measurement? Aot, Measurement? Jit, Measurement? Theirs,
        string? Refusal, long Allocated, bool TheirsPerChunk);

    /// <summary>One file of the per-encoding comparison.</summary>
    /// <param name="Encoding">The file's name: the encoding it holds, or its shape.</param>
    /// <param name="Rows">Its rows.</param>
    /// <param name="Plain">The plain size of its values.</param>
    /// <param name="OursUs">Our warm scan, in microseconds, or 0 when there is none.</param>
    /// <param name="TheirsUs">The reference's warm scan, or 0.</param>
    /// <param name="Problem">Why a side has no figure, or why the two cannot be compared.</param>
    /// <param name="TheirsPerChunk">Whether the reference's figure is its time under one split per chunk, the faster of its two splits on this file.</param>
    private sealed record KernelRow(string Encoding, long Rows, long Plain, double OursUs, double TheirsUs, string? Problem, bool TheirsPerChunk);

    private static string Label(Cores cores) =>
        cores == Cores.One ? "one core" : string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessorCount} cores");

    private static string Text(List<Row> table, List<KernelRow>? kernels, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(Header(runs));
        text.AppendLine();
        text.AppendLine("rows       file         cores     scenario       ours AOT, ms (low-high)  ours JIT, ms (low-high)  " +
            "rust, ms (low-high)     speedup ours GB/s rust GB/s ours alloc  ours MiB  rust MiB  rows out");
        foreach (Row row in table)
        {
            bool asked = row.Scenario.Reference is not null;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{row.Fixture.Rows,-10:N0} {row.Fixture.Writer,-12} {Label(row.Cores),-9} {row.Scenario.Name,-14} " +
                $"{Work(row.Aot),-24} {(row.Cores == Cores.One ? Work(row.Jit) : "-"),-24} " +
                $"{Work(row.Theirs, asked),-23} " +
                $"{Speedup(row),-7} {Throughput(row, row.Aot),9} {Throughput(row, row.Theirs),9} {Bytes(row.Allocated),10}  " +
                $"{Side(row.Aot, m => m.RssBytes.Median / (1024 * 1024)),8}  " +
                $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024), asked: asked),8}  " +
                $"{(row.Aot is null ? "refused" : row.Aot.Rows.ToString("N0", CultureInfo.InvariantCulture)),10}"));
        }

        if (kernels is { Count: > 0 })
        {
            text.AppendLine();
            text.AppendLine("encoding                                     rows  ours ns/row  rust ns/row  speedup  ours GB/s  rust GB/s");
            foreach (KernelRow kernel in kernels)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{kernel.Encoding,-40} {kernel.Rows,9:N0} {NsPerRow(kernel, kernel.OursUs),12} {NsPerRow(kernel, kernel.TheirsUs),12} " +
                    $"{KernelSpeedup(kernel),8} {KernelThroughput(kernel, kernel.OursUs),10} {KernelThroughput(kernel, kernel.TheirsUs),10}" +
                    $"{(kernel.Problem is null ? string.Empty : "  " + kernel.Problem)}"));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The plain bytes of the rows <paramref name="row"/>'s scenario returned: every column of them,
    /// or the one a projection keeps; none for a scenario whose output is not its work.
    /// </summary>
    /// <remarks>
    /// An open returns nothing, a take a thousand rows whose cost is their chunks and not their
    /// bytes, and an append copies the file before it writes: a throughput would describe none of
    /// them.
    /// </remarks>
    private static long PlainBytes(Row row)
    {
        if (row.Aot is null || row.Scenario.Name is "open" or "take" or "append")
        {
            return 0;
        }

        long[] plain = row.Fixture.Plain;
        long columns = row.Scenario.Name == "project"
            ? plain[Array.FindIndex(row.Fixture.Columns, c => c.Name == Set.Field)]
            : plain.Sum();
        return columns * row.Aot.Rows / row.Fixture.Rows;
    }

    /// <summary>A side's gigabytes a second over the plain bytes of its scenario, or a dash.</summary>
    private static string Throughput(Row row, Measurement? measurement) =>
        PlainBytes(row) is > 0 and long bytes && measurement is { WorkMs.Median: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (measurement.WorkMs.Median * 1e6):F1}")
            : "—";

    /// <summary>A byte count in the unit that keeps it short.</summary>
    private static string Bytes(long bytes) => bytes switch
    {
        < 0 => "—",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F1} MiB"),
    };

    private static string NsPerRow(KernelRow kernel, double micros)
    {
        if (micros <= 0 || kernel.Rows == 0)
        {
            return "—";
        }

        double ns = micros * 1000 / kernel.Rows;
        return ns < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{ns:F2}")
            : string.Create(CultureInfo.InvariantCulture, $"{ns:F1}");
    }

    /// <summary>The reference's warm scan over ours, or 0 when either side has none.</summary>
    private static double KernelSpeedupOf(KernelRow kernel) =>
        kernel.Problem is null && kernel.OursUs > 0 && kernel.TheirsUs > 0 ? kernel.TheirsUs / kernel.OursUs : 0;

    private static string KernelSpeedup(KernelRow kernel) =>
        KernelSpeedupOf(kernel) is > 0 and double speedup ? ResultsPage.Speedup(speedup) : "n/a";

    private static string KernelThroughput(KernelRow kernel, double micros) =>
        micros <= 0 || kernel.Plain == 0
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{kernel.Plain / (micros * 1000):F1}");

    /// <summary>The action's time inside the process, median with the spread the runs showed.</summary>
    private static string Work(Measurement? measurement, bool asked = true) =>
        measurement is null
            ? Absent(asked)
            : string.Create(CultureInfo.InvariantCulture,
                $"{measurement.WorkMs.Median:F1} ({measurement.WorkMs.Low:F1}-{measurement.WorkMs.High:F1})");

    private static string Side(
        Measurement? measurement, Func<Measurement, double> of, string unit = "", bool asked = true) =>
        measurement is null
            ? Absent(asked)
            : string.Create(CultureInfo.InvariantCulture, $"{of(measurement):F0}{unit}");

    /// <summary>
    /// Why a cell is empty: a side that was asked and declined, or one that was never asked.
    /// </summary>
    /// <remarks>
    /// The two are not the same claim, and printing "refused" for both says the reference failed at
    /// something it was never given. The shim has no append entry point; that is a gap in this
    /// harness, not a verdict on the reference.
    /// </remarks>
    private static string Absent(bool asked) => asked ? "refused" : "not asked";

    /// <summary>
    /// The scenarios on both files at both sizes and both core counts, what they measure above them
    /// and what they say below, for the page's <c>scenarios</c> section.
    /// </summary>
    private static string ScenariosSection(List<Row> table, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("## Reading and writing a table, process against process");
        text.AppendLine();
        text.AppendLine("* **The data.** A table of four columns: `monotone`, an increasing `i64` that the filters and");
        text.AppendLine("  the projection use; `value`, an `f64`; `label`, one of five short strings; `flag`, a `bool`");
        text.AppendLine("  with nulls. 2^20 rows, and ten times as many.");
        text.AppendLine("* **Two files of the same rows.** Each writer chooses its own encodings, and what a read costs");
        text.AppendLine("  depends on them, so the rows are written twice: by Vorticity's writer (*Vorticity's file*)");
        text.AppendLine("  and by Rust's (*Vortex Rust's file*). Both readers read both files; each size lists what the");
        text.AppendLine("  two writers chose.");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"* **One core, or all {Environment.ProcessorCount}.** On one core, Rust's single-threaded runtime and our scans at one"));
        text.AppendLine("  lane. On all of them, a Tokio worker per processor for Rust; for Vorticity, a lane per");
        text.AppendLine("  processor for a scan and as many threads for the writer, the degree a session's");
        text.AppendLine("  `MaxDegreeOfParallelism` gives both.");
        text.AppendLine("* **The same work on both sides.** Both map the file and read it where it lies, Rust through");
        text.AppendLine("  `open_buffer` over a `memmap2` mapping; both decode every value they return to its plain form,");
        text.AppendLine("  a constant column kept as one value on both; a write hands its bytes to a sink that keeps none,");
        text.AppendLine("  on both. On one core Rust is timed under each of its two ways of splitting a scan, its default");
        text.AppendLine("  and one split per chunk, and its figure is the faster; on all cores it keeps its default.");
        text.AppendLine("* **A figure** is the time a fresh process takes for its action, from opening the file to its");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  last row, by its own clock: {runs} runs a side, the sides taking turns after one discarded run, the"));
        text.AppendLine("  median with the lowest and highest beside it. Both sides must return the same rows or the");
        text.AppendLine("  run fails.");
        if (table.FirstOrDefault(r => r.Fixture.Plain.Length > 0) is { } sized)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"* **Throughput** is the plain size of the rows returned over the time: {(double)sized.Fixture.Plain.Sum() / sized.Fixture.Rows:F1} bytes a row here."));
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"* **Allocated** is what Vorticity allocates on its managed heap for the action: the median of {AllocationRounds - AllocationColdRounds}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  calls in a process that has made {AllocationColdRounds} before them, the cost of a call once the pools are filled."));
        text.AppendLine("  On all cores a call now and then allocates several times the median. Rust's allocator");
        text.AppendLine("  reports no such figure. **Peak** is each process's peak resident memory.");
        text.AppendLine();
        foreach (int rows in Sizes)
        {
            List<Row> of = [.. table.Where(r => r.Fixture.Rows == rows)];
            if (of.Count == 0)
            {
                continue;
            }

            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"### {rows:N0} rows"));
            text.AppendLine();
            List<Fixture> files = [.. of.Select(r => r.Fixture).Distinct()];
            text.AppendLine("| column | " + string.Join(" | ", files.Select(f => string.Create(
                CultureInfo.InvariantCulture, $"{FileName(f)}, {f.Bytes:N0} bytes"))) + " |");
            text.AppendLine("|---|" + string.Concat(files.Select(_ => "---|")));
            for (int column = 0; column < files[0].Columns.Length; column++)
            {
                text.AppendLine($"| `{files[0].Columns[column].Name}` | " +
                    string.Join(" | ", files.Select(f => f.Columns[column].Layout)) + " |");
            }

            text.AppendLine();
            foreach (Cores cores in (Cores[])[Cores.One, Cores.All])
            {
                List<Row> at = [.. of.Where(r => r.Cores == cores)];
                if (at.Count == 0)
                {
                    continue;
                }

                text.AppendLine(cores == Cores.One
                    ? "#### One core"
                    : string.Create(CultureInfo.InvariantCulture, $"#### All {Environment.ProcessorCount} cores"));
                text.AppendLine();
                text.AppendLine("| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |");
                text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
                foreach (Scenario scenario in Scenarios)
                {
                    foreach (Row row in at.Where(r => r.Scenario == scenario))
                    {
                        bool asked = row.Scenario.Reference is not null;
                        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"| `{row.Scenario.Name}` | {FileName(row.Fixture)} | {Work(row.Aot)} | " +
                            $"{Work(row.Theirs, asked)} | {Speedup(row)} | {Throughput(row, row.Aot)} | " +
                            $"{Throughput(row, row.Theirs)} | {Bytes(row.Allocated)} | {Peak(row, asked)} |"));
                    }
                }

                text.AppendLine();
            }
        }

        text.AppendLine(string.Join(" ", Scenarios.Select(s => $"`{s.Name}`: {s.What}.")));
        text.AppendLine();
        text.Append(ScenarioReading(table));
        text.Append(JitSection(table));
        text.AppendLine(ResultsPage.Provenance(Reference()));
        return text.ToString();
    }

    /// <summary>The per-encoding table, what it measures above it and what it says below, for the page's <c>decoding</c> section.</summary>
    private static string DecodingSection(List<KernelRow> kernels)
    {
        StringBuilder text = new StringBuilder(KernelSection(kernels));
        List<KernelRow> decoded = [.. kernels.Where(k => KernelSpeedupOf(k) > 0)];
        if (decoded.Count > 0)
        {
            List<double> speedups = [.. decoded.Select(KernelSpeedupOf)];
            List<KernelRow> behind = [.. decoded.Where(k => KernelSpeedupOf(k) <= 1).OrderBy(KernelSpeedupOf)];
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $"Vorticity decoded {decoded.Count - behind.Count} of {decoded.Count} files in less time than Rust; the median speedup is {ResultsPage.Speedup(Median(speedups))}."));
            text.AppendLine(behind.Count == 0
                ? string.Empty
                : $" At 1.00x or below: {string.Join(", ", behind.Select(k => $"`{k.Encoding}` {KernelSpeedup(k)}"))}.");
            text.AppendLine();
        }

        text.AppendLine(ResultsPage.Provenance(Reference()));
        return text.ToString();
    }

    /// <summary>
    /// The other side of a split count, named: the files or axes under the reference's default when
    /// they are the fewer, or else "the other N", with the ones under one split per chunk named.
    /// </summary>
    /// <param name="perChunk">The names timed under one split per chunk.</param>
    /// <param name="byDefault">The names timed under the default.</param>
    /// <param name="separator">Between two names: a semicolon for axes, whose names hold commas.</param>
    internal static string Fewer(List<string> perChunk, List<string> byDefault, string separator = ", ") =>
        byDefault.Count == 0 ? "none"
        : byDefault.Count <= perChunk.Count ? string.Join(separator, byDefault)
        : string.Create(CultureInfo.InvariantCulture, $"the other {byDefault.Count}, one split per chunk on {string.Join(separator, perChunk)}");

    /// <summary>The reference build the figures were taken against, for a section's provenance.</summary>
    private static string Reference() => $"Vortex 0.86.1, {RustVersion()}";

    /// <summary>How the page names a file: after the writer that made it.</summary>
    private static string FileName(Fixture fixture) => fixture.Writer == Ours ? "Vorticity's" : "Vortex Rust's";

    /// <summary>The two processes' peak resident memory, in one cell; a dash for a side not asked.</summary>
    private static string Peak(Row row, bool asked) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Side(row.Aot, m => m.RssBytes.Median / (1024 * 1024))} / {(asked ? Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024)) : "—")} MiB");

    /// <summary>The per-encoding table, with what it measures above it.</summary>
    private static string KernelSection(List<KernelRow> kernels)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("## Decoding, per encoding");
        text.AppendLine();
        text.AppendLine("One file per encoding, and per notable shape of one, written by Rust's writer");
        text.AppendLine("(`bench/gen-throughput.sh`): a million rows each, `table_wide` fifty columns of fifty thousand.");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Each side scans the file, every value decoded to its plain form, {KernelRounds} times in a process of its own on"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"one core; a figure is the median of the last {KernelRounds - KernelColdRounds} scans, and the median of {KernelProcesses} processes: what a"));
        text.AppendLine("decoder costs once warm. Each scan opens the file and maps it anew, on both sides, and reads it");
        text.AppendLine("where it lies: a column stored in its plain form is a view of the mapping on both sides, nothing");
        text.AppendLine("decoded and nothing copied, and its figure, past what memory can move, measures the walk of the");
        text.AppendLine("layout. Throughput is over the plain size, as above.");
        text.AppendLine();
        text.AppendLine("| encoding | rows | Vorticity, ns/row | Vortex Rust, ns/row | speedup | Vorticity, GB/s | Vortex Rust, GB/s |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (KernelRow kernel in kernels)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| `{kernel.Encoding}` | {kernel.Rows:N0} | {NsPerRow(kernel, kernel.OursUs)} | {NsPerRow(kernel, kernel.TheirsUs)} | " +
                $"{KernelSpeedup(kernel)} | {KernelThroughput(kernel, kernel.OursUs)} | {KernelThroughput(kernel, kernel.TheirsUs)} |"));
        }

        text.AppendLine();
        List<string> perChunk = [.. kernels.Where(k => k.TheirsPerChunk).Select(k => $"`{k.Encoding}`")];
        List<string> byDefault = [.. kernels.Where(k => !k.TheirsPerChunk).Select(k => $"`{k.Encoding}`")];
        text.AppendLine("Rust is timed under each of its two ways of splitting a scan, three processes each, and its");
        text.AppendLine("figure is the faster: its default cuts a chunk of more than 100,000 rows into splits of 100,000 and");
        text.AppendLine("decodes the whole chunk again for each, which on these files of one chunk of a million rows is ten");
        text.AppendLine("times; one split per chunk decodes it once, but builds a chunk made of smaller arrays in one");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"piece. One split per chunk was the faster on {perChunk.Count} of {kernels.Count} files, its default on {Fewer(perChunk, byDefault)}."));
        text.AppendLine();
        foreach (KernelRow kernel in kernels.Where(k => k.Problem is not null))
        {
            text.AppendLine($"`{kernel.Encoding}`: {kernel.Problem}.");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The framework-dependent build's first call, on one core, beside nothing: it has no counterpart.</summary>
    private static string JitSection(List<Row> table)
    {
        List<Row> jit = [.. table.Where(r => r.Cores == Cores.One && r.Jit is not null)];
        if (jit.Count == 0)
        {
            return string.Empty;
        }

        List<Fixture> files = [.. jit.Select(r => r.Fixture).Distinct()];
        StringBuilder text = new StringBuilder();
        text.AppendLine("### A first call on the JIT");
        text.AppendLine();
        text.AppendLine("The same scenarios in the framework-dependent build, under `dotnet`, on one core: what a");
        text.AppendLine("process that is not compiled ahead of time pays the first time, the code compiling as it runs.");
        text.AppendLine("Milliseconds, the median of the same runs.");
        text.AppendLine();
        text.AppendLine("| scenario | " + string.Join(" | ", files.Select(f => string.Create(
            CultureInfo.InvariantCulture, $"{f.Rows:N0} rows, {FileName(f)} file"))) + " |");
        text.AppendLine("|---|" + string.Concat(files.Select(_ => "---:|")));
        foreach (Scenario scenario in Scenarios)
        {
            List<string> cells = [];
            foreach (Fixture file in files)
            {
                Row? row = jit.FirstOrDefault(r => r.Scenario == scenario && r.Fixture == file);
                cells.Add(row?.Jit is { } measured
                    ? string.Create(CultureInfo.InvariantCulture, $"{measured.WorkMs.Median:F1}")
                    : "—");
            }

            if (cells.Any(c => c != "—"))
            {
                text.AppendLine($"| `{scenario.Name}` | {string.Join(" | ", cells)} |");
            }
        }

        text.AppendLine();
        return text.ToString();
    }

    /// <summary>
    /// The reading, computed from the rows rather than written: which way the scenarios went at each
    /// core count, what the process start costs, and which rows have no counterpart.
    /// </summary>
    private static string ScenarioReading(List<Row> table)
    {
        List<Row> compared = [.. table.Where(r => SpeedupOf(r) > 0)];
        StringBuilder text = new StringBuilder();

        foreach (Cores cores in (Cores[])[Cores.One, Cores.All])
        {
            List<Row> at = [.. compared.Where(r => r.Cores == cores)];
            if (at.Count == 0)
            {
                continue;
            }

            Row best = at.MaxBy(SpeedupOf)!;
            Row worst = at.MinBy(SpeedupOf)!;
            int ahead = at.Count(r => SpeedupOf(r) > 1);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**{(cores == Cores.One ? "On one core" : $"On all {Environment.ProcessorCount} cores")}.** Vorticity took less time than Rust on {ahead} of {at.Count} compared rows."));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"The highest speedup is `{best.Scenario.Name}` at {best.Fixture.Rows:N0} rows on {FileName(best.Fixture)} file ({Speedup(best)}), the lowest"));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"`{worst.Scenario.Name}` at {worst.Fixture.Rows:N0} rows on {FileName(worst.Fixture)} file ({Speedup(worst)})."));
            text.AppendLine();
        }

        Row? floor = table.FirstOrDefault(
            r => r.Scenario.Name == "open" && r.Cores == Cores.One && r.Aot is not null && r.Theirs is not null);
        if (floor is { Aot: { } ourFloor, Theirs: { } theirFloor })
        {
            // The process start is the whole process less the action it timed itself, on the row
            // whose action is the smallest.
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**The process start** is not in the figures: {ourFloor.WallMs.Median - ourFloor.WorkMs.Median:F0} ms for Vorticity's native binary and " +
                $"{theirFloor.WallMs.Median - theirFloor.WorkMs.Median:F0} ms for Rust's,"));
            text.AppendLine(floor.Jit is { } jitFloor
                ? string.Create(CultureInfo.InvariantCulture,
                    $"most of it the operating system starting a binary, and {jitFloor.WallMs.Median - jitFloor.WorkMs.Median:F0} ms for the managed runtime on the JIT.")
                : "most of it the operating system starting a binary.");
            text.AppendLine();
        }

        List<Row> refused = [.. table.Where(r => r.Scenario.Reference is not null && r.Theirs is null)];
        if (refused.Count > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Where Rust refused**, the row stays with the reason: " +
                $"{string.Join(", ", refused.Select(r => $"`{r.Scenario.Name}` at {r.Fixture.Rows:N0} rows on {FileName(r.Fixture)} file, {Label(r.Cores)}"))}."));
            foreach (string reason in refused.Select(r => r.Refusal).OfType<string>().Distinct())
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"It said: *{reason}*."));
            }

            text.AppendLine();
        }

        // Per file rather than per row: the two splits differ only where a chunk holds more than
        // 100 000 rows, so on a file of smaller chunks which one wins is noise.
        IEnumerable<string> perFile = table
            .Where(r => r.Cores == Cores.One && r.Theirs is not null)
            .GroupBy(r => FileName(r.Fixture))
            .Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{g.Count(r => r.TheirsPerChunk)} of {g.Count()} rows on {g.Key} file"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"**Rust's splits.** On one core, one split per chunk was the faster of its two on {string.Join(" and ", perFile)};"));
        text.AppendLine("the two differ only where a chunk holds more than 100,000 rows.");
        text.AppendLine();

        List<string> alone = [.. table.Where(r => r.Scenario.Reference is null)
            .Select(r => $"`{r.Scenario.Name}`").Distinct()];
        if (alone.Count > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Not asked of Rust**: {string.Join(", ", alone)}, which its harness has no entry point for; those figures"));
            text.AppendLine("are Vorticity's alone.");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// The reference's action time over our native build's, the speedup every page prints: above
    /// 1.00x, we took less. 0 when either side has no time to divide.
    /// </summary>
    private static double SpeedupOf(Row row) =>
        row.Theirs is null || row.Aot is null || row.Aot.WorkMs.Median <= 0
            ? 0
            : row.Theirs.WorkMs.Median / row.Aot.WorkMs.Median;

    private static string Speedup(Row row) =>
        SpeedupOf(row) is > 0 and double speedup ? ResultsPage.Speedup(speedup) : "n/a";

    private static string First(string message)
    {
        string trimmed = message.Trim();
        int end = trimmed.IndexOf('\n');
        return end < 0 ? trimmed : trimmed[..end];
    }

    private static string Header(int runs) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{runs} runs of each scenario, each in its own process, the median reported with the " +
            $"lowest and highest beside it; one discarded run before them.\n" +
            $"Figures are the action's time inside the process, from its own clock; the process start is reported apart.\n" +
            $"Speedup is the reference's time over our Native AOT build's: above 1.00x, we took less.\n" +
            $"machine: {ResultsPage.Processor()} ({RuntimeInformation.OSArchitecture}), " +
            $"{Environment.ProcessorCount} processors, {RuntimeInformation.OSDescription}\n" +
            $"runtime: {RuntimeInformation.FrameworkDescription}, as Native AOT for this instruction set and on the JIT; " +
            $"reference: Vortex 0.86.1, upstream's benchmark build (mimalloc, target-cpu=native, codegen-units=1, no LTO), " +
            $"{RustVersion()}\n" +
            $"commit: {ResultsPage.Commit()}\n" +
            $"date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

    /// <summary>The Rust compiler the reference was built with, as `rustc --version` says it.</summary>
    private static string RustVersion() => ResultsPage.Ask("rustc", ["--version"]);
}
