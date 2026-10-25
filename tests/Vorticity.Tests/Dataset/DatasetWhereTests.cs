// A filter on a value of a dataset's objects, read through where each value lives: only the ranges of rows of the blocks
// the value appears in, where neither the key's slices nor the zone maps can leave rows out.
//
// THE ORACLE IS THE ROWS: each question's answer equals the rows it selects; and the bytes it reads are held against the
// same question asked through a filter the index does not answer, which reads every object that may hold the value.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetWhereTests
{
    private const int Sources = 30;
    private const int RowsASource = 30_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Sources of 30 000 rows each, each with four endpoints of its own, named so that every source's endpoints interleave
    /// with every other's in their order: a zone of a source spans the names of all, and its zone maps leave none out. A
    /// filter on one endpoint reads the ranges of its source's rows alone: a third of the bytes and a quarter of the reads
    /// the same filter takes as a prefix, which no index answers.
    /// </summary>
    [Fact]
    public async Task AFilterOnAValueReadsItsRangesAlone()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        List<Request> rows = await FillAsync(store);

        const string asked = "/api/h12";
        long expected = rows.Count(r => r.Endpoint == asked);
        (long equal, long equalBytes, long equalReads) = await CountAsync(store, r => r.Endpoint == asked);
        (long prefix, long prefixBytes, long prefixReads) = await CountAsync(store, r => r.Endpoint.StartsWith(asked));
        Console.WriteLine($"WHERE: {expected} rows; equal {equalBytes:N0} bytes in {equalReads} reads, prefix {prefixBytes:N0} bytes in {prefixReads} reads");
        Assert.Equal(expected, equal);
        Assert.Equal(expected, prefix);
        Assert.True(equalBytes * 3 < prefixBytes, $"{equalBytes} bytes read by the value's ranges, {prefixBytes} by the whole objects that may hold it");
        Assert.True(equalReads * 4 < prefixReads, $"{equalReads} reads of the value's ranges, {prefixReads} of the whole objects");
    }

    /// <summary>Conjunctions, disjunctions and sets of values, of the same rows: each answer is the rows' own.</summary>
    [Fact]
    public async Task EveryCombinationOfValuesAnswersItsRows()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        List<Request> rows = await FillAsync(store);
        string[] some = ["/api/a03", "/api/o17", "/api/v29"];
        (string Name, Func<Request, bool> Asked, Func<Probe<Request>, Predicate> Filter)[] questions =
        [
            ("one value", r => r.Endpoint == "/api/a03", r => r.Endpoint == "/api/a03"),
            ("a value no row holds", r => r.Endpoint == "/api/z99", r => r.Endpoint == "/api/z99"),
            ("a set", r => some.Contains(r.Endpoint), r => r.Endpoint.In(some)),
            ("either of two", r => r.Endpoint == "/api/a03" || r.Endpoint == "/api/v29", r => r.Endpoint == "/api/a03" || r.Endpoint == "/api/v29"),
            ("a value and a status", r => r.Endpoint == "/api/o17" && r.Status == 500, r => r.Endpoint == "/api/o17" && r.Status == 500),
            ("a value and its source", r => r.Endpoint == "/api/o17" && r.Source == "source-17", r => r.Endpoint == "/api/o17" && r.Source == "source-17"),
            ("a value and another source", r => r.Endpoint == "/api/o17" && r.Source == "source-18", r => r.Endpoint == "/api/o17" && r.Source == "source-18"),
            ("a value or a status", r => r.Endpoint == "/api/o17" || r.Status == 503, r => r.Endpoint == "/api/o17" || r.Status == 503),
        ];

        foreach ((string name, Func<Request, bool> selects, Func<Probe<Request>, Predicate> filter) in questions)
        {
            (long count, _, _) = await CountAsync(store, filter);
            Assert.True(rows.Count(selects) == count, $"{name}: {count} rows, {rows.Count(selects)} expected");
        }
    }

    /// <summary>The sources' rows in random order, compacted into objects of several sources each, in chunks of 64 KiB.</summary>
    private static async Task<List<Request>> FillAsync(IObjectStore store)
    {
        Random random = new Random(0xD5);
        string letters = "ahov";
        List<Request> rows = [];
        for (int source = 0; source < Sources; source++)
        {
            for (int i = 0; i < RowsASource; i++)
            {
                rows.Add(new Request(
                    $"source-{source:D2}",
                    random.NextInt64(1_000_000_000),
                    $"/api/{letters[random.Next(letters.Length)]}{source:D2}",
                    (short)(random.Next(10) switch { < 7 => 200, < 9 => 500, _ => 503 }),
                    new string((char)('a' + random.Next(26)), 40) + random.Next()));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Request.Source), nameof(Request.At)],
            Clustering = new ClusteringOptions { ObjectBytes = 8 << 20 },
            ContentsObjectBytes = 1,
            ChunkBytes = 64 << 10,
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
        for (int from = 0; from < rows.Count; from += 150_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows.Skip(from).Take(150_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        return rows;
    }

    /// <summary>The rows <paramref name="filter"/> selects, with a handle of its own and no cache: the bytes and the reads it took of the store.</summary>
    private static async Task<(long Rows, long Bytes, long Reads)> CountAsync(CountingObjectStore store, Func<Probe<Request>, Predicate> filter)
    {
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, ChunkBytes = 64 << 10 }, Ct);
        store.Reset();
        long rows = 0;
        await foreach (Request _ in dataset.Scan<Request>().Where(filter).ToRecordsAsync(Ct))
        {
            rows++;
        }

        return (rows, store.BytesRead, store.CountOf(ObjectOperation.Read));
    }

    [VortexRecord]
    public partial record struct Request(string Source, long At, string Endpoint, short Status, string Body);
}
