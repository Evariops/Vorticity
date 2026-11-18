// vortex-traces allocations <trace.nettrace> <output directory> [top]
//
// Folds the allocation samples of a trace dotnet-trace recorded with the runtime's provider: AllocationSampled (keyword
// 0x80000000000, .NET 10 and later), an allocation drawn at random about every 100 KB a thread allocates, unbiased by the
// size of the objects, each with its stack; or, in a trace without them, AllocationTick, every 100 KB, which favours the
// object that crosses the threshold. Each sample stands for the bytes since the one before, so the shares are those of the
// bytes allocated, not of the objects.
//
// What comes out, in the output directory:
//   allocations.md   the bytes by type, by the innermost frame of this repository, by both, and by four frames of ours;
//                    the events the trace holds;
//   timeline.tsv     the MiB allocated in each 10 ms;
//   collections.txt  each collection's start, end and heap statistics, on the trace's clock in milliseconds.
//
// The trace needs the JIT and loader keywords (0x18) beside the allocation keyword for its stacks to name the methods
// compiled while it ran; bench/speed.sh --allocations records it so.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Vorticity.Bench.Traces;

internal static class Program
{
    private const long SampledBytes = 100 * 1024;

    private static int Main(string[] args)
    {
        if (args.Length < 3 || args[0] != "allocations")
        {
            Console.Error.WriteLine("usage: vortex-traces allocations <trace.nettrace> <output directory> [top]");
            return 2;
        }

        string trace = args[1];
        string directory = args[2];
        int top = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 40;
        Directory.CreateDirectory(directory);
        string etlx = TraceLog.CreateFromEventPipeDataFile(trace, Path.Combine(directory, "trace.etlx"));
        try
        {
            using TraceLog log = new TraceLog(etlx);
            Fold(log, trace, directory, top);
        }
        finally
        {
            File.Delete(etlx);
        }

        return 0;
    }

    private static void Fold(TraceLog log, string trace, string directory, int top)
    {
        Dictionary<string, long> events = [];
        foreach (TraceEvent e in log.Events)
        {
            string key = e.ProviderName + "/" + e.EventName;
            events[key] = events.GetValueOrDefault(key) + 1;
        }

        // The runtime names the event AllocationSampled; TraceEvent, from its manifest, AllocationSampling.
        bool sampled = events.Keys.Any(k => k.Contains("AllocationSampl", StringComparison.Ordinal));
        string wanted = sampled ? "AllocationSampl" : "AllocationTick";
        Dictionary<string, long> byType = [];
        Dictionary<string, long> byOurs = [];
        Dictionary<string, long> byTypeAndOurs = [];
        Dictionary<string, long> byStack = [];
        SortedDictionary<long, long> timeline = [];
        List<string> collections = [];
        long total = 0;
        long samples = 0;
        List<string> frames = [];
        foreach (TraceEvent e in log.Events)
        {
            string name = e.EventName;
            if (name is "GC/Start" or "GC/Stop" or "GC/HeapStats")
            {
                collections.Add(string.Create(CultureInfo.InvariantCulture, $"{e.TimeStampRelativeMSec,10:F1} {name} {Payloads(e)}"));
                continue;
            }

            if (!name.Contains(wanted, StringComparison.Ordinal))
            {
                continue;
            }

            string type = e.PayloadByName("TypeName") as string ?? "?";
            long bytes = sampled
                ? SampledBytes
                : Convert.ToInt64(e.PayloadByName("AllocationAmount64") ?? e.PayloadByName("AllocationAmount") ?? 0L, CultureInfo.InvariantCulture);
            total += bytes;
            samples++;
            Add(byType, type, bytes);
            Add(timeline, (long)(e.TimeStampRelativeMSec / 10) * 10, bytes);

            frames.Clear();
            for (TraceCallStack? frame = e.CallStack(); frame is not null && frames.Count < 64; frame = frame.Caller)
            {
                string method = frame.CodeAddress.FullMethodName;
                frames.Add(string.IsNullOrEmpty(method) ? frame.CodeAddress.ModuleName + "!?" : method);
            }

            string ours = frames.FirstOrDefault(Ours) ?? "(no frame of ours) " + (frames.FirstOrDefault() ?? "?");
            Add(byOurs, ours, bytes);
            Add(byTypeAndOurs, type + " <- " + ours, bytes);
            Add(byStack, type + " <- " + string.Join(" <- ", frames.Where(Ours).Take(4)), bytes);
        }

        StringBuilder text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# Allocations: {Path.GetFileName(trace)}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"{samples} samples of {(sampled ? "AllocationSampled" : "AllocationTick")}, {total / 1048576.0:F1} MiB allocated.\n");
        Table(text, "By type", byType, total, top);
        Table(text, "By the innermost frame of this repository", byOurs, total, top);
        Table(text, "By type and the innermost frame of this repository", byTypeAndOurs, total, top);
        Table(text, "By type and four frames of this repository", byStack, total, top);
        text.Append("\n## The events of the trace\n\n");
        foreach (KeyValuePair<string, long> pair in events.OrderByDescending(p => p.Value).Take(30))
        {
            text.Append(CultureInfo.InvariantCulture, $"- {pair.Value} {pair.Key}\n");
        }

        File.WriteAllText(Path.Combine(directory, "allocations.md"), text.ToString());
        File.WriteAllLines(Path.Combine(directory, "collections.txt"), collections);
        File.WriteAllLines(
            Path.Combine(directory, "timeline.tsv"),
            ["ms\tMiB"],
            Encoding.UTF8);
        File.AppendAllLines(
            Path.Combine(directory, "timeline.tsv"),
            timeline.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}\t{p.Value / 1048576.0:F2}")));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{samples} allocation samples, {total / 1048576.0:F1} MiB; report: {Path.Combine(directory, "allocations.md")}"));
    }

    private static bool Ours(string frame) => frame.StartsWith("Vorticity", StringComparison.Ordinal);

    private static void Add<TKey>(IDictionary<TKey, long> counter, TKey key, long bytes)
        where TKey : notnull
        => counter[key] = counter.TryGetValue(key, out long had) ? had + bytes : bytes;

    private static void Table(StringBuilder text, string title, Dictionary<string, long> rows, long total, int top)
    {
        text.Append(CultureInfo.InvariantCulture, $"\n## {title}\n\n| MiB | % | what |\n|---:|---:|---|\n");
        foreach (KeyValuePair<string, long> row in rows.OrderByDescending(r => r.Value).Take(top))
        {
            text.Append(CultureInfo.InvariantCulture, $"| {row.Value / 1048576.0:F1} | {100.0 * row.Value / Math.Max(1, total):F1} | `{row.Key}` |\n");
        }
    }

    private static string Payloads(TraceEvent e)
    {
        List<string> parts = [];
        for (int i = 0; i < e.PayloadNames.Length && i < 12; i++)
        {
            parts.Add(e.PayloadNames[i] + "=" + Convert.ToString(e.PayloadValue(i), CultureInfo.InvariantCulture));
        }

        return string.Join(" ", parts);
    }
}
