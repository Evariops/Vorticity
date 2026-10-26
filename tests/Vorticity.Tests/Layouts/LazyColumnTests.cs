// A struct root's columns, parsed when a walk first reaches them: what a walk parses, how the nodes
// of columns parsed in any order are numbered, and that every rule the whole parse enforces still
// holds, column by column.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed partial class LazyColumnTests
{
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Fact]
    public void AStructRootParsesAColumnWhenAWalkFirstReachesIt()
    {
        DType schema = Schema(3);
        LayoutTree tree = ParseDeferred(Columns(3, rows: 4), schema);

        Assert.Equal(3, tree.Root.ChildCount);
        Assert.False(tree.ColumnParsed(0) || tree.ColumnParsed(1) || tree.ColumnParsed(2));

        LayoutNode second = tree.Root.GetChild(1);
        Assert.True(tree.ColumnParsed(1));
        Assert.False(tree.ColumnParsed(0) || tree.ColumnParsed(2));
        Assert.Equal(schema.GetField(1), second.DType);
        Assert.Equal(4, second.RowCount);
    }

    [Fact]
    public void ColumnsParsedInAnyOrderNumberEveryNodeOnce()
    {
        SyntheticLayout root = Columns(4, rows: 6);
        DType schema = Schema(4);

        // Parsed whole, the nodes are numbered in the order of the tree, as the whole parse did.
        LayoutTree whole = Parse(root, schema);
        List<int> ordered = Walk(whole.Root);
        Assert.Equal(Enumerable.Range(0, whole.NodeCount), ordered);

        // Parsed last column first, every node still has an index of its own, below the count, and
        // the index finds it.
        LayoutTree reversed = ParseDeferred(root, schema);
        for (int column = 3; column >= 0; column--)
        {
            _ = reversed.Root.GetChild(column);
        }

        List<int> indices = Walk(reversed.Root);
        Assert.Equal(whole.NodeCount, reversed.NodeCount);
        Assert.Equal(Enumerable.Range(0, reversed.NodeCount), indices.Order());
        foreach (int index in indices)
        {
            Assert.Equal(index, reversed.GetNode(index).Index);
        }
    }

    [Fact]
    public void WalksThatReachAColumnAtOnceShareItsTree()
    {
        const int columns = 32;
        const int walkers = 16;
        LayoutTree tree = ParseDeferred(Columns(columns, rows: 8), Schema(columns));

        // Every walker reaches every column, each starting at a column of its own, so that most
        // columns are reached by several at once.
        LayoutTree[][] seen = new LayoutTree[walkers][];
        using Barrier start = new Barrier(walkers);
        Parallel.For(0, walkers, new ParallelOptions { MaxDegreeOfParallelism = walkers }, walker =>
        {
            LayoutTree[] trees = seen[walker] = new LayoutTree[columns];
            start.SignalAndWait();
            for (int step = 0; step < columns; step++)
            {
                int column = (walker + step) % columns;
                trees[column] = tree.Root.GetChild(column).Tree;
            }
        });

        for (int column = 0; column < columns; column++)
        {
            for (int walker = 1; walker < walkers; walker++)
            {
                Assert.Same(seen[0][column], seen[walker][column]);
            }
        }

        Assert.Equal(Enumerable.Range(0, tree.NodeCount), Walk(tree.Root).Order());
    }

    [Fact]
    public void AColumnMalformedBelowItsTopFailsWhenReachedAndTheWholeParseRefusesIt()
    {
        // The second column's chunks cover 3 rows of the 4 it declares.
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4).With(
            SyntheticLayout.Flat(4, 0),
            new SyntheticLayout("vortex.chunked", 4).With(SyntheticLayout.Flat(1, 1), SyntheticLayout.Flat(2, 2)));
        DType schema = Schema(2);

        LayoutTree tree = ParseDeferred(root, schema);
        Assert.Equal(LayoutEncodingId.Flat, tree.Root.GetChild(0).Encoding);
        VortexFormatException reached = Assert.Throws<VortexFormatException>(() => tree.Root.GetChild(1));
        Assert.Contains("chunks cover", reached.Message, StringComparison.Ordinal);

        // A walk that reaches it again fails again, rather than finding half a column.
        Assert.Throws<VortexFormatException>(() => tree.Root.GetChild(1));

        VortexFormatException whole = Assert.Throws<VortexFormatException>(() => Parse(root, schema));
        Assert.Contains("chunks cover", whole.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnWhoseRowsDisagreeWithTheRootIsRefusedBeforeAnyIsParsed()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4)
            .With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(3, 1));

        VortexFormatException error = Assert.Throws<VortexFormatException>(() => ParseDeferred(root, Schema(2)));
        Assert.Contains("field 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsThatShareOneSubtreeSpendOneBudget()
    {
        // Eight columns that are all one table of 31 nodes: the buffer holds it once, and its length
        // bounds the nodes the whole tree may make, which one column fits and eight do not.
        const int columns = 8;
        const int levels = 4;
        byte[] bytes = SharedColumns(columns, levels, out string[] ids);
        int perColumn = (1 << (levels + 1)) - 1;
        long budget = (bytes.Length / 4) + 1;
        Assert.True(perColumn < budget && perColumn * columns > budget, $"{perColumn} nodes a column against {budget}");

        DType schema = Schema(columns);
        LayoutTree tree = LayoutTree.ParseDeferred(bytes, schema, ids, segmentCount: 1);
        Assert.Equal(perColumn, CountNodes(tree.Root.GetChild(0)));

        VortexFormatException reached = Assert.Throws<VortexFormatException>(() =>
        {
            for (int column = 1; column < columns; column++)
            {
                _ = tree.Root.GetChild(column);
            }
        });
        Assert.Contains("nodes", reached.Message, StringComparison.Ordinal);

        Assert.Throws<VortexFormatException>(() => LayoutTree.Parse(bytes, schema, ids, segmentCount: 1));
    }

    [Fact]
    public async Task AProjectionParsesTheColumnsItReads()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("nans").ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);

        // The last of five columns, and the first, whose zone map gives the scan its block size.
        LayoutTree tree = file.LayoutTree;
        Assert.True(tree.ColumnParsed(4));
        Assert.False(tree.ColumnParsed(1) || tree.ColumnParsed(2) || tree.ColumnParsed(3));
    }

    [Fact]
    public async Task ColumnsOfOneDTypeAndZoneMapShareTheirZoneTable()
    {
        Twin[] rows = new Twin[5_000];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Twin(i, (i * 7) % 1_000, "s" + (i % 13).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        string path = Path.Combine(Path.GetTempPath(), $"lazy-columns-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Twin>(path))
            {
                await writer.WriteAsync<Twin>(rows, CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            LayoutNode root = file.LayoutTree.Root;
            Assert.Equal(3, root.ChildCount);
            DType first = ZoneTableOf(root.GetChild(0));
            DType second = ZoneTableOf(root.GetChild(1));
            DType text = ZoneTableOf(root.GetChild(2));

            Assert.Same(first.Arena, second.Arena);
            Assert.NotSame(first.Arena, text.Arena);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Twin(long First, long Second, string Text);

    // ---------------------------------------------------------------------------------- helpers

    private static DType ZoneTableOf(LayoutNode column)
    {
        Assert.Equal(LayoutEncodingId.Zoned, column.Encoding);
        Assert.Equal(2, column.ChildCount);
        return column.GetChild(1).DType;
    }

    /// <summary>A struct of <paramref name="columns"/> non-nullable i64 fields, in an arena of its own: the tests run side by side.</summary>
    private static DType Schema(int columns)
    {
        DTypeArena types = new DTypeArena();
        string[] names = new string[columns];
        DType[] fields = new DType[columns];
        for (int i = 0; i < columns; i++)
        {
            names[i] = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[i] = types.Primitive(PType.I64, Nullability.NonNullable);
        }

        return types.Struct(names, fields, Nullability.NonNullable);
    }

    /// <summary>A struct root of <paramref name="columns"/> columns of two chunks each.</summary>
    private static SyntheticLayout Columns(int columns, ulong rows)
    {
        SyntheticLayout[] children = new SyntheticLayout[columns];
        for (int i = 0; i < columns; i++)
        {
            children[i] = new SyntheticLayout("vortex.chunked", rows)
                .With(SyntheticLayout.Flat(rows / 2, (uint)(2 * i)), SyntheticLayout.Flat(rows - (rows / 2), (uint)((2 * i) + 1)));
        }

        return new SyntheticLayout("vortex.struct", rows).With(children);
    }

    /// <summary>
    /// A struct root whose <paramref name="columns"/> children are all one table: <paramref name="levels"/>
    /// chunked layouts each pointing twice at the next, down to one flat leaf.
    /// </summary>
    private static byte[] SharedColumns(int columns, int levels, out string[] ids)
    {
        ids = ["vortex.chunked", "vortex.flat", "vortex.struct"];
        FlatBufferBuilder builder = new FlatBufferBuilder();
        try
        {
            int child = LayoutWriter.Write(builder, 1, 1, default, default, [0u]);
            ulong rows = 1;
            int[] pair = new int[2];
            for (int i = 0; i < levels; i++)
            {
                rows *= 2;
                pair[0] = child;
                pair[1] = child;
                child = LayoutWriter.Write(builder, 0, rows, default, pair, default);
            }

            int[] children = Enumerable.Repeat(child, columns).ToArray();
            int root = LayoutWriter.Write(builder, 2, rows, default, children, default);
            return builder.FinishToArray(root);
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>The indices of the nodes under <paramref name="node"/>, in the order of the tree.</summary>
    private static List<int> Walk(LayoutNode node)
    {
        List<int> indices = [];
        Visit(node, indices);
        return indices;

        static void Visit(LayoutNode node, List<int> indices)
        {
            indices.Add(node.Index);
            for (int i = 0; i < node.ChildCount; i++)
            {
                Visit(node.GetChild(i), indices);
            }
        }
    }

    private static int CountNodes(LayoutNode node) => Walk(node).Count;

    private static LayoutTree Parse(SyntheticLayout root, DType schema)
    {
        byte[] bytes = SyntheticLayoutWriter.Write(root, out string[] specs);
        return LayoutTree.Parse(bytes, schema, specs, segmentCount: 64);
    }

    private static LayoutTree ParseDeferred(SyntheticLayout root, DType schema)
    {
        byte[] bytes = SyntheticLayoutWriter.Write(root, out string[] specs);
        return LayoutTree.ParseDeferred(bytes, schema, specs, segmentCount: 64);
    }
}
