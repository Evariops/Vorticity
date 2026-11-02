using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a consolidation holds, apart from any other work (<c>--micro consolidation --store DIR</c>): <c>--hours</c> hours (4 by
/// default) of the client's log records at <c>--rate</c> a second (5 000), appended in parts of ten minutes under the layout
/// and the declared keys S1's build writes them with, in objects of a quarter of a gibibyte, and left in level 0; then
/// copied, and consolidated alone on one lane, in one piece, and on eight, in ranges side by side. For each job: its passes'
/// seconds and the process's memory over them, the ranges it ran at once and what it reserved. The store of level 0 is built
/// once, in <c>DIR/base</c>; <c>--lanes N</c> consolidates on N lanes alone. <c>--again</c> consolidates each copy twice in the
/// process: the second pays what a node already running pays a row, the process's shelf holding what the first left, where
/// the first pays too each array's first take of a length.
/// </summary>
internal static class ConsolidationMemoryMicro
{
    /// <summary>
    /// The objects the consolidation writes: a quarter of a gibibyte, so that four hours of records, some 1.7 GB, are cut into
    /// six or seven ranges, where S1's day of 10.4 GB wrote objects of 800 MB in eight.
    /// </summary>
    private const long ObjectBytes = 256L << 20;

    internal static async Task<int> Run(string root, int hours, double rate, int only, bool again = false)
    {
        string source = Path.Combine(root, "base");
        if (!System.IO.File.Exists(source + ".built"))
        {
            await AppendAsync(source, hours, rate);
            await System.IO.File.WriteAllTextAsync(source + ".built", string.Create(CultureInfo.InvariantCulture, $"{hours} hours at {rate} a second"));
        }

        foreach (int lanes in only > 0 ? new[] { only } : new[] { 1, 8 })
        {
            string copy = Path.Combine(root, $"lanes-{lanes}");
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }

            for (int pass = 0; pass < (again ? 2 : 1); pass++)
            {
                if (pass > 0)
                {
                    Directory.Delete(copy, recursive: true);
                }

                Copy(source, copy);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{lanes} lanes{(pass > 0 ? ", again" : string.Empty)}:"));
                await ConsolidateAsync(root, copy, lanes);
            }
        }

        return 0;
    }

    /// <summary>The options S1's build writes the log records with, on <paramref name="session"/>.</summary>
    private static DatasetOptions Options(VortexSession session, string scratch) => new DatasetOptions
    {
        Session = session,
        Layout = IdiomLayouts.Logs with { ObjectBytes = ObjectBytes },
        Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(100), ScratchDirectory = scratch },
        ShreddedPaths = [PathsBench.StatusCode, PathsBench.NewCart],
        ShredOnAppend = true,
    };

    /// <summary>The hours' parts appended one after the other, no job run.</summary>
    private static async Task AppendAsync(string directory, int hours, double rate)
    {
        string scratch = Path.Combine(Path.GetDirectoryName(directory)!, "scratch");
        Directory.CreateDirectory(scratch);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        long begin = Stopwatch.GetTimestamp();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(new FileObjectStore(directory), LogRow.Schema, Options(session, scratch));
        long perHour = (long)(rate * 3_600);
        long perPart = perHour / 6;
        double step = 3_600.0 / perHour;
        LogRow[] batch = new LogRow[1 << 16];
        for (int hour = 0; hour < hours; hour++)
        {
            LogGeneratorV2 generator = new LogGeneratorV2(3_000 + hour);
            DateTimeOffset start = QuestionBench.Start.AddHours(hour);
            for (long first = 0; first < perHour; first += perPart)
            {
                await using ObjectDraft draft = dataset.StartObject();
                for (long row = first; row < Math.Min(perHour, first + perPart);)
                {
                    int count = (int)Math.Min(batch.Length, Math.Min(perHour, first + perPart) - row);
                    for (int i = 0; i < count; i++, row++)
                    {
                        batch[i] = generator.Next(start.AddSeconds(row * step));
                    }

                    await draft.Writer.WriteAsync<LogRow>(batch.AsSpan(0, count));
                }

                await dataset.AppendAsync(draft);
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{directory}: {hours * perHour:N0} rows appended in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F0} s, left in level 0"));
    }

    /// <summary>Every partition of the dataset in <paramref name="directory"/> consolidated on <paramref name="lanes"/> lanes, each job said.</summary>
    private static async Task ConsolidateAsync(string root, string directory, int lanes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AlignedBufferPool.Shared.Trim();
        Dictionary<string, IncrementalBuild.Pass> passes = new(StringComparer.Ordinal);
        using System.Diagnostics.ActivityListener listener = IncrementalBuild.ListenToPasses(passes);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(new FileObjectStore(directory), Options(session, Path.Combine(root, "scratch")));
        long begin = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        PeakMemory memory = new PeakMemory();
        IReadOnlyList<CompactionResult> done;
        try
        {
            done = await dataset.ConsolidateAsync();
        }
        finally
        {
            memory.Dispose();
        }

        double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
        allocated = GC.GetTotalAllocatedBytes() - allocated;
        long rows = 0;
        foreach (CompactionResult job in done)
        {
            Console.WriteLine(IncrementalBuild.JobLine(job, seconds / Math.Max(1, done.Count), passes, memory));
            rows += job.Rows;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    {allocated / 1e9:F1} GB allocated, {allocated / (double)Math.Max(rows, 1):F1} bytes a row; {GC.CollectionCount(2)} full collections"));
    }

    /// <summary>Copies the directory <paramref name="from"/> into <paramref name="to"/>, every file.</summary>
    private static void Copy(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.File.Copy(file, target);
        }
    }
}
