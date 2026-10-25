// A scan for an identifier's value: each object whose entry carries a table of contents has its rows found by the
// identifier's index, with no open of the object, the scan's filter kept over them; an object of another kind, or one with
// rows marked, is read as before.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class IdentifierPostingsTests
{
    /// <summary>
    /// Two sealed hours of requests and the minutes after them: a scan for one id finds its row as an open would, every
    /// object whose entry carries a table of contents looked up with no open of it, the others opened; a scan for two ids
    /// finds both rows; a scan for an id no row holds finds none; and the lookups read fewer bytes than the opens.
    /// </summary>
    [Fact]
    public async Task ANeedleScanFindsItsRowsByTheIndexWithNoOpen()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        (VortexDataset dataset, List<Request> appended) = await HoursAsync(store, seed: 29);
        await using (dataset)
        {
            (int contents, int others) = await ContentsAsync(dataset);
            Assert.True(contents >= 2, $"{contents} objects with a table of contents");

            Request needle = appended[1_234];
            bool cold = true;

            // A store that answers in 20 ms: the objects are looked up together, so the scan's dependent round trips are a
            // lookup's, four, however many objects it looks up.
            inner.Latency = TimeSpan.FromMilliseconds(20);
            Request other = appended[5_678];
            foreach ((VortexExpr filter, Request[] rows) in new[]
            {
                (VortexExpr.Parse($"RequestId = '{needle.RequestId}'"), new[] { needle }),
                (VortexExpr.Parse($"RequestId in ('{needle.RequestId}', '{other.RequestId}')"), new[] { needle, other }),
                (VortexExpr.Parse("RequestId = 'no such id'"), Array.Empty<Request>()),
            })
            {
                DatasetScanCounters metrics = new DatasetScanCounters();
                store.Reset();
                List<string> found = await IdsAsync(dataset.ScanBuilder().Where(filter).WithMetrics(metrics));
                long lookedUpBytes = store.BytesRead;
                long lookedUpSteps = store.DependentSteps;
                store.Reset();
                List<string> opened = await IdsAsync(dataset.ScanBuilder().Where(filter).WithIndexes(false));
                long openedBytes = store.BytesRead;
                Console.Out.Write(FormattableString.Invariant(
                    $"NEEDLE {filter}: {found.Count} rows, {metrics.ObjectsLookedUp} objects looked up and {metrics.ObjectsOpened} opened, {lookedUpSteps} dependent steps, {lookedUpBytes} bytes against {openedBytes} opening them.\n"));
                Assert.Equal(rows.Select(r => r.RequestId).Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
                Assert.Equal(opened.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
                Assert.Equal(contents, metrics.ObjectsLookedUp);
                Assert.InRange(metrics.ObjectsOpened, 0, others);

                // Cold, the first time, both: the opens' objects stay open in the handle after, and read nothing again.
                Assert.True(!cold || lookedUpBytes < openedBytes, $"{lookedUpBytes} bytes looked up against {openedBytes} opening");
                Assert.True(!cold || lookedUpSteps <= 4 + others, $"{lookedUpSteps} dependent steps");
                cold = false;
            }

            // The typed scan takes the same path, and finds the same row.
            Assert.Equal([needle], await dataset.Scan<Request>().Where(r => r.RequestId == needle.RequestId).ToRecordsAsync(Ct).ToListAsync(Ct));
        }
    }

    /// <summary>
    /// A sealed hour with a row marked deleted is read as before, its marks kept: the lookup leaves an object with marks to
    /// its open, and the deleted row is not found.
    /// </summary>
    [Fact]
    public async Task AnObjectWithRowsMarkedIsOpenedAndItsMarksKept()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<Request> appended) = await HoursAsync(store, seed: 31);
        await using (dataset)
        {
            Request kept = appended[1_000];
            Request deleted = appended[1_001];
            RowChangeResult result = await dataset.DeleteAsync<Request>(r => r.RequestId == deleted.RequestId, Ct);
            Assert.Equal(1, result.Rows);

            (int contents, _) = await ContentsAsync(dataset);
            DatasetScanCounters metrics = new DatasetScanCounters();
            VortexExpr both = VortexExpr.Parse($"RequestId in ('{kept.RequestId}', '{deleted.RequestId}')");
            Assert.Equal([kept.RequestId], await IdsAsync(dataset.ScanBuilder().Where(both).WithMetrics(metrics)));
            Assert.Equal(contents, metrics.ObjectsLookedUp);
            Assert.Equal([kept], await dataset.Scan<Request>().Where(r => r.RequestId == kept.RequestId).ToRecordsAsync(Ct).ToListAsync(Ct));
            Assert.Empty(await dataset.Scan<Request>().Where(r => r.RequestId == deleted.RequestId).ToRecordsAsync(Ct).ToListAsync(Ct));
        }
    }

    /// <summary>
    /// Sixteen parts of 400 requests a second and a quarter apart, from noon on, compacted until nothing is due: two sealed
    /// hours and the minutes after them; every id 32 hexadecimal digits of its own, declared an identifier; every object
    /// with a table of contents looked up however small its footer.
    /// </summary>
    private static async Task<(VortexDataset Dataset, List<Request> Appended)> HoursAsync(Vorticity.IO.IObjectStore store, int seed)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Identifiers = [nameof(Request.RequestId)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0, SliceTargetRows = 500,
            LookupFooterBytes = 0,
        }, Ct);
        Random random = new Random(seed);
        List<Request> appended = [];
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                Noon.AddSeconds((part * 400 + i) * 1.25), $"{random.NextInt64():x16}{random.NextInt64():x16}", 200 + random.Next(5)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return (dataset, appended);
    }

    /// <summary>
    /// The objects of every level a lookup takes, whose entries carry a table of contents that names their index directory
    /// and mark no row, and the others.
    /// </summary>
    private static async Task<(int Contents, int Others)> ContentsAsync(VortexDataset dataset)
    {
        int contents = 0;
        int others = 0;
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry leaf in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
                bool looked = entry.Contents is { DirectoryRegion: not null } && !entry.HasDeletions;
                contents += looked ? 1 : 0;
                others += looked ? 0 : 1;
            }
        }

        return (contents, others);
    }

    /// <summary>The ids of the rows a scan selects.</summary>
    private static async Task<List<string>> IdsAsync(DatasetScanBuilder scan)
    {
        List<string> ids = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync(Ct))
        {
            Columns<Request> columns = batch.As<Request>();
            Request[] rows = new Request[columns.RowCount];
            Request.ReadRows(columns, rows);
            foreach (int row in columns.Selection)
            {
                ids.Add(rows[row].RequestId);
            }
        }

        return ids;
    }
}
