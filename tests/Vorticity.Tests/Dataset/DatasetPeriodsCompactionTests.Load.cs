// A load of periods: each period's rows written straight into its sealed object in one pass, several at
// once, each committed beside the others, its slices cut as its rows come as a seal's count cuts them.
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
    /// Each period a load is given becomes one object of the periods level, its rows read once, whether they come whole or
    /// in parts, two of which share a second: an hour of fewer rows than a pace is judged from is one slice, read without
    /// its footer, its rows in the order they came, as a seal of its rows writes them. The periods are written at once and
    /// committed one by one, nothing in the other levels, and the dataset reads every row in key order.
    /// </summary>
    [Fact]
    public async Task ALoadWritesEachPeriodInOnePass()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(SliceWidth.Hour);
        options = options with { Periods = options.Periods! with { SliceTargetRows = 16 } };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
        List<DateTimeOffset> appended = [];
        List<Request[]> hours = [];
        List<PeriodRows> periods = [];
        int reads = 0;
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
                parts.Add(token =>
                {
                    Interlocked.Increment(ref reads);
                    return RowsAsync(file, token);
                });
            }

            hours.Add(requests);
            periods.Add(new PeriodRows(TimeSlicer.InstantOf(start, TimeUnit.Microseconds), parts));
            appended.AddRange(instants);
        }

        IReadOnlyList<LoadedPeriod> loaded = await DatasetCompactor.LoadPeriodsAsync(dataset, periods, degree: 3, cancellationToken: Ct);
        Assert.Equal(periods.Sum(period => period.Parts.Count), reads);
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
            Assert.Single(slices.Slices);
            SliceReader reader = (await SliceReader.OpenAsync(lease.File, lease.File.FileLength, lease.File.DType, null, Ct))!;
            using RecordBatch rows = await reader.ReadSliceAsync(0, [0], Ct);
            Assert.Equal(expected, rows.RowCount);
        }

        // The same rows appended and sealed once a row past the horizon closes their hours: the first hour's object holds
        // its rows in the order they came, as the load's does, no slice sorted and no synopsis part.
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
        Assert.Empty(loadedHour.Slices.Order);
        Assert.Empty(sealedHour.Slices.Order);
        Assert.DoesNotContain(loadedHour.Indexes, index => index.StartsWith("vorticity.synopsis.", StringComparison.Ordinal));
        SynopsisContents contents = ObjectEntry.FromBytes((await FirstAsync(dataset, PeriodLevels.Periods)).Value.Span).Contents!;
        Assert.Equal((0L, 0L), (contents.PartStart, contents.PartEnd));
        Assert.Empty(contents.Tables);

        // A period the dataset holds rows of, one that starts no period, rows of another period, parts out of order, and
        // a part whose rows go back in time, are refused, and leave the dataset as it was.
        ulong version = dataset.Version;
        byte[] half = await FileOfAsync(RequestsOf([.. Enumerable.Range(0, 30).Select(i => Noon.AddHours(5).AddMinutes(i))], appended.Count));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [periods[1]], degree: 1, cancellationToken: Ct));
        PeriodRows unaligned = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(5).AddMinutes(1), TimeUnit.Microseconds), [token => RowsAsync(half, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [unaligned], degree: 1, cancellationToken: Ct));
        PeriodRows elsewhere = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(6), TimeUnit.Microseconds), [token => RowsAsync(half, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [elsewhere], degree: 1, cancellationToken: Ct));
        byte[] late = await FileOfAsync(RequestsOf([Noon.AddHours(5).AddMinutes(40)], appended.Count));
        PeriodRows backwards = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(5), TimeUnit.Microseconds), [token => RowsAsync(late, token), token => RowsAsync(half, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [backwards], degree: 1, cancellationToken: Ct));
        byte[] shuffled = await FileOfAsync(RequestsOf([.. Enumerable.Range(0, 30).Select(i => Noon.AddHours(5).AddMinutes(i == 20 ? 3 : i))], appended.Count));
        PeriodRows falling = new PeriodRows(TimeSlicer.InstantOf(Noon.AddHours(5), TimeUnit.Microseconds), [token => RowsAsync(shuffled, token)]);
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetCompactor.LoadPeriodsAsync(dataset, [falling], degree: 1, cancellationToken: Ct));
        Assert.Equal(version, dataset.Version);
        Assert.Equal(3, (await CountsAsync(dataset)).Sum());
    }

    /// <summary>
    /// A period of requests at a steady pace, past the rows a pace is judged from in its first unit, is cut as its rows
    /// come into the slices a seal's count of them cuts, with the same indexes, given the rows it holds, read once a few
    /// thousand at a time.
    /// </summary>
    [Fact]
    public async Task ALoadCutsTheSlicesASealCountsAsItsRowsCome()
    {
        // Three minutes of 1 200 requests a second, in slices of 40 000: a minute holds 72 000, past the 65 536 a pace
        // is judged from, and under twice the target, which an hour is past.
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(SliceWidth.Hour);
        options = options with { Periods = options.Periods! with { SliceTargetRows = 40_000 } };
        List<DateTimeOffset> instants = [.. Enumerable.Range(0, 3 * 60 * 1_200).Select(i => Noon.AddTicks(i * (TimeSpan.TicksPerSecond / 1_200)))];
        Request[] requests = RequestsOf(instants, 0);
        byte[] file = await FileOfAsync(requests);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
        PeriodRows hour = new PeriodRows(TimeSlicer.InstantOf(Noon, TimeUnit.Microseconds), [token => RowsAsync(file, token, batchRows: 4_096)])
        {
            ExpectedRows = requests.Length,
        };
        IReadOnlyList<LoadedPeriod> loaded = await DatasetCompactor.LoadPeriodsAsync(dataset, [hour], degree: 1, cancellationToken: Ct);
        Assert.Equal(requests.Length, loaded[0].Rows);

        await using MemoryObjectStore reference = new MemoryObjectStore();
        await using VortexDataset sealer = await VortexDataset.CreateAsync(reference, Request.Schema, options, Ct);
        foreach (Request[] rows in new[] { requests, RequestsOf([Noon.AddHours(2).AddMinutes(1)], requests.Length) })
        {
            await using ObjectDraft draft = sealer.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await sealer.AppendAsync(draft, Ct);
        }

        Assert.True((await DrainAsync(sealer, new CompactionOptions { LevelZeroCeiling = 1, Fanout = 100 })).GetValueOrDefault(CompactionTrigger.Seal) > 0);
        (SliceTable Slices, string[] Indexes) sealedHour = await ShapeAsync(sealer, await FirstAsync(sealer, PeriodLevels.Periods));
        (SliceTable Slices, string[] Indexes) loadedHour = await ShapeAsync(dataset, await FirstAsync(dataset, PeriodLevels.Periods));
        Assert.Equal(3, loadedHour.Slices.Slices.Count);
        Assert.Equal(sealedHour.Slices.Slices, loadedHour.Slices.Slices);
        Assert.Equal(sealedHour.Indexes, loadedHour.Indexes);
        Assert.DoesNotContain(loadedHour.Indexes, index => index.StartsWith("vorticity.synopsis.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A load whose session has lanes to spare joins and writes its slices on them, and writes the object a load of one lane
    /// writes, its encodings the file's in the order its data met them, over a day whose hours merge while the next slices
    /// are written. The two objects differ by their identity alone.
    /// </summary>
    [Fact]
    public async Task ALoadOnLanesWritesTheBytesOfALoadOnOne()
    {
        // Three hours of ten requests a second, from noon, in slices of 10 000: the writer has given the file its first
        // encodings within a few slices, and an hour is merged every five.
        List<DateTimeOffset> instants = [.. Enumerable.Range(0, 3 * 36_000).Select(i => Noon.AddMilliseconds(i * 100))];
        byte[] file = await FileOfAsync(RequestsOf(instants, 0));
        byte[][] objects = new byte[2][];
        for (int run = 0; run < objects.Length; run++)
        {
            int lanes = run == 0 ? 1 : 8;
            await using VortexSession session = VortexSession.Create(o => o.MaxDegreeOfParallelism = lanes);
            await using MemoryObjectStore store = new MemoryObjectStore();
            DatasetOptions options = Options(SliceWidth.Day);
            options = options with { Session = session, Periods = options.Periods! with { SliceTargetRows = 10_000 } };
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
            PeriodRows day = new PeriodRows(TimeSlicer.InstantOf(new DateTimeOffset(Noon.Date, TimeSpan.Zero), TimeUnit.Microseconds), [token => RowsAsync(file, token)]);
            WriteTimings timings = new WriteTimings();
            await DatasetCompactor.LoadPeriodsAsync(dataset, [day], degree: 1, timings, Ct);
            Assert.True(timings.Write > TimeSpan.Zero, "the slices' writes are timed");

            ObjectEntry entry = ObjectEntry.FromBytes((await FirstAsync(dataset, PeriodLevels.Periods)).Value.Span);
            Assert.Equal(instants.Count, entry.Rows);
            byte[] bytes = new byte[entry.Bytes];
            await store.ReadAsync(entry.Key, 0, bytes, null, Ct);

            // The identity, the bytes of the guid the uid was read from, masked wherever the file holds it.
            byte[] identity = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(identity, (ulong)(entry.Uid >> 64));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(8), (ulong)entry.Uid);
            int masked = 0;
            for (int at = bytes.AsSpan().IndexOf(identity); at >= 0; at = bytes.AsSpan().IndexOf(identity))
            {
                bytes.AsSpan(at, identity.Length).Clear();
                masked++;
            }

            Assert.True(masked > 0, "the file holds its identity");
            objects[run] = bytes;
        }

        Assert.Equal(objects[0].Length, objects[1].Length);
        Assert.True(objects[0].AsSpan().SequenceEqual(objects[1]), "the object a load writes on lanes is the one it writes on one");
    }

    /// <summary>
    /// The requests at <paramref name="instants"/>, their ids counted from <paramref name="first"/>, three statuses taking
    /// turns, and a size drawn from the instant.
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

    /// <summary>The rows of <paramref name="file"/> in its order, <paramref name="batchRows"/> at a time, as a load reads a period.</summary>
    private static async IAsyncEnumerable<RecordBatch> RowsAsync(byte[] file, [EnumeratorCancellation] CancellationToken cancellationToken, int batchRows = 16)
    {
        await using VortexFile open = await VortexFile.OpenAsync(new MemorySegmentSource(file), VortexOpenOptions.Default);
        await foreach (RecordBatch batch in open.Scan<Request>().With(new ScanOptions { BatchRows = batchRows }).ToBatchesAsync(cancellationToken))
        {
            yield return batch;
        }
    }
}
