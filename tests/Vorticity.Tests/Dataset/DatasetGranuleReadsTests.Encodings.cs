using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleReadsTests
{
    /// <summary>
    /// A read of a few granules decodes their rows from the bytes its plan names alone, the rest of the chunk's region left as
    /// the arena gives it: a plan that leaves out a byte its decode reads gives a wrong value, or another row's, without an
    /// error. The dataset's other reads take every encoding the plan knows a geometry of but one, which a decimal takes: its
    /// parts, the unscaled integers. Beside it, a signed integer close to zero with rare outliers. Every read returns LINQ's
    /// rows, under a tenth of the object.
    /// </summary>
    [Fact]
    public async Task AReadOfAFewGranulesDecodesDecimalPartsFromTheirBytesAlone()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Random random = new Random(29);
        Entry[] rows = [.. Enumerable.Range(0, 300_000).Select(i => new Entry(
            $"svc-{random.Next(0, 50):D2}",
            Start.AddMilliseconds(i * 53L),
            i % 997 == 0 ? -1_000_000_000_000L - i : random.Next(-120, 121),
            decimal.Round((decimal)(random.NextDouble() * 20_000) - 10_000m, 2)))];
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Entry.Schema, new DatasetOptions
        {
            Session = Uncached.Session,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Entry.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Entry.Service)), LayoutValue.Column(nameof(Entry.At))],
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        await using (dataset)
        {
            foreach (Entry[] part in (Entry[][])[rows[..(rows.Length / 2)], rows[(rows.Length / 2)..]])
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Entry>(part, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            await dataset.ConsolidateAsync(Ct);
            long objectBytes = 0;
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                objectBytes += held.Entry.Bytes;
            }

            foreach (string service in (string[])["svc-03", "svc-44"])
            {
                store.Reset();
                List<Entry> read = await dataset.Scan<Entry>().Where(r => r.Service == service).ToRecordsAsync(Ct).ToListAsync(Ct);
                Assert.Equal(rows.Where(r => r.Service == service).OrderBy(static r => r.At), read.OrderBy(static r => r.At));
                Assert.True(store.BytesRead < objectBytes / 10, $"{service}: {store.BytesRead} bytes of {objectBytes}");
            }
        }
    }

    [VortexRecord]
    public partial record struct Entry(string Service, DateTime At, long Delta, decimal Amount);
}
