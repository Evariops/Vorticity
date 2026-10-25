// Each column's statistics over a dataset object's blocks, and where each of its values lives, against the object's own
// rows: every object a job writes in its key's order keeps them beside its table of blocks, read with no open of it.
//
// THE ORACLE IS THE OBJECT'S ROWS: each block's rows, nulls, values and their rows, and a measure's count, sum and
// extremes, are counted from the rows the object holds in order; each value's ranges hold every row of it, and only
// blocks it appears in.
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
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetBlockStatsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Sources whose runs are a few rows, thousands, or more than two blocks, arrived in random order and compacted, each
    /// with endpoints of its own and one they all share, statuses of a few values, a code often null, a latency of
    /// quarters often null, an instant and a route of a thousand values, more than a block lists: every block of every column holds the rows, nulls, values and measures of its
    /// rows; each source's and each endpoint's ranges hold its rows, the shared endpoint lives everywhere.
    /// </summary>
    [Fact]
    public async Task EveryBlockOfEveryColumnHoldsItsRows()
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
            store, Event.Schema, new DatasetOptions { ClusteringKey = [nameof(Event.Source), nameof(Event.At)], Clustering = new ClusteringOptions { ObjectBytes = 2 << 20 }, ContentsObjectBytes = 1 }, Ct);
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
        int blocks = 0;
        long[] statsBytes = new long[7];
        long[] whereBytes = new long[7];
        int listed = 0;
        int everywhere = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { BlockStats.Count: > 0 } contents)
            {
                continue;
            }

            objects++;
            BlockTable table = Assert.IsType<BlockTable>(await dataset.ReadBlocksAsync(held.Entry, 0, Ct));
            store.Reset();
            int[] fields = [.. Enumerable.Range(0, 7)];
            (ColumnWhere? Where, ColumnBlocks? Blocks)[] columns = await dataset.ReadBlockColumnsAsync(held.Entry, fields, stats: true, Ct);
            Console.WriteLine($"OBJECT: {held.Entry.Rows} rows, {table.Count} blocks, {store.Requests} requests for every column's statistics");
            Event[] stored = await RowsAsync(dataset, held.Entry);
            blocks += table.Count;
            for (int field = 0; field < fields.Length; field++)
            {
                statsBytes[field] += contents.BlockStatsOf(field)?.Length ?? 0;
                whereBytes[field] += contents.WhereOf(field)?.Length ?? 0;
            }

            // The text and the columns of few values list their values; the instant and the latency, whose values hardly
            // repeat, are measures alone.
            foreach (int field in (int[])[0, 2, 3, 4, 6])
            {
                Assert.True(columns[field].Blocks!.HoldsValueLists, $"column {field} lists no values");
                Assert.True(field == 6 ? columns[field].Where is null : columns[field].Where is not null, $"column {field}'s index");
            }

            // The route, of more values than a block lists and living everywhere, keeps no index of where they live.
            Assert.Null(columns[1].Where);
            Assert.Null(columns[5].Where);
            Assert.Equal(MeasureKind.Signed, columns[1].Blocks!.Measure);
            Assert.Equal(MeasureKind.Float, columns[5].Blocks!.Measure);
            Assert.Null(columns[3].Blocks!.Measure);
            Assert.Null(columns[0].Blocks!.Measure);
            Assert.Equal(MeasureKind.Signed, columns[6].Blocks!.Measure);
            Assert.Contains(Enumerable.Range(0, table.Count), b => columns[6].Blocks!.Distinct![b] > ColumnBlocks.ListedValues);

            for (int field = 0; field < fields.Length; field++)
            {
                ColumnBlocks stats = columns[field].Blocks!;
                Assert.Equal(table.Rows, stats.Rows);
                for (int b = 0; b < table.Count; b++)
                {
                    Event[] block = stored[(int)table.FirstRow(b)..(int)(table.FirstRow(b) + table.Rows[b])];
                    byte[]?[] values = [.. block.Select(row => BytesOf(row, field))];
                    Assert.Equal(values.Count(value => value is null), stats.Nulls[b]);
                    if (stats.HoldsValueLists)
                    {
                        CheckValues(stats, columns[field].Where!, b, values);
                    }

                    if (stats.Measure is { } kind)
                    {
                        CheckMeasure(stats.MeasureOf(b), kind, [.. block.Where(row => BytesOf(row, field) is not null).Select(row => field switch { 1 => row.At, 5 => row.Latency!.Value, _ => row.Route })]);
                    }
                }

                if (columns[field].Where is { } where)
                {
                    (int Listed, int Everywhere) placed = CheckWhere(where, table, stored, field);
                    listed += placed.Listed;
                    everywhere += placed.Everywhere;
                }
            }

            // The shared endpoint lives everywhere, each source's own where its source does.
            ColumnWhere endpoints = columns[2].Where!;
            Assert.True(endpoints.Everywhere(endpoints.Find("/health"u8)));
            ColumnWhere sources = columns[0].Where!;
            Assert.All(Enumerable.Range(0, sources.Values.Length), place => Assert.False(sources.Everywhere(place) && sources.Values.Length > 2));
        }

        Console.WriteLine(
            $"BLOCK STATS: {objects} objects, {blocks} blocks; bytes a block, by column: {string.Join(", ", statsBytes.Select(b => $"{(double)b / blocks:F1}"))}; " +
            $"where, bytes a block: {string.Join(", ", whereBytes.Select(b => $"{(double)b / blocks:F1}"))}; {listed} values placed, {everywhere} everywhere");
        Assert.True(objects >= 2, $"{objects} objects keep statistics over their blocks");
    }

    /// <summary>Block <paramref name="block"/>'s values, numbered by <paramref name="where"/>, against its rows' <paramref name="values"/>.</summary>
    private static void CheckValues(ColumnBlocks stats, ColumnWhere where, int block, byte[]?[] values)
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

        Assert.Equal(truth.Count, stats.Distinct![block]);
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> counted) = stats.ValuesOf(block);
        if (truth.Count > ColumnBlocks.ListedValues)
        {
            Assert.Equal(0, ids.Length);
            return;
        }

        Assert.Equal(truth.Count, ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            int place = where.PlaceOf(ids.Span[i]);
            Assert.True(place >= 0, $"block {block} lists number {ids.Span[i]}, which no value has");
            Assert.Equal(truth[Convert.ToHexString(where.Values[place])], counted.Span[i]);
        }
    }

    /// <summary>A block's measure of <paramref name="kind"/> against its rows' valid <paramref name="values"/>.</summary>
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
    /// Each value's ranges against the rows of column <paramref name="field"/>: whole blocks, each holding the value, and
    /// together every row of it. The values placed and those that live everywhere.
    /// </summary>
    private static (int Listed, int Everywhere) CheckWhere(ColumnWhere where, BlockTable table, Event[] stored, int field)
    {
        HashSet<long> starts = [.. Enumerable.Range(0, table.Count).Select(b => table.FirstRow(b)), table.TotalRows];
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

        Assert.Equal(rowsOf.Count, where.Values.Length);
        Assert.Equal(stored.Length, where.Rows);
        int listed = 0;
        int everywhere = 0;
        for (int place = 0; place < where.Values.Length; place++)
        {
            List<int> held = rowsOf[Convert.ToHexString(where.Values[place])];
            if (where.Everywhere(place))
            {
                everywhere++;
                continue;
            }

            listed++;
            (long Start, long End)[] ranges = [.. where.RangesOf(place)];
            foreach ((long start, long end) in ranges)
            {
                Assert.Contains(start, starts);
                Assert.Contains(end, starts);
            }

            // Every row of the value inside a range, and every block of a range holding it.
            Assert.All(held, row => Assert.Contains(ranges, range => range.Start <= row && row < range.End));
            for (int b = 0; b < table.Count; b++)
            {
                long first = table.FirstRow(b);
                if (Array.Exists(ranges, range => range.Start <= first && first < range.End))
                {
                    Assert.Contains(held, row => row >= first && row < first + table.Rows[b]);
                }
            }
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
