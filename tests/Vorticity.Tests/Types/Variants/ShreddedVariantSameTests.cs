// A shredded row that holds, node by node, what the row before it holds views the value rebuilt for that row, and no
// other row does: which rows do is told a word of 64 rows at a time (ShreddedVariant.SameWord).
//
// THE ORACLE IS WRITTEN HERE, A ROW AND A NODE AT A TIME, OVER THE COLUMNS THE SHREDDING HOLDS (a read of them apart):
// the presence of each group, of its value and of its typed column, the sixteen bytes of each view and the bytes of each
// number. It shares no code with the read it judges. And every row reads back the document written, whichever value it
// views.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantSameTests
{
    private const int ViewSize = 16;

    /// <summary>The bytes a view holds in itself: a value no longer is viewed whole, and two views of equal bytes are equal.</summary>
    private const int InlineBytes = 12;

    /// <summary>Rows a scan decodes at once, in one piece: its batches would otherwise be put together from several.</summary>
    private const int Rows = 2_000;

    /// <summary>The rows of a slice of a read that rebuilds its rows a slice at a time, the least there is.</summary>
    private const int SliceRows = 1_024;

    private static readonly string[] Routes = ["/a", "/api/v1/orders", "/api/v1/orders/{id}/lines", "/health"];

    private static readonly string[] Keys = ["status", "duration", "route", "note", "ok", "mixed", "tags", "user", "other0", "other1", "other2"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Documents that repeat the one before, or differ from it by one key changed, added or taken away, under a shredding
    /// of integers, doubles, texts, booleans, an object, a list and a key of two classes, read whole and a slice of 1 024
    /// rows at a time. Every row reads back its document. A row views the value of the row before exactly when it holds a
    /// typed value as that row does, under the same view of metadata, and every node holds at it what it holds at the row
    /// before; the first row of a slice never does.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public async Task ARowViewsTheValueOfTheRowBeforeWhenEveryNodeHoldsTheSame(int seed, bool sliced)
    {
        Random random = new Random(seed);
        Doc[] docs = Documents(random);

        // A list shredded decodes no range of its rows, and its column's rows are then rebuilt whole: sliced, none is.
        string path = await WriteAsync(docs, list: !sliced);
        try
        {
            VortexOpenOptions options = sliced
                ? new VortexOpenOptions { Read = new VortexReadOptions { VariantSliceBytes = 1 } }
                : new VortexOpenOptions();
            await using VortexFile file = await VortexFile.OpenAsync(path, options, Ct);
            await using VortexFile whole = await VortexFile.OpenAsync(path, Ct);

            // Each row's value and its view, as the rows are put back together.
            List<byte[]> views = [];
            ScanCounters counters = new ScanCounters();
            await foreach (RecordBatch batch in file.ScanBuilder().Project(nameof(Doc.Attributes)).WithMetrics(counters).ExecuteAsync().WithCancellation(Ct))
            {
                using (batch)
                {
                    Assert.Empty(views);
                    VortexColumn variant = batch.Column(0);
                    StructColumn parts = variant.AsStruct();
                    BinaryColumn metadata = parts.GetField(0).AsBinary();
                    BinaryColumn value = parts.GetField(1).AsBinary();
                    CanonicalNode node = batch.Arena.GetNode(batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(0));
                    ReadOnlySpan<byte> held = batch.Arena.GetNode(node.GetFieldIndex(1)).Views.Span;
                    for (int row = 0; row < batch.RowCount; row++)
                    {
                        Variant read = Variant.FromBytes(metadata.GetSpan(row), value.GetSpan(row));
                        Assert.True(docs[row].Attributes.Equals(read), $"row {row}: {docs[row].Attributes.ToJson()} read as {read.ToJson()}");
                        views.Add(held.Slice(row * ViewSize, ViewSize).ToArray());
                    }
                }
            }

            Assert.Equal(Rows, views.Count);
            int slice = sliced ? SliceRows : Rows;
            Assert.Equal(sliced ? (Rows + slice - 1) / slice : 0, counters.VariantSlices);

            // The same rows as their shredding holds them, judged a row and a node at a time.
            int same = 0;
            int rebuilt = 0;
            int judged = 0;
            await foreach (RecordBatch apart in whole.ScanBuilder().Project(nameof(Doc.Attributes)).ApartAsync(null).WithCancellation(Ct))
            {
                using (apart)
                {
                    Assert.Equal(0, judged);
                    CanonicalArena arena = apart.Arena;
                    int group = arena.GetNode(apart.RootIndex).GetFieldIndex(0);
                    Assert.True(ApartCensus.IsGroup(arena, group), "the rows are read as their shredding holds them");
                    (int metadata, _, int typed) = Parts(arena, group);
                    Assert.True(typed >= 0);
                    ReadOnlySpan<byte> metadataViews = arena.GetNode(metadata).Views.Span;
                    for (int row = 0; row < apart.RowCount; row++, judged++)
                    {
                        bool holds = ArenaWords.IsValid(arena, group, row) && ArenaWords.IsValid(arena, typed, row);
                        bool expected = row % slice != 0
                            && holds
                            && ArenaWords.IsValid(arena, group, row - 1) && ArenaWords.IsValid(arena, typed, row - 1)
                            && Slot(metadataViews, ViewSize, row).SequenceEqual(Slot(metadataViews, ViewSize, row - 1))
                            && SameAsBefore(arena, group, row);
                        bool viewed = row > 0 && views[row].AsSpan().SequenceEqual(views[row - 1]);
                        if (expected)
                        {
                            same++;
                            Assert.True(viewed, $"row {row} holds what row {row - 1} holds and is rebuilt");
                        }
                        else if (holds && MemoryMarshal.Read<int>(views[row]) > InlineBytes)
                        {
                            // Rebuilt, its value written after the last: a view of its own.
                            rebuilt++;
                            Assert.False(viewed, $"row {row} does not hold what row {row - 1} holds and views its value");
                        }
                    }
                }
            }

            Assert.Equal(Rows, judged);
            Assert.True(same > Rows / 5, $"{same} rows view the value of the row before");
            Assert.True(rebuilt > Rows / 5, $"{rebuilt} rows are rebuilt");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
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
    /// Whether the group <paramref name="group"/> holds at <paramref name="row"/> what it holds at the row before, as its
    /// slots lie: present alike; its value present alike and, when present, of the same view; its typed column present alike
    /// and, when present, an object whose every field holds the same, a boolean of the same bit, a number of the same
    /// bytes, a text of the same view; a list never.
    /// </summary>
    private static bool SameAsBefore(CanonicalArena arena, int group, int row)
    {
        bool grouped = ArenaWords.IsValid(arena, group, row);
        if (grouped != ArenaWords.IsValid(arena, group, row - 1))
        {
            return false;
        }

        if (!grouped)
        {
            return true;
        }

        (_, int value, int typed) = Parts(arena, group);
        if (value >= 0)
        {
            bool valued = ArenaWords.IsValid(arena, value, row);
            if (valued != ArenaWords.IsValid(arena, value, row - 1))
            {
                return false;
            }

            ReadOnlySpan<byte> views = arena.GetNode(value).Views.Span;
            if (valued && !Slot(views, ViewSize, row).SequenceEqual(Slot(views, ViewSize, row - 1)))
            {
                return false;
            }
        }

        if (typed < 0)
        {
            return true;
        }

        bool held = ArenaWords.IsValid(arena, typed, row);
        if (held != ArenaWords.IsValid(arena, typed, row - 1))
        {
            return false;
        }

        if (!held)
        {
            return true;
        }

        CanonicalNode node = arena.GetNode(typed);
        switch (node.Kind)
        {
            case CanonicalKind.Struct:
                for (int f = 0; f < node.FieldCount; f++)
                {
                    if (!SameAsBefore(arena, node.GetFieldIndex(f), row))
                    {
                        return false;
                    }
                }

                return true;

            case CanonicalKind.ListView:
                return false;

            case CanonicalKind.Bool:
            {
                ReadOnlySpan<byte> bits = node.Bits.Span;
                int at = node.BitOffset + row;
                return ((bits[at >> 3] >> (at & 7)) & 1) == ((bits[(at - 1) >> 3] >> ((at - 1) & 7)) & 1);
            }

            case CanonicalKind.Primitive:
            {
                int width = node.PType.ByteWidth();
                return Slot(node.Values.Span, width, row).SequenceEqual(Slot(node.Values.Span, width, row - 1));
            }

            case CanonicalKind.VarBinView:
                return Slot(node.Views.Span, ViewSize, row).SequenceEqual(Slot(node.Views.Span, ViewSize, row - 1));

            default:
                throw new InvalidOperationException($"These documents shred into no {node.Kind}.");
        }
    }

    private static ReadOnlySpan<byte> Slot(ReadOnlySpan<byte> data, int width, int row) => data.Slice(row * width, width);

    /// <summary>
    /// The documents in one chunk of their variant column, a block of all their rows, under a shredding of every key the
    /// documents name but the others, and but the list of integers unless <paramref name="list"/>.
    /// </summary>
    private static async Task<string> WriteAsync(Doc[] rows, bool list)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-same-{Guid.NewGuid():N}.vortex");
        List<(string, VariantShredding)> keys =
        [
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("note", VariantShredding.Primitive(VariantClass.String)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
            ("mixed", VariantShredding.Primitive(VariantClass.Integer)),
            ("user", VariantShredding.Object(("id", VariantShredding.Primitive(VariantClass.Integer)), ("name", VariantShredding.Primitive(VariantClass.String)))),
        ];
        if (list)
        {
            keys.Add(("tags", VariantShredding.List(VariantClass.Integer)));
        }

        Dictionary<string, VariantShredding> shredding = new() { [nameof(Doc.Attributes)] = VariantShredding.Object([.. keys]) };

        VortexWriteOptions options = new VortexWriteOptions
        {
            BlockRows = rows.Length,
            ShreddedVariants = shredding,
            ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(nameof(Doc.Attributes), 1 << 30),
        };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options);
        await writer.WriteAsync<Doc>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    /// <summary>
    /// <see cref="Rows"/> documents: most repeat the one before, three in ten differ from it by one key changed, added or
    /// taken away, one in ten is another document, and now and then a row holds no object at all.
    /// </summary>
    private static Doc[] Documents(Random random)
    {
        SortedDictionary<string, Variant> fields = Fresh(random);
        Doc[] docs = new Doc[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int roll = random.Next(40);
            if (roll == 0)
            {
                docs[row] = new Doc(row, random.Next(3) switch
                {
                    0 => Variant.Null,
                    1 => Variant.From(random.NextInt64(-1_000, 1_000)),
                    _ => Variant.Array("a list long enough to be viewed whole", 1L),
                });
                continue;
            }

            if (roll < 4)
            {
                fields = Fresh(random);
            }
            else if (roll < 16)
            {
                string key = Keys[random.Next(Keys.Length)];
                if (fields.ContainsKey(key) && random.Next(2) == 0)
                {
                    fields.Remove(key);
                }
                else
                {
                    fields[key] = Value(random, key);
                }
            }

            docs[row] = new Doc(row, Variant.Object([.. fields.Select(static held => (held.Key, held.Value))]));
        }

        return docs;
    }

    /// <summary>An object of some of the keys, with a text every object holds so that its value is longer than a view holds in itself.</summary>
    private static SortedDictionary<string, Variant> Fresh(Random random)
    {
        SortedDictionary<string, Variant> fields = new(StringComparer.Ordinal)
        {
            ["about"] = "a text every object holds, so that its value is longer than a view holds in itself",
        };
        foreach (string key in Keys)
        {
            if (random.Next(10) < 6)
            {
                fields[key] = Value(random, key);
            }
        }

        return fields;
    }

    /// <summary>A value of <paramref name="key"/>: of the class its shredding names, most often, or of another.</summary>
    private static Variant Value(Random random, string key) => key switch
    {
        "status" => Variant.From(random.Next(4) switch { 0 => 200L, 1 => 404L, 2 => 500L, _ => (long)random.Next(600) }),
        "duration" => random.Next(8) == 0 ? Variant.From(7L) : Variant.From(random.Next(3) == 0 ? 0.5 : random.NextDouble() * 1_000),
        "route" => Routes[random.Next(Routes.Length)],
        "note" => $"a note of its own, longer than a short string's sixty-three bytes whatever follows it: {random.Next(6)}",
        "ok" => random.Next(9) == 0 ? Variant.From("yes") : Variant.From(random.Next(2) == 0),
        "mixed" => random.Next(3) == 0 ? Variant.From("three") : Variant.From((long)random.Next(5)),
        "tags" => random.Next(4) == 0
            ? Variant.Array("x", 1L)
            : Variant.Array([.. Enumerable.Range(0, random.Next(4)).Select(i => Variant.From((long)random.Next(20)))]),
        "user" => random.Next(5) == 0
            ? Variant.From("anonymous")
            : Variant.Object(("id", (long)random.Next(8)), ("name", $"user-{random.Next(4)}")),
        _ => Variant.From((long)random.Next(3)),
    };

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
