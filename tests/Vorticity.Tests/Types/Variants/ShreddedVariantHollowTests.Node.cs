// The values of a shredded variant's rows held not put back together (CanonicalKind.Shredded): a node of their sized
// views that names what took the rows, read for its sizes as it lies, and whose values are put back when a reader asks
// for them, whole, a window of it, lent to another arena, copied, or given another validity.
//
// THE ORACLE IS THE REBUILD OF THE SHREDDING READ: every row's value is the bytes it writes, whatever the way the node
// was reached, and the node names the bytes the rebuilt one names.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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
    /// <summary>
    /// Documents written under a shredding and taken as it holds them, their values held as a node of sized views: the
    /// node's views read for their sizes put nothing back and name the bytes the rebuilt values name. Asked for its
    /// values, it gives the rebuilt ones, once; so do a window of it in another arena, a node that lends it there, a copy
    /// of it, the same node under another validity, and the tree it is a field of, decoded.
    /// </summary>
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public async Task ValuesNotPutBackArePutBackWhenAskedFor(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = Steady(random);
        VariantShredding plan = VariantShredding.Object(
            ("about", VariantShredding.Primitive(VariantClass.String)),
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("note", VariantShredding.Primitive(VariantClass.String)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
            ("other0", VariantShredding.Primitive(VariantClass.Integer)));
        VariantShredding read = VariantShredding.Object(
            ("about", VariantShredding.Primitive(VariantClass.String)),
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("duration", VariantShredding.Primitive(VariantClass.Double)),
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("note", VariantShredding.Primitive(VariantClass.String)),
            ("ok", VariantShredding.Primitive(VariantClass.Boolean)));

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-node-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = rows.Length,
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = read, [nameof(Doc.Extra)] = read },
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
                await foreach (RecordBatch apart in file.ScanBuilder().Project(name).ApartAsync(null).WithCancellation(Ct))
                {
                    using (apart)
                    {
                        NodeOf(apart.Arena, apart.Arena.GetNode(apart.RootIndex).GetFieldIndex(0), apart.RowCount, plan, name, random);
                    }
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private readonly record struct Source(CanonicalArena Arena, int Node, int First, int End) : CanonicalRuns.ISource;

    private static void NodeOf(CanonicalArena arena, int group, int rows, VariantShredding plan, string name, Random random)
    {
        VariantShredder.Shredding taken = new VariantShredder.Shredding(plan, ranged: true);
        taken.Begin(new CanonicalArena(), null);
        Assert.True(taken.TakeGroup(arena, group, 0));
        Assert.True(taken.Exact);
        Assert.True(taken.Index());

        // The rows rebuilt, the oracle.
        DTypeArena types = new DTypeArena();
        ShreddedVariant shredding = ShreddedVariant.Compile(VortexTypes.FromDType(arena.GetNode(group).DType), types);
        DType variant = types.Variant(Nullability.Nullable);
        int rebuilt = arena.GetNode(shredding.Assemble(arena, variant, group)).GetFieldIndex(1);
        byte[][] expected = new byte[rows][];
        {
            VortexBuffer[] buffers = [.. arena.GetNode(rebuilt).DataBuffers];
            byte[] views = arena.GetNode(rebuilt).Views.Span.ToArray();
            for (int row = 0; row < rows; row++)
            {
                expected[row] = BlockStatsPass.Value(buffers, views, row).ToArray();
            }
        }

        // The node of the same rows' values not put back, in an arena of its own that keeps what is told of its nodes: their
        // sized views, the value column's buffers for the rows taken as they are, the rows' metadata lent, and what took
        // the rows.
        (int metadata, int value, _) = Parts(arena, group);
        CanonicalArena held = new RetainingArena();
        VortexBuffer sized = held.AllocateUninitialized(rows * ViewSize, ViewSize, out Span<byte> into);
        shredding.BeginHollow();
        try
        {
            Assert.True(shredding.HollowSlice(arena, group, into));
        }
        finally
        {
            shredding.EndHollow();
        }

        VortexBuffer[] left = value < 0 ? [] : [.. arena.GetNode(value).DataBuffers];
        int node = held.AddShredded(arena.GetNode(rebuilt).DType, rows, Validity.NonNullable, sized.Slice(0, rows * ViewSize), left, held.ReferenceFrom(arena, metadata));

        // Nothing is told of what took its rows yet, and nothing is put back.
        Assert.False(held.TryTaken(node, out _, out _));
        Assert.Throws<InvalidOperationException>(() => held.MaterializeShredded(node));
        Assert.True(held.NoteTaken(node, taken, 0));
        Assert.True(held.TryTaken(node, out CanonicalArena.ITakenRows? named, out int first));
        Assert.Same(taken, named);
        Assert.Equal(0, first);

        // Read for its sizes as it lies: what the rebuilt values name, and no value put back.
        Assert.Equal(CanonicalKind.Shredded, held.GetNode(node).Kind);
        Assert.Equal(arena.NamedBytes(rebuilt), held.NamedBytes(node));
        int records = held.NodeCount;
        _ = held.NamedBytes(node);
        _ = held.GetNode(node).HollowViews;
        Assert.Equal(records, held.NodeCount);

        // A window of it in another arena that keeps what is told, before any value is put back: it is told the same rows
        // took it, from its first on.
        int start = random.Next(1, rows / 2);
        int some = random.Next(1, rows - start);
        CanonicalArena other = new RetainingArena();
        int window = CanonicalSlice.SliceAcross(held, other, node, start, some);
        Assert.Equal(CanonicalKind.Shredded, other.GetNode(window).Kind);
        Assert.True(other.TryTaken(window, out named, out first));
        Assert.Same(taken, named);
        Assert.Equal(start, first);
        Same(other, window, start, some, "a window of it");

        // The node lent whole there, a copy of it, and the node under another validity.
        int lent = other.ReferenceFrom(held, node);
        Assert.Equal(CanonicalKind.Shredded, other.GetNode(lent).Kind);
        Same(other, lent, 0, rows, "the node lent");
        CanonicalArena copies = new CanonicalArena();
        int copy = copies.CopyFrom(held, node);
        Assert.Equal(CanonicalKind.VarBinView, copies.GetNode(copy).Kind);
        Same(copies, copy, 0, rows, "a copy of it");

        // In an arena that keeps nothing of what is told, a window of it and the node lent have their values put back as
        // they arrive: they could not be later.
        int cut = copies.NodeCount;
        int arrived = CanonicalSlice.SliceAcross(held, copies, node, start, some);
        Assert.Equal(CanonicalKind.VarBinView, copies.GetNode(arrived).Kind);
        Same(copies, arrived, start, some, "a window of it where nothing is kept");
        arrived = copies.ReferenceFrom(held, node);
        Assert.Equal(CanonicalKind.VarBinView, copies.GetNode(arrived).Kind);
        Same(copies, arrived, 0, rows, "the node lent where nothing is kept");
        Assert.True(copies.NodeCount > cut);
        int wrapped = CanonicalRewrap.WithValidity(held, node, held.GetNode(node).DType, Validity.NonNullable, rows);
        Assert.Equal(CanonicalKind.Shredded, held.GetNode(wrapped).Kind);
        Same(held, wrapped, 0, rows, "the node under another validity");

        // The values asked for: put back once, the twin kept.
        Assert.Equal(CanonicalKind.Shredded, held.GetNode(node).Kind);
        Same(held, node, 0, rows, "the node");
        int twin = held.MaterializeShredded(node);
        Assert.Equal(twin, held.MaterializeShredded(node));
        Assert.Equal(twin, held.Decoded(node));
        Assert.Equal(CanonicalKind.VarBinView, held.GetNode(twin).Kind);
        Assert.Equal(arena.NamedBytes(rebuilt), held.NamedBytes(twin));

        // The variant the values are a field of, decoded: its values put back, its metadata as it was.
        int column = held.AddStruct(variant, rows, Validity.NonNullable, [held.GetNode(node).ShreddedMetadataIndex, node]);
        int decoded = held.DecodedTree(column);
        Assert.NotEqual(column, decoded);
        Assert.Equal(CanonicalKind.VarBinView, held.GetNode(held.GetNode(decoded).GetFieldIndex(1)).Kind);
        Same(held, held.GetNode(decoded).GetFieldIndex(1), 0, rows, "the variant decoded");

        // Gathered by runs under the variant they are of, from two nodes that two shreddings took: the rows, and their
        // second half sized on its own, whose values are sized from the same place on as the first rows'. Asked to stay
        // so, the values gathered are still not put back, each given a place of its own, and name nothing until they
        // name a shredding that took the runs' rows in their order.
        int half = rows / 2;
        int tail = CanonicalSlice.SliceAcross(arena, arena, group, half, rows - half);
        VariantShredder.Shredding again = new VariantShredder.Shredding(plan, ranged: true);
        again.Begin(new CanonicalArena(), null);
        Assert.True(again.TakeGroup(arena, tail, 0));
        Assert.True(again.Index());
        CanonicalArena second = new RetainingArena();
        VortexBuffer tailSized = second.AllocateUninitialized((rows - half) * ViewSize, ViewSize, out Span<byte> tailInto);
        shredding.BeginHollow();
        try
        {
            Assert.True(shredding.HollowSlice(arena, tail, tailInto));
        }
        finally
        {
            shredding.EndHollow();
        }

        int twice = second.AddShredded(
            held.GetNode(node).DType, rows - half, Validity.NonNullable, tailSized.Slice(0, (rows - half) * ViewSize), left,
            second.ReferenceFrom(arena, Parts(arena, tail).Metadata));
        Assert.True(second.NoteTaken(twice, again, 0));
        DType shape = types.Struct(["v"], [variant], Nullability.NonNullable);
        int firstRoot = held.AddStruct(shape, rows, Validity.NonNullable, [held.AddStruct(variant, rows, Validity.NonNullable, [held.GetNode(node).ShreddedMetadataIndex, node])]);
        int secondRoot = second.AddStruct(
            shape, rows - half, Validity.NonNullable, [second.AddStruct(variant, rows - half, Validity.NonNullable, [second.GetNode(twice).ShreddedMetadataIndex, twice])]);
        Source[] sources = [new Source(held, firstRoot, 0, rows), new Source(second, secondRoot, 0, rows - half)];

        // The first rows of each, sized from the same place on, among the runs.
        List<CanonicalRuns.Run> runs = [new CanonicalRuns.Run(0, 0, 150), new CanonicalRuns.Run(1, 0, 150)];
        for (int extra = 0; extra < 20; extra++)
        {
            int from = random.Next(2);
            int at = random.Next((from == 0 ? rows : rows - half) - 1);
            runs.Add(new CanonicalRuns.Run(from, at, random.Next(1, Math.Min(40, (from == 0 ? rows : rows - half) - at) + 1)));
        }

        int total = runs.Sum(static run => run.Rows);
        int[] rowOf = [.. runs.SelectMany(run => Enumerable.Range((run.Source == 0 ? 0 : half) + run.Row, run.Rows))];
        using (ScanContext scan = new ScanContext(Vorticity.Tests.Arrays.Decoders.Canonical.TestEncodings.Ids))
        {
            int gathered = CanonicalRuns.Gather<Source>(scan.Decode, shape, sources, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(runs), total, [true]);
            int values = scan.Canonical.GetNode(scan.Canonical.GetNode(gathered).GetFieldIndex(0)).GetFieldIndex(1);
            Assert.Equal(CanonicalKind.Shredded, scan.Canonical.GetNode(values).Kind);
            Assert.False(scan.Canonical.TryTaken(values, out _, out _));
            Assert.Throws<InvalidOperationException>(() => scan.Canonical.MaterializeShredded(values));

            VariantShredder.Shredding block = new VariantShredder.Shredding(plan);
            block.Begin(new CanonicalArena(), null, counted: true);
            int taking = 0;
            foreach (CanonicalRuns.Run run in runs)
            {
                block.AppendRange(run.Source == 0 ? taken : again, run.Row, run.Rows, taking);
                taking += run.Rows;
            }

            // Its caller knows what took the rows gathered, and has the values put back by it: a reader then reads them.
            long sizedAt = scan.Canonical.NamedBytes(values);
            int back = scan.Canonical.MaterializeShredded(values, block, 0);
            Assert.Equal(back, scan.Canonical.MaterializeShredded(values));
            SameRows(scan.Canonical, values, rowOf, "the runs gathered not put back");

            // Not asked to: the values are put back over the rows the runs span, and gathered as any values.
            int plain = CanonicalRuns.Gather<Source>(scan.Decode, shape, sources, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(runs), total);
            int put = scan.Canonical.GetNode(scan.Canonical.GetNode(plain).GetFieldIndex(0)).GetFieldIndex(1);
            Assert.Equal(CanonicalKind.VarBinView, scan.Canonical.GetNode(put).Kind);
            SameRows(scan.Canonical, put, rowOf, "the runs gathered put back");
            Assert.Equal(scan.Canonical.NamedBytes(put), sizedAt);
        }

        // Each row of `index` is the rebuilt value of the row `rowOf` says.
        void SameRows(CanonicalArena read, int index, int[] rowOf, string what)
        {
            Assert.Equal(rowOf.Length, read.GetNode(index).Length);
            VortexBuffer[] buffers = [.. read.GetNode(index).DataBuffers];
            byte[] views = read.GetNode(index).Views.Span.ToArray();
            for (int at = 0; at < rowOf.Length; at++)
            {
                ReadOnlySpan<byte> actual = BlockStatsPass.Value(buffers, views, at);
                Assert.True(
                    actual.SequenceEqual(expected[rowOf[at]]),
                    $"{name}, {what}, row {at} of row {rowOf[at]}: {Convert.ToHexString(actual)} against {Convert.ToHexString(expected[rowOf[at]])}");
            }
        }

        // Rows `from` on of the rebuilt values are the rows of `index`, read through its views.
        void Same(CanonicalArena read, int index, int from, int count, string what)
        {
            CanonicalNode values = read.GetNode(index);
            Assert.Equal(count, values.Length);
            VortexBuffer[] buffers = [.. values.DataBuffers];
            byte[] views = read.GetNode(index).Views.Span.ToArray();
            for (int row = 0; row < count; row++)
            {
                ReadOnlySpan<byte> actual = BlockStatsPass.Value(buffers, views, row);
                Assert.True(
                    actual.SequenceEqual(expected[from + row]),
                    $"{name}, {what}, row {from + row}: {Convert.ToHexString(actual)} against {Convert.ToHexString(expected[from + row])}");
            }
        }
    }
}
