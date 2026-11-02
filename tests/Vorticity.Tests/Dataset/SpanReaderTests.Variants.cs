using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Tests.File;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SpanReaderTests
{
    /// <summary>
    /// An hour of 200 000 events from two appends consolidated into one object, their attributes shredded: a service in runs
    /// of 5 000 rows, a user of 2 000 drawn unevenly, a status, a constant region, a latency, a nested request, and in the
    /// second case a trace of free text. Every span, of one row to a unit's 32 768, across granules and the appends' seam,
    /// read alone or with others, gives the rows a whole read gives; a span of a hundred rows reads its granules of the
    /// variant alone, under a quarter of the bytes of a read of every row, where the variant's tree has no free text. A
    /// filter on a user outside any index keeps its rows.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AVariantsSpansReadTheRowsAWholeReadGives(bool text)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(Event.At))],
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        DateTimeOffset noon = DateTimeOffset.FromUnixTimeMilliseconds(Noon / 1000);
        Random random = new Random(47);
        for (int part = 0; part < 2; part++)
        {
            Event[] rows = [.. Enumerable.Range(part * 100_000, 100_000).Select(i => new Event(noon.AddMilliseconds(i * 15L), Attributes(i, random, text)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.ConsolidateAsync(Ct);
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        ObjectEntry hour = held.Entry;
        byte[] bytes = new byte[hour.Bytes];
        await store.ReadAsync(hour.Key, 0, bytes, null, Ct);
        List<Event> stored;
        await using (ObjectLease lease = await dataset.RentAsync(hour, Ct))
        {
            stored = await lease.File.Scan<Event>().ToRecordsAsync(Ct).ToListAsync(Ct);
        }

        Assert.Equal(200_000, stored.Count);

        // Every row read: the bytes a span is weighed against.
        long whole;
        {
            TestSegmentSource source = new(bytes) { Ranges = [] };
            using SpanReader reader = SpanReader.Open(source, null, hour.Contents!)!;
            int opened = source.Ranges!.Count;
            using RecordBatch batch = await reader.ReadRowsAsync(0, stored.Count, null, Ct);
            Assert.Equal(stored, Read(batch));
            whole = source.Ranges.Skip(opened).Sum(static range => range.Length);
        }

        (long Start, int Length)[] spans =
        [
            (0, 1), (1, 7), (8_191, 2), (8_192, 100), (12_345, 1_000), (50_000, 32_768), (99_950, 100), (199_999, 1),
            (131_072, 32_768), (77_777, 3),
        ];
        foreach ((long start, int length) in spans)
        {
            TestSegmentSource source = new(bytes) { Ranges = [] };
            using SpanReader reader = SpanReader.Open(source, null, hour.Contents!)!;
            int opened = source.Ranges!.Count;
            using RecordBatch batch = await reader.ReadRowsAsync(start, length, null, Ct);
            Assert.Equal(stored.GetRange((int)start, length), Read(batch));
            long read = source.Ranges.Skip(opened).Sum(static range => range.Length);
            Console.WriteLine($"VARIANT SPAN text={text} {start}+{length}: {read} bytes of {whole}");
            if (!text && length == 100)
            {
                Assert.True(read < whole / 4, $"{start}+{length}: {read} bytes of {whole}");
            }
        }

        // The spans read together, each its own batch.
        {
            TestSegmentSource source = new(bytes);
            using SpanReader reader = SpanReader.Open(source, null, hour.Contents!)!;
            RecordBatch[] batches = await reader.ReadSpansAsync([.. spans.Select(static s => RowRange.FromLength(s.Start, s.Length))], null, Ct);
            try
            {
                for (int s = 0; s < spans.Length; s++)
                {
                    Assert.Equal(stored.GetRange((int)spans[s].Start, spans[s].Length), Read(batches[s]));
                }
            }
            finally
            {
                foreach (RecordBatch batch in batches)
                {
                    batch.Dispose();
                }
            }
        }

        // A user of a few rows found by a filter outside any index: its first pass reads the user's key, its second the rows kept.
        List<Event> found = await dataset.Scan<Event>().Where(r => r.Attributes["user"].AsString() == "u-rare").ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(stored.Where(static e => e.Attributes["user"]?.AsString() == "u-rare"), found);
        Assert.Equal(3, found.Count);

        static Event[] Read(RecordBatch batch)
        {
            Event[] rows = new Event[batch.RowCount];
            Event.ReadRows(batch.As<Event>(), rows);
            return rows;
        }
    }

    private static Variant Attributes(int i, Random random, bool text)
    {
        string user = i is 12_000 or 120_000 or 190_001 ? "u-rare" : $"u-{(int)(2_000 * Math.Pow(random.NextDouble(), 3))}";
        List<(string, Variant)> fields =
        [
            ("service", Variant.From($"svc-{i / 5_000 % 8}")),
            ("user", Variant.From(user)),
            ("status", Variant.From((long)(random.Next(10) == 0 ? 500 : 200))),
            ("region", Variant.From("eu-west-1")),
            ("latency", Variant.From(Math.Round(random.NextDouble() * 250, 2))),
            ("request", Variant.Object(("method", Variant.From(i % 3 == 0 ? "POST" : "GET")), ("bytes", Variant.From((long)random.Next(1_000))))),
        ];
        if (text)
        {
            fields.Add(("trace", Variant.From(Convert.ToHexString(BitConverter.GetBytes(random.NextInt64())))));
        }

        return Variant.Object([.. fields]);
    }

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, Variant Attributes);
}
