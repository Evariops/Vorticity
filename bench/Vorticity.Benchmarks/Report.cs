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
    private sealed record Fixture(int Rows, string Writer, string Path, long Bytes, (string Name, string Layout)[] Columns);

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

            Fixture[] fixtures =
            [
                new Fixture(rows, Ours, ours, new FileInfo(ours).Length, await ColumnsAsync(ours).ConfigureAwait(false)),
                new Fixture(rows, Theirs, theirs, new FileInfo(theirs).Length, await ColumnsAsync(theirs).ConfigureAwait(false)),
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

        string text = markdown ? Markdown(table, runs) : Text(table, runs);
        int outAt = Array.IndexOf(args, "--out");
        if (outAt >= 0 && outAt + 1 < args.Length)
        {
            await System.IO.File.WriteAllTextAsync(args[outAt + 1], text).ConfigureAwait(false);
            Console.Error.WriteLine($"written to {args[outAt + 1]}");
        }
        else
        {
            Console.WriteLine(text);
        }

        return disagreed ? 1 : 0;
    }

    /// <summary>One scenario on one file at one core count, every side's runs interleaved.</summary>
    private static async Task<Row> MeasureRowAsync(
        Fixture fixture, Cores cores, Scenario scenario, string runner, string reference, int runs)
    {
        string[] threads = cores == Cores.All ? ["--threads", "all"] : [];
        string[] ours = ["--scenario", scenario.Name, fixture.Path, fixture.Rows.ToString(CultureInfo.InvariantCulture), .. threads];
        List<(string Exe, string[] Args)?> commands =
        [
            (runner, ours),
            cores == Cores.One ? OurCommand(ours) : null,
            scenario.Reference is null ? null : (reference, [.. scenario.Reference(fixture.Path, fixture.Rows), .. threads]),
        ];

        List<(Measurement? Measurement, string? Refusal)> measured = await MeasureAsync(commands, runs).ConfigureAwait(false);
        return new Row(fixture, cores, scenario, measured[0].Measurement, measured[1].Measurement, measured[2].Measurement, measured[2].Refusal);
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
    private static async Task WriteFixtureAsync(string path, int rows)
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
    private sealed record Row(
        Fixture Fixture, Cores Cores, Scenario Scenario, Measurement? Aot, Measurement? Jit, Measurement? Theirs,
        string? Refusal);

    private static string Label(Cores cores) =>
        cores == Cores.One ? "one core" : string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessorCount} cores");

    private static string Text(List<Row> table, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(Header(runs));
        text.AppendLine();
        text.AppendLine("rows       file         cores     scenario       ours AOT, ms (low-high)  ours JIT, ms (low-high)  " +
            "rust, ms (low-high)     ratio   ours MiB  rust MiB  rows out");
        foreach (Row row in table)
        {
            bool asked = row.Scenario.Reference is not null;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{row.Fixture.Rows,-10:N0} {row.Fixture.Writer,-12} {Label(row.Cores),-9} {row.Scenario.Name,-14} " +
                $"{Work(row.Aot),-24} {(row.Cores == Cores.One ? Work(row.Jit) : "-"),-24} " +
                $"{Work(row.Theirs, asked),-23} " +
                $"{Ratio(row),-7} {Side(row.Aot, m => m.RssBytes.Median / (1024 * 1024)),8}  " +
                $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024), asked: asked),8}  " +
                $"{(row.Aot is null ? "refused" : row.Aot.Rows.ToString("N0", CultureInfo.InvariantCulture)),10}"));
        }

        return text.ToString();
    }

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

    private static string Markdown(List<Row> table, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("# Benchmarks");
        text.AppendLine();
        text.AppendLine("What this library costs against the Rust implementation, on scenarios a caller");
        text.AppendLine("would recognise, on one core and on all of them. **This page is generated. Do not edit");
        text.AppendLine("it** — every figure comes from");
        text.AppendLine("`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,");
        text.AppendLine("and a hand-written number here would be a number nothing re-measures.");
        text.AppendLine();
        text.AppendLine("Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,");
        text.AppendLine("not affiliated with or endorsed by the Vortex project or LF Projects, LLC.");
        text.AppendLine();
        text.AppendLine("## How the two sides are built and run");
        text.AppendLine();
        text.AppendLine("**Vortex Rust 0.86.1** is built and run as upstream's own benchmarks are: mimalloc as the");
        text.AppendLine("allocator, `-C target-cpu=native -C force-frame-pointers=yes`, one codegen unit, no LTO. On");
        text.AppendLine("one core it runs on its single-threaded runtime, all the work on the calling thread; on all");
        text.AppendLine("cores, on a multi-threaded Tokio runtime of one worker per processor, which is how upstream's");
        text.AppendLine("benchmarks use every core, with each split decoded on its own task, as upstream's Arrow");
        text.AppendLine("conversion does. Its scans decode every column to its plain form: the reference's scan");
        text.AppendLine("otherwise hands back arrays in their stored encodings, which is not the work a reader");
        text.AppendLine("with no other representation does. `tools/vxbench-rs` is the harness.");
        text.AppendLine();
        text.AppendLine("**Vorticity** runs as a Native AOT binary built for this machine's instruction set");
        text.AppendLine("(`IlcInstructionSet=native`), with the workstation garbage collector. On one core its");
        text.AppendLine("scans run at one lane, the library's default; on all cores at one lane per processor,");
        text.AppendLine("`ScanBuilder.DefaultDegreeOfParallelism`, and its writer compresses a column's zstd frames on");
        text.AppendLine("as many threads, `VortexWriteOptions.DegreeOfParallelism`, but chooses and writes every other");
        text.AppendLine("encoding on one, where the reference compresses its chunks on every core. It also runs on one");
        text.AppendLine("core as the framework-dependent build under `dotnet`, the **JIT** column, in which the action");
        text.AppendLine("compiles its own code as it goes: what the first call costs in a fresh `dotnet` process.");
        text.AppendLine();
        text.AppendLine("**Both sides read the same two files**: the one Vorticity's writer makes, and the one Vortex");
        text.AppendLine("Rust's writer makes from the same rows, four columns of 2^20 and ten times as many rows: a");
        text.AppendLine("monotone `i64`, an `f64`, a short `utf8` and a nullable `bool`. Each writer chooses its own");
        text.AppendLine("chunks and encodings, which each size lists first: on a given file, both readers decode the");
        text.AppendLine("same encodings, and from one file to the other, the encodings change what a read costs.");
        text.AppendLine();
        text.AppendLine("**Each side runs in its own process**, once per run, and **times the action from its own");
        text.AppendLine("clock** once the process is up: opening the file, doing the work, rendering the rows. The");
        text.AppendLine("sides take turns run after run, the first of them changing, so that a machine that drifts");
        text.AppendLine("drifts under both. Peak resident memory and processor time are each side's own `getrusage`.");
        text.AppendLine("Both sides render the same rows, and the harness fails rather than print a ratio between two");
        text.AppendLine("different answers. The page cache is warm: one run of each side is discarded first.");
        text.AppendLine();
        text.AppendLine(Header(runs));
        text.AppendLine();
        foreach (int rows in Sizes)
        {
            List<Row> of = [.. table.Where(r => r.Fixture.Rows == rows)];
            if (of.Count == 0)
            {
                continue;
            }

            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## {rows:N0} rows"));
            text.AppendLine();
            List<Fixture> files = [.. of.Select(r => r.Fixture).Distinct()];
            text.AppendLine("| column | " + string.Join(" | ", files.Select(f => string.Create(
                CultureInfo.InvariantCulture, $"as {f.Writer} writes it, {f.Bytes:N0} bytes in all"))) + " |");
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
                    ? "### One core"
                    : string.Create(CultureInfo.InvariantCulture, $"### All {Environment.ProcessorCount} cores"));
                text.AppendLine();
                text.AppendLine(cores == Cores.One
                    ? "| scenario | file written by | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |"
                    : "| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |");
                text.AppendLine(cores == Cores.One ? "|---|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|");
                foreach (Scenario scenario in Scenarios)
                {
                    foreach (Row row in at.Where(r => r.Scenario == scenario))
                    {
                        bool asked = row.Scenario.Reference is not null;
                        string jit = cores == Cores.One ? $" {Work(row.Jit)} |" : string.Empty;
                        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"| `{row.Scenario.Name}` | {row.Fixture.Writer} | {Work(row.Aot)} |{jit} " +
                            $"{Work(row.Theirs, asked)} | {Ratio(row)} | " +
                            $"{Side(row.Aot, m => m.RssBytes.Median / (1024 * 1024), " MiB")} | " +
                            $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024), " MiB", asked)} |"));
                    }
                }

                text.AppendLine();
            }
        }

        text.AppendLine(string.Join(" ", Scenarios.Select(s => $"`{s.Name}`: {s.What}.")));
        text.AppendLine();
        text.Append(Reading(table));
        return text.ToString();
    }

    /// <summary>
    /// The reading, computed from the rows rather than written: which way each scenario went at each
    /// core count, what the startup floor is, and what the table does not say.
    /// </summary>
    private static string Reading(List<Row> table)
    {
        List<Row> compared = [.. table.Where(r => RatioOf(r) > 0)];
        StringBuilder text = new StringBuilder();
        text.AppendLine("## Reading it");
        text.AppendLine();

        Row? floor = table.FirstOrDefault(
            r => r.Scenario.Name == "open" && r.Cores == Cores.One && r.Aot is not null && r.Theirs is not null);
        if (floor is { Aot: { } ourFloor, Theirs: { } theirFloor })
        {
            // The process start is the whole process less the action it timed itself, on the row
            // whose action is the smallest.
            text.AppendLine("**What the table leaves out.** Starting the process, up to the point where it begins");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"its action, costs {ourFloor.WallMs.Median - ourFloor.WorkMs.Median:F0} ms for the native build and " +
                $"{theirFloor.WallMs.Median - theirFloor.WorkMs.Median:F0} ms for the reference on this machine, most of it"));
            text.AppendLine("the operating system starting any binary at all.");
            if (floor.Jit is { } jitFloor)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"The JIT build's start is {jitFloor.WallMs.Median - jitFloor.WorkMs.Median:F0} ms, the managed runtime coming up; what its"));
                text.AppendLine("column then shows is the action compiling its own code as it runs, which is why an");
                text.AppendLine("`open` that takes the native build a fraction of a millisecond takes it tens.");
            }

            text.AppendLine();
        }

        foreach (Cores cores in (Cores[])[Cores.One, Cores.All])
        {
            List<Row> at = [.. compared.Where(r => r.Cores == cores)];
            if (at.Count == 0)
            {
                continue;
            }

            Row best = at.MinBy(RatioOf)!;
            Row worst = at.MaxBy(RatioOf)!;
            int ahead = at.Count(r => RatioOf(r) < 1);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**{(cores == Cores.One ? "On one core" : $"On all {Environment.ProcessorCount} cores")}.** Of {at.Count} compared rows, the native build took less time than"));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"the reference on {ahead}. The best is `{best.Scenario.Name}` at {best.Fixture.Rows:N0} rows on {best.Fixture.Writer}'s file ({Ratio(best)}),"));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"the worst `{worst.Scenario.Name}` at {worst.Fixture.Rows:N0} rows on {worst.Fixture.Writer}'s file ({Ratio(worst)})."));
            text.AppendLine();
        }

        text.AppendLine("A ratio is the native build's time over the reference's: under 1.00x, we took less.");
        text.AppendLine();

        List<Row> refused = [.. table.Where(r => r.Scenario.Reference is not null && r.Theirs is null)];
        if (refused.Count > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Where the reference refused.** No figure for it on " +
                $"{string.Join(", ", refused.Select(r => $"`{r.Scenario.Name}` at {r.Fixture.Rows:N0} rows on {r.Fixture.Writer}'s file, {Label(r.Cores)}"))}."));
            foreach (string reason in refused
                .Select(r => r.Refusal).OfType<string>().Distinct())
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"It said: *{reason}*."));
            }

            text.AppendLine("The harness records the refusal rather than dropping the row: a table that shows only");
            text.AppendLine("what worked is not a comparison.");
            text.AppendLine();
        }

        List<string> alone = [.. table.Where(r => r.Scenario.Reference is null)
            .Select(r => $"`{r.Scenario.Name}`").Distinct()];
        if (alone.Count > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Where there is nothing to compare against.** {string.Join(", ", alone)}: the reference shim"));
            text.AppendLine("exposes no such entry point, so the figure is ours alone and is not a ratio. Those");
            text.AppendLine("cells read `not asked`, which is not the same claim as `refused`.");
            text.AppendLine();
        }

        text.AppendLine("## What this does not measure");
        text.AppendLine();
        text.AppendLine("* **Steady state.** Every figure is a first and only action in a fresh process: a page cache");
        text.AppendLine("  warmed only by the discarded run before it, thread pools starting, and in the JIT column");
        text.AppendLine("  the code compiling as it runs. The per-encoding ratios in `bench/README.md` measure the");
        text.AppendLine("  other thing — the same code after warm-up, in one process — and they are the place to");
        text.AppendLine("  look for what a decoder costs.");
        text.AppendLine("* **Pinned cores.** macOS pins no process to a set of cores, and this machine's cores are of");
        text.AppendLine("  two kinds; both sides see all of them and the same count. Upstream's own figures come");
        text.AppendLine("  from 94 pinned cores of one kind, and are not this machine's.");
        text.AppendLine("* **A cold cache.** Upstream flushes the page cache before each query and keeps the first,");
        text.AppendLine("  cold, run in its median; here the cache is warm for every run, on both sides.");
        text.AppendLine("* **Your data.** One table of four columns is not every file. A column the compressor likes");
        text.AppendLine("  less, or a filter a zone map cannot prune, moves these numbers more than any implementation");
        text.AppendLine("  detail does.");
        text.AppendLine("* **Your machine.** These figures belong to the one named above.");
        return text.ToString();
    }

    /// <summary>
    /// Our native build's action time over the reference's, the direction of every ratio in the
    /// bench: under 1.00x, we took less. 0 when either side has no time to divide.
    /// </summary>
    private static double RatioOf(Row row) =>
        row.Theirs is null || row.Aot is null || row.Theirs.WorkMs.Median <= 0
            ? 0
            : row.Aot.WorkMs.Median / row.Theirs.WorkMs.Median;

    private static string Ratio(Row row) =>
        RatioOf(row) is > 0 and double ratio
            ? string.Create(CultureInfo.InvariantCulture, $"{ratio:F2}x")
            : "n/a";

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
            $"Ratio is our Native AOT build's time over the reference's: under 1.00x, we took less.\n" +
            $"machine: {Processor()} ({RuntimeInformation.OSArchitecture}), " +
            $"{Environment.ProcessorCount} processors, {RuntimeInformation.OSDescription}\n" +
            $"runtime: {RuntimeInformation.FrameworkDescription}, as Native AOT for this instruction set and on the JIT; " +
            $"reference: Vortex 0.86.1, upstream's benchmark build (mimalloc, target-cpu=native, codegen-units=1, no LTO), " +
            $"{RustVersion()}\n" +
            $"commit: {Commit()}\n" +
            $"date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

    /// <summary>The Rust compiler the reference was built with, as `rustc --version` says it.</summary>
    private static string RustVersion() => Ask("rustc", ["--version"]);

    /// <summary>
    /// The processor, by name. A ratio between two implementations is a property of the machine as
    /// much as of the code, and "Arm64" does not say which one.
    /// </summary>
    private static string Processor()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Ask("sysctl", ["-n", "machdep.cpu.brand_string"]);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && System.IO.File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in System.IO.File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RuntimeInformation.OSArchitecture.ToString();
        }

        return RuntimeInformation.OSArchitecture.ToString();
    }

    private static string Ask(string exe, string[] arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process? child = Process.Start(start);
            if (child is null)
            {
                return "unknown";
            }

            string answer = child.StandardOutput.ReadToEnd().Trim();
            child.WaitForExit();
            return child.ExitCode == 0 && answer.Length > 0 ? answer : "unknown";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>The commit the figures belong to, so a table outliving its run says what it measured.</summary>
    private static string Commit()
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("rev-parse");
            start.ArgumentList.Add("--short");
            start.ArgumentList.Add("HEAD");
            using Process? git = Process.Start(start);
            if (git is null)
            {
                return "unknown";
            }

            string head = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && head.Length > 0 ? head : "unknown";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}
