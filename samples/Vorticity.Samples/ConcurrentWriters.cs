using System;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Samples;

/// <summary>A count of a source's entries over a range of them: what an aggregator appends per commit.</summary>
[VortexRecord]
public partial record struct SpanCount(string Source, long Until, long Spans);

internal static class ConcurrentWriters
{
    private const string Source = "raw/spans/17";

    internal static async Task RunAsync()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset a = await VortexDataset.CreateAsync(store, SpanCount.Schema, new DatasetOptions
        {
            ClusteringKey = [SpanCount.ColumnNames.Until],
        });

        SourceCommit start = await CountAsync(a, 0, 41_200);
        Console.WriteLine($"counted up to {a.SourceOffset(Source)}: {start.Outcome}, version {start.Version}");

        // Two aggregators read the same offset. A pauses; B takes more entries and commits first.
        await using VortexDataset b = await VortexDataset.OpenAsync(store);
        ulong from = a.SourceOffset(Source);
        SourceCommit first = await CountAsync(b, from, 42_300);
        Console.WriteLine($"B counts ({from}, 42300]: {first.Outcome}, version {first.Version}");

        // A comes back with a range B's covers: counted already, and nothing is added.
        SourceCommit late = await CountAsync(a, from, 41_950);
        Console.WriteLine($"A counts ({from}, 41950]: {late.Outcome}, version {late.Version}, and now reads {a.SourceOffset(Source)}");

        // A takes up from where the dataset counts the source, and B, still on its own commit,
        // overlaps it: B learns the offset and starts again from it.
        SourceCommit next = await CountAsync(a, a.SourceOffset(Source), 42_900);
        Console.WriteLine($"A counts (42300, 42900]: {next.Outcome}, version {next.Version}");
        try
        {
            await CountAsync(b, b.SourceOffset(Source), 43_000);
        }
        catch (SourceOffsetConflictException conflict)
        {
            Console.WriteLine($"B counts (42300, 43000]: conflict, the dataset counts the source up to {conflict.Recorded[Source]} at version {conflict.Version}");
            SourceCommit retried = await CountAsync(b, b.SourceOffset(Source), 43_000);
            Console.WriteLine($"B counts ({conflict.Recorded[Source]}, 43000]: {retried.Outcome}, version {retried.Version}");
        }

        await a.RefreshAsync();
        long spans = (await a.Scan<SpanCount>().ToRecordsAsync().ToListAsync()).Sum(row => row.Spans);
        Console.WriteLine($"in the dataset: {spans} spans counted, the source's offset {a.SourceOffset(Source)}, every span once");
    }

    /// <summary>One aggregate row for the entries past <paramref name="from"/> up to <paramref name="to"/>, committed with the range it counts.</summary>
    private static async Task<SourceCommit> CountAsync(VortexDataset dataset, ulong from, ulong to)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<SpanCount>([new SpanCount(Source, (long)to, (long)(to - from))]);
        return await dataset.AppendAsync(draft, [new SourceRange(Source, from, to)]);
    }
}
