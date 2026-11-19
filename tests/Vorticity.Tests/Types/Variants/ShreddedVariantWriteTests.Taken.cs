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
using Vorticity.Scanning;
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

    /// <summary>
    /// The same documents, in runs of rows that repeat: each block read alone and added to the chunk writes the blob of the
    /// chunk taken whole, and counts its keys block by block as the rows taken in place do, a run at once.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public async Task BlocksOfRowsThatRepeatReadAloneAndAddedAreTheChunkTakenWhole(int seed)
    {
        Random random = new Random(seed);
        await AssertSameBlobsAsync(Repeated(random), Shredding(random), Shredding(random));
    }

    /// <summary>
    /// The column of each key a shredding types, read in its class, written from what a block's read took of it, is the column
    /// a read of the rows' keys writes: the same rows hold a value, and the same value.
    /// </summary>
    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public async Task AKeysColumnWrittenFromABlocksReadIsTheColumnReadFromItsRows(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = Repeated(random);
        VariantShredding plan = VariantShredding.Object(
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
            ("big", VariantShredding.Primitive(VariantClass.String)),
            ("missing", VariantShredding.Primitive(VariantClass.String)),
            ("never", VariantShredding.Primitive(VariantClass.Integer)));
        // Each key read in the class its typed column holds, in the plan's order of keys.
        Vorticity.Compute.VariantRead[] reads = [.. plan.Keys.Select(static key => key.Field.Class switch
        {
            VariantClass.Integer => Vorticity.Compute.VariantRead.AsInt64,
            VariantClass.Double => Vorticity.Compute.VariantRead.AsDouble,
            VariantClass.String => Vorticity.Compute.VariantRead.AsString,
            _ => Vorticity.Compute.VariantRead.AsBool,
        })];
        string path = await WriteAsync(rows, null, 1, blockRows: rows.Length);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            int field = file.DType.IndexOfField(nameof(Doc.Attributes));
            Vorticity.Compute.VariantFieldExpr[] paths = new Vorticity.Compute.VariantFieldExpr[plan.Keys.Count];
            for (int key = 0; key < paths.Length; key++)
            {
                Assert.True(VariantShredder.Shredding.Reads(plan, key, reads[key]), plan.Keys[key].Name);
                paths[key] = new Vorticity.Compute.VariantFieldExpr(
                    new Vorticity.Expressions.ColumnFieldExpr(nameof(Doc.Attributes)), [Vorticity.Compute.VariantStep.Of(plan.Keys[key].Name)], reads[key]);
            }

            Vorticity.Compute.VariantFieldExpr.KeyReads together = new(paths);
            int[] read = new int[paths.Length];
            await foreach (RecordBatch batch in file.Scan<Doc>().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    VariantShredder.Shredding alone = new VariantShredder.Shredding(plan);
                    CanonicalArena store = new CanonicalArena();
                    CanonicalArena part = new CanonicalArena();
                    for (int start = 0; start < batch.RowCount; start += 700)
                    {
                        int count = Math.Min(700, batch.RowCount - start);
                        int slice = Vorticity.Layouts.CanonicalSlice.SliceAcross(batch.Arena, part, batch.RootIndex, start, count);
                        int column = part.GetNode(slice).GetFieldIndex(field);
                        Vorticity.Compute.VariantFieldExpr.ApplyKeys(part, column, together, read);
                        VariantShredder.Shredding.Rows taken = VariantShredder.Shredding.Prepare(part, column);
                        store.Reset();
                        alone.Begin(store, null, counted: true);
                        alone.TakeMore(in taken, 0);
                        Vorticity.Buffers.VortexBuffer absent = default;
                        for (int key = 0; key < paths.Length; key++)
                        {
                            int written = alone.KeyColumn(part, key, reads[key], count, ref absent);
                            SameColumn(part, read[key], written, $"{plan.Keys[key].Name} from row {start}");
                        }

                        part.Reset();
                    }
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Whether two columns of an arena hold a value at the same rows, and the same: a number's bits, a boolean, a text's bytes.</summary>
    private static void SameColumn(CanonicalArena arena, int expected, int actual, string what)
    {
        CanonicalNode a = arena.GetNode(expected);
        CanonicalNode b = arena.GetNode(actual);
        Assert.True(a.Kind == b.Kind, $"{what}: {b.Kind} against {a.Kind}");
        Assert.Equal(a.Length, b.Length);
        for (int row = 0; row < a.Length; row++)
        {
            bool held = ArenaWords.IsValid(arena, expected, row);
            Assert.True(held == ArenaWords.IsValid(arena, actual, row), $"{what}: row {row} holds {(held ? "none" : "a value")} against {(held ? "a value" : "none")}");
            if (!held)
            {
                continue;
            }

            switch (a.Kind)
            {
                case CanonicalKind.Bool:
                    Assert.True(Bit(a, row) == Bit(b, row), $"{what}: row {row}");
                    break;
                case CanonicalKind.VarBinView:
                    Assert.True(new ViewValues(a).At(row).SequenceEqual(new ViewValues(b).At(row)), $"{what}: row {row}");
                    break;
                default:
                    Assert.True(
                        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(a.Values.Span)[row] == System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(b.Values.Span)[row],
                        $"{what}: row {row}");
                    break;
            }
        }

        static bool Bit(CanonicalNode node, int row) => (node.Bits.Span[(node.BitOffset + row) >> 3] & (1 << ((node.BitOffset + row) & 7))) != 0;
    }

    /// <summary>Documents of every shape and of measures, in runs of rows of one document, some null in the second column.</summary>
    private static Doc[] Repeated(Random random)
    {
        List<Doc> rows = [];
        while (rows.Count < 3_000)
        {
            Variant attributes = random.Next(4) == 0 ? Measured(random) : Document(random);
            Variant? extra = random.Next(10) == 0 ? null : Document(random);
            for (int run = random.Next(3) == 0 ? 1 + random.Next(40) : 1; run > 0; run--)
            {
                rows.Add(new Doc(rows.Count, attributes, random.Next(4) == 0 ? null : extra));
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// Rows written under one shredding, read back as that shredding holds them and taken as they lie into another
    /// (<see cref="VariantShredder.Shredding.TakeGroup"/>): the chunk written from it is the chunk written from the rows put
    /// back together, to the byte, and its census theirs, when every key the first types is one the second types in the
    /// same class, whatever else the second types of what the first left. A shredding that types fewer keys, or one in
    /// another class, is refused, and nothing taken.
    /// </summary>
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    public async Task AShreddingTakenFromAnotherAsItLiesIsTheShreddingOfItsRowsPutBack(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = [.. Enumerable.Range(0, 3_000).Select(i => new Doc(i, Flat(random), random.Next(10) == 0 ? null : Flat(random)))];
        VariantShredding written = VariantShredding.Object(
            ("n", VariantShredding.Primitive(VariantClass.Integer)),
            ("d", VariantShredding.Primitive(VariantClass.Double)),
            ("s", VariantShredding.Primitive(VariantClass.String)),
            ("l", VariantShredding.Primitive(VariantClass.String)),
            ("b", VariantShredding.Primitive(VariantClass.Boolean)));
        VariantShredding wider = VariantShredding.Object(
            ("n", VariantShredding.Primitive(VariantClass.Integer)),
            ("d", VariantShredding.Primitive(VariantClass.Double)),
            ("s", VariantShredding.Primitive(VariantClass.String)),
            ("l", VariantShredding.Primitive(VariantClass.String)),
            ("b", VariantShredding.Primitive(VariantClass.Boolean)),
            ("m", VariantShredding.Primitive(VariantClass.Integer)),
            ("o", VariantShredding.Object(("id", VariantShredding.Primitive(VariantClass.Integer)))),
            ("t", VariantShredding.List(VariantClass.Integer)),
            ("never", VariantShredding.Primitive(VariantClass.String)));
        VariantShredding narrower = VariantShredding.Object(
            ("n", VariantShredding.Primitive(VariantClass.Integer)),
            ("d", VariantShredding.Primitive(VariantClass.Double)),
            ("s", VariantShredding.Primitive(VariantClass.String)));
        VariantShredding otherwise = VariantShredding.Object(
            ("n", VariantShredding.Primitive(VariantClass.String)),
            ("d", VariantShredding.Primitive(VariantClass.Double)),
            ("s", VariantShredding.Primitive(VariantClass.String)),
            ("l", VariantShredding.Primitive(VariantClass.String)),
            ("b", VariantShredding.Primitive(VariantClass.Boolean)));

        string path = await WriteAsync(rows, new() { [nameof(Doc.Attributes)] = written, [nameof(Doc.Extra)] = written }, 1, blockRows: rows.Length);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<RecordBatch> whole = [];
            await foreach (RecordBatch batch in file.Scan<Doc>().ToBatchesAsync(Ct))
            {
                whole.Add(batch);
            }

            RecordBatch back = Assert.Single(whole);
            using (back)
            {
                foreach (string name in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
                {
                    int column = back.Arena.GetNode(back.RootIndex).GetFieldIndex(file.DType.IndexOfField(name));
                    foreach ((VariantShredding plan, bool taken) in new[] { (written, true), (wider, true), (narrower, false), (otherwise, false) })
                    {
                        using ArrayBlobWriter.Workspace asRows = new ArrayBlobWriter.Workspace { Account = TestAccount.Shared };
                        using ArrayBlobWriter.Workspace asItLies = new ArrayBlobWriter.Workspace { Account = TestAccount.Shared };
                        using ArrayBlobWriter.BlobLease expected = VariantShredder.Write(
                            asRows, back.Arena, column, plan, null, new EncodingDictionary(ComponentKind.Array, Vorticity.Editions.EditionRegistry.Newest), compress: true);

                        VariantShredder.Shredding projected = asItLies.Shredding(plan);
                        CanonicalArena store = new CanonicalArena();
                        projected.Begin(store, null, counted: true);
                        bool every = true;
                        int groups = 0;
                        await foreach (RecordBatch apart in file.ScanBuilder().Project(name).ApartAsync(null).WithCancellation(Ct))
                        {
                            using (apart)
                            {
                                int group = apart.Arena.GetNode(apart.RootIndex).GetFieldIndex(0);
                                Assert.True(ApartCensus.IsGroup(apart.Arena, group), $"{name}: the rows are read as their shredding holds them");
                                every &= every && projected.TakeGroup(apart.Arena, group, checked((int)apart.StartRow));
                                groups++;
                            }
                        }

                        Assert.True(groups > 0);
                        Assert.True(every == taken, $"{name}: a shredding of {plan.Keys.Count} keys {(every ? "took" : "refused")} the rows as they lie");
                        if (!every)
                        {
                            continue;
                        }

                        Assert.Equal(back.RowCount, projected.Taken);
                        PathCensus.Counting counting = PathCensus.Prepare(back.Arena, column, back.RowCount);
                        PathCensus byRows = new PathCensus(16, CensusGap);
                        PathCensus byTake = new PathCensus(16, CensusGap);
                        byRows.Add(in counting);
                        VariantShredder.Shredding.Rows put = VariantShredder.Shredding.Prepare(back.Arena, column);
                        Assert.True(projected.Count(byTake, in put, PathCensus.ValueBytesOf(in counting)));
                        Assert.Equal(byRows.Rows, byTake.Rows);
                        Assert.Equal(byRows.ValueBytes, byTake.ValueBytes);
                        SameCounts(byRows.Root, byTake.Root, name);

                        projected.Settle(back.Arena, column);
                        using ArrayBlobWriter.BlobLease actual = projected.Write(
                            asItLies, new EncodingDictionary(ComponentKind.Array, Vorticity.Editions.EditionRegistry.Newest), compress: true);
                        Assert.True(
                            expected.Memory.Span.SequenceEqual(actual.Memory.Span),
                            $"{name}: {actual.Length} bytes written from the rows taken as they lie against {expected.Length} from the rows put back together");
                    }
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A document whose keys each hold values of one class, but for one that mixes two: an integer, a double, a text short
    /// or long, a boolean, an object, a list, and keys no shredding names; now and then no object at all.
    /// </summary>
    private static Variant Flat(Random random)
    {
        switch (random.Next(25))
        {
            case 0:
                return Variant.Null;
            case 1:
                return Variant.From(random.NextInt64(-1_000, 1_000));
            case 2:
                return Variant.Array("a", 1L);
        }

        List<(string, Variant)> fields = [];
        void Maybe(string key, int tenths, Func<Variant> value)
        {
            if (random.Next(10) < tenths)
            {
                fields.Add((key, value()));
            }
        }

        Maybe("n", 7, () => Variant.From(random.Next(4) switch { 0 => random.Next(100), 1 => random.Next(30_000), 2 => random.Next(), _ => random.NextInt64() }));
        Maybe("d", 6, () => Variant.From(random.NextDouble() * 1_000));
        Maybe("s", 8, () => Routes[random.Next(Routes.Length)]);
        Maybe("l", 3, () => $"a text long enough to pass a short string's sixty-three bytes, whatever follows it: {random.Next(40)}");
        Maybe("b", 5, () => Variant.From(random.Next(2) == 0));
        Maybe("m", 5, () => random.Next(3) == 0 ? Variant.From("three") : Variant.From((long)random.Next(1_000)));
        Maybe("o", 4, () => random.Next(4) == 0 ? Variant.From("none") : Variant.Object(("id", random.NextInt64(1, 1_000)), ("name", $"user-{random.Next(50)}")));
        Maybe("t", 4, () => random.Next(4) == 0
            ? Variant.Array("x", 1L)
            : Variant.Array([.. Enumerable.Range(0, random.Next(4)).Select(i => Variant.From((long)random.Next(20)))]));
        Maybe($"x{random.Next(6)}", 5, () => random.NextDouble());
        Maybe("z", 2, () => Variant.Null);
        return Variant.Object(fields.ToArray());
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
