// Censuses merged (PathCensus.Merge), as a job's first pass counts each input apart and adds the counts of a value up: the
// rows cut in parts, each part counted, the parts' counts merged in order, give the census of the rows counted one after
// the other, path by path; past the keys a census counts, the merged one is partial as the other is.
using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class PathCensusMergeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartsCountedApartAndMergedAreTheRowsCountedInOrder(int seed)
    {
        Random random = new Random(seed);
        Variant[] rows = [.. Enumerable.Range(0, 12_000).Select(_ => Row(random, keys: 1_000))];
        int[] cuts = [0, .. Enumerable.Range(0, 3).Select(_ => random.Next(rows.Length)).Order(), rows.Length];

        PathCensus whole = Census(rows, 0, rows.Length);
        PathCensus merged = Census(rows, cuts[0], cuts[1]);
        for (int part = 1; part + 1 < cuts.Length; part++)
        {
            merged.Merge(Census(rows, cuts[part], cuts[part + 1]));
        }

        Assert.False(whole.Partial);
        Assert.Equal(whole.Rows, merged.Rows);
        Assert.Equal(whole.ValueBytes, merged.ValueBytes);
        Assert.Equal(whole.Partial, merged.Partial);
        Same(whole.Root, merged.Root, "$");
    }

    [Fact]
    public void PastItsKeysAMergedCensusIsPartialAndCountsEveryValue()
    {
        Random random = new Random(4);
        Variant[] rows = [.. Enumerable.Range(0, 30_000).Select(_ => Row(random, keys: 40_000))];
        PathCensus whole = Census(rows, 0, rows.Length);
        PathCensus merged = Census(rows, 0, rows.Length / 2);
        merged.Merge(Census(rows, rows.Length / 2, rows.Length));

        Assert.True(whole.Partial);
        Assert.True(merged.Partial);
        Assert.Equal(whole.Rows, merged.Rows);
        Assert.Equal(whole.Root.Values, merged.Root.Values);
        Assert.Equal(Values(whole.Root), Values(merged.Root));
        Assert.Equal(whole.Root.Keys["status"].Of(VariantClass.Integer), merged.Root.Keys["status"].Of(VariantClass.Integer));
    }

    /// <summary>A row of a log's attributes: a few keys every row holds, nested objects, lists, texts past the bound, and a key that is data.</summary>
    private static Variant Row(Random random, int keys)
    {
        if (random.Next(50) == 0)
        {
            return Variant.Null;
        }

        List<(string, Variant)> fields =
        [
            ("status", Variant.From(200L + random.Next(5))),
            ("route", Variant.From($"/route/{random.Next(300)}/{new string('x', random.Next(12))}")),
            ("latency", random.Next(20) == 0 ? Variant.From(double.NaN) : Variant.From(random.NextDouble() * 100)),
            ("user", Variant.Object(("id", random.Next(1_000)), ("name", $"user-{random.Next(80)}"), ("admin", Variant.From(random.Next(9) == 0)))),
            ("tags", random.Next(3) == 0 ? Variant.Array() : Variant.Array("a", "b")),
            ("at", Variant.From(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(86_400)))),
            ("mixed", random.Next(10) < 7 ? Variant.From((long)random.Next(9)) : Variant.From("text")),
            ($"k{random.Next(keys):D5}", Variant.From(1L)),
        ];
        if (random.Next(2) == 0)
        {
            fields.Add(("ok", Variant.From(true)));
        }

        return Variant.Object(fields.ToArray());
    }

    private static PathCensus Census(Variant[] rows, int from, int to)
    {
        PathCensus census = new PathCensus(stringBoundBytes: 8);
        for (int row = from; row < to; row++)
        {
            if (rows[row].IsNull)
            {
                census.AddNull();
            }
            else
            {
                census.Add(rows[row].Metadata.Span, rows[row].Value.Span);
            }
        }

        return census;
    }

    /// <summary>Every value counted under <paramref name="count"/>, its objects' other keys included.</summary>
    private static long Values(PathCensus.PathCount count) =>
        count.Values + count.OtherKeys + count.Keys.Values.Sum(Values);

    private static void Same(PathCensus.PathCount expected, PathCensus.PathCount actual, string path)
    {
        Assert.True(expected.Values == actual.Values, $"{path}: {actual.Values} values, not {expected.Values}");
        Assert.Equal(expected.OtherKeys, actual.OtherKeys);
        foreach (VariantClass @class in Enum.GetValues<VariantClass>())
        {
            Assert.Equal(expected.Of(@class), actual.Of(@class));
            Assert.Equal(expected.ListsOf(@class), actual.ListsOf(@class));
        }

        Assert.Equal(expected.Objects, actual.Objects);
        foreach (PathClass @class in Enum.GetValues<PathClass>())
        {
            Assert.Equal(expected.Read(@class), actual.Read(@class));
        }

        Assert.Equal(expected.Integers, actual.Integers);
        Assert.Equal(expected.Doubles, actual.Doubles);
        Assert.Equal(expected.Times, actual.Times);
        Assert.Equal(expected.Booleans, actual.Booleans);
        Assert.Equal(expected.HasTextBounds, actual.HasTextBounds);
        Assert.Equal(expected.MinText.ToArray(), actual.MinText.ToArray());
        Assert.Equal(expected.MaxText.ToArray(), actual.MaxText.ToArray());
        Assert.Equal(expected.MaxTextCut, actual.MaxTextCut);
        Assert.Equal(expected.Keys.Keys.Order(StringComparer.Ordinal), actual.Keys.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, PathCensus.PathCount count) in expected.Keys)
        {
            Same(count, actual.Keys[name], $"{path}.{name}");
        }
    }
}
