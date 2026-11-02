// The key's pruning over the sparse indexes a handle holds: an index in hand is searched only where it may refute a granule,
// as one still to read is.
using System;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyPruningTests
{
    /// <summary>
    /// Every object's sparse index in hand, as a question on a service leaves them: a window on the hour, past the indexed
    /// prefix, searches none of them, where it searched every one, for no granule refuted; the service still searches those it
    /// may refute a granule of. Both return the rows LINQ keeps.
    /// </summary>
    [Fact]
    public async Task AnIndexInHandIsSearchedOnlyWhereItMayRefuteAGranule()
    {
        Random random = new Random(63);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour)],
                IndexedPrefix = 1,
            },
            ContentsObjectBytes = 0,

            // Priced as a remote store, over columns of many chunks, so that a sparse index spares more than its read.
            CostProfile = CostProfile.Remote,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
        }, Ct);
        Row[] all = [.. Enumerable.Range(0, 200_000).Select(i =>
            new Row(0, $"svc-{random.Next(0, 3):D2}", Start.AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay) / 10 * 10), i, random.NextDouble()))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Row>(all, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        DateTime ten = Start.AddHours(10);
        DateTime eleven = Start.AddHours(11);
        VortexExpr window = Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(eleven)));
        VortexExpr service = Eq(nameof(Row.Service), FilterLiteral.From("svc-01"));
        DatasetSchema schema = dataset.Snapshot.Schema;
        KeyPruning byWindow = KeyPruning.Of(schema, window)!;
        KeyPruning byService = KeyPruning.Of(schema, service)!;
        DatasetScanBuilder builder = dataset.ScanBuilder();
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.True(builder.ReadsKeyIndex(held), $"'{held.Entry.Key}': its index is not worth reading");
            Assert.NotNull(await dataset.KeyIndexAsync(held.Entry, held.Entry.Contents!, Ct));
            Assert.True(held.Entry.Contents!.Keys is { HoldsEvery: true });
            ReadOnlyMemory<byte> first = VortexDataset.OrderOf(held.TreeKey);
            Assert.Null(await builder.KeptGranulesAsync(held, byWindow, first, read: true, Ct));
            Assert.NotNull(await builder.KeptGranulesAsync(held, byService, first, read: true, Ct));
            objects++;
        }

        Assert.True(objects > 0);
        Assert.Equal(0, byWindow.Searches);
        Assert.Equal(objects, byService.Searches);
        Assert.Equal(all.Count(row => row.At >= ten && row.At < eleven), await dataset.ScanBuilder().Where(window).CountAsync(Ct));
        Assert.Equal(all.Count(static row => row.Service == "svc-01"), await dataset.ScanBuilder().Where(service).CountAsync(Ct));
    }
}
