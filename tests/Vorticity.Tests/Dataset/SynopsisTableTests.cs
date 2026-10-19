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
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = length, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(5);
        for (int part = 0; part < 16; part++)
        {
            Call[] rows = [.. Enumerable.Range(0, 400).Select(i => new Call(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                random.Next(10) == 0 ? null : $"region-{random.Next(3)}",
                200 + (random.Next(5) * 100),
                random.NextInt64(-1_000, 1_000_000),
                random.NextDouble() * 1e6,
                random.Next(4) == 0 ? null : random.NextInt64(1L << 40),
                $"host-{random.Next(100_000)}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2, Fanout = 2 };
        while (await dataset.CompactAsync(compaction, Ct) is not null)
        {
        }

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
                    // The run statistics of both columns, about a row for three rows of data, hold more bytes
                    // than the columns they sum and are left out; those of the first column are kept.
                    Assert.Contains(tables, t => t.Info.Kind == SynopsisTableKind.Runs && t.Info.Keys.Length == 1);
                    Assert.DoesNotContain(tables, t => t.Info.Kind == SynopsisTableKind.Runs && t.Info.Keys.Length == 2);
                    sealedObjects++;
                }

                spanningHours += tables.Any(t => t.Info.Bucket == SynopsisBucket.Object) ? 1 : 0;
            }
        }

        Assert.Equal(length == SliceWidth.Hour ? 2 : 0, sealedObjects);
        Assert.True(length == SliceWidth.Hour || spanningHours > 0, "an object of a day must span hours");
    }

    /// <summary>The tables of <paramref name="file"/>, each row what the rows it covers give, and each coarse table the merge of the finer ones.</summary>
    private static async Task<List<(SynopsisTableInfo Info, List<TableRow> Rows)>> CountsExactlyAsync(VortexFile file, SliceTable slices)
    {
        DType dtype = file.DType;
        int[] keys = [.. slices.Order];
        int[] measures = [dtype.IndexOfField(nameof(Call.Duration)), dtype.IndexOfField(nameof(Call.Bytes)), dtype.IndexOfField(nameof(Call.Retries))];
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
        Kind[] kinds = Kinds(dtype, measures);
        foreach ((SynopsisTableInfo info, List<TableRow> rows) in tables)
        {
            Assert.Equal(measures, info.Measures);
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
                units.Add(Unit(bucket, [.. info.Keys.Select(k => fields[row][k])], [.. measures.Select(m => fields[row][m])], kinds));
            }

            List<TableRow> expected = Merge(units, static row => row.Bucket, [.. Enumerable.Range(0, info.Keys.Length)], kinds);
            Assert.Equal(Show(expected, kinds), Show(rows, kinds));
        }

        Assert.Equal(tables.Count, await CoarseTablesAreTheMergeOfTheirRunsAsync(file, slices));
        return tables;
    }

    /// <summary>
    /// Two synopsis columns of 100 and 400 bytes and a measure of 10 000: a table is kept when it holds
    /// fewer bytes than the smallest kept table that answers its questions, or than its columns when none
    /// does, and of two tables holding the same rows, only the first.
    /// </summary>
    [Fact]
    public void ATableIsKeptWhenItHoldsFewerBytesThanWhatAnswersWithoutIt()
    {
        static SynopsisTableInfo Info(SynopsisTableKind kind, SynopsisBucket bucket, params int[] keys) => new(kind, bucket, keys, [3], 0);
        (SynopsisTableInfo, long)[] tables =
        [
            // More than its columns, 10 500 bytes.
            (Info(SynopsisTableKind.Runs, SynopsisBucket.Slice, 1, 2), 12_000),
            (Info(SynopsisTableKind.Runs, SynopsisBucket.Slice, 1), 3_000),
            // No finer table kept holds the second column: against the columns.
            (Info(SynopsisTableKind.Coarse, SynopsisBucket.Hour, 1, 2), 2_000),
            (Info(SynopsisTableKind.Coarse, SynopsisBucket.Hour, 1), 500),
            // As many bytes as the hour's, which answers it.
            (Info(SynopsisTableKind.Coarse, SynopsisBucket.Object, 1), 500),
            // The same rows as the hour's at depth 1.
            (Info(SynopsisTableKind.Values, SynopsisBucket.Hour, 1), 500),
            (Info(SynopsisTableKind.Values, SynopsisBucket.Hour, 2), 300),
        ];

        Assert.Equal(
            [false, true, true, true, false, false, true],
            SynopsisTableInfo.WorthTheirBytes(tables, static field => field switch { 1 => 100, 2 => 400, 3 => 10_000, _ => 0 }));
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

        Kind[] kinds = Kinds(file.DType, tables[0].Info.Measures);
        long BucketOf(long start, SynopsisBucket bucket) => bucket switch
        {
            SynopsisBucket.Slice => start,
            SynopsisBucket.Hour => TimeSlicer.Floor(start, slices.Unit, SliceWidth.Hour),
            _ => 0,
        };

        foreach ((SynopsisTableInfo table, List<TableRow> rows) in tables)
        {
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

            Assert.Equal(expected.OrderBy(s => s.Key), counted.OrderBy(c => c.Key));

            // Its rows in the order of their bucket then their keys, each combination once.
            Assert.Equal(Show(Merge(rows, static row => row.Bucket, [.. Enumerable.Range(0, table.Keys.Length)], kinds), kinds), Show(rows, kinds));
            foreach ((SynopsisTableInfo finer, List<TableRow> finerRows) in tables)
            {
                if (ReferenceEquals(finer, table) || finer.Bucket > table.Bucket || !table.Keys.All(finer.Keys.Contains))
                {
                    continue;
                }

                Assert.False(finer.Bucket == table.Bucket && finer.Keys.Length == table.Keys.Length, "two tables hold the same rows");
                int[] positions = [.. table.Keys.Select(k => Array.IndexOf(finer.Keys, k))];
                Assert.Equal(Show(Merge(finerRows, row => BucketOf(row.Bucket, table.Bucket), positions, kinds), kinds), Show(rows, kinds));
            }
        }

        return tables.Count;
    }

    /// <summary>Every synopsis table of <paramref name="file"/>, its rows read.</summary>
    private static async Task<List<(SynopsisTableInfo Info, List<TableRow> Rows)>> TablesAsync(VortexFile file)
    {
        using SynopsisTableSet set = await SynopsisTableInfo.ReadAsync(file, Ct);
        List<(SynopsisTableInfo, List<TableRow>)> tables = [];
        foreach ((SynopsisTableInfo info, int node) in set.Tables)
        {
            tables.Add((info, Read(set.Arena, node, info, Kinds(file.DType, info.Measures))));
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

        Assert.Equal(columns.Length, at);
        return [.. Enumerable.Range(0, buckets.Length).Select(row => new TableRow(buckets[row], keys[row], counts[row], states[row]))];
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

    /// <summary>A table's row for one data row: its bucket, its keys, a count of one, each measure's value as a state.</summary>
    private static TableRow Unit(long bucket, object?[] keys, object?[] measures, Kind[] kinds)
    {
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

        return new TableRow(bucket, keys, 1, states);
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
                merged[name] = new TableRow(at, keys, row.Count, [.. row.States]);
                continue;
            }

            State[] states = new State[kinds.Length];
            for (int m = 0; m < kinds.Length; m++)
            {
                states[m] = into.States[m].Merge(row.States[m], kinds[m]);
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
        $"{row.Bucket} [{string.Join(", ", row.Keys.Select(Show))}] {row.Count}: {string.Join(" ", row.States.Select((s, m) => s.Show(kinds[m])))}")];

    private static string Show(object? key) => key switch
    {
        null => "null",
        string text => $"'{text}'",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => key.ToString()!,
    };

    private static Kind[] Kinds(DType schema, int[] measures) => [.. measures.Select(m =>
    {
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

    /// <summary>A row of a synopsis table.</summary>
    internal sealed record TableRow(long Bucket, object?[] Keys, long Count, State[] States);

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
