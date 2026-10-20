// A load of periods: each period's rows written straight into its sealed object, several at once,
// each committed beside the others, and a period whose rows change between its two reads refused.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPeriodsCompactionTests
{
    /// <summary>
    /// Each period a load is given becomes one object of the periods level, the object a seal of its rows writes: the
    /// same time slices, the same order inside them, the same synopsis tables and indexes, its slices read without its
    /// footer, whether its rows come whole or in parts planned at once, two of which share a second. The periods are
    /// written at once and committed one by one, nothing in the other levels, and the dataset reads every row in key order.
    /// </summary>
    [Fact]
    public async Task ALoadWritesEachPeriodAsTheObjectASealWrites()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(SliceWidth.Hour);
        options = options with { Periods = options.Periods! with { SliceTargetRows = 16 } };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
        List<DateTimeOffset> appended = [];
        List<Request[]> hours = [];
        List<PeriodRows> periods = [];
        for (int hour = 0; hour < 3; hour++)
        {
            // A request every minute, and then some within the last one, which slices it unevenly; the first hour has two
            // more within the second at half past, where its second part starts.
            DateTimeOffset start = Noon.AddHours(hour);
            List<DateTimeOffset> instants = [.. Enumerable.Range(0, 60).Select(i => start.AddMinutes(i)), .. Enumerable.Range(1, 5).Select(i => start.AddMinutes(59).AddSeconds(i))];
            if (hour == 0)
            {
                instants.InsertRange(31, [start.AddMinutes(30).AddMilliseconds(250), start.AddMinutes(30).AddMilliseconds(500)]);
            }

            Request[] requests = RequestsOf(instants, appended.Count);
            int[] cuts = hour == 0 ? [0, 31, 47, requests.Length] : [0, requests.Length];
            List<Func<CancellationToken, IAsyncEnumerable<RecordBatch>>> parts = [];
            for (int part = 0; part + 1 < cuts.Length; part++)
            {
                byte[] file = await FileOfAsync(requests[cuts[part]..cuts[part + 1]]);
                parts.Add(token => RowsAsync(file, token));
            }

            hours.Add(requests);
            periods.Add(new PeriodRows(TimeSlicer.InstantOf(start, TimeUnit.Microseconds), parts));
            appended.AddRange(instants);
        }

        IReadOnlyList<LoadedPeriod> loaded = await DatasetCompactor.LoadPeriodsAsync(dataset, periods, degree: 3, cancellationToken: Ct);
        Assert.Equal(hours.Select(requests => (long)requests.Length), loaded.Select(period => period.Rows));
        Assert.Equal(periods.Select(period => period.Period), loaded.Select(period => period.Period));
        Assert.Equal(3, loaded.Select(period => period.Version).Distinct().Count());
        Assert.Equal(loaded.Max(period => period.Version), dataset.Version);

        int[] counts = await CountsAsync(dataset);
        Assert.Equal(3, counts[PeriodLevels.Periods]);
        Assert.Equal(3, counts.Sum());
        await CheckAsync(dataset, appended, SliceWidth.Hour);

        int index = 0;
        await foreach (TreeEntry period in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(period.Value.Span);
            int expected = hours[index++].Length;
            Assert.Equal(expected, entry.Rows);
            await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
            SliceTable slices = (await SliceTable.ReadAsync(lease.File, Ct))!;
            Assert.Equal(expected, slices.Rows);
            Assert.True(slices.Slices.Count > 1, "an hour of a request a minute is cut into slices");
            SliceReader reader = (await SliceReader.OpenAsync(lease.File, lease.File.FileLength, lease.File.DType, null, Ct))!;
            using RecordBatch rows = await reader.ReadSliceAsync(slices.Slices.Count - 1, [0], Ct);
            Assert.Equal(slices.Slices[^1].Rows, rows.RowCount);
        }

        // The same rows appended and sealed once a row past the horizon closes their hours: the first hour's object
        // holds the slices, the order and the indexes the load wrote from its three parts.
        await using MemoryObjectStore reference = new MemoryObjectStore();
        await using VortexDataset sealer = await VortexDataset.CreateAsync(reference, Request.Schema, options, Ct);
        foreach (Request[] requests in hours.Append(RequestsOf([Noon.AddHours(4).AddMinutes(1)], appended.Count)))
        {
            await using ObjectDraft draft = sealer.StartObject();
            await draft.Writer.WriteAsync<Request>(requests, Ct);
            await sealer.AppendAsync(draft, Ct);
        }

        Assert.True((await DrainAsync(sealer, new CompactionOptions { LevelZeroCeiling = 1, Fanout = 100 })).GetValueOrDefault(CompactionTrigger.Seal) > 0);
        (SliceTable Slices, string[] Indexes) sealedHour = await ShapeAsync(sealer, await FirstAsync(sealer, PeriodLevels.Periods));
        (SliceTable Slices, string[] Indexes) loadedHour = await ShapeAsync(dataset, await FirstAsync(dataset, PeriodLevels.Periods));
        Assert.Equal(sealedHour.Slices.Slices, loadedHour.Slices.Slices);
        Assert.Equal(sealedHour.Slices.Order, loadedHour.Slices.Order);
        Assert.Equal(sealedHour.Indexes, loadedHour.Indexes);
        Assert.Contains(loadedHour.Indexes, index => index.StartsWith(IndexKinds.SynopsisTable, StringComparison.Ordinal));

        // A period whose second read differs from its first is refused and leaves the dataset as it was.
        ulong version = dataset.Version;
        int reads = 0;
        byte[] shrinking = await FileOfAsync(RequestsOf([.. Enumerable.Range(0, 30).Select(i => Noon.AddHours(5).AddMinutes(i))], appended.Count));
        byte[] shrunk = await FileOfAsync(RequestsOf([.. Enumerable.Range(0, 29).Select(i => Noon.AddHours(5).AddMinutes(i))], appended.Count));
        PeriodRows changing = new PeriodRows(
            TimeSlicer.InstantOf(Noon.AddHours(5), TimeUnit.Microseconds), [token => RowsAsync(++reads == 1 ? shrinking : shrunk, token)]);
        await Assert.ThrowsAsync<DatasetIntegrityException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [changing], degree: 1, cancellationToken: Ct));
        Assert.Equal(2, reads);

        // A period the dataset holds rows of, one that starts no period, rows of another period, and parts out of order,
        // are refused, and leave it as it was too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [periods[1]], degree: 1, cancellationToken: Ct));
        PeriodRows unaligned = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(5).AddMinutes(1), TimeUnit.Microseconds), [token => RowsAsync(shrinking, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [unaligned], degree: 1, cancellationToken: Ct));
        PeriodRows elsewhere = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(6), TimeUnit.Microseconds), [token => RowsAsync(shrinking, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [elsewhere], degree: 1, cancellationToken: Ct));
        byte[] late = await FileOfAsync(RequestsOf([Noon.AddHours(5).AddMinutes(40)], appended.Count));
        PeriodRows backwards = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(5), TimeUnit.Microseconds), [token => RowsAsync(late, token), token => RowsAsync(shrinking, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [backwards], degree: 1, cancellationToken: Ct));
        Assert.Equal(version, dataset.Version);
        Assert.Equal(3, (await CountsAsync(dataset)).Sum());
    }

    /// <summary>
    /// The requests at <paramref name="instants"/>, their ids counted from <paramref name="first"/>, three statuses taking
    /// turns, which a synopsis table counts, and a size drawn from the instant, which it sums.
    /// </summary>
    private static Request[] RequestsOf(List<DateTimeOffset> instants, int first) =>
        [.. instants.Select((at, i) => new Request(at, $"r-{first + i}", 200 + (i % 3 * 100), at.ToUnixTimeSeconds() * 7_919L % 1_000_003))];

    /// <summary>An object's slice table, and each of its indexes as its kind, its column and its options.</summary>
    private static async Task<(SliceTable Slices, string[] Indexes)> ShapeAsync(VortexDataset dataset, TreeEntry period)
    {
        await using ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(period.Value.Span), Ct);
        SliceTable slices = (await SliceTable.ReadAsync(lease.File, Ct))!;
        IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
        return (slices, [.. directory.Entries.Select(index => $"{index.Kind} {string.Join('.', index.ColumnPath)} {Convert.ToHexString(index.Options)}")]);
    }

    /// <summary><paramref name="requests"/> as a file of their own, in that order.</summary>
    private static async Task<byte[]> FileOfAsync(Request[] requests)
    {
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(Request.Schema, new DTypeArena()), new VortexWriteOptions()))
        {
            await writer.WriteAsync<Request>(requests, Ct);
            await writer.CompleteAsync(Ct);
        }

        return stream.ToArray();
    }

    /// <summary>The rows of <paramref name="file"/> in its order, a few at a time, as a load reads a period.</summary>
    private static async IAsyncEnumerable<RecordBatch> RowsAsync(byte[] file, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using VortexFile open = await VortexFile.OpenAsync(new MemorySegmentSource(file), VortexOpenOptions.Default);
        await foreach (RecordBatch batch in open.Scan<Request>().With(new ScanOptions { BatchRows = 16 }).ToBatchesAsync(cancellationToken))
        {
            yield return batch;
        }
    }
}
