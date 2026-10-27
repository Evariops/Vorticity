// Each column's statistics over a dataset object's granules, and where each of its values lies, against the object's own
// rows: every object a job writes in its key's order keeps them in its table of contents, read with no open of it.
//
// THE ORACLE IS THE OBJECT'S ROWS: each granule's rows, nulls, values and their rows, and a measure's count, sum and
// extremes, are counted from the rows the object holds in order; each value's granules hold every row of it, and only
// granules it appears in.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleStatsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Sources whose runs are a few rows, thousands, or more than two granules, arrived in random order and compacted, each
    /// with endpoints of its own and one they all share, statuses of a few values, a code often null, a latency of quarters
    /// often null, an instant and a route of a thousand values, more than a granule lists: every granule of every column
    /// holds the rows, nulls, values and measures of its rows; each source's and each endpoint's granules hold its rows, the
    /// shared endpoint lies everywhere.
    /// </summary>
    [Fact]
    public async Task EveryGranuleOfEveryColumnHoldsItsRows()
    {
        Random random = new Random(0xD4);
        List<Event> rows = [];
        for (int source = 0; source < 40; source++)
        {
            int runs = (source % 3) switch
            {
                0 => random.Next(1, 40),
                1 => random.Next(500, 6_000),
                _ => random.Next(20_000, 120_000),
            };

            int endpoints = 1 + random.Next(20);
            for (int i = 0; i < runs; i++)
            {
                rows.Add(new Event(
                    $"source-{source:D3}",
                    random.NextInt64(1_000_000_000),
                    random.Next(20) == 0 ? "/health" : $"/s{source}/e{random.Next(endpoints)}",
                    (short)(random.Next(10) switch { < 6 => 200, < 8 => 404, 8 => 500, _ => 503 }),
                    random.Next(4) == 0 ? null : random.Next(7),
                    random.Next(30) == 0 ? null : random.Next(400_000) / 4.0,
                    random.Next(1_000)));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Event.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.Source)), LayoutValue.Column(nameof(Event.At))], ObjectBytes = 2 << 20 }, ContentsObjectBytes = 1 }, Ct);
        for (int from = 0; from < rows.Count; from += 100_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows.Skip(from).Take(100_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int objects = 0;
        int granules = 0;
        long[] statsBytes = new long[7];
        long[] locateBytes = new long[7];
        int listed = 0;
        int everywhere = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { GranuleStats.Count: > 0 } contents)
            {
                continue;
            }

            objects++;
            store.Reset();
            int[] fields = [.. Enumerable.Range(0, 7)];
            (GranuleStats?[] stats, _) = await dataset.ReadGranuleStatsAsync(held.Entry, fields, [], Ct);
            ColumnLocate?[] locates = await dataset.ReadLocatesAsync(held.Entry, fields, Ct);
            GranuleStats cut = stats[0]!;
            Console.WriteLine($"OBJECT: {held.Entry.Rows} rows, {cut.Count} granules, {store.Requests} requests for every column's statistics and locates");
            Event[] stored = await RowsAsync(dataset, held.Entry);
            granules += cut.Count;
            foreach ((int field, IndexSegment region) in contents.GranuleStats)
            {
                statsBytes[field] += region.Length;
            }

            foreach ((int field, IndexSegment region) in contents.Locates)
            {
                locateBytes[field] += region.Length;
            }

            // The text and the columns of few values list their values; the instant and the latency, whose values hardly
            // repeat, are measures alone. The sources and the endpoints lie in a few granules each and keep a locate; every
            // status and code lies in nearly every granule, which a locate would spare none of, and they keep none.
            foreach (int field in (int[])[0, 2, 3, 4])
            {
                Assert.True(stats[field]!.HoldsValueLists, $"column {field} lists no values");
                Assert.True(field is 0 or 2 ? locates[field] is not null : locates[field] is null, $"column {field}'s locate");
            }

            // The route, of more values than a granule lists and lying everywhere, keeps no locate, and its values only where a
            // granule, a short one, lists them.
            Assert.Null(locates[1]);
            Assert.Null(locates[5]);
            Assert.Null(locates[6]);
            Assert.Equal(MeasureKind.Signed, stats[1]!.Measure);
            Assert.Equal(MeasureKind.Float, stats[5]!.Measure);
            Assert.Null(stats[3]!.Measure);
            Assert.Null(stats[0]!.Measure);
            Assert.Equal(MeasureKind.Signed, stats[6]!.Measure);
            Assert.True(!stats[6]!.HoldsValueLists || Enumerable.Range(0, cut.Count).Any(g => stats[6]!.Distinct![g] <= GranuleStats.ListedValues));

            // The table of contents says the rows of the granules that list each column's values.
            for (int field = 0; field < fields.Length; field++)
            {
                GranuleStats column = stats[field]!;
                long listing = column.HoldsValueLists
                    ? Enumerable.Range(0, cut.Count).Where(g => column.Distinct![g] <= GranuleStats.ListedValues).Sum(g => cut.Rows[g])
                    : 0;
                Assert.Equal(listing, contents.ListedRowsOf(field));
            }

            for (int field = 0; field < fields.Length; field++)
            {
                GranuleStats column = stats[field]!;
                Assert.Equal(cut.Rows, column.Rows);
                for (int g = 0; g < cut.Count; g++)
                {
                    Event[] granule = stored[(int)cut.FirstRow(g)..(int)(cut.FirstRow(g) + cut.Rows[g])];
                    byte[]?[] values = [.. granule.Select(row => BytesOf(row, field))];
                    Assert.Equal(values.Count(value => value is null), column.Nulls[g]);
                    if (column.HoldsValueLists)
                    {
                        CheckValues(column, g, values);
                    }

                    if (column.Measure is { } kind)
                    {
                        CheckMeasure(column.MeasureOf(g), kind, [.. granule.Where(row => BytesOf(row, field) is not null).Select(row => field switch { 1 => row.At, 5 => row.Latency!.Value, _ => row.Route })]);
                    }
                }

                if (locates[field] is { } locate)
                {
                    (int Listed, int Everywhere) placed = CheckLocate(locate, stored, field);
                    listed += placed.Listed;
                    everywhere += placed.Everywhere;
                }
            }

            // The shared endpoint lies everywhere, each source's own where its source does.
            ColumnLocate endpoints = locates[2]!;
            Assert.True(endpoints.IsEverywhere(endpoints.Find("/health"u8)));
            // A source lies everywhere where its rows fill more than three granules in four, as a large one's may.
            ColumnLocate sources = locates[0]!;
            long most = sources.Granules - (sources.Granules / 4);
            for (int place = 0; place < sources.Values.Count; place++)
            {
                string value = Convert.ToHexString(sources.Values[place]);
                long holding = Enumerable.Range(0, stored.Length)
                    .Where(row => Convert.ToHexString(BytesOf(stored[row], 0)!) == value)
                    .Select(row => row / sources.GranuleRows)
                    .Distinct()
                    .LongCount();
                Assert.Equal(holding > most, sources.IsEverywhere(place));
            }
        }

        Console.WriteLine(
            $"GRANULE STATS: {objects} objects, {granules} granules; bytes a granule, by column: {string.Join(", ", statsBytes.Select(b => $"{(double)b / granules:F1}"))}; " +
            $"locates, bytes a granule: {string.Join(", ", locateBytes.Select(b => $"{(double)b / granules:F1}"))}; {listed} values placed, {everywhere} everywhere");
        Assert.True(objects >= 2, $"{objects} objects keep statistics over their granules");
    }

    /// <summary>Granule <paramref name="granule"/>'s values, numbered by the statistics' dictionary, against its rows' <paramref name="values"/>.</summary>
    private static void CheckValues(GranuleStats stats, int granule, byte[]?[] values)
    {
        Dictionary<string, long> truth = [];
        foreach (byte[]? value in values)
        {
            if (value is not null)
            {
                string key = Convert.ToHexString(value);
                truth[key] = truth.GetValueOrDefault(key) + 1;
            }
        }

        Assert.Equal(truth.Count, stats.Distinct![granule]);
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> counted) = stats.ValuesOf(granule);
        if (truth.Count > GranuleStats.ListedValues)
        {
            Assert.Equal(0, ids.Length);
            return;
        }

        Assert.Equal(truth.Count, ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            byte[]? value = stats.ValueOf(ids.Span[i]);
            Assert.NotNull(value);
            Assert.Equal(truth[Convert.ToHexString(value)], counted.Span[i]);
        }
    }

    /// <summary>A granule's measure of <paramref name="kind"/> against its rows' valid <paramref name="values"/>.</summary>
    private static void CheckMeasure(ReadOnlySpan<long> words, MeasureKind kind, double[] values)
    {
        Assert.Equal(values.Length, words[MeasureLayout.Values]);
        if (values.Length == 0)
        {
            return;
        }

        bool floats = kind == MeasureKind.Float;
        if (floats)
        {
            // Quarters under a hundred thousand: their sum in doubles is exact.
            Assert.Equal(values.Sum(), StateWords.SumOf(words, MeasureLayout.Sum).Value);
            Assert.Equal(values.Min(), BitConverter.Int64BitsToDouble(words[MeasureLayout.FloatLeast]));
            Assert.Equal(values.Max(), BitConverter.Int64BitsToDouble(words[MeasureLayout.FloatGreatest]));
        }
        else
        {
            Int128 sum = 0;
            foreach (double value in values)
            {
                sum += (long)value;
            }

            Assert.Equal(sum, StateWords.IntegerOf(words, MeasureLayout.Sum));
            Assert.Equal((long)values.Min(), words[MeasureLayout.IntegerLeast]);
            Assert.Equal((long)values.Max(), words[MeasureLayout.IntegerGreatest]);
        }
    }

    /// <summary>
    /// Each value's granules against the rows of column <paramref name="field"/>: each holding the value, and together every
    /// row of it. The values placed and those that lie everywhere.
    /// </summary>
    private static (int Listed, int Everywhere) CheckLocate(ColumnLocate locate, Event[] stored, int field)
    {
        Dictionary<string, List<int>> rowsOf = [];
        for (int row = 0; row < stored.Length; row++)
        {
            if (BytesOf(stored[row], field) is { } value)
            {
                string key = Convert.ToHexString(value);
                if (!rowsOf.TryGetValue(key, out List<int>? list))
                {
                    rowsOf[key] = list = [];
                }

                list.Add(row);
            }
        }

        Assert.Equal(rowsOf.Count, locate.Values.Count);
        Assert.Equal(stored.Length, locate.Rows);
        int listed = 0;
        int everywhere = 0;
        ulong[] bits = new ulong[(locate.Granules + 63) / 64];
        for (int place = 0; place < locate.Values.Count; place++)
        {
            List<int> held = rowsOf[Convert.ToHexString(locate.Values[place])];
            if (locate.IsEverywhere(place))
            {
                everywhere++;
                continue;
            }

            listed++;
            Array.Clear(bits);
            locate.Or(place, bits);
            bool In(long granule) => ((bits[granule >> 6] >> (int)(granule & 63)) & 1) != 0;

            // Every row of the value in a granule it names, and every granule named holding it.
            Assert.All(held, row => Assert.True(In(row / locate.GranuleRows), $"row {row} of a value lies in no granule named"));
            HashSet<long> holding = [.. held.Select(row => (long)(row / locate.GranuleRows))];
            int named = 0;
            for (long g = 0; g < locate.Granules; g++)
            {
                if (In(g))
                {
                    named++;
                    Assert.Contains(g, holding);
                }
            }

            Assert.Equal(locate.GranulesOf(place), named);
        }

        return (listed, everywhere);
    }

    /// <summary>The bytes of column <paramref name="field"/>'s value of <paramref name="row"/>, as the column stores them; null for a null.</summary>
    private static byte[]? BytesOf(Event row, int field) => field switch
    {
        0 => Encoding.UTF8.GetBytes(row.Source),
        1 => Le(row.At),
        2 => Encoding.UTF8.GetBytes(row.Endpoint),
        3 => BitConverter.GetBytes(row.Status),
        4 => row.Code is { } code ? BitConverter.GetBytes(code) : null,
        5 => row.Latency is { } latency ? BitConverter.GetBytes(latency) : null,
        _ => BitConverter.GetBytes(row.Route),
    };

    private static byte[] Le(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>The object's rows, in its order.</summary>
    private static async Task<Event[]> RowsAsync(VortexDataset dataset, ObjectEntry entry)
    {
        await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
        return [.. await lease.File.Scan<Event>().ToRecordsAsync(Ct).ToListAsync(Ct)];
    }

    [VortexRecord]
    public partial record struct Event(string Source, long At, string Endpoint, short Status, int? Code, double? Latency, int Route);
}
