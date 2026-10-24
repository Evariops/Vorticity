// The sheets and counts of the objects of datasets cut by a clustering value of each kind, or by size alone, against each
// object's own rows; and questions drawn at random on them.
//
// WHAT IS HELD: on tables drawn at random, cut by an instant's hour, an instant's bucket of forty minutes, a code, a
// bucket of codes, a float's bucket, a text, a size alone, or an hour and a size, every object a job writes keeps a sheet
// of each column, read for every column in one request, and the counts of each column whose values repeat, a column a
// request, with no open of the object; a sheet's rows, nulls, distinct values, frequent values and filter hold to the
// object's rows, and a column's counts to the rows of each of its values. Questions drawn at random answer as their rows
// do, on one lane as on four, and the counts answer some of them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;
using Drawn = Vorticity.Tests.Dataset.DatasetCountsTests.Drawn;
using Kind = Vorticity.Tests.Dataset.DatasetCountsTests.Kind;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetClusteredSynopsesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> Clusterings() => new()
    {
        { "an instant's hour", 51 },
        { "an instant's 40 minutes", 52 },
        { "a code", 53 },
        { "a code's bucket of 8", 54 },
        { "a float's bucket of 400", 55 },
        { "a text", 56 },
        { "96 KiB", 57 },
        { "an instant's hour and 40 KiB", 58 },
    };

    [Theory]
    [MemberData(nameof(Clusterings))]
    public async Task AnObjectKeepsTheSheetsAndCountsItsRowsGive(string clustering, int seed)
    {
        Random random = new Random(seed);
        (Drawn[] columns, DatasetOptions options) = Clustered(clustering, random);
        object?[][] rows = DatasetCountsTests.Rows(columns, random);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await DatasetCountsTests.WriteAsync(store, columns, rows, options);

        int[] fields = [.. Enumerable.Range(0, columns.Length + 1)];
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { Counts.Count: > 0 })
            {
                continue;
            }

            objects++;
            store.Reset();
            ColumnSheet?[] sheets = await dataset.ReadSheetsAsync(held.Entry, fields, Ct);
            Assert.Equal(1, store.Requests);
            (ColumnCounts? Counts, ColumnSketches? Sketches)[] counts = new (ColumnCounts?, ColumnSketches?)[fields.Length];
            for (int field = 0; field < fields.Length; field++)
            {
                store.Reset();
                counts[field] = await dataset.ReadCountsAsync(held.Entry, field, sketches: true, Ct);
                Assert.Equal(counts[field].Counts is null ? 0 : 1, store.Requests);
            }

            // The object's own rows; the instant, a value a row, counted by none.
            object?[][] mine = await RowsAsync(dataset, held.Entry, columns);
            Assert.Equal(held.Entry.Rows, mine.Length);
            Assert.Null(counts[0].Counts);
            DType schema = dataset.Snapshot.Schema.DType;
            for (int c = 0; c < columns.Length; c++)
            {
                Drawn column = columns[c];
                int distinct = CheckSheet(Assert.IsType<ColumnSheet>(sheets[c + 1]), schema.GetField(c + 1), c, column, mine);
                long valid = mine.Count(r => r[c + 1] is not null);
                bool expected = distinct * 4L <= valid && (!column.Number || distinct <= ColumnCountsBuilder.FewValues) && distinct > 0;
                Assert.True(expected == counts[c + 1].Counts is not null, $"{clustering}: column {c} ({column}) counted {counts[c + 1].Counts is not null}, {distinct} values in {valid} rows");
                if (counts[c + 1] is ({ } kept, var sketches))
                {
                    DatasetCountsTests.Check(kept, sketches, c, columns, mine, distinct);
                    DatasetCountsTests.CheckDistinct(kept, sketches, c, columns, mine, distinct);
                }
            }
        }

        Console.WriteLine($"CLUSTERED {clustering}: {objects} objects with their counts");
        Assert.True(objects >= 2, $"{clustering}: {objects} objects with their counts");

        // Questions drawn at random, as rows, then on one lane and on four as the counts answer them.
        const int Questions = 12;
        int answered = 0;
        for (int question = 0; question < Questions; question++)
        {
            (string[] keys, string[] results, string? filter) = DatasetPlannerTests.Draw(random, columns, rows);
            string asked = $"{clustering}: by ({string.Join(", ", keys)}) {string.Join(", ", results)} where {filter ?? "true"}";
            List<string> read = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: false);
            foreach (int lanes in new[] { 1, 4 })
            {
                List<string> counted = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: true, lanes: lanes);
                Assert.True(read.SequenceEqual(counted), $"{asked}, {lanes} lanes: {read.Count} groups as rows, {counted.Count} with the counts, " +
                    $"first apart: '{read.Except(counted).FirstOrDefault()}' against '{counted.Except(read).FirstOrDefault()}'");
            }

            ScanPlan plan = await DatasetPlannerTests.Ask(dataset, keys, results, filter, synopses: true).ExplainAsync(Ct);
            answered += plan.States.Any(step => step.Read == "states") ? 1 : 0;
        }

        Console.WriteLine($"CLUSTERED {clustering}: the counts answered {answered} questions of {Questions}");
        Assert.True(answered > 0, $"{clustering}: the counts answered no question");
    }

    /// <summary>
    /// The table drawn for <paramref name="clustering"/> and the options of a dataset cut by it: the clustering column leads
    /// the key and holds no null; a size alone cuts blocks of a few kilobytes, so that an object's size moves as its rows
    /// are written. Every object a job writes keeps its sheets and counts, whatever its size.
    /// </summary>
    private static (Drawn[] Columns, DatasetOptions Options) Clustered(string clustering, Random random)
    {
        Drawn[] columns = DatasetCountsTests.Draw(random);
        string[] byInstant = ["At"];
        VortexWriteOptions small = new VortexWriteOptions { RowBlockSize = 512, DataBlockTargetBytes = 8 << 10 };
        (string[] key, ClusteringOptions cut, VortexWriteOptions write) = clustering switch
        {
            "an instant's hour" => (byInstant, new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, VortexWriteOptions.Default),
            "an instant's 40 minutes" => (byInstant, new ClusteringOptions { Value = ClusteringValue.Bucket(TimeSpan.FromMinutes(40)) }, VortexWriteOptions.Default),
            "a code" => (Lead(columns, 1), new ClusteringOptions { Value = ClusteringValue.Column }, VortexWriteOptions.Default),
            "a code's bucket of 8" => (Lead(columns, 1), new ClusteringOptions { Value = ClusteringValue.Bucket(8L) }, VortexWriteOptions.Default),
            "a float's bucket of 400" => (Lead(columns = [.. columns, new Drawn(Kind.Float64, false, 0)], columns.Length - 1),
                new ClusteringOptions { Value = ClusteringValue.Bucket(400.0) }, VortexWriteOptions.Default),
            "a text" => (Lead(columns, 0, values: 2 + random.Next(8)), new ClusteringOptions { Value = ClusteringValue.Column }, VortexWriteOptions.Default),
            "96 KiB" => (byInstant, new ClusteringOptions { ObjectBytes = 96L << 10 }, small),
            "an instant's hour and 40 KiB" => (byInstant, new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour), ObjectBytes = 40L << 10 }, small),
            _ => throw new ArgumentException($"No clustering named '{clustering}'.", nameof(clustering)),
        };

        return (columns, new DatasetOptions
        {
            ClusteringKey = key,
            Clustering = cut,
            Write = write,
            SheetObjectBytes = 0,
            ContentsObjectBytes = 0,
        });
    }

    /// <summary>The key led by column <paramref name="c"/>, then the instant; the column made to hold no null, and of <paramref name="values"/> values when given.</summary>
    private static string[] Lead(Drawn[] columns, int c, int? values = null)
    {
        columns[c] = columns[c] with { Nullable = false, Values = values ?? columns[c].Values };
        return [$"c{c}", "At"];
    }

    /// <summary>
    /// Holds <paramref name="sheet"/> of column <paramref name="c"/>, of <paramref name="dtype"/>, to the object's rows
    /// <paramref name="rows"/>: its rows and nulls, its distinct values, its frequent values' rows, the most first, and a
    /// filter that holds every value. Returns the distinct values.
    /// </summary>
    private static int CheckSheet(ColumnSheet sheet, DType dtype, int c, Drawn column, object?[][] rows)
    {
        Dictionary<string, (object Value, long Rows)> values = [];
        foreach (object? value in rows.Select(r => r[c + 1]))
        {
            if (value is not null)
            {
                string key = DatasetCountsTests.Key(value);
                values[key] = (value, values.TryGetValue(key, out (object Value, long Rows) seen) ? seen.Rows + 1 : 1);
            }
        }

        Assert.Equal(rows.Length, sheet.Rows);
        Assert.Equal(rows.Count(r => r[c + 1] is null), sheet.Nulls);
        Assert.True(sheet.IsExact);
        Assert.Equal(values.Count, sheet.Distinct);
        long previous = long.MaxValue;
        foreach (SheetValue top in sheet.Top)
        {
            Assert.Equal(0, top.Error);
            Assert.Equal(values[DatasetCountsTests.Key(DatasetCountsTests.Value(column, top.Value))].Rows, top.Rows);
            Assert.True(top.Rows <= previous, $"column {c}: a frequent value of {top.Rows} rows after one of {previous}");
            previous = top.Rows;
        }

        if (sheet.Filter is not null)
        {
            foreach ((object value, long _) in values.Values)
            {
                Assert.True(sheet.MayContain(Literal(value), dtype), $"column {c}: the filter says {value} absent");
            }
        }

        return values.Count;
    }

    private static FilterLiteral Literal(object value) => value switch
    {
        string text => FilterLiteral.From(text),
        short code => FilterLiteral.From(code),
        int integer => FilterLiteral.From(integer),
        long wide => FilterLiteral.From(wide),
        uint unsigned => FilterLiteral.From((ulong)unsigned),
        float single => FilterLiteral.From(single),
        _ => FilterLiteral.From((double)value),
    };

    /// <summary>The rows of the object <paramref name="entry"/> names, each as <see cref="DatasetCountsTests.Rows"/> draws them, its instant left null.</summary>
    private static async Task<object?[][]> RowsAsync(VortexDataset dataset, ObjectEntry entry, Drawn[] columns)
    {
        List<object?[]> rows = [];
        await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
        await foreach (RecordBatch batch in lease.File.ScanBuilder().ExecuteAsync().WithCancellation(Ct))
        {
            Add(batch, columns, rows);
        }

        return [.. rows];
    }

    private static void Add(RecordBatch batch, Drawn[] columns, List<object?[]> rows)
    {
        for (int row = 0; row < batch.RowCount; row++)
        {
            object?[] values = new object?[columns.Length + 1];
            for (int c = 0; c < columns.Length; c++)
            {
                VortexColumn column = batch.Column(c + 1);
                values[c + 1] = !column.IsValid(row) ? null : columns[c].Kind switch
                {
                    Kind.Text or Kind.Wide or Kind.Identifier => Encoding.UTF8.GetString(column.AsBinary().GetSpan(row)),
                    Kind.Code => column.AsPrimitive<short>().Values[row],
                    Kind.Int32 => column.AsPrimitive<int>().Values[row],
                    Kind.Int64 => column.AsPrimitive<long>().Values[row],
                    Kind.UInt32 => column.AsPrimitive<uint>().Values[row],
                    Kind.Float32 => column.AsPrimitive<float>().Values[row],
                    _ => (object)column.AsPrimitive<double>().Values[row],
                };
            }

            rows.Add(values);
        }
    }
}
