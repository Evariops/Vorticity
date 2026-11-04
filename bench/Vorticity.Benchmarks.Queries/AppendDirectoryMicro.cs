using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What an append's path directory costs (<c>--micro appenddirectory</c>): <c>--rows</c> log records (<see cref="LogGeneratorV2"/>)
/// drawn once, then appended in objects of <c>--part</c> rows to a dataset in memory partitioned by day, with the directory
/// each append counts and without, the rounds interleaved: the time, the processors' time a row, and the bytes the
/// directories add to the objects. The best of <c>--rounds</c> of each.
/// </summary>
internal static class AppendDirectoryMicro
{
    internal static async Task<int> Run(long rows, int part, int rounds, int degree)
    {
        LogGeneratorV2 generator = new LogGeneratorV2(3_000);
        DateTimeOffset start = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        LogRow[] records = new LogRow[rows];
        for (long row = 0; row < rows; row++)
        {
            records[row] = generator.Next(start.AddTicks(row * TimeSpan.TicksPerSecond / 1_000));
        }

        Dictionary<bool, (double Seconds, double Busy, long Bytes)> best = [];
        for (int round = 0; round < rounds; round++)
        {
            // Interleaved, each round starting with the other mode, so that a drift of the machine falls on both.
            foreach (bool directories in round % 2 == 0 ? [true, false] : (bool[])[false, true])
            {
                (double seconds, double busy, long bytes) = await AppendAsync(records, part, degree, directories);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"round {round} {(directories ? "with   " : "without")} the directory: {seconds:F3} s, {busy * 1e9 / rows:F0} ns of the processors a row, {bytes:N0} bytes"));
                if (!best.TryGetValue(directories, out (double Seconds, double Busy, long Bytes) kept) || busy < kept.Busy)
                {
                    best[directories] = (seconds, busy, bytes);
                }
            }
        }

        (double withSeconds, double withBusy, long withBytes) = best[true];
        (double withoutSeconds, double withoutBusy, long withoutBytes) = best[false];
        long appends = (rows + part - 1) / part;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{rows:N0} log records in appends of {part:N0} rows at {degree} lanes, best of {rounds}: the directory costs {(withBusy - withoutBusy) * 1e9 / rows:F0} ns of the processors a row ({withoutBusy * 1e9 / rows:F0} -> {withBusy * 1e9 / rows:F0}), {(withSeconds - withoutSeconds) * 1e3:F0} ms of {withoutSeconds * 1e3:F0} in time, and {(withBytes - withoutBytes) / (double)appends:N0} bytes an append ({100.0 * (withBytes - withoutBytes) / withoutBytes:F2} %)"));
        return 0;
    }

    /// <summary>The rows appended to a dataset of their own, a part an object: the time, the processors' time, and the objects' bytes.</summary>
    private static async Task<(double Seconds, double Busy, long Bytes)> AppendAsync(LogRow[] records, int part, int degree, bool directories)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, LogRow.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(LogRow.At)).Truncate(CalendarUnit.Day), SortKey = [LayoutValue.Column(nameof(LogRow.At))], Organization = LayoutOrganization.Leveled },
            PathDirectoryOnAppend = directories,
        });

        GC.Collect();
        using Process self = Process.GetCurrentProcess();
        TimeSpan cpu = self.TotalProcessorTime;
        long begin = Stopwatch.GetTimestamp();
        for (int from = 0; from < records.Length; from += part)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<LogRow>(records.AsSpan(from, Math.Min(part, records.Length - from)));
            await dataset.AppendAsync(draft);
        }

        double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
        self.Refresh();
        double busy = (self.TotalProcessorTime - cpu).TotalSeconds;
        long bytes = 0;
        await foreach (DataObject held in dataset.ListObjectsAsync())
        {
            bytes += held.Bytes;
        }

        return (seconds, busy, bytes);
    }
}
