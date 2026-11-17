// A census of a directory's paths (PathCensus.AddPaths), as a job's first pass counts an input whose directory tells its paths:
// the counts a shredding is chosen from are those its rows counted make, path by path, and so is the shredding; the rows and
// the bytes of their values come from the rows (PathCensus.AddRows). A directory that holds a list, a number or a partial
// count tells nothing, and its rows are counted.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class PathCensusDirectoryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ADirectorysPathsCountWhatItsRowsCountAndChooseTheSameShredding(int seed)
    {
        Random random = new Random(seed);
        Variant[] rows = [.. Enumerable.Range(0, 6_000).Select(_ => Row(random))];
        PathCensus counted = Census(rows);

        using VariantDirectory.DirectoryBody body = Body(counted);
        Assert.True(PathCensus.Tells(body));
        PathCensus read = new PathCensus(stringBoundBytes: 8);
        read.AddPaths(body);

        Assert.False(read.Partial);
        Same(counted.Root, read.Root, "$");
        Assert.Equal(Describe(ShreddingChoice.Choose(counted)), Describe(ShreddingChoice.Choose(read)));
        Assert.NotNull(ShreddingChoice.Choose(read));

        // The rows and the bytes of their values, which the directory keeps too: a first pass that reads none of the rows
        // sizes the column's chunks as one that read them.
        Assert.Equal(counted.ValueBytes, body.ValueBytes);
        read.AddCounts(body.Rows, body.ValueBytes);
        Assert.Equal(counted.Rows, read.Rows);
        Assert.Equal(counted.ValueBytes, read.ValueBytes);
        Assert.Equal(ShreddingChoice.ChunkTargetBytes(counted), ShreddingChoice.ChunkTargetBytes(read));
    }

    /// <summary>
    /// A directory of the version before the rows' value bytes still reads, its paths whole, and says it keeps none of them:
    /// a first pass then reads the rows for their bytes.
    /// </summary>
    [Fact]
    public void ADirectoryOfTheVersionBeforeKeepsNoValueBytes()
    {
        Random random = new Random(5);
        PathCensus counted = Census([.. Enumerable.Range(0, 1_000).Select(_ => Row(random))]);
        VariantDirectory written = VariantDirectory.From(counted, null);
        VariantDirectory older = new VariantDirectory(written.Rows, written.Entries, written.GroupRows, written.Partial);
        using VariantDirectory.DirectoryBody body = VariantDirectory.Open(older.ToBytes())!;
        Assert.Equal(-1, body.ValueBytes);
        Assert.Equal(counted.Rows, body.Rows);
        Assert.True(PathCensus.Tells(body));
        Assert.Equal(written.Entries.Count, VariantDirectory.From(older.ToBytes())!.Entries.Count);
    }

    /// <summary>
    /// A census rented again after other rows, as a lane's census counts one input after another: the keys it kept count from
    /// nothing, and the census of the directory is the one a new census makes of it.
    /// </summary>
    [Fact]
    public void ACensusClearedCountsADirectoryAsANewOneDoes()
    {
        Random random = new Random(7);
        PathCensus counted = Census([.. Enumerable.Range(0, 3_000).Select(_ => Row(random))]);
        using VariantDirectory.DirectoryBody body = Body(counted);

        PathCensus cleared = Census([.. Enumerable.Range(0, 3_000).Select(_ => Row(random, prefix: "old"))]);
        cleared.Clear();
        cleared.AddPaths(body);

        Same(counted.Root, cleared.Root, "$");
        Assert.Equal(Describe(ShreddingChoice.Choose(counted)), Describe(ShreddingChoice.Choose(cleared)));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("double")]
    public void ADirectoryThatHoldsAListOrANumberTellsNothing(string held)
    {
        Random random = new Random(11);
        Variant[] rows = [.. Enumerable.Range(0, 500).Select(_ => Row(random))];
        rows[250] = held == "list"
            ? Variant.Object(("tags", Variant.Array("a", "b")))
            : Variant.Object(("ratio", Variant.From(0.25)));

        using VariantDirectory.DirectoryBody body = Body(Census(rows));
        Assert.False(PathCensus.Tells(body));
    }

    [Fact]
    public void APartialDirectoryTellsNothing()
    {
        PathCensus counted = new PathCensus(stringBoundBytes: 8);
        for (int row = 0; row <= PathCensus.MaxKeys; row++)
        {
            Variant value = Variant.Object(($"k{row}", Variant.From((long)row)));
            counted.Add(value.Metadata.Span, value.Value.Span);
        }

        Assert.True(counted.Partial);
        using VariantDirectory.DirectoryBody body = Body(counted);
        Assert.False(PathCensus.Tells(body));
    }

    /// <summary>
    /// A row of a metric's attributes, with no list and no number: texts past the bound, an integer that is sometimes a text,
    /// a nested object, a boolean, an instant, the variant null at a key, a key that is data, and null rows.
    /// </summary>
    private static Variant Row(Random random, string prefix = "k")
    {
        if (random.Next(40) == 0)
        {
            return Variant.Null;
        }

        List<(string, Variant)> fields =
        [
            ("http.route", Variant.From($"/api/v{random.Next(3)}/r{random.Next(211)}/{{id}}")),
            ("http.response.status_code", random.Next(10) < 8 ? Variant.From(200L + random.Next(5)) : Variant.From("unknown")),
            ("server", Variant.Object(("address", $"{random.Next(97)}.svc"), ("port", Variant.From((long)random.Next(8_000, 8_100))))),
            ("at", Variant.From(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(86_400)))),
            ("absent", Variant.Null),
            ($"{prefix}{random.Next(400):D4}", Variant.From(1L)),
        ];
        if (random.Next(2) == 0)
        {
            fields.Add(("sampled", Variant.From(random.Next(3) == 0)));
        }

        return Variant.Object(fields.ToArray());
    }

    private static PathCensus Census(Variant[] rows)
    {
        PathCensus census = new PathCensus(stringBoundBytes: 8);
        foreach (Variant row in rows)
        {
            if (row.IsNull)
            {
                census.AddNull();
            }
            else
            {
                census.Add(row.Metadata.Span, row.Value.Span);
            }
        }

        return census;
    }

    /// <summary>The directory of what <paramref name="census"/> counted, as an object writes it and a job reads it back.</summary>
    private static VariantDirectory.DirectoryBody Body(PathCensus census) =>
        VariantDirectory.Open(VariantDirectory.From(census, null).ToBytes())!;

    private static string Describe(VariantShredding? shredding)
    {
        if (shredding is null)
        {
            return "none";
        }

        StringBuilder text = new StringBuilder();
        Describe(shredding, text);
        return text.ToString();
    }

    private static void Describe(VariantShredding shredding, StringBuilder text)
    {
        text.Append(shredding.Kind).Append(':').Append(shredding.Class);
        if (shredding.Element is { } element)
        {
            text.Append('[');
            Describe(element, text);
            text.Append(']');
        }

        if (shredding.Kind == VariantShreddingKind.Object)
        {
            text.Append('{');
            foreach (ShreddedKey key in shredding.Keys)
            {
                text.Append(key.Name).Append('=');
                Describe(key.Field, text);
                text.Append(';');
            }

            text.Append('}');
        }
    }

    /// <summary>The counts a shredding is chosen from, the same path by path; a directory keeps no bounds a first pass reads.</summary>
    private static void Same(PathCensus.PathCount expected, PathCensus.PathCount actual, string path)
    {
        Assert.True(expected.Values == actual.Values, $"{path}: {actual.Values} values, not {expected.Values}");
        Assert.Equal(expected.OtherKeys, actual.OtherKeys);
        foreach (VariantClass @class in Enum.GetValues<VariantClass>())
        {
            Assert.True(expected.Of(@class) == actual.Of(@class), $"{path}: {actual.Of(@class)} of {@class}, not {expected.Of(@class)}");
            Assert.Equal(expected.ListsOf(@class), actual.ListsOf(@class));
        }

        Assert.Equal(expected.Objects, actual.Objects);
        foreach (PathClass @class in Enum.GetValues<PathClass>())
        {
            Assert.Equal(expected.Read(@class), actual.Read(@class));
        }

        string[] counted = [.. expected.Keys.Where(key => key.Value.Values > 0).Select(key => key.Key).Order(StringComparer.Ordinal)];
        Assert.Equal(counted, actual.Keys.Where(key => key.Value.Values > 0).Select(key => key.Key).Order(StringComparer.Ordinal));
        foreach (string name in counted)
        {
            Same(expected.Keys[name], actual.Keys[name], $"{path}.{name}");
        }
    }
}
