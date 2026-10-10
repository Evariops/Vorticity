using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Files whose row groups declare the columns their rows are sorted on: the declaration this writer
/// makes, holding the rows it is given to it, and the scans that read such a file in its order with
/// no sort, a group by on its key closing its groups as the scan passes them.
/// </summary>
public sealed partial class SortedReadTests : IDisposable
{
    private const int Rows = 50_000;
    private const int GroupRows = 4_096;

    private static readonly string[] Desks = ["alpha", "beta", "delta", "gamma"];

    private readonly List<string> _paths = [];

    [VortexRecord]
    public partial record struct Tick(long Key, string Desk, long Size);

    [VortexRecord]
    public partial record struct Stamp([VortexColumn(Unit = TimeUnit.Microseconds)] DateTime At, int Seq);

    [VortexRecord]
    public partial record struct Quote(string? Desk, long Key, decimal Price);

    [VortexRecord]
    public partial record struct Row(long Id);

    [VortexRecord]
    public partial record struct KeyTotal(long Key, long Count, long Sum);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task StreamsTheRowsOfAFileSortedOnItsKey()
    {
        Tick[] rows = Ticks();
        string path = await WriteAsync(rows, [new ParquetSortingColumn("Key")]);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);

        // Every row group declares the key, and its rows bear the declaration out.
        Assert.True(file.Metadata.RowGroups.Count > 8, $"{file.Metadata.RowGroups.Count} row groups");
        Assert.All(file.Metadata.RowGroups, group => Assert.Equal([new ParquetSortingColumn("Key")], group.SortingColumns));
        Assert.Empty(await file.VerifyAsync(Ct));

        // In the key's order, the file streams as it lies: no sort, the same rows in the same order,
        // under a filter and a take as well.
        Assert.Equal(nameof(KeySourceKind.SortedColumn), (await file.Scan<Tick>().OrderBy(t => t.Key).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(rows, await ListAsync(file.Scan<Tick>().OrderBy(t => t.Key)));
        Assert.Equal(
            rows.Where(t => t.Key >= 9_000 && t.Size < 40),
            await ListAsync(file.Scan<Tick>().Where(t => t.Key >= 9_000 & t.Size < 40).OrderBy(t => t.Key)));
        Assert.Equal(rows.Take(10).Select(t => t.Key), await file.Scan<Tick>().OrderBy(t => t.Key).Select(t => t.Key).Take(10).ToListAsync(Ct));

        // Any other order is a sort: the key backwards, or another column.
        Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Tick>().OrderByDescending(t => t.Key).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(rows.OrderByDescending(t => t.Key), await ListAsync(file.Scan<Tick>().OrderByDescending(t => t.Key)));
        Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Tick>().OrderBy(t => t.Size).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(rows.OrderBy(t => t.Size), await ListAsync(file.Scan<Tick>().OrderBy(t => t.Size)));

        // The same rows with no declaration are sorted, to the same order.
        string plain = await WriteAsync(rows, null);
        await using ParquetFile undeclared = await ParquetFile.OpenAsync(plain, Ct);
        Assert.All(undeclared.Metadata.RowGroups, group => Assert.Empty(group.SortingColumns));
        Assert.Equal(nameof(KeySourceKind.InMemory), (await undeclared.Scan<Tick>().OrderBy(t => t.Key).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(rows, await ListAsync(undeclared.Scan<Tick>().OrderBy(t => t.Key)));
    }

    [Fact]
    public async Task StreamsAFileSortedDescendingOnATime()
    {
        DateTime start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        Stamp[] rows = [.. Enumerable.Range(0, Rows).Select(i => new Stamp(start.AddSeconds((Rows - i) / 3 * 7), i))];
        string path = await WriteAsync(rows, [new ParquetSortingColumn("At", Descending: true)]);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        Assert.Empty(await file.VerifyAsync(Ct));

        Assert.Equal(nameof(KeySourceKind.SortedColumn), (await file.Scan<Stamp>().OrderByDescending(s => s.At).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(rows, await ListAsync(file.Scan<Stamp>().OrderByDescending(s => s.At)));
        Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Stamp>().OrderBy(s => s.At).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(rows.OrderBy(s => s.At), await ListAsync(file.Scan<Stamp>().OrderBy(s => s.At)));
    }

    [Fact]
    public async Task HoldsAChainOfKeysWithTheirNullsInPlace()
    {
        // The desks nulls first, then by name; within a desk, the keys from the greatest down.
        List<Quote> quotes = [];
        foreach (string? desk in (string?[])[null, .. Desks])
        {
            for (int i = 0; i < 3_000; i++)
            {
                quotes.Add(new Quote(desk, 2_000 - (i / 2), 10m + (i % 997 / 100m)));
            }
        }

        Quote[] rows = [.. quotes];
        ParquetSortingColumn[] sorting = [new("Desk", NullsFirst: true), new("Key", Descending: true)];
        string path = await WriteAsync(rows, sorting, batch: 999);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        Assert.All(file.Metadata.RowGroups, group => Assert.Equal(sorting, group.SortingColumns));
        Assert.Empty(await file.VerifyAsync(Ct));

        // A key that holds nulls is sorted: the scan places them last, where the file has them first.
        Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Quote>().OrderBy(q => q.Desk).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal(
            [.. rows.Where(q => q.Desk is not null).OrderBy(q => q.Desk, StringComparer.Ordinal), .. rows.Where(q => q.Desk is null)],
            await ListAsync(file.Scan<Quote>().OrderBy(q => q.Desk)));

        // The second key orders the rows only within the first's ties: no order of the file's.
        Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Quote>().OrderByDescending(q => q.Key).ExplainAsync(Ct)).Order!.Source);
    }

    [Fact]
    public async Task RefusesRowsOutOfTheDeclaredOrder()
    {
        string path = Track($"vx-sorted-{Guid.NewGuid():N}.parquet");
        Tick[] first = [new(1, "a", 0), new(2, "a", 0), new(2, "b", 0)];
        Tick[] within = [new(3, "a", 0), new(5, "a", 0), new(4, "a", 0)];
        Tick[] across = [new(1, "c", 0)];
        Tick[] last = [new(2, "c", 0), new(9, "a", 0)];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Tick>(path, Options([new ParquetSortingColumn("Key")])))
        {
            await writer.WriteAsync<Tick>(first, Ct);

            // A row before the one before it in its batch, or the batch's first before the last row
            // written: the batch refused whole, before any column holds a row of it.
            ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync<Tick>(within, Ct).AsTask());
            Assert.Contains("Row 2 of the batch", refused.Message, StringComparison.Ordinal);
            refused = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync<Tick>(across, Ct).AsTask());
            Assert.Contains("Row 0 of the batch", refused.Message, StringComparison.Ordinal);

            // The file goes on from the rows it holds.
            await writer.WriteAsync<Tick>(last, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        Assert.Equal([.. first, .. last], await ListAsync(file.Scan<Tick>()));
        Assert.Empty(await file.VerifyAsync(Ct));
    }

    /// <summary>
    /// An integer first key, walked a register at a time, its ties held to the key after it: a row
    /// out of either order deep in a batch refused by its place, in both directions.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldsTheTiesOfAnIntegerKeyToTheKeyAfterIt(bool descending)
    {
        Tick[] rows = [.. Ticks().OrderBy(t => t.Key).ThenBy(t => t.Desk, StringComparer.Ordinal)];
        if (descending)
        {
            rows = [.. rows.OrderByDescending(t => t.Key).ThenBy(t => t.Desk, StringComparer.Ordinal)];
        }

        ParquetSortingColumn[] sorting = [new("Key", Descending: descending), new("Desk")];
        string path = await WriteAsync(rows, sorting, batch: 9_999);
        await using (ParquetFile file = await ParquetFile.OpenAsync(path, Ct))
        {
            Assert.Empty(await file.VerifyAsync(Ct));
            Assert.Equal(rows, await ListAsync(file.Scan<Tick>()));
        }

        // A tie whose desks go backwards, and a key that goes the wrong way, each 5 000 rows in.
        int tie = Enumerable.Range(5_000, 1_000).First(i => rows[i].Key == rows[i - 1].Key && rows[i].Desk != rows[i - 1].Desk);
        Tick[] desks = rows[..10_000];
        (desks[tie - 1], desks[tie]) = (desks[tie], desks[tie - 1]);
        Tick[] keys = rows[..10_000];
        int fall = Enumerable.Range(5_000, 1_000).First(i => rows[i].Key != rows[i - 1].Key);
        keys[fall] = keys[fall] with { Key = keys[fall - 1].Key + (descending ? 1 : -1) };
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Tick>(Track($"vx-sorted-{Guid.NewGuid():N}.parquet"), Options(sorting));
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync<Tick>(desks, Ct).AsTask());
        Assert.Contains($"Row {tie} of the batch", refused.Message, StringComparison.Ordinal);
        refused = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync<Tick>(keys, Ct).AsTask());
        Assert.Contains($"Row {fall} of the batch", refused.Message, StringComparison.Ordinal);
        writer.Abandon();
    }

    [Theory]
    [InlineData("Score", "is f64")]
    [InlineData("Point", "not a flat column")]
    [InlineData("Point.X", "not a flat column")]
    [InlineData("Tags", "not a flat column")]
    [InlineData("Missing", "not a flat column")]
    public void RefusesAColumnItCannotHoldTo(string column, string reason)
    {
        VortexSchema schema =
        [
            ("Key", VortexType.Int64),
            ("Score", VortexType.Float64),
            ("Point", VortexType.Struct([("X", VortexType.Int32), ("Y", VortexType.Int32)])),
            ("Tags", VortexType.List(VortexType.Int32)),
        ];
        string path = Track($"vx-sorted-{Guid.NewGuid():N}.parquet");
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => VortexSession.Default.CreateParquetWriter(path, schema, Options([new ParquetSortingColumn("Key"), new ParquetSortingColumn(column)])));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file of another writer whose row group declares an order its rows break, or a column it does
    /// not have: the verifier says so, and a scan, which no statistics let stream, sorts the rows.
    /// </summary>
    [Theory]
    [InlineData(0, "row 2 comes before row 1")]
    [InlineData(3, "names column 3 of 1")]
    public async Task FindsADeclarationTheRowsBreak(int column, string finding)
    {
        HandBuiltFile built = new HandBuiltFile(1) { Rows = 4, Sorting = [(column, false, false)] }.Leaf("Id", FieldRepetition.Required, PhysicalType.Int64);
        built.Chunk(PhysicalType.Int64, "Id").V2(null, 0, [0, 0, 0, 0], 0, 4, HandBuiltFile.Longs(5, 6, 3, 7));
        string path = Track($"vx-sorted-{Guid.NewGuid():N}.parquet");
        await System.IO.File.WriteAllBytesAsync(path, built.ToBytes(), Ct);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);

        Assert.Equal([new ParquetSortingColumn(column == 0 ? "Id" : $"#{column}")], file.Metadata.RowGroups[0].SortingColumns);
        ParquetFinding found = Assert.Single(await file.VerifyAsync(Ct));
        Assert.Equal("sorting_columns", found.Structure);
        Assert.Contains(finding, found.Message, StringComparison.Ordinal);

        Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Row>().OrderBy(r => r.Id).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal([new Row(3), new Row(5), new Row(6), new Row(7)], await ListAsync(file.Scan<Row>().OrderBy(r => r.Id)));
    }

    [Fact]
    public async Task StreamsAGroupByOnTheKeyTheFileIsSortedOn()
    {
        Tick[] rows = Ticks();
        KeyTotal[] expected = [.. rows.GroupBy(t => t.Key).Select(g => new KeyTotal(g.Key, g.Count(), g.Sum(t => t.Size)))];
        string path = await WriteAsync(rows, [new ParquetSortingColumn("Key")]);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);

        // Each key's group out once the scan reads past it, in the key's order.
        Vorticity.Aggregation byKey = file.Scan<Tick>().GroupBy(t => t.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Size)));
        Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)byKey.Query) >= 0);
        Assert.Equal(expected, await ListAsync(byKey.As<KeyTotal>()));
        Assert.True(((AggregationQuery)byKey.Query).PeakGroups < expected.Length / 4, $"{((AggregationQuery)byKey.Query).PeakGroups} of {expected.Length} groups held");

        // The same rows with no declaration are grouped whole, to the same groups.
        string plain = await WriteAsync(rows, null);
        await using ParquetFile undeclared = await ParquetFile.OpenAsync(plain, Ct);
        Vorticity.Aggregation hashed = undeclared.Scan<Tick>().GroupBy(t => t.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Size)));
        Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)hashed.Query) < 0);
        Assert.Equal(expected, (await ListAsync(hashed.As<KeyTotal>())).OrderBy(t => t.Key));
    }

    /// <summary>Rows in the order of their key, each key on one to five rows, the other columns in none.</summary>
    private static Tick[] Ticks()
    {
        Random random = new(17);
        Tick[] rows = new Tick[Rows];
        long key = 100;
        for (int i = 0; i < rows.Length; i++)
        {
            key += random.Next(5) == 0 ? 1 : 0;
            rows[i] = new Tick(key, Desks[random.Next(Desks.Length)], random.Next(100));
        }

        return rows;
    }

    private static ParquetWriteOptions Options(IReadOnlyList<ParquetSortingColumn>? sorting) =>
        new() { SortingColumns = sorting, RowGroupRows = GroupRows, BlockRows = 1_024 };

    private async Task<string> WriteAsync<T>(T[] rows, IReadOnlyList<ParquetSortingColumn>? sorting, int batch = 7_000)
        where T : IVortexRecord<T>
    {
        string path = Track($"vx-sorted-{Guid.NewGuid():N}.parquet");
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<T>(path, Options(sorting));
        for (int at = 0; at < rows.Length; at += batch)
        {
            await writer.WriteAsync<T>(rows.AsMemory(at, Math.Min(batch, rows.Length - at)).ToArray(), Ct);
        }

        await writer.CompleteAsync(Ct);
        return path;
    }

    private string Track(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), name);
        _paths.Add(path);
        return path;
    }

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }
}
