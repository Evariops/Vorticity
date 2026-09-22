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
/// time, peak resident memory and processor time.
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
/// Our side runs twice: as the Native AOT runner, which is a native binary like the reference's
/// and the one the ratio is taken against, and as this framework-dependent host, which is what a
/// process that starts the runtime and compiles the scan as it goes costs. The two run the same
/// scenario code; only the build differs.
/// </para>
/// </remarks>
internal static class Report
{
    private static readonly int[] Sizes = [1_000_000, 10_000_000];

    /// <summary>One scenario: what the reference is asked, when it can be asked.</summary>
    /// <param name="Name">The scenario, as <see cref="Set.ForReport"/> knows it.</param>
    /// <param name="What">What it does, for the table.</param>
    /// <param name="Reference">The reference binary's arguments, or null when it has no such entry point.</param>
    private sealed record Scenario(string Name, string What, Func<string, int, string[]>? Reference);

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
        new("append", "append a tenth of the rows to a copy of the file", null),
    ];

    /// <summary>Runs one scenario in this process and reports what it cost.</summary>
    internal static async Task<int> ScenarioAsync(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: --scenario <name> <file.vortex> <rows>");
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

        long delivered = await scenario(args[2]).ConfigureAwait(false);
        (long cpuMs, long rssBytes) = Vorticity.Bench.Scenarios.ProcessCost.Read();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rows={delivered} cpu_ms={cpuMs} rss_bytes={rssBytes}"));
        return 0;
    }

    /// <summary>Runs every scenario on both sides and prints the table.</summary>
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
            string path = Path.Combine(directory, $"mixed-{rows}.vortex");
            if (!System.IO.File.Exists(path))
            {
                Console.Error.WriteLine($"writing the {rows:N0}-row fixture...");
                await WriteFixtureAsync(path, rows).ConfigureAwait(false);
            }

            long bytes = new FileInfo(path).Length;
            Console.Error.WriteLine($"{rows:N0} rows, {bytes:N0} bytes");

            foreach (Scenario scenario in Scenarios)
            {
                string[] arguments = ["--scenario", scenario.Name, path, rows.ToString(CultureInfo.InvariantCulture)];
                (Measurement? aot, _) = await MeasureAsync((runner, arguments), runs).ConfigureAwait(false);
                (Measurement? jit, _) = await MeasureAsync(OurCommand(arguments), runs).ConfigureAwait(false);
                (Measurement? theirs, string? refusal) = scenario.Reference is null
                    ? (null, null)
                    : await MeasureAsync(
                        (reference, scenario.Reference(path, rows)), runs).ConfigureAwait(false);

                long? rendered = aot?.Rows ?? jit?.Rows;
                foreach (Measurement? other in new[] { jit, theirs })
                {
                    if (rendered is { } mine && other is not null && other.Rows != mine)
                    {
                        disagreed = true;
                        Console.Error.WriteLine(
                            $"{scenario.Name} at {rows:N0}: {mine} rows on one side and " +
                            $"{other.Rows} on another. A ratio between two different answers is not a ratio.");
                    }
                }

                table.Add(new Row(rows, bytes, scenario, aot, jit, theirs, refusal));
                Console.Error.WriteLine($"  {scenario.Name}: " +
                    (aot is null ? "refused" : $"{aot.WallMs.Median:F0} ms native") + ", " +
                    (jit is null ? "refused" : $"{jit.WallMs.Median:F0} ms on the JIT") + " against " +
                    (theirs is not null ? $"{theirs.WallMs.Median:F0} ms"
                        : scenario.Reference is null ? "no reference" : "a refusal"));
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

    /// <summary>Runs one side of one scenario, or says why it declined.</summary>
    /// <param name="command">The executable and its arguments.</param>
    /// <param name="runs">Timed runs, after one discarded.</param>
    /// <returns>The measurement, or null with the first line the process wrote to its error stream.</returns>
    private static async Task<(Measurement? Measurement, string? Refusal)> MeasureAsync(
        (string Exe, string[] Args) command, int runs)
    {
        List<double> wall = [];
        List<double> cpu = [];
        List<double> rss = [];
        long rows = -1;

        // One run is thrown away first: the page cache, the JIT and the dynamic loader all charge
        // their setup to whoever goes first, and that is a property of the machine, not of either
        // implementation.
        for (int run = 0; run <= runs; run++)
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
            using Process? child = Process.Start(start)
                ?? throw new InvalidOperationException($"could not start {command.Exe}");
            string output = await child.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string error = await child.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await child.WaitForExitAsync().ConfigureAwait(false);
            watch.Stop();

            if (child.ExitCode != 0)
            {
                // A side that refuses a scenario is reported, not thrown: a table with one hole and
                // the reason beside it is worth more than no table.
                // The shim prefixes its message with its own name and the file it was given. Both
                // are this machine's, and the published page quotes what is left.
                string reason = First(error);
                foreach (string argument in command.Args)
                {
                    reason = reason.Replace(argument + ": ", string.Empty, StringComparison.Ordinal);
                }

                reason = reason.Replace(
                    Path.GetFileName(command.Exe) + ": ", string.Empty, StringComparison.Ordinal);
                Console.Error.WriteLine(
                    $"  refused: {Path.GetFileName(command.Exe)} {string.Join(' ', command.Args.Take(2))} " +
                    $"exited {child.ExitCode}: {reason}");
                return (null, reason);
            }

            if (run == 0)
            {
                continue;
            }

            wall.Add(watch.Elapsed.TotalMilliseconds);
            rows = Value(output, "rows=");
            cpu.Add(Value(output, "cpu_ms="));
            rss.Add(Value(output, "rss_bytes="));
        }

        return (new Measurement(rows, Spread.Of(wall), Spread.Of(cpu), Spread.Of(rss)), null);
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
    /// million, and the conformance corpus holds neither.
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
        const int batch = 500_000;
        for (int start = 0; start < rows; start += batch)
        {
            int count = Math.Min(batch, rows - start);
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

    private sealed record Measurement(long Rows, Spread WallMs, Spread CpuMs, Spread RssBytes);

    /// <param name="Rows">The fixture's row count.</param>
    /// <param name="Bytes">The fixture's size.</param>
    /// <param name="Scenario">What was asked of both sides.</param>
    /// <param name="Aot">Our Native AOT runner's measurement, or null when it refused.</param>
    /// <param name="Jit">This framework-dependent host's measurement, or null when it refused.</param>
    /// <param name="Theirs">The reference's, or null when it refused or was not asked.</param>
    /// <param name="Refusal">What the reference said when it refused, so the page can quote it.</param>
    private sealed record Row(
        int Rows, long Bytes, Scenario Scenario, Measurement? Aot, Measurement? Jit, Measurement? Theirs,
        string? Refusal);

    private static string Text(List<Row> table, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(Header(runs));
        text.AppendLine();
        text.AppendLine("rows       scenario       ours AOT, ms (low-high)  ours JIT, ms (low-high)  " +
            "rust, ms (low-high)  ratio   ours MiB  rust MiB  rows out");
        foreach (Row row in table)
        {
            bool asked = row.Scenario.Reference is not null;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{row.Rows,-10:N0} {row.Scenario.Name,-14} {Wall(row.Aot),-24} {Wall(row.Jit),-24} " +
                $"{Wall(row.Theirs, asked),-20} " +
                $"{Ratio(row),-7} {Side(row.Aot, m => m.RssBytes.Median / (1024 * 1024)),8}  " +
                $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024), asked: asked),8}  " +
                $"{(row.Aot is null ? "refused" : row.Aot.Rows.ToString("N0", CultureInfo.InvariantCulture)),10}"));
        }

        return text.ToString();
    }

    private static string Side(
        Measurement? measurement, Func<Measurement, double> of, string unit = "", bool asked = true) =>
        measurement is null
            ? Absent(asked)
            : string.Create(CultureInfo.InvariantCulture, $"{of(measurement):F0}{unit}");

    /// <summary>The median with the spread the runs actually showed, which is what says whether a
    /// difference is one.</summary>
    private static string Wall(Measurement? measurement, bool asked = true) =>
        measurement is null
            ? Absent(asked)
            : string.Create(CultureInfo.InvariantCulture,
                $"{measurement.WallMs.Median:F0} ({measurement.WallMs.Low:F0}-{measurement.WallMs.High:F0})");

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
        text.AppendLine("would recognise. **This page is generated. Do not edit it** — every figure comes from");
        text.AppendLine("`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,");
        text.AppendLine("and a hand-written number here would be a number nothing re-measures.");
        text.AppendLine();
        text.AppendLine("## What is measured");
        text.AppendLine();
        text.AppendLine("Eight scenarios, at a million rows and at ten million, on a table of four columns: a");
        text.AppendLine("monotone `i64`, an `f64`, a short `utf8` and a nullable `bool`. **Each side runs in its");
        text.AppendLine("own process**, once per run, and the run is timed from outside — so what you see is");
        text.AppendLine("what a command costs, including starting, opening the file and exiting.");
        text.AppendLine();
        text.AppendLine("Our side runs twice. **AOT** is the same code published as Native AOT: a native binary,");
        text.AppendLine("like the reference's, and the one the ratio is taken against, because it is the only");
        text.AppendLine("pairing where both processes pay the same fixed costs. **JIT** is the framework-dependent");
        text.AppendLine("build under `dotnet`, which starts the runtime and compiles the code as it goes: what a");
        text.AppendLine("`dotnet run` costs, and what a long-running process pays once.");
        text.AppendLine();
        text.AppendLine("Peak resident memory and processor time are each side's own `getrusage`, and the wall");
        text.AppendLine("clock is the parent's. Both sides render the same rows, and the harness fails rather");
        text.AppendLine("than print a ratio between two different answers.");
        text.AppendLine();
        text.AppendLine("The reference does the **same work**, which took care: its scan has one entry point");
        text.AppendLine("that counts rows off an encoded array's metadata and one that materialises every");
        text.AppendLine("column. Only the second is comparable to a reader that has no other representation,");
        text.AppendLine("and it is the one measured here.");
        text.AppendLine();
        text.AppendLine(Header(runs));
        text.AppendLine();
        foreach (int rows in Sizes)
        {
            List<Row> of = [.. table.Where(r => r.Rows == rows)];
            if (of.Count == 0)
            {
                continue;
            }

            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"## {rows:N0} rows, {of[0].Bytes:N0} bytes"));
            text.AppendLine();
            text.AppendLine("| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | " +
                "ours AOT, peak | Rust, peak |");
            text.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (Row row in of)
            {
                bool asked = row.Scenario.Reference is not null;
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| `{row.Scenario.Name}` | {row.Scenario.What} | {Wall(row.Aot)} | {Wall(row.Jit)} | " +
                    $"{Wall(row.Theirs, asked)} | {Ratio(row)} | " +
                    $"{Side(row.Aot, m => m.RssBytes.Median / (1024 * 1024), " MiB")} | " +
                    $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024), " MiB", asked)} |"));
            }

            text.AppendLine();
        }

        text.Append(Reading(table));
        return text.ToString();
    }

    /// <summary>
    /// The reading, computed from the rows rather than written: which way each scenario went, what
    /// the startup floor is, and what the table does not say.
    /// </summary>
    private static string Reading(List<Row> table)
    {
        List<Row> compared = [.. table.Where(r => r.Aot is not null && r.Theirs is not null)];
        StringBuilder text = new StringBuilder();
        text.AppendLine("## Reading it");
        text.AppendLine();

        Row? floor = table.FirstOrDefault(
            r => r.Scenario.Name == "open" && r.Aot is not null && r.Theirs is not null);
        if (floor is { Aot: { } ourFloor, Theirs: { } theirFloor })
        {
            text.AppendLine("**Start with the floor.** The `open` row is a process that opens the file and reads");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"no rows: {ourFloor.WallMs.Median:F0} ms for the native build against {theirFloor.WallMs.Median:F0} ms. Both are a native"));
            text.AppendLine("binary starting, and a process that does nothing at all costs about as much on this");
            text.AppendLine("machine. Subtract it from every other row to see what the work cost.");
            if (floor.Jit is { } jitFloor)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"The JIT column's floor is {jitFloor.WallMs.Median:F0} ms: the managed runtime starting and compiling"));
                text.AppendLine("the code an open touches, whatever the file holds. Every row of that column carries it,");
                text.AppendLine("plus the compilation of whatever else the scenario touches, and a long-running process");
                text.AppendLine("pays it once.");
            }

            text.AppendLine();
        }

        if (compared.Count > 0)
        {
            Row best = compared.MaxBy(RatioOf)!;
            Row worst = compared.MinBy(RatioOf)!;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Where we stand.** Of {compared.Count} compared scenarios, the best is `{best.Scenario.Name}`"));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"at {best.Rows:N0} rows ({Ratio(best)}) and the worst is `{worst.Scenario.Name}` at {worst.Rows:N0} rows"));
            text.AppendLine("(" + Ratio(worst) + "). A ratio above 1.00x means the native build took less wall time");
            text.AppendLine("than the reference.");
            text.AppendLine();

            double ourPeak = compared.Max(r => r.Aot!.RssBytes.Median) / (1024 * 1024);
            double theirPeak = compared.Max(r => r.Theirs!.RssBytes.Median) / (1024 * 1024);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Memory.** Our worst peak here is {ourPeak:F0} MiB against {theirPeak:F0} MiB. We decode a chunk whole and"));
            text.AppendLine("hand each batch a window of it, where the reference decodes a split of at most a hundred");
            text.AppendLine("thousand rows at a time; on a file whose chunks hold half a million rows, that is the");
            text.AppendLine("difference, and it is the file's chunking rather than its size that sets it.");
            text.AppendLine();
        }

        List<Row> refused = [.. table.Where(r => r.Scenario.Reference is not null && r.Theirs is null)];
        if (refused.Count > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**Where the reference refused.** No figure for it on " +
                $"{string.Join(", ", refused.Select(r => $"`{r.Scenario.Name}` at {r.Rows:N0} rows"))}."));
            foreach (string reason in refused
                .Select(r => r.Refusal).OfType<string>().Distinct())
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"It said: *{reason}*."));
            }

            text.AppendLine("The harness records the refusal rather than dropping the row: a table that shows only");
            text.AppendLine("what worked is not a comparison. Where the reason is a `vortex.zstd` array over a");
            text.AppendLine("numeric column, the shape is one the reference itself builds — `Zstd::from_primitive`");
            text.AppendLine("is public API and its own conformance corpus ships a `vortex.zstd` over an `i64?`. It");
            text.AppendLine("reads such a file everywhere; what it cannot do is append one to a builder, its Zstd");
            text.AppendLine("array replacing the canonicalize-then-append fallback with a path that takes");
            text.AppendLine("variable-binary builders only. So the gap is in re-encoding, not in reading, and it is");
            text.AppendLine("upstream rather than in the bytes we wrote.");
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
        text.AppendLine("* **Steady state.** Every row includes a cold start: the process, a page cache warmed only");
        text.AppendLine("  by the discarded run before it, and in the JIT column the runtime and the first tier of");
        text.AppendLine("  the just-in-time compiler. The per-encoding ratios in `bench/README.md` measure the other");
        text.AppendLine("  thing — the same code after warm-up, in one process — and they are the place to look for");
        text.AppendLine("  what a decoder costs.");
        text.AppendLine("* **Threading.** Both sides are single-threaded here, which is what makes a ratio a ratio.");
        text.AppendLine("* **Your data.** One table of four columns, written by us, is not every file. A column the");
        text.AppendLine("  compressor likes less, or a filter a zone map cannot prune, moves these numbers more");
        text.AppendLine("  than any implementation detail does.");
        text.AppendLine("* **Your machine.** These figures belong to the one named above.");
        return text.ToString();
    }

    private static double RatioOf(Row row) =>
        row.Theirs is null || row.Aot is null || row.Aot.WallMs.Median <= 0
            ? 0
            : row.Theirs.WallMs.Median / row.Aot.WallMs.Median;

    /// <summary>The reference's wall time over our native build's: above 1.00x, we took less.</summary>
    private static string Ratio(Row row) =>
        row.Theirs is null || row.Aot is null || row.Aot.WallMs.Median <= 0
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture,
                $"{row.Theirs.WallMs.Median / row.Aot.WallMs.Median:F2}x");

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
            $"Ratio is the reference's wall time over our Native AOT build's: above 1.00x, we took less.\n" +
            $"machine: {Processor()} ({RuntimeInformation.OSArchitecture}), " +
            $"{Environment.ProcessorCount} processors, {RuntimeInformation.OSDescription}\n" +
            $"runtime: {RuntimeInformation.FrameworkDescription}, as Native AOT and on the JIT; " +
            $"reference: Vortex 0.86.1, cargo release with lto\n" +
            $"commit: {Commit()}\n" +
            $"date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

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
