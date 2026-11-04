using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The columns of a chosen row that are no single value — lists and variants — read as the rows they are: kept as rows
/// once the pass has chosen them and gathered into the result, the same at every degree, blocking or streaming, through a
/// top-k, over a whole scan and over a dataset with deleted rows, against LINQ over the rows in file order.
/// </summary>
public sealed partial class ChosenRowTests
{
    private const int SampleRows = 20_000;

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    public async Task ListsAndVariantsOfChosenRowsAreReadAsRows(int degree, bool blocking)
    {
        Sample[] rows = Samples();
        string path = await WriteRowsAsync(rows, Chunked);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Sample>().With(new ScanOptions { BatchRows = 1_024 })
                .GroupBy(r => r.Minute)
                .OrderBy(g => g.Key)
                .Select(g => (
                    g.Key,
                    g.First().Names,
                    g.Last().Counts,
                    g.MaxBy(x => x.Value).Attributes,
                    g.MinBy(x => x.Value).Extra,
                    g.First().Extra,
                    g.Where(x => x.Kind == 3).Last().Names));
            query.Plan.Blocking = blocking;
            List<SampleEnds> read = await ListAsync(query.As<SampleEnds>());

            Assert.Equal(rows.GroupBy(r => r.Minute).OrderBy(g => g.Key).Select(g => Show(SampleEndsOf(g.Key, [.. g]))), read.Select(Show));
            Assert.Contains(read, ends => ends.LateNames is null);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AGroupWithoutAChosenRowReadsANullListOrVariant()
    {
        Sample[] rows = Samples();
        string path = await WriteRowsAsync(rows, Chunked);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<NoChosen> groups = await ListAsync(file.Scan<Sample>()
                .Where(r => r.Value == null)
                .GroupBy(r => r.Kind)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.MaxBy(x => x.Value).Attributes, g.Where(x => x.Minute < 0).Last().Names, g.First().Names))
                .As<NoChosen>());

            Assert.Equal(rows.Where(r => r.Value is null).Select(r => r.Kind).Distinct().Order(), groups.Select(g => g.Kind));
            Assert.All(groups, g =>
            {
                Assert.Null(g.Attributes);
                Assert.Null(g.Names);
                Assert.Equal(Join(rows.First(r => r.Value is null && r.Kind == g.Kind).Names), Join(g.FirstNames));
            });
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATopKReadsTheListsAndVariantsOfTheGroupsItDelivers()
    {
        Sample[] rows = Samples();
        string path = await WriteRowsAsync(rows, Chunked);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<TopSample> top = await ListAsync(file.Scan<Sample>()
                .GroupBy(r => r.Minute)
                .OrderByDescending(g => g.Max(x => x.Value))
                .ThenBy(g => g.Key)
                .Take(12)
                .Select(g => (g.Key, g.MaxBy(x => x.Value).Names, g.MaxBy(x => x.Value).Attributes, g.Last().Extra))
                .As<TopSample>());

            Assert.Equal(
                rows.GroupBy(r => r.Minute).OrderByDescending(g => g.Max(r => r.Value)).ThenBy(g => g.Key).Take(12).Select(g =>
                {
                    Sample high = BestSample([.. g], max: true)!.Value;
                    return $"{g.Key} {Join(high.Names)} {Json(high.Attributes)} {Json(g.Last().Extra)}";
                }),
                top.Select(t => $"{t.Minute} {Join(t.Names)} {Json(t.Attributes)} {Json(t.Extra)}"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AWholeScanReadsTheListsAndVariantsOfItsChosenRowsThroughARecord()
    {
        Sample[] rows = Samples();
        string path = await WriteRowsAsync(rows, Chunked);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            ScanEnds ends = await file.Scan<Sample>()
                .Where(r => r.Minute >= 100)
                .AggregateAsync<ScanEnds>(a => (a.First().Names, a.Last().Attributes, a.MaxBy(x => x.Value).Extra, a.Count()), Ct);

            Sample[] kept = [.. rows.Where(r => r.Minute >= 100)];
            Assert.Equal(Join(kept[0].Names), Join(ends.FirstNames));
            Assert.Equal(kept[^1].Attributes, ends.LastAttributes);
            Assert.Equal(Json(BestSample(kept, max: true)!.Value.Extra), Json(ends.HighExtra));
            Assert.Equal(kept.Length, ends.Rows);

            // One answer is a value, which a list or a variant of a chosen row is not: the record reads it.
            NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await file.Scan<Sample>().AggregateAsync(a => a.Last().Attributes, Ct));
            Assert.Contains("record", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AChosenListIsFilteredOnAndReadThroughARecordButNeitherOrderedNorReadAsAValue()
    {
        Sample[] rows = Samples();
        string path = await WriteRowsAsync(rows, Chunked);
        try
        {
            // A filter on groups reads the rows gathered as a column, as the result does.
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<int> minutes = await file.Scan<Sample>()
                .GroupBy(r => r.Minute)
                .Where(g => g.First().Extra.IsNull)
                .OrderBy(g => g.Key)
                .Select(g => g.Key)
                .ToListAsync(Ct);
            Assert.Equal(rows.GroupBy(r => r.Minute).Where(g => g.First().Extra is null).Select(g => g.Key).Order(), minutes);
            Assert.NotEmpty(minutes);

            // A list has no order; one value of it read alone is told to go through a record.
            Aggregation<int> ordered = file.Scan<Sample>().GroupBy(r => r.Kind).OrderBy(g => g.First().Names).Select(g => g.Key);
            ArgumentException unordered = await Assert.ThrowsAsync<ArgumentException>(async () => await ordered.ToListAsync(Ct));
            Assert.Contains("has no order", unordered.Message, StringComparison.Ordinal);
            Aggregation<ReadOnlyMemory<string>> firsts = file.Scan<Sample>().GroupBy(r => r.Kind).Select(g => g.First().Names);
            NotSupportedException alone = await Assert.ThrowsAsync<NotSupportedException>(async () => await firsts.ToListAsync(Ct));
            Assert.Contains("As<TRecord>()", alone.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AChosenRowOfADatasetIsOneOfItsLiveRows()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Sample.Schema, new DatasetOptions
        {
            Write = new VortexWriteOptions { RowBlockSize = 512 },
            MarkedObjectBytes = 0,
            MarkedShare = 1,
        }, Ct);
        Sample[] rows = Samples();
        for (int part = 0; part < 4; part++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Sample>(rows[(part * 5_000)..((part + 1) * 5_000)], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        RowChangeResult deleted = await dataset.DeleteAsync<Sample>(r => (r.Minute >= 100 & r.Minute < 140) | r.Value > 900, Ct);
        Assert.True(deleted.ObjectsMarked > 0);
        Sample[] live = [.. rows.Where(r => !((r.Minute >= 100 && r.Minute < 140) || r.Value > 900))];

        List<DatasetEnds> read = await ListAsync(dataset.Scan<Sample>()
            .GroupBy(r => r.Kind)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.MaxBy(x => x.Id).Names, g.MinBy(x => x.Id).Attributes, g.MaxBy(x => x.Id).Extra))
            .As<DatasetEnds>());

        Assert.Equal(
            live.GroupBy(r => r.Kind).OrderBy(g => g.Key).Select(g =>
                $"{g.Key} {Join(g.MaxBy(r => r.Id).Names)} {Json(g.MinBy(r => r.Id).Attributes)} {Json(g.MaxBy(r => r.Id).Extra)}"),
            read.Select(r => $"{r.Kind} {Join(r.Names)} {Json(r.Attributes)} {Json(r.Extra)}"));
    }

    [Fact]
    public async Task RowsWithNullVariantsSortAndWriteIntoADataset()
    {
        Sample[] rows = Samples();
        string path = await WriteRowsAsync(rows, Chunked);
        try
        {
            // A result sorted in memory keeps its rows in a store, a null variant among them.
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<IdExtra> sorted = await ListAsync(file.Scan<Sample>().Select(r => (r.Id, r.Extra)).As<IdExtra>().OrderByDescending(r => r.Id));
            Assert.Equal(rows.OrderByDescending(r => r.Id).Select(r => $"{r.Id} {Json(r.Extra)}"), sorted.Select(r => $"{r.Id} {Json(r.Extra)}"));

            // A dataset's writer takes the rows of a scan into its store; an update rewrites rows that hold nulls.
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Sample.Schema, cancellationToken: Ct);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync(file.Scan<Sample>(), Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            RowChangeResult updated = await dataset.UpdateAsync<Sample>(r => r.Kind == 1, row => row with { Value = -1 }, Ct);
            Assert.True(updated.Rows > 0);
            List<Sample> read = await ListAsync(dataset.Scan<Sample>());
            Assert.Equal(
                rows.Select(r => r.Kind == 1 ? r with { Value = -1 } : r).OrderBy(r => r.Id).Select(ShowRow),
                read.OrderBy(r => r.Id).Select(ShowRow));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>What a group's chosen rows hold, as LINQ finds them.</summary>
    private static SampleEnds SampleEndsOf(int minute, Sample[] of)
    {
        Sample? high = BestSample(of, max: true);
        Sample? low = BestSample(of, max: false);
        Sample? late = of.Where(r => r.Kind == 3).Select(r => (Sample?)r).LastOrDefault();
        return new SampleEnds(minute, of[0].Names, of[^1].Counts, high?.Attributes, low?.Extra, of[0].Extra, late?.Names);
    }

    /// <summary>The row of the largest or smallest value, the first on a tie, a null never.</summary>
    private static Sample? BestSample(Sample[] rows, bool max)
    {
        Sample? best = null;
        long kept = 0;
        foreach (Sample row in rows)
        {
            if (row.Value is not long value)
            {
                continue;
            }

            if (best is null || (max ? value > kept : value < kept))
            {
                best = row;
                kept = value;
            }
        }

        return best;
    }

    private static string Show(SampleEnds e) => string.Join(
        " | ", e.Minute, Join(e.FirstNames), Join(e.LastCounts), Json(e.HighAttributes), Json(e.LowExtra), Json(e.FirstExtra), Join(e.LateNames));

    private static string ShowRow(Sample r) =>
        $"{r.Id} {r.Kind} {r.Minute} {r.Value} {Join(r.Names)} {Join(r.Counts)} {r.Attributes.ToJson()} {Json(r.Extra)}";

    private static string Join<T>(ReadOnlyMemory<T> values) => "[" + string.Join(",", values.ToArray()) + "]";

    /// <summary>A list's elements; "none" for a null row.</summary>
    private static string Join<T>(ReadOnlyMemory<T>? values) => values is { } held ? Join(held) : "none";

    /// <summary>A variant's text; "none" for a null row, which a variant that holds a null tells apart.</summary>
    private static string Json(Variant? value) => value is { } held ? held.ToJson() : "none";

    /// <summary>
    /// Rows by the minute, forty a minute: values with nulls and ties, lists of every length from none, attributes whose
    /// keys vary, and an extra variant that is a null row, a null variant or an object.
    /// </summary>
    private static Sample[] Samples()
    {
        Random random = new Random(31);
        Sample[] rows = new Sample[SampleRows];
        for (int row = 0; row < rows.Length; row++)
        {
            long? value = row % 13 == 0 ? null : random.Next(1_000);
            string[] names = [.. Enumerable.Range(0, random.Next(6)).Select(_ => $"n{random.Next(50)}")];
            long[] counts = [.. Enumerable.Range(0, random.Next(4)).Select(_ => (long)random.Next(-5, 100_000))];
            List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(9)}"))];
            if (random.Next(3) == 0)
            {
                fields.Add(($"key.{random.Next(40)}", Variant.From(random.NextDouble())));
            }

            if (random.Next(5) == 0)
            {
                fields.Add(("cart", Variant.Object(("items", Variant.From(random.Next(9))), ("tags", Variant.Array(Variant.From("a"), Variant.From(row))))));
            }

            Variant attributes = row % 97 == 0 ? Variant.Null : Variant.Object([.. fields]);
            Variant? extra = (row % 3) switch
            {
                0 => (Variant?)null,
                1 when row % 11 == 0 => Variant.Null,
                _ => Variant.Object(("n", Variant.From(row)), ("odd", Variant.From(row % 2 == 1))),
            };
            rows[row] = new Sample(row, random.Next(8), row / 40, value, names, counts, attributes, extra);
        }

        return rows;
    }

    private static async Task<string> WriteRowsAsync<T>(T[] rows, VortexWriteOptions? options = null)
        where T : IVortexRecord<T>
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "chosen-rows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{typeof(T).Name}-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<T>(path, options))
        {
            await writer.WriteAsync<T>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Sample(
        long Id, int Kind, int Minute, long? Value, ReadOnlyMemory<string> Names, ReadOnlyMemory<long> Counts, Variant Attributes, Variant? Extra);

    [VortexRecord]
    public partial record struct SampleEnds(
        int Minute,
        ReadOnlyMemory<string> FirstNames,
        ReadOnlyMemory<long> LastCounts,
        Variant? HighAttributes,
        Variant? LowExtra,
        Variant? FirstExtra,
        ReadOnlyMemory<string>? LateNames);

    [VortexRecord]
    public partial record struct NoChosen(int Kind, Variant? Attributes, ReadOnlyMemory<string>? Names, ReadOnlyMemory<string> FirstNames);

    [VortexRecord]
    public partial record struct TopSample(int Minute, ReadOnlyMemory<string>? Names, Variant? Attributes, Variant? Extra);

    [VortexRecord]
    public partial record struct ScanEnds(ReadOnlyMemory<string> FirstNames, Variant LastAttributes, Variant? HighExtra, long Rows);

    [VortexRecord]
    public partial record struct DatasetEnds(int Kind, ReadOnlyMemory<string>? Names, Variant? Attributes, Variant? Extra);

    [VortexRecord]
    public partial record struct IdExtra(long Id, Variant? Extra);
}
