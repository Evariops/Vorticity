// A writer's census of a block counted from what the block's shredding took of its rows (VariantShredder.Shredding.Count),
// in place of reading every row again.
//
// THE ORACLE IS THE CENSUS OF THE ROWS READ ONE AFTER THE OTHER (PathCensus.Add): every path, every class, every bound,
// and the rows each class of each path lies in.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Types.Variants;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantWriteTests
{
    /// <summary>The rows two values of a path share a span within, small enough that the rows of a block make several.</summary>
    private const long CensusGap = 16;

    /// <summary>
    /// Documents of every shape, some repeated row after row, floats among the doubles, under shreddings drawn at random,
    /// counted block by block of 700 rows: the census of what each block's read took is the census of its rows, a path, a
    /// class, a bound and a span of rows at a time.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task TheCensusOfWhatABlocksReadTookIsTheCensusOfItsRows(int seed)
    {
        Random random = new Random(seed);
        List<Doc> rows = [];
        while (rows.Count < 3_000)
        {
            Variant attributes = random.Next(4) == 0 ? Measured(random) : Document(random);
            Variant? extra = random.Next(10) == 0 ? null : Document(random);
            // A run of rows of one document, some of them null in the second column: a row that repeats one a null row away.
            for (int run = random.Next(5) == 0 ? 1 + random.Next(6) : 1; run > 0; run--)
            {
                rows.Add(new Doc(rows.Count, attributes, random.Next(4) == 0 ? null : extra));
            }
        }

        VariantShredding measured = VariantShredding.Object(
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
            ("tags", VariantShredding.List(VariantClass.Integer)),
            ("user", VariantShredding.Object(("id", VariantShredding.Primitive(VariantClass.Integer)))));
        await AssertSameCensusAsync([.. rows], [Shredding(random), Shredding(random), measured]);
    }

    /// <summary>An object whose key twice over is taken once by a shredding and counted twice by a census: counted from its rows.</summary>
    [Fact]
    public void ABlockWhoseObjectHoldsAShreddedKeyTwiceIsCountedFromItsRows()
    {
        // The key "a" twice, by hand: no writer of ours makes one.
        byte[] metadata = [0x01, 0x01, 0x00, 0x01, (byte)'a'];
        byte[] value = [0x02, 0x02, 0x00, 0x00, 0x00, 0x02, 0x04, 0x0C, 0x05, 0x0C, 0x07];
        VariantShredding plan = VariantShredding.Object(("a", VariantShredding.Primitive(VariantClass.Integer)));
        CanonicalArena arena = new CanonicalArena();
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        int column = VariantColumn(arena, types, metadata, value, rows: 3);
        VariantShredder.Shredding alone = new VariantShredder.Shredding(plan);
        VariantShredder.Shredding.Rows taken = VariantShredder.Shredding.Prepare(arena, column);
        alone.Begin(new CanonicalArena(), null, counted: true);
        alone.TakeMore(in taken, 0);
        PathCensus census = new PathCensus(16, CensusGap);
        Assert.False(alone.Count(census, in taken, 3 * value.Length));
        Assert.Equal(0, census.Rows);
    }

    /// <summary>A variant column of <paramref name="rows"/> rows, each of <paramref name="metadata"/> and <paramref name="value"/>.</summary>
    private static int VariantColumn(CanonicalArena arena, Vorticity.Types.DTypeArena types, byte[] metadata, byte[] value, int rows)
    {
        int Binary(byte[] bytes)
        {
            Vorticity.Buffers.VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> into);
            Vorticity.Buffers.VortexBuffer data = arena.Allocate(Math.Max(bytes.Length, 1), 1, out Span<byte> held);
            bytes.CopyTo(held);
            for (int row = 0; row < rows; row++)
            {
                Vorticity.Arrays.Decoders.Canonical.CanonicalSupport.WriteView(into.Slice(row * 16, 16), bytes, bytes.Length, 0, 0);
            }

            return arena.AddVarBinView(types.Binary(Vorticity.Types.Nullability.NonNullable), rows, Validity.NonNullable, views, [data]);
        }

        return arena.AddStruct(types.Variant(Vorticity.Types.Nullability.NonNullable), rows, Validity.NonNullable, [Binary(metadata), Binary(value)]);
    }

    /// <summary>An object of measures: a duration a float or a double, flags, lists of integers that fit or not, a user.</summary>
    private static Variant Measured(Random random)
    {
        List<(string, Variant)> fields = [];
        if (random.Next(10) < 8)
        {
            fields.Add(("duration", random.Next(3) switch
            {
                0 => Variant.From((float)(random.NextDouble() * 100)),
                1 => Variant.From(double.NaN),
                _ => Variant.From(random.NextDouble() * 100),
            }));
        }

        if (random.Next(10) < 6)
        {
            fields.Add(("ok", random.Next(6) == 0 ? Variant.Null : Variant.From(random.Next(2) == 0)));
        }

        if (random.Next(10) < 6)
        {
            fields.Add(("tags", random.Next(3) switch
            {
                0 => Variant.Array([]),
                1 => Variant.Array(1L, "two"),
                _ => Variant.Array([.. Enumerable.Range(0, 1 + random.Next(3)).Select(i => Variant.From((long)random.Next(100)))]),
            }));
        }

        if (random.Next(10) < 6)
        {
            Variant deep = Variant.Object(("er", Variant.Object(("still", $"t{random.Next(5)}"))));
            fields.Add(("user", random.Next(4) == 0
                ? Variant.Array(Variant.Object(("id", 1L)))
                : Variant.Object(("id", random.NextInt64(1, 1_000)), ("deep", deep))));
        }

        if (random.Next(10) < 3)
        {
            fields.Add(($"other{random.Next(6)}", Variant.From(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(1_000)))));
        }

        return Variant.Object(fields.ToArray());
    }

    private static async Task AssertSameCensusAsync(Doc[] rows, VariantShredding[] plans)
    {
        string path = await WriteAsync(rows, null, 1, blockRows: rows.Length);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await foreach (RecordBatch batch in file.Scan<Doc>().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    foreach (string name in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
                    {
                        int field = file.DType.IndexOfField(name);
                        foreach (VariantShredding plan in plans)
                        {
                            PathCensus byRows = new PathCensus(16, CensusGap);
                            PathCensus byTake = new PathCensus(16, CensusGap);
                            VariantShredder.Shredding alone = new VariantShredder.Shredding(plan);
                            CanonicalArena store = new CanonicalArena();
                            CanonicalArena part = new CanonicalArena();
                            for (int start = 0; start < batch.RowCount; start += 700)
                            {
                                int count = Math.Min(700, batch.RowCount - start);
                                int slice = Vorticity.Layouts.CanonicalSlice.SliceAcross(batch.Arena, part, batch.RootIndex, start, count);
                                int column = part.GetNode(slice).GetFieldIndex(field);
                                PathCensus.Counting counting = PathCensus.Prepare(part, column, count);
                                byRows.Add(in counting);
                                VariantShredder.Shredding.Rows taken = VariantShredder.Shredding.Prepare(part, column);
                                store.Reset();
                                alone.Begin(store, null, counted: true);
                                alone.TakeMore(in taken, 0);
                                Assert.True(alone.Count(byTake, in taken, PathCensus.ValueBytesOf(in counting)), $"{name}: a block of rows a writer of ours wrote is counted from its read");
                                part.Reset();
                            }

                            Assert.Equal(byRows.Rows, byTake.Rows);
                            Assert.Equal(byRows.ValueBytes, byTake.ValueBytes);
                            Assert.Equal(byRows.Partial, byTake.Partial);
                            SameCounts(byRows.Root, byTake.Root, name);
                        }
                    }
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Whether two counts of one path, and of every key below it, hold the same.</summary>
    private static void SameCounts(PathCensus.PathCount expected, PathCensus.PathCount actual, string path)
    {
        Assert.True(expected.Values == actual.Values, $"{path}: {actual.Values} values against {expected.Values}");
        Assert.True(expected.OtherKeys == actual.OtherKeys, $"{path}: other keys");
        Assert.True(expected.Objects == actual.Objects, $"{path}: {actual.Objects} objects against {expected.Objects}");
        foreach (VariantClass @class in Enum.GetValues<VariantClass>())
        {
            Assert.True(expected.Of(@class) == actual.Of(@class), $"{path}: {actual.Of(@class)} of {@class} against {expected.Of(@class)}");
            Assert.True(expected.ListsOf(@class) == actual.ListsOf(@class), $"{path}: {actual.ListsOf(@class)} lists of {@class} against {expected.ListsOf(@class)}");
        }

        foreach (PathClass @class in Enum.GetValues<PathClass>())
        {
            Assert.True(expected.Read(@class) == actual.Read(@class), $"{path}: {actual.Read(@class)} read as {@class} against {expected.Read(@class)}");
            Assert.True(
                expected.SpansOf(@class).SequenceEqual(actual.SpansOf(@class)),
                $"{path}: the rows of {@class} in spans [{string.Join(' ', actual.SpansOf(@class).ToArray())}] against [{string.Join(' ', expected.SpansOf(@class).ToArray())}]");
        }

        Assert.True(expected.Integers == actual.Integers, $"{path}: integers {actual.Integers} against {expected.Integers}");
        Assert.True(expected.Doubles.Equals(actual.Doubles), $"{path}: doubles {actual.Doubles} against {expected.Doubles}");
        Assert.True(expected.Times == actual.Times, $"{path}: times");
        Assert.True(expected.Booleans == actual.Booleans, $"{path}: booleans {actual.Booleans} against {expected.Booleans}");
        Assert.True(expected.HasTextBounds == actual.HasTextBounds, $"{path}: text bounds");
        Assert.True(expected.MinText.SequenceEqual(actual.MinText), $"{path}: least text");
        Assert.True(expected.MaxText.SequenceEqual(actual.MaxText), $"{path}: greatest text");
        Assert.True(expected.MaxTextCut == actual.MaxTextCut, $"{path}: greatest text cut");

        string[] keys = [.. expected.Keys.Keys.Order(StringComparer.Ordinal)];
        Assert.True(keys.SequenceEqual(actual.Keys.Keys.Order(StringComparer.Ordinal)), $"{path}: keys [{string.Join(' ', actual.Keys.Keys.Order(StringComparer.Ordinal))}] against [{string.Join(' ', keys)}]");
        foreach (string key in keys)
        {
            SameCounts(expected.Keys[key], actual.Keys[key], $"{path}.{key}");
        }
    }
}
