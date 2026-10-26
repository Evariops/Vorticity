using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a question warm from the synopsis cache costs when the data lies in the cache's own directory, and when it lies in
/// the directory of another cache that shares the root (<c>--micro peercache</c>): <c>--objects</c> objects of
/// <c>--rows</c> rows in memory, one cache that read them, through its own session, and another of the root, through a
/// session of its own, each asking the same question <c>--rounds</c> times after a first ask, every data segment from the
/// disk. Each line says the median time of a question, the store's requests, and the hits the cache counted.
/// </summary>
internal static class PeerCacheMicro
{
    internal static async Task<int> Run(string root, int objects, int rows, int rounds)
    {
        string directory = Path.Combine(root, $"peercache-{Guid.NewGuid():N}");
        try
        {
            await using MemoryObjectStore memory = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using (VortexDataset writer = await VortexDataset.CreateAsync(store, PeerRow.Schema, new DatasetOptions { ClusteringKey = [nameof(PeerRow.Id)] }))
            {
                Random random = new Random(0x9EE2);
                for (int part = 0; part < objects; part++)
                {
                    await using ObjectDraft draft = writer.StartObject();
                    await draft.Writer.WriteAsync<PeerRow>([.. Enumerable.Range(0, rows).Select(i => new PeerRow(((long)part * rows) + i, i % 97, random.NextDouble()))]);
                    await writer.AppendAsync(draft);
                }
            }

            const long Regions = 256L << 20;
            const long Data = 8L << 30;
            await using VortexSession owner = VortexSession.Create(options => options.SynopsisCache = SynopsisCache.OpenShared(directory, Regions, Data));
            await using VortexDataset own = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = owner });
            double answer = await AskAsync(own);
            owner.Options.SynopsisCache!.Flush();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{objects} objects of {rows:N0} rows; the question's data {owner.Options.SynopsisCache.DataHeldBytes / 1e6:F1} MB on the disk"));
            Line("own directory", await TimeAsync(own, store, owner.Options.SynopsisCache, rounds, answer));

            await using VortexSession other = VortexSession.Create(options => options.SynopsisCache = SynopsisCache.OpenShared(directory, Regions, Data));
            await using VortexDataset peer = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = other });
            _ = await AskAsync(peer);
            Line("another's directory", await TimeAsync(peer, store, other.Options.SynopsisCache!, rounds, answer));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return 0;
    }

    private static void Line(string where, (double Median, long Requests, long Hits, long PeerHits) measured) =>
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {where,-20} {measured.Median,8:F2} ms a question, {measured.Requests} requests to the store, {measured.Hits} hits ({measured.PeerHits} from another's directory)"));

    private static async Task<(double Median, long Requests, long Hits, long PeerHits)> TimeAsync(
        VortexDataset dataset, CountingObjectStore store, SynopsisCache cache, int rounds, double answer)
    {
        List<double> times = [];
        store.Reset();
        long hits = cache.Hits;
        long peerHits = cache.PeerHits;
        for (int round = 0; round < rounds; round++)
        {
            long begin = Stopwatch.GetTimestamp();
            if (await AskAsync(dataset) != answer)
            {
                throw new InvalidOperationException("The question answered otherwise through the cache.");
            }

            times.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        }

        times.Sort();
        return (times[times.Count / 2], store.Requests, (cache.Hits - hits) / rounds, (cache.PeerHits - peerHits) / rounds);
    }

    /// <summary>The sum of the values past a quarter, which reads every object's values.</summary>
    private static async Task<double> AskAsync(VortexDataset dataset)
    {
        List<double> values = await dataset.Scan<PeerRow>().Where(r => r.Value > 0.25).Select(r => r.Value).ToListAsync();
        return values.Sum();
    }

}

/// <summary>A row of <see cref="PeerCacheMicro"/>: an id, a group, a value.</summary>
[VortexRecord]
internal partial record struct PeerRow(long Id, long Group, double Value);
