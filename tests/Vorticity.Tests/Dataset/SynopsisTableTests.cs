// Synopsis tables: an object of a dataset of periods holds, per slice and per depth of its synopsis
// columns, every combination of their values with its count and the states of every measure column, the
// run statistics; their merges per hour and per object, the coarse tables; and each synopsis column's
// value counts per hour. Each table holds what the rows give, and each coarse table is the merge of its
// slices' runs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisTableTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two hours and a quarter of calls: a region, a tenth of them null, and a status as synopsis columns, a
    /// duration, a size and a count of retries, a quarter of them null, as measures. Every sorted object
    /// holds, at every depth from 0 to both columns, the run statistics of its slices, its coarse tables per
    /// hour, and per object when it spans hours, and the second column's value counts, those its bytes are
    /// worth: each row what the rows it covers give, in the order of its bucket then its keys. With periods
    /// of an hour, two are sealed; with periods of a day, fresh objects merge across the hours.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnObjectCountsItsRowsExactlyInItsTables(bool days)
    {
        SliceWidth length = days ? SliceWidth.Day : SliceWidth.Hour;
        await using MemoryObjectStore store = new MemoryObjectStore();

        // A budget twice the data, so that every kind of table that saves anything is kept and checked: as large as the data,
        // the tables of sketches with their distinct sketches left the runs of the first column out of one object.
        await using VortexDataset dataset = await CallsAsync(store, length, 2_000);
        int sealedObjects = 0;
        int spanningHours = 0;
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                await using ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(entry.Value.Span), Ct);
                if (await SliceTable.ReadAsync(lease.File, Ct) is not { } slices)
                {
                    continue;
                }

                List<(SynopsisTableInfo Info, List<TableRow> Rows)> tables = await CountsExactlyAsync(lease.File, slices);
                if (level == PeriodLevels.Periods)
                {
                    // The run statistics of both columns, about a row for three rows of data, cost more to read
                    // than the columns they sum and are left out; those of the first column are kept.
                    Assert.Contains(tables, t => t.Info.Kind == SynopsisTableKind.Runs && t.Info.Keys.Length == 1);
                    Assert.DoesNotContain(tables, t => t.Info.Kind == SynopsisTableKind.Runs && t.Info.Keys.Length == 2);

                    // The hour's distributions, sketched at every depth: a sketch of each measure per group.
                    Assert.Contains(tables, t => t.Info.Kind == SynopsisTableKind.Sketches && t.Info.Bucket == SynopsisBucket.Hour && t.Info.Keys.Length == 2);
                    sealedObjects++;
                }

                spanningHours += tables.Any(t => t.Info.Bucket == SynopsisBucket.Object) ? 1 : 0;
            }
        }

        Assert.Equal(length == SliceWidth.Hour ? 2 : 0, sealedObjects);
        Assert.True(length == SliceWidth.Hour || spanningHours > 0, "an object of a day must span hours");
    }

    /// <summary>
    /// The same calls under the default budget, a tenth of the data's bytes: every object's tables hold no
    /// more, and the sealed hours keep some.
    /// </summary>
    [Fact]
    public async Task TheTablesStayWithinTheirBudget()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CallsAsync(store, SliceWidth.Hour, null);
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                await using ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(entry.Value.Span), Ct);
                IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
                long bytes = directory.Entries.Where(e => e.Kind == IndexKinds.SynopsisTable).Sum(e => e.Runs[0].Payload.Sum(chunk => (long)chunk.Length));
                Assert.InRange(bytes, 0, lease.File.FileLength / 10);
                Assert.True(level != PeriodLevels.Periods || bytes > 0, "a sealed hour keeps tables");
            }
        }
    }

    /// <summary>
    /// A dataset of periods of <paramref name="length"/> holding two hours and a quarter of calls, compacted:
    /// sealed periods, or fresh objects merged two by two; its index budget <paramref name="budgetPerMille"/>,
    /// the default when null.
    /// </summary>
    private static async Task<VortexDataset> CallsAsync(MemoryObjectStore store, SliceWidth length, int? budgetPerMille, bool oneWrite = false)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = length, SliceTargetRows = 500 },
            Write = budgetPerMille is { } perMille ? new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(perMille) } : VortexWriteOptions.Default,
        }, Ct);
        Random random = new Random(5);
        List<Call[]> parts = [];
        for (int part = 0; part < 16; part++)
        {
            parts.Add([.. Enumerable.Range(0, 400).Select(i => new Call(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                random.Next(10) == 0 ? null : $"region-{random.Next(3)}",
                200 + (random.Next(5) * 100),
                random.NextInt64(-1_000, 1_000_000),
                random.NextDouble() * 1e6,
                random.Next(4) == 0 ? null : random.NextInt64(1L << 40),
                $"host-{random.Next(100_000)}"))]);
        }

        foreach (Call[] rows in oneWrite ? [[.. parts.SelectMany(p => p)]] : parts)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        // One write leaves level 0 at once, the parts two by two.
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = oneWrite ? 0 : 2, Fanout = 2 };
        while (await dataset.CompactAsync(compaction, Ct) is not null)
        {
        }

        return dataset;
    }

    /// <summary>
    /// The same calls appended in sixteen parts, merged two by two through the fresh levels then sealed, and
    /// appended in one write then sealed: each sealed hour holds the same slices, in the same order, and the
    /// same synopsis tables, row for row.
    /// </summary>
    [Fact]
    public async Task APeriodSealedThroughMergesHoldsTheTablesOfOneWrite()
    {
        await using MemoryObjectStore mergedStore = new MemoryObjectStore();
        await using MemoryObjectStore directStore = new MemoryObjectStore();
        await using VortexDataset merged = await CallsAsync(mergedStore, SliceWidth.Hour, 1_000);
        await using VortexDataset direct = await CallsAsync(directStore, SliceWidth.Hour, 1_000, oneWrite: true);
        List<ObjectEntry> mergedPeriods = await PeriodsAsync(merged);
        List<ObjectEntry> directPeriods = await PeriodsAsync(direct);
        Assert.Equal(2, mergedPeriods.Count);
        Assert.Equal(mergedPeriods.Count, directPeriods.Count);
        for (int i = 0; i < mergedPeriods.Count; i++)
        {
            await using ObjectLease left = await merged.RentAsync(mergedPeriods[i], Ct);
            await using ObjectLease right = await direct.RentAsync(directPeriods[i], Ct);
            SliceTable leftSlices = (await SliceTable.ReadAsync(left.File, Ct))!;
            SliceTable rightSlices = (await SliceTable.ReadAsync(right.File, Ct))!;
            Assert.Equal(leftSlices.Slices, rightSlices.Slices);
            Assert.Equal(leftSlices.Order, rightSlices.Order);
            List<(SynopsisTableInfo Info, List<TableRow> Rows)> leftTables = await TablesAsync(left.File);
            List<(SynopsisTableInfo Info, List<TableRow> Rows)> rightTables = await TablesAsync(right.File);
            Assert.NotEmpty(leftTables);
            Assert.Equal(leftTables.Count, rightTables.Count);
            Kind[] kinds = Kinds(left.File.DType, leftTables[0].Info.Measures);
            for (int t = 0; t < leftTables.Count; t++)
            {
                Assert.Equal(
                    (leftTables[t].Info.Kind, leftTables[t].Info.Bucket, string.Join(',', leftTables[t].Info.Keys)),
                    (rightTables[t].Info.Kind, rightTables[t].Info.Bucket, string.Join(',', rightTables[t].Info.Keys)));
                Assert.Equal(Show(leftTables[t].Rows, kinds), Show(rightTables[t].Rows, kinds));
            }
        }
    }

    private static async Task<List<ObjectEntry>> PeriodsAsync(VortexDataset dataset)
    {
        List<ObjectEntry> periods = [];
        await foreach (TreeEntry entry in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            periods.Add(ObjectEntry.FromBytes(entry.Value.Span));
        }

        return periods;
    }

    /// <summary>The tables of <paramref name="file"/>, each row what the rows it covers give, and each coarse table the merge of the finer ones.</summary>
    private static async Task<List<(SynopsisTableInfo Info, List<TableRow> Rows)>> CountsExactlyAsync(VortexFile file, SliceTable slices)
    {
        DType dtype = file.DType;
        int[] keys = [.. slices.Order];

        // Every number but the instant may be a measure of the tables it is no key of.
        int[] numbers =
        [
            dtype.IndexOfField(nameof(Call.Status)), dtype.IndexOfField(nameof(Call.Duration)), dtype.IndexOfField(nameof(Call.Bytes)),
            dtype.IndexOfField(nameof(Call.Retries)),
        ];
        Assert.Equal([dtype.IndexOfField(nameof(Call.Region)), dtype.IndexOfField(nameof(Call.Status))], keys.Order());

        // Each stored row's fields, and the start of its slice.
        List<object?[]> fields = [];
        List<long> starts = [];
        await foreach (Call row in file.Scan<Call>().ToRecordsAsync(Ct))
        {
            object?[] values = new object?[dtype.FieldCount];
            values[dtype.IndexOfField(nameof(Call.Region))] = row.Region;
            values[dtype.IndexOfField(nameof(Call.Status))] = (long)row.Status;
            values[dtype.IndexOfField(nameof(Call.Duration))] = row.Duration;
            values[dtype.IndexOfField(nameof(Call.Bytes))] = row.Bytes;
            values[dtype.IndexOfField(nameof(Call.Retries))] = row.Retries;
            fields.Add(values);
        }

        foreach (TimeSlice slice in slices.Slices)
        {
            starts.AddRange(Enumerable.Repeat(slice.Start, (int)slice.Rows));
        }

        Assert.Equal(fields.Count, starts.Count);
        List<(SynopsisTableInfo Info, List<TableRow> Rows)> tables = await TablesAsync(file);
        foreach ((SynopsisTableInfo info, List<TableRow> rows) in tables)
        {
            // A table measures every number the order does not place, and a number it places when its values seldom
            // repeat, never one of its own keys.
            int[] measures = info.Measures;
            Assert.All(measures, m => Assert.True(numbers.Contains(m) && !info.Keys.Contains(m), $"measure {m} of a table keyed by [{string.Join(", ", info.Keys)}]"));
            Assert.All(numbers.Where(m => !keys.Contains(m)), m => Assert.Contains(m, measures));
            Kind[] kinds = Kinds(dtype, measures);
            Assert.Equal(info.Kind == SynopsisTableKind.Values ? [keys[Array.IndexOf(keys, info.Keys[0])]] : keys[..info.Keys.Length], info.Keys);
            Assert.Equal(info.Rows, rows.Count);
            List<TableRow> units = [];
            for (int row = 0; row < fields.Count; row++)
            {
                long bucket = info.Bucket switch
                {
                    SynopsisBucket.Slice => starts[row],
                    SynopsisBucket.Hour => TimeSlicer.Floor(starts[row], slices.Unit, SliceWidth.Hour),
                    _ => 0,
                };
                units.Add(Unit(
                    bucket, [.. info.Keys.Select(k => fields[row][k])], [.. measures.Select(m => fields[row][m])], kinds, info.Kind == SynopsisTableKind.Sketches,
                    [.. Whole(info).Select(k => ValueHash(fields[row][k], dtype.GetField(k)))]));
            }

            List<TableRow> expected = Merge(units, static row => row.Bucket, [.. Enumerable.Range(0, info.Keys.Length)], kinds);
            Assert.Equal(Show(Stored(expected, info, slices.Unit), kinds), Show(rows, kinds));
        }

        Assert.Equal(tables.Count, await CoarseTablesAreTheMergeOfTheirRunsAsync(file, slices));
        return tables;
    }

    /// <summary>
    /// Two synopsis columns of 100 and 400 bytes and a measure of 10 000 over 10 000 rows, a byte and a row
    /// at 10 ns each: the choice keeps, round after round, the table that saves the most per byte over the
    /// questions it answers, the hour's tables first, then the slices' at depth 1, until the budget of
    /// 6 000 bytes leaves no room for the run statistics of both columns nor for the object's table. A
    /// table holding the same rows as one kept saves nothing. The same tables give the same verdicts.
    /// </summary>
    [Fact]
    public void TheChoiceKeepsWhatSavesMostPerByteWithinTheBudget()
    {
        static SynopsisTableInfo Info(SynopsisTableKind kind, SynopsisBucket bucket, int rows, params int[] keys) => new(kind, bucket, keys, [3], rows);
        (SynopsisTableInfo, long)[] tables =
        [
            (Info(SynopsisTableKind.Runs, SynopsisBucket.Slice, 3_000, 1, 2), 12_000),
            (Info(SynopsisTableKind.Runs, SynopsisBucket.Slice, 300, 1), 3_000),
            (Info(SynopsisTableKind.Coarse, SynopsisBucket.Hour, 200, 1, 2), 2_000),
            (Info(SynopsisTableKind.Coarse, SynopsisBucket.Hour, 20, 1), 500),
            (Info(SynopsisTableKind.Coarse, SynopsisBucket.Object, 10, 1), 400),
            (Info(SynopsisTableKind.Values, SynopsisBucket.Hour, 20, 2), 300),
            (Info(SynopsisTableKind.Values, SynopsisBucket.Hour, 20, 1), 500),
        ];
        static long Columns(int field) => field switch { 1 => 100, 2 => 400, 3 => 10_000, _ => 0 };
        SynopsisPrices prices = new SynopsisPrices(1e-8, 1e-8);

        (bool[] kept, string[] verdicts) = SynopsisTableInfo.Choose(tables, Columns, 10_000, 6_000, prices);
        Assert.Equal([false, true, true, true, false, true, false], kept);
        Assert.StartsWith("left out: 12000 bytes past the 200 the budget of 6000 left", verdicts[0]);
        Assert.StartsWith("kept: saves", verdicts[3]);
        Assert.StartsWith("left out: 400 bytes past", verdicts[4]);
        Assert.StartsWith("left out: its 500 bytes save nothing", verdicts[6]);
        (bool[] again, string[] reasons) = SynopsisTableInfo.Choose(tables, Columns, 10_000, 6_000, prices);
        Assert.Equal(kept, again);
        Assert.Equal(verdicts, reasons);

        // With room for every table, all are kept but the one holding the hour's rows a second time.
        Assert.Equal([true, true, true, true, true, true, false], SynopsisTableInfo.Choose(tables, Columns, 10_000, 1_000_000, prices).Kept);
    }

    /// <summary>
    /// The synopsis tables of <paramref name="file"/>, whose slices <paramref name="slices"/> lists: every
    /// table counts each row once, per slice, per hour or per object, and every table a finer one answers
    /// is that one's rows merged, the run statistics' merges included. No two tables hold the same rows.
    /// Returns the tables.
    /// </summary>
    internal static async Task<int> CoarseTablesAreTheMergeOfTheirRunsAsync(VortexFile file, SliceTable slices)
    {
        List<(SynopsisTableInfo Info, List<TableRow> Rows)> tables = await TablesAsync(file);
        if (tables.Count == 0)
        {
            return 0;
        }

        long BucketOf(long start, SynopsisBucket bucket) => bucket switch
        {
            SynopsisBucket.Slice => start,
            SynopsisBucket.Hour => TimeSlicer.Floor(start, slices.Unit, SliceWidth.Hour),
            _ => 0,
        };

        foreach ((SynopsisTableInfo table, List<TableRow> rows) in tables)
        {
            Kind[] kinds = Kinds(file.DType, table.Measures, slices.Paths);
            Dictionary<long, long> expected = [];
            foreach (TimeSlice slice in slices.Slices.Where(s => s.Rows > 0))
            {
                long bucket = BucketOf(slice.Start, table.Bucket);
                expected[bucket] = expected.GetValueOrDefault(bucket) + slice.Rows;
            }

            Dictionary<long, long> counted = [];
            foreach (TableRow row in rows)
            {
                counted[row.Bucket] = counted.GetValueOrDefault(row.Bucket) + row.Count;
            }

            // A path left in the residual counts the rows that hold it only.
            if (table.Keys.Any(k => SynopsisPath.Names(k) && slices.Paths[SynopsisPath.Place(k)].Residual))
            {
                Assert.All(counted, c => Assert.InRange(c.Value, 0, expected.GetValueOrDefault(c.Key)));
            }
            else
            {
                Assert.Equal(expected.OrderBy(s => s.Key), counted.OrderBy(c => c.Key));
            }

            // Its rows in the order of their bucket then their keys, each combination once.
            Assert.Equal(Show(Stored(Merge(rows, static row => row.Bucket, [.. Enumerable.Range(0, table.Keys.Length)], kinds), table, slices.Unit), kinds), Show(rows, kinds));
            foreach ((SynopsisTableInfo finer, List<TableRow> finerRows) in tables)
            {
                if (ReferenceEquals(finer, table) || finer.Bucket > table.Bucket || !table.Keys.All(finer.Keys.Contains))
                {
                    continue;
                }

                // A table of sketches holds the rows of the table of states of its grain, and its sketches besides, which
                // only another table of sketches compares.
                bool sketches = finer.Kind == SynopsisTableKind.Sketches && table.Kind == SynopsisTableKind.Sketches;
                Assert.False(
                    finer.Bucket == table.Bucket && finer.Keys.Length == table.Keys.Length && (finer.Kind == SynopsisTableKind.Sketches) == (table.Kind == SynopsisTableKind.Sketches),
                    "two tables hold the same rows");
                int[] positions = [.. table.Keys.Select(k => Array.IndexOf(finer.Keys, k))];
                List<TableRow> widened = [.. finerRows.Select(row => Widened(row, finer, table, kinds, file.DType))];
                List<TableRow> merged = Merge(widened, row => BucketOf(row.Bucket, table.Bucket), positions, kinds);
                sketches &= widened.All(row => row.Sketches is not null);
                merged = Stored(merged, table, slices.Unit);
                Assert.Equal(Show(sketches ? merged : Unsketched(merged), kinds), Show(sketches ? rows : Unsketched(rows), kinds));
            }
        }

        return tables.Count;
    }

    /// <summary>
    /// A row of the finer table <paramref name="finer"/> with the states of <paramref name="table"/>'s measures, which
    /// hold those of the finer one and the numbers among its keys: a key's value counted as many times as its row's rows.
    /// </summary>
    private static TableRow Widened(TableRow row, SynopsisTableInfo finer, SynopsisTableInfo table, Kind[] kinds, DType schema)
    {
        // A distinct sketch of the coarser table's is the finer's, or the value of a key the finer table keys.
        int[] whole = Whole(table);
        CardinalitySketch[]? distincts = whole.Length == 0 || row.Sketches is null ? null : new CardinalitySketch[whole.Length];
        for (int j = 0; j < (distincts?.Length ?? 0); j++)
        {
            int key = whole[j];
            CardinalitySketch sketch = distincts![j] = new CardinalitySketch();
            int keyed = Array.IndexOf(finer.Keys, key);
            int held = Array.IndexOf(Whole(finer), key);
            if (keyed >= 0 && row.Keys[keyed] is { } value)
            {
                sketch.Add(ValueHash(value, schema.GetField(key))!.Value);
            }
            else if (keyed < 0 && held >= 0 && row.Distincts is { } theirs)
            {
                sketch.Merge(theirs[held]);
            }
            else if (keyed < 0)
            {
                distincts = null;
            }
        }

        State[] states = new State[table.Measures.Length];
        QuantileSketch[]? sketches = row.Sketches is null ? null : new QuantileSketch[table.Measures.Length];
        for (int m = 0; m < states.Length; m++)
        {
            int at = Array.IndexOf(finer.Measures, table.Measures[m]);
            if (at >= 0)
            {
                states[m] = row.States[at];
                if (sketches is not null)
                {
                    sketches[m] = row.Sketches![at];
                }

                continue;
            }

            // A key widened to a measure has no sketch: only a table of states compares it.
            sketches = null;

            // A key reads back as a number of the switch's common type, whatever its own: its measure's kind says which.
            object? key = row.Keys[Array.IndexOf(finer.Keys, table.Measures[m])];
            states[m] = (key, kinds[m]) switch
            {
                (null, _) => new State(0, 0, default, 0, 0),
                (_, Kind.Float) => FloatTimes(Convert.ToDouble(key, CultureInfo.InvariantCulture), row.Count),
                (_, Kind.Unsigned) => Unsigned(Convert.ToUInt64(key, CultureInfo.InvariantCulture), row.Count),
                _ => Signed(Convert.ToInt64(key, CultureInfo.InvariantCulture), row.Count),
            };
        }

        return row with { States = states, Sketches = sketches, Distincts = sketches is null ? null : distincts };

        static State Signed(long number, long count) => new State(count, (Int128)number * count, default, number, number);

        static State Unsigned(ulong number, long count) => new State(count, (Int128)(UInt128)number * count, default, (long)number, (long)number);

        static State FloatTimes(double number, long count)
        {
            WideIndexedSum sum = default;
            for (long i = 0; i < count; i++)
            {
                sum.Add(number);
            }

            long bits = BitConverter.DoubleToInt64Bits(number);
            return new State(count, 0, sum, bits, bits);
        }
    }

    /// <summary>Every synopsis table of <paramref name="file"/>, its rows read.</summary>
    private static async Task<List<(SynopsisTableInfo Info, List<TableRow> Rows)>> TablesAsync(VortexFile file)
    {
        using SynopsisTableSet set = await SynopsisTableInfo.ReadAsync(file, Ct);
        IReadOnlyList<SynopsisPath> paths = (await SliceTable.ReadAsync(file, Ct))?.Paths ?? [];
        List<(SynopsisTableInfo, List<TableRow>)> tables = [];
        foreach ((SynopsisTableInfo info, TableChunk _, int node) in set.Tables)
        {
            tables.Add((info, Read(set.Arena, node, info, Kinds(file.DType, info.Measures, paths))));
        }

        return tables;
    }

    /// <summary>The rows of a table: the bucket, the keys, the count, then each measure's states.</summary>
    private static List<TableRow> Read(CanonicalArena arena, int node, SynopsisTableInfo info, Kind[] kinds)
    {
        CanonicalNode table = arena.GetNode(node);
        int[] columns = [.. Enumerable.Range(0, table.DType.FieldCount).Select(f => arena.GetNode(node).GetFieldIndex(f))];
        DType[] dtypes = [.. Enumerable.Range(0, table.DType.FieldCount).Select(f => Storage(arena.GetNode(node).DType.GetField(f)))];
        int at = 0;
        int Next() => columns[at++];
        long[] buckets = ColumnData.Values<long>(arena, Next()).ToArray();
        object?[][] keys = new object?[buckets.Length][];
        for (int row = 0; row < keys.Length; row++)
        {
            keys[row] = new object?[info.Keys.Length];
        }

        for (int k = 0; k < info.Keys.Length; k++)
        {
            DType dtype = dtypes[at];
            int column = Next();
            for (int row = 0; row < keys.Length; row++)
            {
                keys[row][k] = KeyAt(arena, column, dtype, row);
            }
        }

        long[] counts = ColumnData.Values<long>(arena, Next()).ToArray();
        State[][] states = new State[buckets.Length][];
        for (int row = 0; row < states.Length; row++)
        {
            states[row] = new State[kinds.Length];
        }

        for (int m = 0; m < kinds.Length; m++)
        {
            long[] values = ColumnData.Values<long>(arena, Next()).ToArray();
            Int128[] sums = new Int128[buckets.Length];
            WideIndexedSum[] floats = new WideIndexedSum[buckets.Length];
            if (kinds[m] == Kind.Float)
            {
                long[][] words = [.. Enumerable.Range(0, 5).Select(_ => ColumnData.Values<long>(arena, Next()).ToArray())];
                PersistedStates.Read(words[0], words[1], words[2], words[3], words[4], floats);
            }
            else
            {
                long[] low = ColumnData.Values<long>(arena, Next()).ToArray();
                long[] high = ColumnData.Values<long>(arena, Next()).ToArray();
                PersistedStates.Read(low, high, sums);
            }

            long[] least = ColumnData.Values<long>(arena, Next()).ToArray();
            long[] greatest = ColumnData.Values<long>(arena, Next()).ToArray();
            for (int row = 0; row < buckets.Length; row++)
            {
                states[row][m] = new State(values[row], sums[row], floats[row], least[row], greatest[row]);
            }
        }

        // A table of sketches holds each measure's sketch after every measure's states, then the distinct sketch of each
        // key it names deeper than its own.
        QuantileSketch[]?[] sketches = new QuantileSketch[buckets.Length][];
        for (int m = 0; m < kinds.Length && info.Kind == SynopsisTableKind.Sketches; m++)
        {
            int column = Next();
            for (int row = 0; row < buckets.Length; row++)
            {
                sketches[row] ??= new QuantileSketch[kinds.Length];
                sketches[row]![m] = new QuantileSketch(SynopsisTableInfo.SketchBits);
                sketches[row]![m].MergeEncoded(ColumnData.Bytes(arena, column, row));
            }
        }

        CardinalitySketch[]?[] distincts = new CardinalitySketch[buckets.Length][];
        int[] whole = Whole(info);
        for (int j = 0; j < info.Distinct.Length; j++)
        {
            int column = Next();
            int place = Array.IndexOf(whole, info.Distinct[j]);
            for (int row = 0; row < buckets.Length && place >= 0; row++)
            {
                distincts[row] ??= new CardinalitySketch[whole.Length];
                distincts[row]![place] = new CardinalitySketch();
                distincts[row]![place].MergeEncoded(ColumnData.Bytes(arena, column, row));
            }
        }

        Assert.Equal(columns.Length, at);
        return [.. Enumerable.Range(0, buckets.Length).Select(row => new TableRow(buckets[row], keys[row], counts[row], states[row])
        {
            Sketches = sketches[row],
            Distincts = distincts[row],
        })];
    }

    private static object? KeyAt(CanonicalArena arena, int column, DType dtype, int row)
    {
        if (!ArenaWords.IsValid(arena, EncodedForms.Canonical(arena, column), row))
        {
            return null;
        }

        return dtype.Kind switch
        {
            DTypeKind.Utf8 => Encoding.UTF8.GetString(ColumnData.Bytes(arena, column, row)),
            DTypeKind.Primitive => dtype.PType switch
            {
                PType.I8 => (long)ColumnData.Values<sbyte>(arena, column)[row],
                PType.I16 => (long)ColumnData.Values<short>(arena, column)[row],
                PType.I32 => (long)ColumnData.Values<int>(arena, column)[row],
                PType.I64 => ColumnData.Values<long>(arena, column)[row],
                PType.U8 => (ulong)ColumnData.Values<byte>(arena, column)[row],
                PType.U16 => (ulong)ColumnData.Values<ushort>(arena, column)[row],
                PType.U32 => (ulong)ColumnData.Values<uint>(arena, column)[row],
                PType.U64 => ColumnData.Values<ulong>(arena, column)[row],
                PType.F32 => (double)ColumnData.Values<float>(arena, column)[row],
                _ => ColumnData.Values<double>(arena, column)[row],
            },
            _ => throw new NotSupportedException($"A key of kind {dtype.Kind}."),
        };
    }

    /// <summary>
    /// A table's row for one data row: its bucket, its keys, a count of one, each measure's value as a state, and with
    /// <paramref name="sketched"/> as a sketch of the grid of a table of sketches, a null counted by neither.
    /// </summary>
    /// <remarks>A table of sketches holds the distinct sketch of each key <paramref name="distinct"/> hashes the row's value of, a null by none.</remarks>
    private static TableRow Unit(long bucket, object?[] keys, object?[] measures, Kind[] kinds, bool sketched = false, ulong?[]? distinct = null)
    {
        CardinalitySketch[]? distincts = distinct is not { Length: > 0 } ? null : new CardinalitySketch[distinct.Length];
        for (int j = 0; j < (distincts?.Length ?? 0); j++)
        {
            distincts![j] = new CardinalitySketch();
            if (distinct![j] is { } hash)
            {
                distincts[j].Add(hash);
            }
        }

        QuantileSketch[]? sketches = sketched ? [.. measures.Select(_ => new QuantileSketch(SynopsisTableInfo.SketchBits))] : null;
        for (int m = 0; m < measures.Length && sketches is not null; m++)
        {
            if (measures[m] is { } value)
            {
                sketches[m].Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
        }

        State[] states = new State[kinds.Length];
        for (int m = 0; m < kinds.Length; m++)
        {
            switch (measures[m])
            {
                case null:
                    states[m] = new State(0, 0, default, 0, 0);
                    break;
                case double number:
                {
                    WideIndexedSum sum = default;
                    sum.Add(number);
                    long bits = BitConverter.DoubleToInt64Bits(number);
                    states[m] = new State(1, 0, sum, bits, bits);
                    break;
                }

                case long number:
                    states[m] = new State(1, number, default, number, number);
                    break;
                default:
                    throw new NotSupportedException($"A measure of {measures[m]!.GetType()}.");
            }
        }

        return new TableRow(bucket, keys, 1, states) { Sketches = sketches, Distincts = distincts };
    }

    /// <summary>
    /// The hash an approximate distinct count takes of a key's value, <paramref name="value"/> as a row reads back, at the
    /// width of <paramref name="dtype"/>'s storage; null for a null.
    /// </summary>
    private static ulong? ValueHash(object? value, DType dtype)
    {
        DType storage = Storage(dtype);
        byte[]? bytes = (value, storage.Kind) switch
        {
            (null, _) => null,
            (string text, _) => Encoding.UTF8.GetBytes(text),
            (_, DTypeKind.Primitive) => storage.PType switch
            {
                PType.I8 => [(byte)(sbyte)Convert.ToInt64(value, CultureInfo.InvariantCulture)],
                PType.I16 => BitConverter.GetBytes((short)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                PType.I32 => BitConverter.GetBytes((int)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                PType.I64 => BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                PType.U8 => [(byte)Convert.ToUInt64(value, CultureInfo.InvariantCulture)],
                PType.U16 => BitConverter.GetBytes((ushort)Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
                PType.U32 => BitConverter.GetBytes((uint)Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
                PType.U64 => BitConverter.GetBytes(Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
                PType.F32 => BitConverter.GetBytes(NormalSingle((float)Convert.ToDouble(value, CultureInfo.InvariantCulture))),
                _ => BitConverter.GetBytes(Normal(Convert.ToDouble(value, CultureInfo.InvariantCulture))),
            },
            _ => throw new NotSupportedException($"A distinct key of {storage.Kind}."),
        };
        return bytes is null ? null : CardinalitySlot.Hash(bytes);

        static double Normal(double d) => double.IsNaN(d) ? double.NaN : d == 0 ? 0 : d;

        static float NormalSingle(float f) => float.IsNaN(f) ? float.NaN : f == 0 ? 0 : f;
    }

    /// <summary>
    /// <paramref name="rows"/> merged per <paramref name="bucket"/> and per their keys at
    /// <paramref name="positions"/>: counts added, states merged, in the order of the bucket then the keys.
    /// </summary>
    private static List<TableRow> Merge(IEnumerable<TableRow> rows, Func<TableRow, long> bucket, int[] positions, Kind[] kinds)
    {
        Dictionary<string, TableRow> merged = [];
        foreach (TableRow row in rows)
        {
            object?[] keys = [.. positions.Select(p => row.Keys[p])];
            long at = bucket(row);
            string name = $"{at} {string.Join(", ", keys.Select(Show))}";
            if (!merged.TryGetValue(name, out TableRow? into))
            {
                merged[name] = new TableRow(at, keys, row.Count, [.. row.States]) { Sketches = Copied(row.Sketches), Distincts = Copied(row.Distincts) };
                continue;
            }

            State[] states = new State[kinds.Length];
            for (int m = 0; m < kinds.Length; m++)
            {
                states[m] = into.States[m].Merge(row.States[m], kinds[m]);
                into.Sketches?[m].Merge(row.Sketches![m]);
            }

            for (int j = 0; j < (into.Distincts?.Length ?? 0); j++)
            {
                into.Distincts![j].Merge(row.Distincts![j]);
            }

            merged[name] = into with { Count = into.Count + row.Count, States = states };
        }

        List<TableRow> sorted = [.. merged.Values];
        sorted.Sort(static (left, right) =>
        {
            int compared = left.Bucket.CompareTo(right.Bucket);
            for (int k = 0; compared == 0 && k < left.Keys.Length; k++)
            {
                compared = CompareKeys(left.Keys[k], right.Keys[k]);
            }

            return compared;
        });
        return sorted;
    }

    /// <summary>Distinct sketches of the same hashes, which a merge may add to without touching the row's own.</summary>
    private static CardinalitySketch[]? Copied(CardinalitySketch[]? sketches)
    {
        if (sketches is null)
        {
            return null;
        }

        CardinalitySketch[] copies = new CardinalitySketch[sketches.Length];
        for (int j = 0; j < copies.Length; j++)
        {
            copies[j] = new CardinalitySketch();
            copies[j].Merge(sketches[j]);
        }

        return copies;
    }

    /// <summary>Sketches of the same counts, which a merge may add to without touching the row's own.</summary>
    private static QuantileSketch[]? Copied(QuantileSketch[]? sketches)
    {
        if (sketches is null)
        {
            return null;
        }

        QuantileSketch[] copies = new QuantileSketch[sketches.Length];
        for (int m = 0; m < copies.Length; m++)
        {
            copies[m] = new QuantileSketch(sketches[m].Bits);
            copies[m].Merge(sketches[m]);
        }

        return copies;
    }

    /// <summary>
    /// Rows in the order of their bucket then of their keys, as <paramref name="table"/> holds them: those of a table of a
    /// column's values per slice in the order of their hour, then of their value, then of their slice, its instants in
    /// <paramref name="unit"/>.
    /// </summary>
    private static List<TableRow> Stored(List<TableRow> rows, SynopsisTableInfo table, TimeUnit unit)
    {
        if (table is not { Kind: SynopsisTableKind.Values, Bucket: SynopsisBucket.Slice })
        {
            return rows;
        }

        List<TableRow> stored = [.. rows];
        stored.Sort((left, right) =>
        {
            int compared = TimeSlicer.Floor(left.Bucket, unit, SliceWidth.Hour).CompareTo(TimeSlicer.Floor(right.Bucket, unit, SliceWidth.Hour));
            compared = compared != 0 ? compared : CompareKeys(left.Keys[0], right.Keys[0]);
            return compared != 0 ? compared : left.Bucket.CompareTo(right.Bucket);
        });
        return stored;
    }

    /// <summary>Keys in their values' order, nulls last.</summary>
    private static int CompareKeys(object? left, object? right) => (left, right) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string l, string r) => string.CompareOrdinal(l, r),
        (long l, long r) => l.CompareTo(r),
        (ulong l, ulong r) => l.CompareTo(r),
        (double l, double r) => l.CompareTo(r),
        _ => throw new NotSupportedException($"Keys of {left.GetType()} and {right.GetType()}."),
    };

    private static List<string> Show(List<TableRow> rows, Kind[] kinds) => [.. rows.Select(row =>
        $"{row.Bucket} [{string.Join(", ", row.Keys.Select(Show))}] {row.Count}: {string.Join(" ", row.States.Select((s, m) => s.Show(kinds[m])))}"
        + (row.Sketches is { } sketches ? $" | {string.Join(" | ", sketches.Select(Show))}" : string.Empty)
        + (row.Distincts is { } distincts ? $" || {string.Join(" | ", distincts.Select(Show))}" : string.Empty))];

    /// <summary>A distinct sketch's estimate and its words, the same for the same content whichever form holds it.</summary>
    private static string Show(CardinalitySketch sketch)
    {
        List<long> words = [];
        sketch.Write(words.Add);
        return FormattableString.Invariant($"{sketch.Estimate()}~{string.Join(",", words)}");
    }

    private static string Show(object? key) => key switch
    {
        null => "null",
        string text => $"'{text}'",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => key.ToString()!,
    };

    private static Kind[] Kinds(DType schema, int[] measures, IReadOnlyList<SynopsisPath>? paths = null) => [.. measures.Select(m =>
    {
        if (SynopsisPath.Names(m))
        {
            return paths![SynopsisPath.Place(m)].Class == VariantClass.Double ? Kind.Float : Kind.Signed;
        }

        PType ptype = Storage(schema.GetField(m)).PType;
        return ptype.IsFloat() ? Kind.Float : ptype.IsUnsignedInteger() ? Kind.Unsigned : Kind.Signed;
    })];

    private static DType Storage(DType dtype)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype;
    }

    internal enum Kind
    {
        Signed,
        Unsigned,
        Float,
    }

    /// <summary>A row of a synopsis table; for a table of sketches, each measure's sketch besides its states.</summary>
    internal sealed record TableRow(long Bucket, object?[] Keys, long Count, State[] States)
    {
        internal QuantileSketch[]? Sketches { get; init; }

        /// <summary>For a table of sketches, the distinct sketch of each key it names deeper than its own.</summary>
        internal CardinalitySketch[]? Distincts { get; init; }
    }

    /// <summary>The keys whose distinct sketches <paramref name="info"/> holds whole: none it lost.</summary>
    private static int[] Whole(SynopsisTableInfo info) => [.. info.Distinct.Where(info.HoldsDistinct)];

    /// <summary>Rows without their sketches: what a table of states holds of a table of sketches.</summary>
    private static List<TableRow> Unsketched(IEnumerable<TableRow> rows) => [.. rows.Select(row => row with { Sketches = null, Distincts = null })];

    /// <summary>A sketch's buckets and their counts, and the values it counts.</summary>
    private static string Show(QuantileSketch sketch) =>
        FormattableString.Invariant($"{sketch.Count}~{string.Join(",", sketch.Buckets().Select(b => $"{b.Lower:R}x{b.Count}"))}");

    /// <summary>A measure's states in a row: its values, its exact sum, its least and its greatest, a float's as its bits.</summary>
    internal readonly record struct State(long Values, Int128 Sum, WideIndexedSum Float, long Least, long Greatest)
    {
        internal State Merge(State other, Kind kind)
        {
            if (other.Values == 0)
            {
                return this;
            }

            WideIndexedSum sum = Float;
            WideIndexedSum added = other.Float;
            sum.Merge(in added);
            if (Values == 0)
            {
                return other with { Sum = Sum + other.Sum, Float = sum };
            }

            return new State(
                Values + other.Values,
                Sum + other.Sum,
                sum,
                Less(kind, other.Least, Least) ? other.Least : Least,
                Less(kind, Greatest, other.Greatest) ? other.Greatest : Greatest);
        }

        internal string Show(Kind kind) => Values == 0
            ? "0"
            : kind == Kind.Float
                ? FormattableString.Invariant($"{Values}/{Float.Value:R}/{BitConverter.Int64BitsToDouble(Least):R}/{BitConverter.Int64BitsToDouble(Greatest):R}")
                : kind == Kind.Unsigned
                    ? FormattableString.Invariant($"{Values}/{(UInt128)Sum}/{(ulong)Least}/{(ulong)Greatest}")
                    : FormattableString.Invariant($"{Values}/{Sum}/{Least}/{Greatest}");

        private static bool Less(Kind kind, long left, long right) => kind switch
        {
            Kind.Float => BitConverter.Int64BitsToDouble(left) < BitConverter.Int64BitsToDouble(right),
            Kind.Unsigned => (ulong)left < (ulong)right,
            _ => left < right,
        };
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string? Region, int Status, long Duration, double Bytes, long? Retries, string Host);
}
