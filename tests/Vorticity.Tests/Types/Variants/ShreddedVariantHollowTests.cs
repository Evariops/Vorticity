// The rows of a shredded variant sized without being put back together (ShreddedVariant.HollowSlice): each row's view is
// the view the rebuild writes of it (ShreddedVariant.WriteSlice), of a value never written.
//
// THE ORACLE IS THE REBUILD ITSELF, over the same columns, a slice at a time: the size of every row, where a row's value
// would lie when it is too long to lie in its view, and which rows name the value of the row before. The two share the
// columns' binding and the tell of the rows that repeat (ShreddedVariant.SameWord), which ShreddedVariantSameTests judges
// from the columns; a row's size is computed here by a path of its own, a key at a time over the rows that hold it.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantHollowTests
{
    private const int ViewSize = 16;

    /// <summary>The bytes a view holds in itself: a longer value lies in a buffer, at an offset its view names.</summary>
    private const int InlineBytes = 12;

    /// <summary>Rows a scan decodes at once, in one piece.</summary>
    private const int Rows = 2_000;

    private static readonly string[] Routes = ["/a", "/api/v1/orders", "/api/v1/orders/{id}/lines", "/health"];

    private static readonly string[] Typed = ["about", "status", "duration", "route", "note", "ok", "mixed"];

    private static readonly string[] Left = ["tags", "user", "other0", "other1", "other2"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Documents of a few keys, most typed in one class, one in two, some left in the row's value with an object and a list
    /// among them, rows that repeat the one before, rows of no key, rows of one small key, rows that are no object and
    /// null rows: sized whole and in slices of uneven rows, every row's view is the one the rebuild writes, and a row
    /// names the value of the row before exactly when the rebuild has it view that value. A null row holds neither a
    /// typed object nor a value, and is viewed as a row that takes its value as it is.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ARowSizedIsViewedAsTheRowRebuilt(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = Documents(random);
        VariantShredding shredding = VariantShredding.Object(
            ("about", VariantShredding.Primitive(VariantClass.String)),
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("note", VariantShredding.Primitive(VariantClass.String)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
            ("mixed", VariantShredding.Primitive(VariantClass.Integer)));
        Tally tally = await SameViewsAsync(rows, shredding, [[Rows], [1, 63, 64, 65, 700, Rows - 893], [1_024, Rows - 1_024]]);

        // Every kind of row was met, a slice and a column at a time.
        Assert.True(tally.Repeats > Rows / 5, $"{tally.Repeats} rows name the value of the row before");
        Assert.True(tally.Rebuilt > Rows / 5, $"{tally.Rebuilt} rows are sized");
        Assert.True(tally.Inline > 0, "no row is short enough to lie in its view");
        Assert.True(tally.AsItIs > 0, "no row takes its value as it is");
        Assert.True(tally.WideOffsets > 0, "no row's values take more than 255 bytes");
    }

    /// <summary>
    /// Documents of some of three hundred keys, each typed, a few of all of them and a few of 260: a row of more than 255
    /// keys counts them in four bytes, and a row whose metadata names more than 256 keys writes each id in two, which the
    /// sizes follow.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public async Task ARowOfHundredsOfKeysIsSizedAsItIsRebuilt(int seed)
    {
        Random random = new Random(seed);
        string[] keys = [.. Enumerable.Range(0, 300).Select(static key => $"k{key:D3}")];
        Doc[] rows = new Doc[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int held = random.Next(20) switch { 0 => 300, 1 => 260, 2 => 256, 3 => 257, _ => random.Next(1, 30) };
            List<(string, Variant)> fields = [.. keys.OrderBy(_ => random.Next()).Take(held).Select(key => (key, Variant.From((long)random.Next(100_000))))];
            if (random.Next(4) == 0)
            {
                // Keys left in the row's value, whose ids count among the row's.
                fields.Add(("z-left", Variant.From("a value left in the row's value")));
                fields.Add(("a-left", Variant.From((long)random.Next(9))));
            }

            rows[row] = new Doc(row, Variant.Object([.. fields]), null);
        }

        Tally tally = await SameViewsAsync(
            rows, VariantShredding.Object([.. keys.Select(static key => (key, VariantShredding.Primitive(VariantClass.Integer)))]), [[Rows], [777, Rows - 777]]);
        Assert.True(tally.WideCounts > 0, "no row holds more than 255 keys");
        Assert.True(tally.WideIds > 0, "no row writes its ids in two bytes");
    }

    /// <summary>What the rows judged were, so that a test tells it met each kind.</summary>
    private sealed class Tally
    {
        internal int Repeats;
        internal int Rebuilt;
        internal int Inline;
        internal int AsItIs;
        internal int WideOffsets;
        internal int WideCounts;
        internal int WideIds;
    }

    /// <summary>
    /// Writes <paramref name="rows"/> with both variant columns under <paramref name="shredding"/>, reads each column as
    /// its shredding holds it, and for each way of <paramref name="cuts"/> to cut its rows in slices, sizes them a slice at
    /// a time and rebuilds them a slice at a time: the views are the same.
    /// </summary>
    private static async Task<Tally> SameViewsAsync(Doc[] rows, VariantShredding shredding, int[][] cuts)
    {
        Tally tally = new Tally();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-hollow-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = rows.Length,
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = shredding, [nameof(Doc.Extra)] = shredding },
                ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(nameof(Doc.Attributes), 1 << 30).Add(nameof(Doc.Extra), 1 << 30),
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options))
            {
                await writer.WriteAsync<Doc>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            foreach (string name in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
            {
                int judged = 0;
                await foreach (RecordBatch apart in file.ScanBuilder().Project(name).ApartAsync(null).WithCancellation(Ct))
                {
                    using (apart)
                    {
                        CanonicalArena arena = apart.Arena;
                        int group = arena.GetNode(apart.RootIndex).GetFieldIndex(0);
                        Assert.True(ApartCensus.IsGroup(arena, group), $"{name}: the rows are read as their shredding holds them");
                        foreach (int[] cut in cuts)
                        {
                            Judge(arena, group, apart.RowCount, cut, name, tally);
                        }

                        judged += apart.RowCount;
                    }
                }

                Assert.Equal(rows.Length, judged);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        return tally;
    }

    /// <summary>The rows of <paramref name="group"/> rebuilt, then sized, in slices of <paramref name="cut"/> rows: view by view the same.</summary>
    private static void Judge(CanonicalArena arena, int group, int rows, int[] cut, string name, Tally tally)
    {
        Assert.Equal(rows, cut.Sum());
        ShreddedVariant plan = ShreddedVariant.Compile(VortexTypes.FromDType(arena.GetNode(group).DType), new DTypeArena());
        Assert.True(plan.Sizes, $"{name}: a shredding of keys that hold no key of their own is sized");

        // The slices, each a node of its own, as a decode a slice at a time hands them.
        int[] slices = new int[cut.Length];
        for (int s = 0, at = 0; s < cut.Length; at += cut[s], s++)
        {
            slices[s] = CanonicalSlice.SliceAcross(arena, arena, group, at, cut[s]);
        }

        byte[] rebuilt = new byte[rows * ViewSize];
        plan.BeginSlices();
        try
        {
            for (int s = 0, at = 0; s < cut.Length; at += cut[s], s++)
            {
                plan.WriteSlice(arena, slices[s], rebuilt.AsSpan(at * ViewSize, cut[s] * ViewSize));
            }
        }
        finally
        {
            plan.EndSlices();
        }

        byte[] sized = new byte[rows * ViewSize];
        plan.BeginHollow();
        try
        {
            for (int s = 0, at = 0; s < cut.Length; at += cut[s], s++)
            {
                Assert.True(plan.HollowSlice(arena, slices[s], sized.AsSpan(at * ViewSize, cut[s] * ViewSize)), $"{name}: slice {s} is sized");
            }
        }
        finally
        {
            plan.EndHollow();
        }

        (int metadata, int value, int typed) = Parts(arena, group);
        Assert.True(typed >= 0);
        ReadOnlySpan<byte> metadataViews = arena.GetNode(metadata).Views.Span;
        VortexBuffer[] metadataBuffers = [.. arena.GetNode(metadata).DataBuffers];
        for (int row = 0; row < rows; row++)
        {
            ReadOnlySpan<byte> expected = rebuilt.AsSpan(row * ViewSize, ViewSize);
            ReadOnlySpan<byte> actual = sized.AsSpan(row * ViewSize, ViewSize);
            int size = MemoryMarshal.Read<int>(expected);
            Assert.True(size == MemoryMarshal.Read<int>(actual), $"{name} row {row}: sized {MemoryMarshal.Read<int>(actual)} bytes, rebuilt in {size}");
            if (!ArenaWords.IsValid(arena, group, row) || !ArenaWords.IsValid(arena, typed, row))
            {
                // No typed object, a null row among them: the value column's view, the variant null or no view, as the
                // rebuild keeps it.
                Assert.True(expected.SequenceEqual(actual), $"{name} row {row}: the view of a value taken as it is");
                tally.AsItIs++;
                continue;
            }

            // What the header of the row's object was sized with.
            int count = Count(arena, typed, value, row);
            tally.WideCounts += count > byte.MaxValue ? 1 : 0;
            tally.WideIds += VariantDictionary.Read(BlockStatsPass.Value(metadataBuffers, metadataViews, row)).Count > 256 && count > 0 ? 1 : 0;
            tally.WideOffsets += size > 300 ? 1 : 0;
            if (size <= InlineBytes)
            {
                // A value its view holds names no place: it is rebuilt there, as the rebuild holds it.
                Assert.True(expected.SequenceEqual(actual), $"{name} row {row}: the view of a value it holds, {Convert.ToHexString(actual)} against {Convert.ToHexString(expected)}");
                tally.Inline++;
                continue;
            }

            Assert.True(
                expected[12..].SequenceEqual(actual[12..]),
                $"{name} row {row}: its value named at offset {MemoryMarshal.Read<int>(actual[12..])} and rebuilt at offset {MemoryMarshal.Read<int>(expected[12..])}");
            Assert.True(MemoryMarshal.Read<uint>(actual[8..]) == uint.MaxValue, $"{name} row {row}: a value not written lies in no buffer");
            Assert.True(MemoryMarshal.Read<int>(actual[4..]) == 0, $"{name} row {row}: a value not written has no prefix");
            bool repeats = row > 0 && expected.SequenceEqual(rebuilt.AsSpan((row - 1) * ViewSize, ViewSize));
            bool names = row > 0 && actual.SequenceEqual(sized.AsSpan((row - 1) * ViewSize, ViewSize));
            Assert.True(repeats == names, $"{name} row {row}: {(names ? "names" : "does not name")} the value of the row before, which the rebuild {(repeats ? "views" : "does not view")}");
            tally.Repeats += repeats ? 1 : 0;
            tally.Rebuilt += repeats ? 0 : 1;
        }
    }

    /// <summary>The keys row <paramref name="row"/> holds: those its typed object holds, and those of the object left in its value.</summary>
    private static int Count(CanonicalArena arena, int typed, int value, int row)
    {
        CanonicalNode fields = arena.GetNode(typed);
        int count = 0;
        for (int f = 0; f < fields.FieldCount; f++)
        {
            int pair = fields.GetFieldIndex(f);
            if (!ArenaWords.IsValid(arena, pair, row))
            {
                continue;
            }

            CanonicalNode columns = arena.GetNode(pair);
            bool held = false;
            for (int c = 0; c < columns.FieldCount; c++)
            {
                held |= ArenaWords.IsValid(arena, columns.GetFieldIndex(c), row);
            }

            count += held ? 1 : 0;
        }

        if (value >= 0 && ArenaWords.IsValid(arena, value, row))
        {
            CanonicalNode left = arena.GetNode(value);
            count += VariantNested.Read(BlockStatsPass.Value(left.DataBuffers, left.Views.Span, row)).Count;
        }

        return count;
    }

    /// <summary>The fields of a group of the standard: its metadata, its value and its typed value, -1 for one it lacks.</summary>
    private static (int Metadata, int Value, int Typed) Parts(CanonicalArena arena, int group)
    {
        CanonicalNode node = arena.GetNode(group);
        int metadata = -1;
        int value = -1;
        int typed = -1;
        for (int f = 0; f < node.FieldCount; f++)
        {
            ReadOnlySpan<byte> name = node.DType.GetFieldNameUtf8(f);
            if (name.SequenceEqual("metadata"u8))
            {
                metadata = node.GetFieldIndex(f);
            }
            else if (name.SequenceEqual("value"u8))
            {
                value = node.GetFieldIndex(f);
            }
            else if (name.SequenceEqual("typed_value"u8))
            {
                typed = node.GetFieldIndex(f);
            }
        }

        return (metadata, value, typed);
    }

    /// <summary>
    /// <see cref="Rows"/> documents: most repeat the one before, three in ten differ from it by one key changed, added or
    /// taken away, one in ten is another document, and now and then a row holds no key, one small key, or no object at
    /// all. The second column holds the same kind of documents, a row in ten null.
    /// </summary>
    private static Doc[] Documents(Random random)
    {
        SortedDictionary<string, Variant> fields = Fresh(random);
        SortedDictionary<string, Variant> extra = Fresh(random);
        Doc[] docs = new Doc[Rows];
        for (int row = 0; row < Rows; row++)
        {
            docs[row] = new Doc(row, Next(random, ref fields), random.Next(10) == 0 ? null : Next(random, ref extra));
        }

        return docs;
    }

    private static Variant Next(Random random, ref SortedDictionary<string, Variant> fields)
    {
        int roll = random.Next(40);
        switch (roll)
        {
            case 0:
                return random.Next(3) switch
                {
                    0 => Variant.Null,
                    1 => Variant.From(random.NextInt64(-1_000, 1_000)),
                    _ => Variant.Array("a list long enough to be viewed whole", 1L),
                };
            case 1:
                return Variant.Object(ReadOnlySpan<(string, Variant)>.Empty);
            case 2:
                return Variant.Object(("ok", Variant.From(random.Next(2) == 0)));
            case 3:
                return Variant.Object(("status", Variant.From((long)random.Next(600))), ("other0", Variant.From(1L)));
        }

        if (roll < 8)
        {
            fields = Fresh(random);
        }
        else if (roll < 20)
        {
            string key = random.Next(2) == 0 ? Typed[random.Next(Typed.Length)] : Left[random.Next(Left.Length)];
            if (fields.ContainsKey(key) && random.Next(2) == 0)
            {
                fields.Remove(key);
            }
            else
            {
                fields[key] = Value(random, key);
            }
        }

        return Variant.Object([.. fields.Select(static held => (held.Key, held.Value))]);
    }

    /// <summary>An object of some of the keys: half of them hold none of the keys left in the row's value.</summary>
    private static SortedDictionary<string, Variant> Fresh(Random random)
    {
        SortedDictionary<string, Variant> fields = new(StringComparer.Ordinal);
        foreach (string key in Typed)
        {
            if (random.Next(10) < 6)
            {
                fields[key] = Value(random, key);
            }
        }

        if (random.Next(2) == 0)
        {
            foreach (string key in Left)
            {
                if (random.Next(10) < 4)
                {
                    fields[key] = Value(random, key);
                }
            }
        }

        return fields;
    }

    /// <summary>A value of <paramref name="key"/>: of the class its shredding names, most often, or of another.</summary>
    private static Variant Value(Random random, string key) => key switch
    {
        "about" => $"a text of more than 255 bytes, so that the object that holds it writes each of its offsets in two bytes where an object that does not writes them in one, {new string('.', 180)} {random.Next(3)}",
        "status" => Variant.From(random.Next(4) switch { 0 => 200L, 1 => 404L, 2 => 500L, _ => (long)random.Next(600) }),
        "duration" => random.Next(8) == 0 ? Variant.From(7L) : Variant.From(random.Next(3) == 0 ? 0.5 : random.NextDouble() * 1_000),
        "route" => Routes[random.Next(Routes.Length)],
        "note" => new string('n', random.Next(60, 68)),
        "ok" => random.Next(9) == 0 ? Variant.From("yes") : Variant.From(random.Next(2) == 0),
        "mixed" => random.Next(3) == 0 ? Variant.From("three") : Variant.From((long)random.Next(5)),
        "tags" => Variant.Array([.. Enumerable.Range(0, random.Next(4)).Select(i => Variant.From((long)random.Next(20)))]),
        "user" => random.Next(5) == 0
            ? Variant.From("anonymous")
            : Variant.Object(("id", (long)random.Next(8)), ("name", $"user-{random.Next(4)}")),
        _ => Variant.From((long)random.Next(70_000)),
    };

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes, Variant? Extra);
}
