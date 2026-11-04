// The paths of a variant column among the rows a scan of a file keeps: from the file's path directory, for the granules a count's
// plan keeps, every path a kept row holds listed and an exact count exact; from a census of the kept rows where the file has no
// directory, or the column is nested, every count exact; through the typed path and the tool path alike.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed partial class VariantPathsFileTests
{
    private const int Rows = 6_000;

    private const int GranuleRows = 256;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFileAnswersFromItsDirectoryForTheGranulesItKeeps()
    {
        Item[] rows = Items();
        string path = await WriteAsync(rows, directory: true);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // Every row: the directory's counts, exact.
            VariantPaths all = await file.Scan<Item>().PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.True(all.Exact && all.Complete);
            Assert.Equal(Sorted(Truth(rows, r => r.Attributes)), Sorted(Counts(all)));

            // A range of the sorted column: the granules inside it whole and exact, the two it cuts estimated, nothing missed.
            Item[] window = [.. rows.Where(r => r.Minute >= 30 && r.Minute < 70)];
            VariantPaths cut = await file.Scan<Item>().Where(r => r.Minute >= 30 & r.Minute < 70).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            AgreeWith(Truth(window, r => r.Attributes), cut);
            Assert.Contains(cut, p => p.Exact);
            Assert.Contains(cut, p => !p.Exact);

            // A column the file is not sorted by: every granule kept in part, every count estimated, nothing missed; exact on asking.
            Item[] one = [.. rows.Where(r => r.Kind == 2)];
            VariantPaths spread = await file.Scan<Item>().Where(r => r.Kind == 2).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            AgreeWith(Truth(one, r => r.Attributes), spread);
            VariantPaths exact = await file.Scan<Item>().Where(r => r.Kind == 2).PathsAsync(r => r.Attributes, new PathsOptions { Exact = true }, Ct);
            Assert.True(exact.Exact && exact.Complete);
            Assert.Equal(Sorted(Truth(one, r => r.Attributes)), Sorted(Counts(exact)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileWithoutADirectoryCountsTheRowsItKeeps()
    {
        Item[] rows = Items();
        string path = await WriteAsync(rows, directory: false);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            VariantPaths found = await file.Scan<Item>().Where(r => r.Kind == 2 | r.Minute < 10).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.True(found.Exact && found.Complete);
            Assert.Equal(Sorted(Truth(rows.Where(r => r.Kind == 2 || r.Minute < 10), r => r.Attributes)), Sorted(Counts(found)));

            // A nested variant column has no directory of its own: its kept rows are counted.
            VariantPaths nested = await file.Scan<Item>().Where(r => r.Minute >= 50).PathsAsync(r => r.Inner.Payload, cancellationToken: Ct);
            Assert.Equal("Inner.Payload", nested.Column);
            Assert.Equal(Sorted(Truth(rows.Where(r => r.Minute >= 50), r => r.Inner.Payload)), Sorted(Counts(nested)));

            // The tool path, by the columns' names, several at once.
            IReadOnlyList<VariantPaths> named = await file.Scan().PathsAsync(["Attributes", "Inner.Payload"], cancellationToken: Ct);
            Assert.Equal(Sorted(Truth(rows, r => r.Attributes)), Sorted(Counts(named[0])));
            Assert.Equal(Sorted(Truth(rows, r => r.Inner.Payload)), Sorted(Counts(named[1])));
            ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await file.Scan().PathsAsync("Minute", cancellationToken: Ct));
            Assert.Contains("variant", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Twenty thousand rows, each with a key of its own: a census of them is held under the question's memory, which a small
    /// budget refuses rather than exceed, and which a large one grants; past the census's keys, the answer says it is not
    /// complete.
    /// </summary>
    [Fact]
    public async Task ACensusPastItsBudgetFailsByTheBudget()
    {
        Item[] rows = [.. Enumerable.Range(0, 20_000).Select(row => new Item(row / 60, row % 5, Variant.Object(($"id.{row}", Variant.From(row))), new Nested(row, Variant.Null)))];
        string path = await WriteAsync(rows, directory: false);
        try
        {
            await using (VortexSession small = VortexSession.Create(options => options.MemoryBudget = new QueryMemoryBudget(1 << 20)))
            {
                await using VortexFile file = await small.OpenAsync(path, cancellationToken: Ct);
                await Assert.ThrowsAsync<VortexMemoryException>(async () => await file.Scan<Item>().PathsAsync(r => r.Attributes, cancellationToken: Ct));
            }

            await using VortexSession large = VortexSession.Create(options => options.MemoryBudget = new QueryMemoryBudget(256 << 20));
            await using VortexFile wide = await large.OpenAsync(path, cancellationToken: Ct);
            VariantPaths found = await wide.Scan<Item>().PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.False(found.Complete);
            Assert.Equal(PathCensus.MaxKeys + 1, found.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void APathPrintsAsAFilterWritesIt()
    {
        VariantPath path = new VariantPath(["http.route", "it's"], VariantPathClass.String, 3, Exact: false);
        Assert.Equal("['http.route']['it\\'s']", path.Path);
        Assert.Equal("['http.route']['it\\'s'] String: 3 rows (estimated)", path.ToString());
        Assert.Equal("(root) Object: 1 row", new VariantPath([], VariantPathClass.Object, 1, Exact: true).ToString());
    }

    /// <summary>Every path the kept rows hold is listed; every count said exact is the truth's.</summary>
    private static void AgreeWith(Dictionary<string, long> truth, VariantPaths found)
    {
        Assert.True(found.Complete);
        Dictionary<string, VariantPath> listed = found.ToDictionary(Key);
        foreach ((string path, long held) in truth)
        {
            Assert.True(listed.TryGetValue(path, out VariantPath at), $"{path} is missing");
            Assert.True(!at.Exact || at.Rows == held, $"{path}: {held} rows hold it and {at.Rows} said exact");
        }

        Assert.All(found.Where(path => path.Exact), path => Assert.True(truth.ContainsKey(Key(path)), $"{Key(path)} said exact and held by no kept row"));
    }

    /// <summary>Rows by the minute, sorted by it; a kind in no order; attributes whose keys vary by the minute's tens and by the row.</summary>
    private static Item[] Items()
    {
        Random random = new Random(5);
        Item[] rows = new Item[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int minute = row / 60;
            List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(5)}"))];
            fields.Add(($"m{minute / 10}", Variant.From(minute)));
            if (random.Next(4) == 0)
            {
                fields.Add(($"user.{random.Next(30)}", random.Next(2) == 0 ? Variant.From(random.NextDouble()) : Variant.From("x")));
            }

            if (row % 500 == 7)
            {
                fields.Add(("rare", Variant.Object(("deep", Variant.From(true)))));
            }

            Variant attributes = row % 211 == 0 ? Variant.From(row) : Variant.Object([.. fields]);
            Variant payload = random.Next(3) == 0 ? Variant.Null : Variant.Object(("n", Variant.From(row)), ("list", Variant.Array(Variant.From(1))));
            rows[row] = new Item(minute, random.Next(5), attributes, new Nested(row, payload));
        }

        return rows;
    }

    /// <summary>The rows written; with <paramref name="directory"/>, the directory of their attributes declared, a group a granule.</summary>
    private static async Task<string> WriteAsync(Item[] rows, bool directory)
    {
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = GranuleRows };
        if (directory)
        {
            PathCensus census = new PathCensus(options.StringBoundBytes, GranuleRows);
            foreach (Item row in rows)
            {
                census.Add(row.Attributes.Metadata.Span, row.Attributes.Value.Span);
            }

            options = options with { VariantDirectories = new Dictionary<string, VariantDirectory> { [nameof(Item.Attributes)] = VariantDirectory.From(census, null, GranuleRows) } };
            census.Release();
        }

        string path = Path.Combine(AppContext.BaseDirectory, $"variant-paths-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Item>(path, options))
        {
            await writer.WriteAsync<Item>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    /// <summary>Each path and class the rows' variants hold, with the rows that hold it.</summary>
    private static Dictionary<string, long> Truth(IEnumerable<Item> rows, Func<Item, Variant> column)
    {
        Dictionary<string, long> truth = [];
        foreach (Item row in rows)
        {
            Walk(column(row), string.Empty, truth);
        }

        return truth;

        static void Walk(Variant value, string path, Dictionary<string, long> truth)
        {
            string key = $"{path}#{ClassOf(value.Kind)}";
            truth[key] = truth.GetValueOrDefault(key) + 1;
            if (value.Kind == VariantKind.Object)
            {
                foreach (string name in value.GetKeys())
                {
                    Walk(value[name]!.Value, $"{path}/{name}", truth);
                }
            }
        }
    }

    private static VariantPathClass ClassOf(VariantKind kind) => kind switch
    {
        VariantKind.Null => VariantPathClass.Null,
        VariantKind.Bool => VariantPathClass.Boolean,
        VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => VariantPathClass.Integer,
        VariantKind.Float or VariantKind.Double => VariantPathClass.Double,
        VariantKind.String => VariantPathClass.String,
        VariantKind.Timestamp => VariantPathClass.Timestamp,
        VariantKind.Object => VariantPathClass.Object,
        VariantKind.Array => VariantPathClass.List,
        _ => VariantPathClass.Other,
    };

    private static string Key(VariantPath path) => $"{string.Concat(path.Keys.Select(key => "/" + key))}#{path.Class}";

    private static Dictionary<string, long> Counts(VariantPaths paths) => paths.ToDictionary(Key, path => path.Rows);

    private static List<KeyValuePair<string, long>> Sorted(Dictionary<string, long> paths) =>
        [.. paths.OrderBy(path => path.Key, StringComparer.Ordinal)];

    [VortexRecord]
    public partial record struct Item(int Minute, int Kind, Variant Attributes, Nested Inner);

    [VortexRecord]
    public partial record struct Nested(long Id, Variant Payload);
}
