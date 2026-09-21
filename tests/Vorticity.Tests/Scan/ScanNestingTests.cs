// Deep nesting, chunk-aligned ranges on a real chunked file, and two enumerators drawn from one
// enumerable at the same time.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanNestingTests
{
    [Fact]
    public void ADeeplyNestedPathResolvesToOneLeaf()
    {
        const int Depth = 20;
        DTypeArena arena = new DTypeArena();
        DType leaf = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType current = leaf;
        for (int i = 0; i < Depth; i++)
        {
            current = arena.Struct(["inner", "other"], [current, leaf], Nullability.NonNullable);
        }

        System.Text.StringBuilder path = new System.Text.StringBuilder();
        for (int i = 0; i < Depth; i++)
        {
            if (i > 0)
            {
                path.Append('.');
            }

            path.Append("inner");
        }

        Projection projection = Projection.Parse(current, [path.ToString()]);
        Assert.Equal(1, projection.LeafCount);

        DType projected = projection.ProjectedSchema(current, new DTypeArena());
        for (int i = 0; i < Depth; i++)
        {
            Assert.Equal(1, projected.FieldCount);
            Assert.Equal("inner", projected.GetFieldName(0));
            projected = projected.GetField(0);
        }

        Assert.Equal(DTypeKind.Primitive, projected.Kind);
    }

    [Fact]
    public void APathLongerThanAnySchemaIsACallerError()
    {
        // A path with more segments than MaxDTypeDepth cannot resolve against any legal schema -
        // the schema would have to be deeper than DTypeArena permits. It must therefore come back
        // as an ArgumentException, whichever guard catches it first, and never as a buffer overrun.
        DTypeArena arena = new DTypeArena();
        DType leaf = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType root = arena.Struct(["a"], [leaf], Nullability.NonNullable);

        System.Text.StringBuilder path = new System.Text.StringBuilder("a");
        for (int i = 0; i < VortexLimits.MaxDTypeDepth + 4; i++)
        {
            path.Append(".a");
        }

        Assert.Throws<ArgumentException>(() => Projection.Parse(root, [path.ToString()]));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(0, 100)]
    [InlineData(100, 200)]
    [InlineData(99, 201)]
    [InlineData(1, 299)]
    [InlineData(200, 300)]
    public async Task ChunkAlignedRangesOfARealChunkedFile(long start, long end)
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/chunked_stream_3"), CancellationToken.None);
        Assert.Equal(300, file.RowCount);

        LayoutTree tree = LayoutTree.Parse(file);
        Assert.Equal(LayoutEncodingId.Chunked, tree.Root.Encoding);

        long[] all = await Read(file, null);
        long[] ranged = await Read(file, new RowRange(start, end));

        Assert.Equal(end - start, ranged.Length);
        for (int i = 0; i < ranged.Length; i++)
        {
            Assert.Equal(all[start + i], ranged[i]);
        }
    }

    [Fact]
    public async Task ChunkBoundariesBecomeBatchBoundaries()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/chunked_stream_3"), CancellationToken.None);

        LayoutTree tree = LayoutTree.Parse(file);
        int chunks = tree.Root.ChildCount;
        Assert.True(chunks > 1, "the fixture is a multi-chunk stream");

        List<int> sizes = new List<int>();
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            sizes.Add(batch.RowCount);
        }

        // One batch per chunk: a chunk is the unit of independence and the default cap (8192) is
        // wider than any of them.
        Assert.Equal(chunks, sizes.Count);
        for (int i = 0; i < chunks; i++)
        {
            Assert.Equal(tree.Root.GetChild(i).RowCount, sizes[i]);
        }
    }

    [Fact]
    public async Task TwoEnumeratorsOfOneEnumerableRunIndependently()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("distributions/high_cardinality_i64_r8193"), CancellationToken.None);

        IAsyncEnumerable<RecordBatch> scan = file.ScanBuilder().WithMaxBatchRows(700).ExecuteAsync();
        IAsyncEnumerator<RecordBatch> left = scan.GetAsyncEnumerator();
        IAsyncEnumerator<RecordBatch> right = scan.GetAsyncEnumerator();

        try
        {
            // Interleaved: each enumerator has its own ScanContext, so one advancing must not
            // invalidate the other's live batch.
            Assert.True(await left.MoveNextAsync());
            Assert.True(await right.MoveNextAsync());

            int leftRows = left.Current.RowCount;
            Assert.True(await right.MoveNextAsync());
            Assert.Equal(leftRows, left.Current.RowCount);
            Assert.Equal(0, left.Current.StartRow);
            Assert.Equal(leftRows, right.Current.StartRow);
        }
        finally
        {
            await left.DisposeAsync();
            await right.DisposeAsync();
        }
    }

    private static async Task<long[]> Read(VortexFile file, RowRange? range)
    {
        ScanBuilder builder = file.ScanBuilder();
        if (range is RowRange rows)
        {
            builder = builder.Rows(rows);
        }

        List<long> values = new List<long>();
        await foreach (RecordBatch batch in builder.ExecuteAsync())
        {
            ReadOnlySpan<long> span = batch.Column(0).AsPrimitive<long>().Values;
            for (int i = 0; i < span.Length; i++)
            {
                values.Add(span[i]);
            }
        }

        return values.ToArray();
    }
}
