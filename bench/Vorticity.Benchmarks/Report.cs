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
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

using Set = Vorticity.Bench.Scenarios.ScenarioSet;

namespace Vorticity.Benchmarks;

/// <summary>
/// The published comparison: high-level scenarios, each side in its own process, measured by wall
/// time, peak resident memory and processor time.
/// </summary>
/// <remarks>
/// Why processes rather than the in-process ratios the rest of the bench uses: those three figures
/// belong to a process. A loop that shares one with the other implementation can report neither a
/// peak nor a total, and a reader deciding between two libraries wants what a run of each costs.
/// The price is that the fixed cost of starting a runtime is inside every figure, which is why the
/// table carries it as its own scenario rather than subtracting it silently.
/// </remarks>
internal static class Report
{
    /// <summary>The column the filter and the projection read. An i64: the reference's predicate
    /// builds an i64 literal and refuses a narrower column rather than coercing it.</summary>
    private const string Field = "monotone";

    /// <summary>The lower edge of both filter bands.</summary>
    private const long BandLow = 1_000_000;

    /// <summary>Rows a scattered take asks for.</summary>
    private const long TakeCount = 1_000;

    private static readonly int[] Sizes = [1_000_000, 10_000_000];

    /// <summary>One scenario: what each side is asked to do, and how the rows are compared.</summary>
    private sealed record Scenario(
        string Name,
        string What,
        Func<string, int, Task<long>> Ours,
        Func<string, int, string[]>? Reference);

    private static readonly Scenario[] Scenarios =
    [
        new("open", "open the file and read no rows",
            (path, rows) => FooterOnlyAsync(path),
            (path, rows) => ["open", path]),
        new("scan", "read every column of every row",
            (path, rows) => Set.ScanAll(path),
            (path, rows) => ["scan", path]),
        new("project", "read one column of four",
            (path, rows) => Set.ScanProjectedField(path, Field),
            (path, rows) => ["project", path, Field]),
        new("filter-narrow", "read the rows of a band holding about one in a hundred",
            (path, rows) => Set.FilteredScan(path, BandLow, rows / 100),
            (path, rows) => ["filter", path, Field, BandLow.ToString(CultureInfo.InvariantCulture),
                (rows / 100).ToString(CultureInfo.InvariantCulture)]),
        new("filter-wide", "read the rows of a band holding about half",
            (path, rows) => Set.FilteredScan(path, BandLow, rows / 2),
            (path, rows) => ["filter", path, Field, BandLow.ToString(CultureInfo.InvariantCulture),
                (rows / 2).ToString(CultureInfo.InvariantCulture)]),
        new("take", "take a thousand rows spread across the file",
            (path, rows) => Set.ScatteredTake(path, TakeCount, rows / TakeCount),
            (path, rows) => ["take", path, TakeCount.ToString(CultureInfo.InvariantCulture),
                (rows / TakeCount).ToString(CultureInfo.InvariantCulture)]),
        new("write", "read the file and encode it back out",
            (path, rows) => Set.ReadAndWrite(path),
            (path, rows) => ["write", path]),
        new("append", "append a tenth of the rows to a copy of the file",
            (path, rows) => AppendAsync(path, rows / 10),
            null),
    ];

    /// <summary>Runs one scenario in this process and reports what it cost.</summary>
    internal static async Task<int> ScenarioAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --scenario <name> <file.vortex> <rows>");
            return 2;
        }

        Scenario? scenario = Scenarios.FirstOrDefault(s => s.Name == args[1]);
        if (scenario is null)
        {
            Console.Error.WriteLine($"no scenario named '{args[1]}'");
            return 2;
        }

        if (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows))
        {
            Console.Error.WriteLine($"'{args[3]}' is not a row count");
            return 2;
        }

        long delivered = await scenario.Ours(args[2], rows).ConfigureAwait(false);
        Process self = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rows={delivered} cpu_ms={self.TotalProcessorTime.TotalMilliseconds:F0} rss_bytes={PeakResident()}"));
        return 0;
    }

    /// <summary>Runs every scenario on both sides and prints the table.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        int runs = Count(args, "--runs", 5);
        bool markdown = Array.IndexOf(args, "--markdown") >= 0;
        string? reference = LocateReference();
        if (reference is null)
        {
            Console.Error.WriteLine(
                "The reference binary is missing: tools/vxbench-rs/target/release/vxbench. " +
                "Build it with: cd tools/vxbench-rs && cargo build --release");
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
                Measurement? ours = await MeasureAsync(
                    OurCommand(scenario.Name, path, rows), runs).ConfigureAwait(false);
                Measurement? theirs = scenario.Reference is null
                    ? null
                    : await MeasureAsync(
                        (reference, scenario.Reference(path, rows)), runs).ConfigureAwait(false);

                if (ours is { } mine && theirs is { } other && other.Rows != mine.Rows)
                {
                    disagreed = true;
                    Console.Error.WriteLine(
                        $"{scenario.Name} at {rows:N0}: we rendered {mine.Rows} rows and the " +
                        $"reference {other.Rows}. A ratio between two different answers is not a ratio.");
                }

                table.Add(new Row(rows, bytes, scenario, ours, theirs));
                Console.Error.WriteLine($"  {scenario.Name}: " +
                    (ours is null ? "refused" : $"{ours.WallMs.Median:F0} ms") + " against " +
                    (theirs is null ? "no reference" : $"{theirs.WallMs.Median:F0} ms"));
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

    private static async Task<Measurement?> MeasureAsync((string Exe, string[] Args) command, int runs)
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
                Console.Error.WriteLine(
                    $"  refused: {Path.GetFileName(command.Exe)} {string.Join(' ', command.Args.Take(2))} " +
                    $"exited {child.ExitCode}: {First(error)}");
                return null;
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

        return new Measurement(rows, Spread.Of(wall), Spread.Of(cpu), Spread.Of(rss));
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

    private static (string Exe, string[] Args) OurCommand(string scenario, string path, int rows)
    {
        string host = Environment.ProcessPath ?? "dotnet";
        string[] arguments = ["--scenario", scenario, path, rows.ToString(CultureInfo.InvariantCulture)];

        // Under `dotnet run` the host is the muxer and the assembly has to be named; a published
        // executable takes the arguments directly.
        return Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (host, [Assembly.GetExecutingAssembly().Location, .. arguments])
            : (host, arguments);
    }

    private static string? LocateReference()
    {
        string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vxbench.exe" : "vxbench";
        string relative = Path.Combine("tools", "vxbench-rs", "target", "release", name);
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

    /// <summary>
    /// The peak resident set of this process, in bytes. <c>ru_maxrss</c> is bytes on macOS and
    /// kilobytes elsewhere, and sits at the same offset on both because the two timevals before it
    /// are sixteen bytes either way.
    /// </summary>
    private static long PeakResident()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Process.GetCurrentProcess().PeakWorkingSet64;
        }

        byte[] usage = new byte[512];
        if (GetRUsage(0, usage) != 0)
        {
            return -1;
        }

        long maxrss = BitConverter.ToInt64(usage, 32);
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? maxrss : maxrss * 1024;
    }

    [DllImport("libc", EntryPoint = "getrusage", SetLastError = true)]
    private static extern int GetRUsage(int who, byte[] usage);

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
            [Field, "value", "label", "flag"],
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
                    monotone[i] = BandLow + row;
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

    /// <summary>What an open costs on its own: the footer, and no row read.</summary>
    private static async Task<long> FooterOnlyAsync(string path)
    {
        await using Vorticity.File.VortexFile file =
            await Vorticity.File.VortexFile.OpenAsync(path).ConfigureAwait(false);
        return file.RowCount;
    }

    /// <summary>Appends to a copy, so the scenario can run again on the same fixture.</summary>
    private static async Task<long> AppendAsync(string path, int rows)
    {
        string copy = path + ".append";
        System.IO.File.Copy(path, copy, overwrite: true);
        try
        {
            await using VortexFileWriter appender =
                await VortexFileWriter.AppendAsync(copy).ConfigureAwait(false);
            long start = appender.RowCount;
            await using Vorticity.File.VortexFile source =
                await Vorticity.File.VortexFile.OpenAsync(path).ConfigureAwait(false);
            long written = 0;
            await foreach (RecordBatch batch in source.Scan()
                .Rows(new RowRange(0, rows)).ExecuteAsync().ConfigureAwait(false))
            {
                using (batch)
                {
                    await appender.WriteAsync(batch).ConfigureAwait(false);
                    written += batch.RowCount;
                }
            }

            WriteReport report = await appender.CompleteAsync().ConfigureAwait(false);
            return report.RowCount - start == 0 ? written : report.RowCount;
        }
        finally
        {
            System.IO.File.Delete(copy);
        }
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

    private sealed record Row(int Rows, long Bytes, Scenario Scenario, Measurement? Ours, Measurement? Theirs);

    private static string Text(List<Row> table, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(Header(runs));
        text.AppendLine();
        text.AppendLine("rows       scenario       ours, ms (low-high)  rust, ms (low-high)  ratio   " +
            "ours MiB  rust MiB  rows out");
        foreach (Row row in table)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{row.Rows,-10:N0} {row.Scenario.Name,-14} {Wall(row.Ours),-20} {Wall(row.Theirs),-20} " +
                $"{Ratio(row),-7} {Side(row.Ours, m => m.RssBytes.Median / (1024 * 1024)),8}  " +
                $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024)),8}  " +
                $"{(row.Ours is null ? "refused" : row.Ours.Rows.ToString("N0", CultureInfo.InvariantCulture)),10}"));
        }

        return text.ToString();
    }

    private static string Side(Measurement? measurement, Func<Measurement, double> of, string unit = "") =>
        measurement is null
            ? "refused"
            : string.Create(CultureInfo.InvariantCulture, $"{of(measurement):F0}{unit}");

    /// <summary>The median with the spread the runs actually showed, which is what says whether a
    /// difference is one.</summary>
    private static string Wall(Measurement? measurement) =>
        measurement is null
            ? "refused"
            : string.Create(CultureInfo.InvariantCulture,
                $"{measurement.WallMs.Median:F0} ({measurement.WallMs.Low:F0}-{measurement.WallMs.High:F0})");

    private static string Markdown(List<Row> table, int runs)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("# Benchmarks");
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
            text.AppendLine("| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | " +
                "ours, peak | Rust, peak |");
            text.AppendLine("|---|---|---|---|---|---|---|");
            foreach (Row row in of)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| `{row.Scenario.Name}` | {row.Scenario.What} | {Wall(row.Ours)} | " +
                    $"{Wall(row.Theirs)} | {Ratio(row)} | " +
                    $"{Side(row.Ours, m => m.RssBytes.Median / (1024 * 1024), " MiB")} | " +
                    $"{Side(row.Theirs, m => m.RssBytes.Median / (1024 * 1024), " MiB")} |"));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Ratio(Row row) =>
        row.Theirs is null || row.Ours is null || row.Ours.WallMs.Median <= 0
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture,
                $"{row.Theirs.WallMs.Median / row.Ours.WallMs.Median:F2}x");

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
            $"Ratio above 1.00x means this library took less wall time.\n" +
            $"machine: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, " +
            $"{Environment.ProcessorCount} processors\n" +
            $"runtime: {RuntimeInformation.FrameworkDescription}, reference: Vortex 0.86.1\n" +
            $"commit: {Commit()}\n" +
            $"date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

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
