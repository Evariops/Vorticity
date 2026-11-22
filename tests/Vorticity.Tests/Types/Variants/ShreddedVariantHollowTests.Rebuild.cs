// The rows a shredding took as another shredding holds them (VariantShredder.Shredding.TakeGroup), put back together
// from what it took (Shredding.Rebuild): each row's object is, to the byte, the object the rebuild of the shredding it
// was taken from writes (ShreddedVariant), where the row's sized view says it lies.
//
// THE ORACLE IS THE REBUILD OF THE SHREDDING READ, which shares nothing with what a shredding of another plan took of
// the rows: the keys it types come back from typed columns, those it keeps whole from the values kept, and the keys kept
// apart from an object of their own, merged in the order of their names.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantHollowTests
{
    /// <summary>
    /// Documents written under a shredding of seven keys and taken as it holds them into a shredding of the same keys, and
    /// into one that types two keys more, which the first left in the rows' values: integers read there in one, two and
    /// four bytes, and under the other key values of another class than the one it names, kept whole. Every object comes
    /// back to the byte, whole, a range at a time, and from a shredding that took ranges of the first in another order,
    /// which no rank finds its rows in.
    /// </summary>
    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task ARowPutBackFromWhatAShreddingTookIsTheRowRebuilt(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = Steady(random);
        (string, VariantShredding)[] read =
        [
            ("about", VariantShredding.Primitive(VariantClass.String)),
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("note", VariantShredding.Primitive(VariantClass.String)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
        ];
        VariantShredding same = VariantShredding.Object(read);
        VariantShredding wider = VariantShredding.Object(
        [
            .. read,
            ("other0", VariantShredding.Primitive(VariantClass.Integer)),
            ("other1", VariantShredding.Primitive(VariantClass.String)),
            ("never", VariantShredding.Primitive(VariantClass.Boolean)),
        ]);
        VariantShredding listed = VariantShredding.Object([.. read, ("tags", VariantShredding.List(VariantClass.Integer))]);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-rebuild-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = rows.Length,
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = same, [nameof(Doc.Extra)] = same },
                ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(nameof(Doc.Attributes), 1 << 30).Add(nameof(Doc.Extra), 1 << 30),
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options))
            {
                await writer.WriteAsync<Doc>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            int narrow = 0;
            foreach (string name in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
            {
                int batches = 0;
                await foreach (RecordBatch apart in file.ScanBuilder().Project(name).ApartAsync(null).WithCancellation(Ct))
                {
                    using (apart)
                    {
                        CanonicalArena arena = apart.Arena;
                        int group = arena.GetNode(apart.RootIndex).GetFieldIndex(0);
                        Assert.True(ApartCensus.IsGroup(arena, group), $"{name}: the rows are read as their shredding holds them");
                        narrow += PutBack(arena, group, apart.RowCount, same, name, random);
                        narrow += PutBack(arena, group, apart.RowCount, wider, name, random);

                        // A list under a key takes its elements apart: the rows are not put back from it.
                        VariantShredder.Shredding lists = new VariantShredder.Shredding(listed, ranged: true);
                        lists.Begin(new CanonicalArena(), null);
                        Assert.True(lists.TakeGroup(arena, group, 0));
                        Assert.False(lists.Exact, $"{name}: a shredding that took lists apart keeps every byte of its rows");
                        batches++;
                    }
                }

                Assert.Equal(1, batches);
            }

            Assert.True(narrow > 100, $"{narrow} integers were typed from fewer than eight bytes");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The rows of <paramref name="group"/> taken into a shredding of <paramref name="plan"/> and put back from it: whole,
    /// a few ranges of them, and from a shredding that took two ranges of it, the later rows first. Returns the integers
    /// the rows' values hold under <c>other0</c> in fewer than eight bytes when the plan types that key.
    /// </summary>
    private static int PutBack(CanonicalArena arena, int group, int rows, VariantShredding plan, string name, Random random)
    {
        VariantShredder.Shredding taken = new VariantShredder.Shredding(plan, ranged: true);
        taken.Begin(new CanonicalArena(), null);
        Assert.True(taken.TakeGroup(arena, group, 0), $"{name}: a shredding of {plan.Keys.Count} keys takes the rows as they lie");
        Assert.True(taken.Exact, $"{name}: a shredding of {plan.Keys.Count} keys keeps every byte of the rows it took");
        Assert.True(taken.Index());

        // The rows rebuilt, the oracle, and the rows sized.
        DTypeArena types = new DTypeArena();
        ShreddedVariant read = ShreddedVariant.Compile(VortexTypes.FromDType(arena.GetNode(group).DType), types);
        int rebuilt = arena.GetNode(read.Assemble(arena, types.Variant(Nullability.Nullable), group)).GetFieldIndex(1);
        VortexBuffer[] rebuiltBuffers = [.. arena.GetNode(rebuilt).DataBuffers];
        byte[] rebuiltViews = arena.GetNode(rebuilt).Views.Span.ToArray();
        byte[] sized = new byte[rows * ViewSize];
        read.BeginHollow();
        try
        {
            Assert.True(read.HollowSlice(arena, group, sized));
        }
        finally
        {
            read.EndHollow();
        }

        (int metadata, int value, int typed) = Parts(arena, group);
        byte[] metadataViews = arena.GetNode(metadata).Views.Span.ToArray();
        VortexBuffer[] metadataBuffers = [.. arena.GetNode(metadata).DataBuffers];
        // The objects too long to lie in their views: those put back. One short enough is rebuilt in its view as it is sized.
        bool[] objects = new bool[rows];
        int end = 0;
        int inline = 0;
        for (int row = 0; row < rows; row++)
        {
            bool holds = ArenaWords.IsValid(arena, group, row) && ArenaWords.IsValid(arena, typed, row);
            int size = MemoryMarshal.Read<int>(sized.AsSpan(row * ViewSize));
            objects[row] = holds && size > InlineBytes;
            if (objects[row])
            {
                end = Math.Max(end, MemoryMarshal.Read<int>(sized.AsSpan((row * ViewSize) + 12)) + size);
            }
            else if (holds)
            {
                Assert.True(sized.AsSpan(row * ViewSize, ViewSize).SequenceEqual(rebuiltViews.AsSpan(row * ViewSize, ViewSize)), $"{name} row {row}: the view of a value it holds");
                inline++;
            }
        }

        Assert.True(inline > 0, $"{name}: no object is short enough to lie in its view");

        // Whole.
        byte[] whole = new byte[end];
        taken.Rebuild(0, rows, sized, metadataViews, metadataBuffers, whole, 0);
        for (int row = 0; row < rows; row++)
        {
            Same(row, row);
        }

        // A range at a time, each written where the views say, from the first byte its rows name.
        for (int range = 0; range < 12; range++)
        {
            int first = random.Next(rows);
            int count = random.Next(1, Math.Min(rows - first, 300) + 1);
            int origin = int.MaxValue;
            for (int row = first; row < first + count; row++)
            {
                origin = objects[row] ? Math.Min(origin, MemoryMarshal.Read<int>(sized.AsSpan((row * ViewSize) + 12))) : origin;
            }

            Array.Clear(whole);
            taken.Rebuild(
                first, count, sized.AsSpan(first * ViewSize, count * ViewSize), metadataViews.AsSpan(first * ViewSize, count * ViewSize), metadataBuffers,
                whole.AsSpan(origin == int.MaxValue ? 0 : origin), origin == int.MaxValue ? 0 : origin);
            for (int row = first; row < first + count; row++)
            {
                Same(row, row);
            }
        }

        // A block that took two ranges of the rows, the later first, as a merge hands its writer runs of a batch: its rows
        // lie one after the other, each sized as it was, and no rank tells where a range of them starts.
        int late = random.Next(rows / 2, rows - 40);
        int lateRows = random.Next(20, rows - late);
        int early = random.Next(0, rows / 2 - 40);
        int earlyRows = random.Next(20, 400);
        VariantShredder.Shredding block = new VariantShredder.Shredding(plan);
        block.Begin(new CanonicalArena(), null, counted: true);
        block.AppendRange(taken, late, lateRows, 0);
        block.AppendRange(taken, early, earlyRows, lateRows);
        int blockRows = lateRows + earlyRows;
        int[] source = [.. Enumerable.Range(late, lateRows), .. Enumerable.Range(early, earlyRows)];
        byte[] blockViews = new byte[blockRows * ViewSize];
        byte[] blockMetadata = new byte[blockRows * ViewSize];
        int used = 0;
        for (int row = 0; row < blockRows; row++)
        {
            metadataViews.AsSpan(source[row] * ViewSize, ViewSize).CopyTo(blockMetadata.AsSpan(row * ViewSize));
            if (!objects[source[row]])
            {
                // A row that is no object, or one its view holds: its view as it is.
                sized.AsSpan(source[row] * ViewSize, ViewSize).CopyTo(blockViews.AsSpan(row * ViewSize));
                continue;
            }

            int size = MemoryMarshal.Read<int>(sized.AsSpan(source[row] * ViewSize));
            MemoryMarshal.Write(blockViews.AsSpan(row * ViewSize), size);
            MemoryMarshal.Write(blockViews.AsSpan((row * ViewSize) + 8), uint.MaxValue);
            MemoryMarshal.Write(blockViews.AsSpan((row * ViewSize) + 12), used);
            used += size;
        }

        whole = new byte[used];
        block.Rebuild(0, blockRows, blockViews, blockMetadata, metadataBuffers, whole, 0);
        byte[] blockSized = sized;
        sized = blockViews;
        for (int row = 0; row < blockRows; row++)
        {
            Same(row, source[row]);
        }

        // And a range of the block, its rows searched for.
        int from = random.Next(1, blockRows - 10);
        int some = random.Next(1, blockRows - from);
        Array.Clear(whole);
        block.Rebuild(from, some, blockViews.AsSpan(from * ViewSize, some * ViewSize), blockMetadata.AsSpan(from * ViewSize, some * ViewSize), metadataBuffers, whole, 0);
        for (int row = from; row < from + some; row++)
        {
            Same(row, source[row]);
        }

        sized = blockSized;

        // What the wider shredding typed of the rows' values in fewer than eight bytes.
        int narrow = 0;
        if (plan.IndexOf("other0"u8) >= 0 && value >= 0)
        {
            CanonicalNode left = arena.GetNode(value);
            VortexBuffer[] leftBuffers = [.. left.DataBuffers];
            ReadOnlySpan<byte> leftViews = left.Views.Span;
            for (int row = 0; row < rows; row++)
            {
                if (!objects[row] || !ArenaWords.IsValid(arena, value, row))
                {
                    continue;
                }

                VariantDictionary dictionary = VariantDictionary.Read(BlockStatsPass.Value(metadataBuffers, metadataViews, row));
                VariantNested kept = VariantNested.Read(BlockStatsPass.Value(leftBuffers, leftViews, row));
                for (int f = 0; f < kept.Count; f++)
                {
                    ReadOnlySpan<byte> held = kept.ValueAt(f);
                    narrow += dictionary[kept.IdAt(f)].SequenceEqual("other0"u8) && held.Length < 9 && (held[0] >> 2) is >= 3 and <= 5 ? 1 : 0;
                }
            }
        }

        return narrow;

        // Row `written` of the views put back holds the bytes the rebuild wrote of row `row`.
        void Same(int written, int row)
        {
            if (!objects[row])
            {
                return;
            }

            ReadOnlySpan<byte> expected = BlockStatsPass.Value(rebuiltBuffers, rebuiltViews, row);
            ReadOnlySpan<byte> view = sized.AsSpan(written * ViewSize, ViewSize);
            ReadOnlySpan<byte> actual = whole.AsSpan(MemoryMarshal.Read<int>(view[12..]), MemoryMarshal.Read<int>(view));
            Assert.True(
                expected.SequenceEqual(actual),
                $"{name} row {row}, {plan.Keys.Count} keys: put back as {Convert.ToHexString(actual)}, rebuilt as {Convert.ToHexString(expected)}");
        }
    }

    /// <summary>
    /// Documents whose keys each hold values of one class, so that a file written under their shredding holds each in a
    /// typed column alone, with keys left in the rows' values: integers of one byte, two and four, a text, an object and
    /// a list. Most rows repeat the one before.
    /// </summary>
    private static Doc[] Steady(Random random)
    {
        SortedDictionary<string, Variant> fields = FreshSteady(random);
        SortedDictionary<string, Variant> extra = FreshSteady(random);
        Doc[] docs = new Doc[Rows];
        for (int row = 0; row < Rows; row++)
        {
            docs[row] = new Doc(row, NextSteady(random, ref fields), random.Next(10) == 0 ? null : NextSteady(random, ref extra));
        }

        return docs;
    }

    private static Variant NextSteady(Random random, ref SortedDictionary<string, Variant> fields)
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
            fields = FreshSteady(random);
        }
        else if (roll < 20)
        {
            string key = SteadyKeys[random.Next(SteadyKeys.Length)];
            if (fields.ContainsKey(key) && random.Next(2) == 0)
            {
                fields.Remove(key);
            }
            else
            {
                fields[key] = SteadyValue(random, key);
            }
        }

        return Variant.Object([.. fields.Select(static held => (held.Key, held.Value))]);
    }

    private static readonly string[] SteadyKeys = ["about", "status", "duration", "route", "note", "ok", "tags", "user", "other0", "other1", "other2"];

    private static SortedDictionary<string, Variant> FreshSteady(Random random)
    {
        SortedDictionary<string, Variant> fields = new(StringComparer.Ordinal);
        foreach (string key in SteadyKeys)
        {
            if (random.Next(10) < 5)
            {
                fields[key] = SteadyValue(random, key);
            }
        }

        return fields;
    }

    private static Variant SteadyValue(Random random, string key) => key switch
    {
        "about" => $"a text of more than 255 bytes, so that the object that holds it writes each of its offsets in two bytes, {new string('.', 180)} {random.Next(3)}",
        "status" => Variant.From((long)random.Next(600)),
        "duration" => Variant.From(random.NextDouble() * 1_000),
        "route" => Routes[random.Next(Routes.Length)],
        "note" => new string('n', random.Next(60, 68)),
        "ok" => Variant.From(random.Next(2) == 0),
        "tags" => Variant.Array([.. Enumerable.Range(0, random.Next(4)).Select(i => Variant.From((long)random.Next(20)))]),
        "user" => Variant.Object(("id", (long)random.Next(8)), ("name", $"user-{random.Next(4)}")),
        "other0" => Variant.From(random.Next(4) switch { 0 => (long)random.Next(100), 1 => 30_000L, 2 => 2_000_000L, _ => 5_000_000_000L }),
        "other1" => random.Next(3) == 0 ? Variant.From("a text") : Variant.From((long)random.Next(70_000)),
        _ => Variant.From((long)random.Next(70_000)),
    };
}
