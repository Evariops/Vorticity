// The counts by hour of a dataset's sealed objects, against their rows.
//
// WHAT IS HELD: on schemas drawn at random, a sealed object counts each column whose values repeat by the hours of its
// period's instant, its nulls apart, and carries every number column of many values as a measure: the rows of each value
// and hour, and each measure's values, exact sum, least and greatest, equal those its rows give, all of a column's read
// in one request with no open of the object. A number column of few values is counted and carried by none, and an
// identifier is not counted.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetCountsTests
{
    internal static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    internal const int Hours = 3;

    internal const int RowsPerHour = 6_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task ASealedObjectCountsItsColumnsAsItsRowsDo(int seed)
    {
        Random random = new Random(seed);
        Drawn[] columns = Draw(random);
        object?[][] rows = Rows(columns, random);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await WriteAsync(store, columns, rows);

        int counted = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { Counts.Count: > 0 })
            {
                continue;
            }

            // The instant keeps no counts; each other column its counts and their sketches, side by side, in a request.
            store.Reset();
            Assert.Equal((null, null), await dataset.ReadCountsAsync(held.Entry, 0, sketches: true, Ct));
            Assert.Equal(0, store.Requests);
            (ColumnCounts? Counts, ColumnSketches? Sketches)[] counts = new (ColumnCounts?, ColumnSketches?)[columns.Length + 1];
            for (int field = 1; field <= columns.Length; field++)
            {
                store.Reset();
                counts[field] = await dataset.ReadCountsAsync(held.Entry, field, sketches: true, Ct);
                Assert.Equal(counts[field].Counts is null ? 0 : 1, store.Requests);
            }

            // The object's rows: those of the hour its counts name, which its period holds whole.
            long hour = counts.First(c => c.Counts is not null).Counts!.Hours[0];
            object?[][] mine = [.. rows.Where(r => HourOf((DateTimeOffset)r[0]!) == hour)];
            Assert.Equal(mine.Length, held.Entry.Rows);
            for (int c = 0; c < columns.Length; c++)
            {
                Drawn column = columns[c];
                int distinct = mine.Select(r => r[c + 1]).Where(v => v is not null).Distinct().Count();
                long valid = mine.Count(r => r[c + 1] is not null);
                bool expected = distinct * 4L <= valid && (!column.Number || distinct <= ColumnCountsBuilder.FewValues) && distinct > 0;
                Assert.True(expected == counts[c + 1].Counts is not null, $"seed {seed}: column {c} ({column}) counted {counts[c + 1].Counts is not null}, {distinct} values in {valid} rows");
                if (counts[c + 1] is ({ } kept, var sketches))
                {
                    Check(kept, sketches, c, columns, mine, hour, distinct);
                    CheckDistinct(kept, sketches, c, columns, mine, distinct);
                    counted++;
                }
            }
        }

        Assert.True(counted >= 2, $"seed {seed}: {counted} columns counted");
    }

    /// <summary>
    /// Rows of an hour already sealed come late, and the jobs merge them: the counts of every object, added entry by entry,
    /// are those of every row of the hours they count, late ones included.
    /// </summary>
    [Fact]
    public async Task TheCountsOfMergedObjectsAddUp()
    {
        Random random = new Random(29);
        Drawn[] columns = [new Drawn(Kind.Text, false, 5), new Drawn(Kind.Code, false, 4), new Drawn(Kind.Int64, true, 0), new Drawn(Kind.Float64, false, 0)];
        List<object?[]> rows = [.. Rows(columns, random)];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await WriteAsync(store, columns, [.. rows]);

        // A thousand rows of the first hour, sealed by now, come late, in two parts.
        object?[][] late = [.. Rows(columns, random).Take(RowsPerHour).Where((_, i) => i % 6 == 0).Take(1_000)];
        for (int part = 0; part < 2; part++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            ColumnsBuilder builder = draft.Writer.Builder();
            foreach (object?[] row in late.Skip(part * 500).Take(500))
            {
                builder.Column<DateTimeOffset>(0).Append((DateTimeOffset)row[0]!);
                for (int c = 0; c < columns.Length; c++)
                {
                    Append(builder, c + 1, columns[c], row[c + 1]);
                }
            }

            await draft.Writer.WriteAsync(builder, Ct);
            await dataset.AppendAsync(draft, Ct);
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        rows.AddRange(late);

        // Every object's counts of the text column, added by value and hour.
        Dictionary<(string Value, long Hour), long[]> added = [];
        Dictionary<(string Value, long Hour), QuantileSketch> sketches = [];
        MeasureKind[]? kinds = null;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (await dataset.ReadCountsAsync(held.Entry, 1, sketches: true, Ct) is not ({ } counts, var sketched))
            {
                continue;
            }

            kinds ??= counts.Kinds;
            Assert.Equal([3, 4], counts.MeasureFields);
            foreach (CountGroup group in counts.Groups)
            {
                for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
                {
                    (string, long) key = (Encoding.UTF8.GetString(group.Value!), counts.Hours[e]);
                    if (sketched?.SketchOf(e, 1) is { } weights)
                    {
                        if (!sketches.TryGetValue(key, out QuantileSketch? sketch))
                        {
                            sketches[key] = sketch = new QuantileSketch(QuantileSketch.BitsFor(0.01));
                        }

                        sketch.MergeEncoded(weights);
                    }

                    if (added.TryGetValue(key, out long[]? into))
                    {
                        StateWords.Merge(into, counts.WordsOf(e), kinds);
                    }
                    else
                    {
                        added[key] = counts.WordsOf(e).ToArray();
                    }
                }
            }
        }

        // The hours counted hold every row of theirs, late ones included.
        HashSet<long> hours = [.. added.Keys.Select(k => k.Hour)];
        Assert.Contains(HourOf(Noon), hours);
        foreach (IGrouping<(string, long), object?[]> group in rows.Where(r => hours.Contains(HourOf((DateTimeOffset)r[0]!)))
            .GroupBy(r => ((string)r[1]!, HourOf((DateTimeOffset)r[0]!))))
        {
            long[] words = added[group.Key];
            Assert.Equal(group.Count(), words[0]);
            long[] durations = [.. group.Where(r => r[3] is not null).Select(r => (long)r[3]!)];
            Assert.Equal(durations.Length, words[1 + MeasureLayout.Values]);
            Assert.Equal(durations.Aggregate(Int128.Zero, (s, v) => s + v), StateWords.IntegerOf(words, 1 + MeasureLayout.Sum));
            Assert.Equal(durations.Min(), words[1 + MeasureLayout.IntegerLeast]);
            Assert.Equal(durations.Max(), words[1 + MeasureLayout.IntegerGreatest]);
            WideIndexedSum weights = default;
            foreach (object?[] row in group)
            {
                weights.Add((double)row[4]!);
            }

            int floats = 1 + MeasureLayout.IntegerWords;
            Assert.Equal(weights.Value, StateWords.SumOf(words, floats + MeasureLayout.Sum).Value);

            // The weights' sketches of every object, merged, are the sketch of every weight.
            System.Buffers.ArrayBufferWriter<byte> merged = new System.Buffers.ArrayBufferWriter<byte>();
            sketches[group.Key].Encode(merged);
            Assert.Equal(Sketch(group.Select(r => (double)r[4]!)), merged.WrittenSpan.ToArray());
        }

        Assert.Equal(rows.Count(r => hours.Contains(HourOf((DateTimeOffset)r[0]!))), added.Values.Sum(w => w[0]));
    }

    /// <summary>Holds <paramref name="counts"/> of column <paramref name="c"/>, of <paramref name="distinct"/> values, to what the rows <paramref name="rows"/> of hour <paramref name="hour"/> give.</summary>
    private static void Check(ColumnCounts counts, ColumnSketches? sketches, int c, Drawn[] columns, object?[][] rows, long hour, int distinct)
    {
        // The measures: every number column of more than a few values in these rows but the column itself.
        int[] measures = [.. Enumerable.Range(0, columns.Length).Where(m => m != c && columns[m].Number
            && rows.Select(r => r[m + 1]).Where(v => v is not null).Distinct().Count() > ColumnCountsBuilder.FewValues)];
        Assert.Equal(measures.Select(m => m + 1), counts.MeasureFields);

        Dictionary<string, object?[][]> byValue = rows.GroupBy(r => Key(r[c + 1])).ToDictionary(g => g.Key, g => g.ToArray());
        Assert.Equal(byValue.Count, counts.Groups.Length);
        foreach (CountGroup group in counts.Groups)
        {
            Assert.NotEqual(CountedValue.Others, group.Kind);
            string key = group.Kind == CountedValue.Null ? "null" : Key(Value(columns[c], group.Value!));
            Assert.True(byValue.TryGetValue(key, out object?[][]? held), $"column {c} counts {key}, which no row holds");
            Assert.Equal(1, group.Entries);
            Assert.Equal(hour, counts.Hours[group.FirstEntry]);
            ReadOnlySpan<long> words = counts.WordsOf(group.FirstEntry);
            Assert.Equal(held.Length, words[0]);
            int at = 1;
            for (int i = 0; i < measures.Length; i++)
            {
                object[] values = [.. held.Select(r => r[measures[i] + 1]).Where(v => v is not null).Cast<object>()];
                MeasureKind kind = counts.Kinds[i];
                Assert.Equal(values.Length, words[at + MeasureLayout.Values]);

                // The entry's quantile sketch of the measure: the one its values give, a column of at most 1 000 values keeping them.
                Assert.Equal(distinct <= ColumnCountsBuilder.SketchValues, counts.Sketched);
                Assert.Equal(counts.Sketched, sketches?.Sketches is not null);
                if (counts.Sketched)
                {
                    Assert.Equal(values.Length == 0 ? null : Sketch(values.Select(Convert.ToDouble)), sketches!.SketchOf(group.FirstEntry, i));
                }
                if (kind == MeasureKind.Float)
                {
                    double[] floats = [.. values.Select(Convert.ToDouble)];
                    WideIndexedSum sum = default;
                    foreach (double value in floats)
                    {
                        sum.Add(value);
                    }

                    Assert.Equal(sum.Value, StateWords.SumOf(words, at + MeasureLayout.Sum).Value);
                    if (floats.Length > 0)
                    {
                        Assert.Equal(floats.Min(), BitConverter.Int64BitsToDouble(words[at + MeasureLayout.FloatLeast]));
                        Assert.Equal(floats.Max(), BitConverter.Int64BitsToDouble(words[at + MeasureLayout.FloatGreatest]));
                    }

                    at += MeasureLayout.FloatWords;
                    continue;
                }

                Int128[] integers = [.. values.Select(v => v is ulong u ? (Int128)u : v is uint w ? (Int128)w : (Int128)Convert.ToInt64(v))];
                Assert.Equal(integers.Aggregate(Int128.Zero, (s, v) => s + v), StateWords.IntegerOf(words, at + MeasureLayout.Sum));
                if (integers.Length > 0)
                {
                    Int128 least = kind == MeasureKind.Unsigned ? (Int128)(ulong)words[at + MeasureLayout.IntegerLeast] : words[at + MeasureLayout.IntegerLeast];
                    Int128 greatest = kind == MeasureKind.Unsigned ? (Int128)(ulong)words[at + MeasureLayout.IntegerGreatest] : words[at + MeasureLayout.IntegerGreatest];
                    Assert.Equal(integers.Min(), least);
                    Assert.Equal(integers.Max(), greatest);
                }

                at += MeasureLayout.IntegerWords;
            }
        }
    }

    /// <summary>
    /// Holds the registers <paramref name="counts"/> of column <paramref name="c"/>, of <paramref name="distinct"/> values,
    /// keep of the wide columns to those their values in each entry's rows give: a column of at most 100 values keeps those
    /// of every text column of more than 1 000 values that repeat, but itself; any other none.
    /// </summary>
    private static void CheckDistinct(ColumnCounts counts, ColumnSketches? sketches, int c, Drawn[] columns, object?[][] rows, int distinct)
    {
        int[] wide = distinct > ColumnCountsBuilder.AttachValues ? [] : [.. Enumerable.Range(0, columns.Length).Where(w =>
        {
            int values = rows.Select(r => r[w + 1]).Where(v => v is not null).Distinct().Count();
            long held = rows.Count(r => r[w + 1] is not null);
            return w != c && !columns[w].Number && values > ColumnCountsBuilder.WideValues && values * 4L <= held;
        })];
        Assert.Equal(wide.Select(w => w + 1), counts.AttachedFields);
        Assert.Equal(wide.Length > 0, sketches?.Distinct is not null);
        Dictionary<string, object?[][]> byValue = rows.GroupBy(r => Key(r[c + 1])).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (CountGroup group in counts.Groups)
        {
            object?[][] held = byValue[group.Kind == CountedValue.Null ? "null" : Key(Value(columns[c], group.Value!))];
            for (int a = 0; a < wide.Length; a++)
            {
                byte[] ranks = new byte[ColumnSheet.Registers];
                foreach (object? value in held.Select(r => r[wide[a] + 1]).Where(v => v is not null))
                {
                    ulong hash = SplitBlockBloom.Hash(Encoding.UTF8.GetBytes((string)value!), BloomHash.XxHash3);
                    ref byte rank = ref ranks[HyperLogLog.Register(hash, ColumnSheet.Precision)];
                    rank = Math.Max(rank, HyperLogLog.Rank(hash, ColumnSheet.Precision));
                }

                Assert.Equal(ranks.Any(r => r != 0) ? HyperLogLog.Encode(ranks) : null, sketches!.DistinctOf(group.FirstEntry, a));
            }
        }
    }

    /// <summary>The encoded quantile sketch, at 1 % as the counts keep them, of <paramref name="values"/>.</summary>
    private static byte[] Sketch(IEnumerable<double> values)
    {
        QuantileSketch sketch = new QuantileSketch(QuantileSketch.BitsFor(0.01));
        foreach (double value in values)
        {
            sketch.Add(value);
        }

        System.Buffers.ArrayBufferWriter<byte> writer = new System.Buffers.ArrayBufferWriter<byte>();
        sketch.Encode(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static string Key(object? value) => value switch
    {
        null => "null",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => $"{value.GetType().Name}:{value}",
    };

    /// <summary>The value <paramref name="bytes"/> hold, as the column <paramref name="column"/> stores it.</summary>
    private static object Value(Drawn column, byte[] bytes) => column.Kind switch
    {
        Kind.Text or Kind.Wide or Kind.Identifier => Encoding.UTF8.GetString(bytes),
        Kind.Code => BitConverter.ToInt16(bytes),
        Kind.Int32 => BitConverter.ToInt32(bytes),
        Kind.Int64 => BitConverter.ToInt64(bytes),
        Kind.UInt32 => BitConverter.ToUInt32(bytes),
        Kind.Float32 => BitConverter.ToSingle(bytes),
        _ => BitConverter.ToDouble(bytes),
    };

    private static long HourOf(DateTimeOffset at) => (long)Math.Floor((at - DateTimeOffset.UnixEpoch).TotalHours);

    internal enum Kind
    {
        Text,
        Wide,
        Code,
        Identifier,
        Int32,
        Int64,
        UInt32,
        Float64,
        Float32,
        FewFloats,
    }

    /// <summary>A column drawn: its kind, whether it holds nulls, and how many values it draws from.</summary>
    internal sealed record Drawn(Kind Kind, bool Nullable, int Values)
    {
        internal bool Number => Kind is not (Kind.Text or Kind.Wide or Kind.Identifier);

        internal VortexType Type => Nullable ? Plain.Nullable : Plain;

        private VortexType Plain => Kind switch
        {
            Kind.Text or Kind.Wide or Kind.Identifier => VortexType.Utf8,
            Kind.Code => VortexType.Int16,
            Kind.Int32 => VortexType.Int32,
            Kind.Int64 => VortexType.Int64,
            Kind.UInt32 => VortexType.UInt32,
            Kind.Float32 => VortexType.Float32,
            _ => VortexType.Float64,
        };
    }

    /// <summary>Seven to ten columns: texts and codes of a few to a few hundred values, a wide text, an identifier, and measures of many.</summary>
    internal static Drawn[] Draw(Random random)
    {
        // A text and a code of few values, and a wide text, whose distinct values the columns of few values keep; then more.
        List<Drawn> columns =
        [
            new Drawn(Kind.Text, random.Next(2) == 0, 2 + random.Next(300)),
            new Drawn(Kind.Code, random.Next(2) == 0, 2 + random.Next(40)),
            new Drawn(Kind.Wide, random.Next(2) == 0, 1_200),
        ];
        Kind[] menu = [Kind.Text, Kind.Wide, Kind.Code, Kind.Identifier, Kind.Int32, Kind.Int64, Kind.UInt32, Kind.Float64, Kind.Float32, Kind.FewFloats];
        int more = 4 + random.Next(4);
        for (int i = 0; i < more; i++)
        {
            Kind kind = menu[random.Next(menu.Length)];
            int values = kind switch
            {
                Kind.Text => 2 + random.Next(300),
                Kind.Wide => 1_200,
                Kind.Code or Kind.FewFloats => 2 + random.Next(40),
                _ => 0,
            };
            columns.Add(new Drawn(kind, random.Next(3) == 0, values));
        }

        return [.. columns];
    }

    /// <summary>The rows: an instant every 0.6 seconds from noon, in order, then each column's value or a null.</summary>
    internal static object?[][] Rows(Drawn[] columns, Random random)
    {
        object?[][] rows = new object?[Hours * RowsPerHour][];
        for (int row = 0; row < rows.Length; row++)
        {
            object?[] values = rows[row] = new object?[columns.Length + 1];
            values[0] = Noon.AddMilliseconds(row * 3_600_000.0 / RowsPerHour);
            for (int c = 0; c < columns.Length; c++)
            {
                Drawn column = columns[c];
                values[c + 1] = column.Nullable && random.Next(10) == 0 ? null : column.Kind switch
                {
                    Kind.Text => $"v{random.Next(column.Values)}",
                    Kind.Wide => $"w{random.Next(column.Values)}",
                    Kind.Code => (short)(100 + random.Next(column.Values)),
                    Kind.Identifier => $"id-{row}",
                    Kind.Int32 => random.Next(-1_000_000, 1_000_000),
                    Kind.Int64 => random.NextInt64(0, 5_000_000_000),
                    Kind.UInt32 => (uint)random.NextInt64(0, uint.MaxValue),
                    Kind.Float64 => Math.Round((random.NextDouble() - 0.4) * 2_000, 3) is var d && d != 0 ? d : 1.5,
                    Kind.Float32 => (float)Math.Round((random.NextDouble() - 0.4) * 2_000, 2) is var f && f != 0 ? f : 1.5f,
                    _ => 0.25 + random.Next(column.Values),
                };
            }
        }

        return rows;
    }

    /// <summary>A dataset of periods of an hour holding <paramref name="rows"/>, appended in parts of five minutes and compacted to the end.</summary>
    internal static async Task<VortexDataset> WriteAsync(CountingObjectStore store, Drawn[] columns, object?[][] rows)
    {
        List<VortexField> fields = [new VortexField("At", VortexType.Timestamp(TimeUnit.Microseconds, "UTC"))];
        for (int c = 0; c < columns.Length; c++)
        {
            fields.Add(new VortexField($"c{c}", columns[c].Type));
        }

        VortexDataset dataset = await VortexDataset.CreateAsync(store, VortexSchema.Create([.. fields]), new DatasetOptions
        {
            ClusteringKey = ["At"],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            SheetObjectBytes = 0,
        }, Ct);
        int part = RowsPerHour / 12;
        for (int from = 0; from < rows.Length; from += part)
        {
            await using ObjectDraft draft = dataset.StartObject();
            ColumnsBuilder builder = draft.Writer.Builder();
            for (int row = from; row < from + part; row++)
            {
                builder.Column<DateTimeOffset>(0).Append((DateTimeOffset)rows[row][0]!);
                for (int c = 0; c < columns.Length; c++)
                {
                    Append(builder, c + 1, columns[c], rows[row][c + 1]);
                }
            }

            await draft.Writer.WriteAsync(builder, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    private static void Append(ColumnsBuilder builder, int field, Drawn column, object? value)
    {
        switch (column.Kind)
        {
            case Kind.Text or Kind.Wide or Kind.Identifier when column.Nullable:
                ColumnBuilder<string?> text = builder.Column<string?>(field);
                if (value is null)
                {
                    text.AppendNull();
                }
                else
                {
                    text.Append(((string)value).AsSpan());
                }

                return;
            case Kind.Text or Kind.Wide or Kind.Identifier:
                builder.Column<string>(field).Append(((string)value!).AsSpan());
                return;
            case Kind.Code:
                Fixed<short>(builder, field, column.Nullable, value);
                return;
            case Kind.Int32:
                Fixed<int>(builder, field, column.Nullable, value);
                return;
            case Kind.Int64:
                Fixed<long>(builder, field, column.Nullable, value);
                return;
            case Kind.UInt32:
                Fixed<uint>(builder, field, column.Nullable, value);
                return;
            case Kind.Float32:
                Fixed<float>(builder, field, column.Nullable, value);
                return;
            default:
                Fixed<double>(builder, field, column.Nullable, value);
                return;
        }
    }

    private static void Fixed<T>(ColumnsBuilder builder, int field, bool nullable, object? value)
        where T : unmanaged, System.Numerics.IBinaryNumber<T>
    {
        if (!nullable)
        {
            builder.Column<T>(field).Append((T)value!);
            return;
        }

        ColumnBuilder<T?> column = builder.Column<T?>(field);
        if (value is null)
        {
            column.AppendNull();
        }
        else
        {
            column.Append((T)value);
        }
    }
}
