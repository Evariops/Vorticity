// docs/08-semantics.md §4, realized at the layout level: an unknown component is fatal ONLY when a
// projection puts it on the path to data.
//
// The struct layout is where that happens. A field the FieldMask excludes is neither registered nor
// executed, so neither its layout id nor the array encodings under it are ever resolved. Opening
// the file, parsing its layout tree and reading its other columns all succeed.
using System;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Types;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LazyResolutionTests
{
    [Fact]
    public async Task AnUnknownLayoutOnAnUnprojectedFieldDoesNotThrow()
    {
        // {items=list(i32)} stored under a vortex.list LAYOUT, which is in no core edition
        // (contract §2.8). Selecting no field reads the struct's rows and nothing under them.
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/experimental_list_layout");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        Assert.Equal(LayoutEncodingId.Struct, tree.Root.Encoding);
        Assert.True(HasUnknown(tree.Root), "the fixture no longer carries an unknown layout.");

        int root = await LayoutExecutor.ReadAsync(
            file, tree, context, new RowRange(0, file.RowCount), FieldMask.Empty);

        CanonicalNode node = context.Canonical.GetNode(root);
        Assert.Equal(CanonicalKind.Struct, node.Kind);
        Assert.Equal(file.RowCount, node.Length);
        Assert.Equal(0, node.FieldCount);
        Assert.Equal(0, node.DType.FieldCount);
    }

    private static bool HasUnknown(LayoutNode node)
    {
        if (node.Encoding == LayoutEncodingId.Unknown)
        {
            return true;
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            if (HasUnknown(node.GetChild(i)))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task ProjectingTheUnknownLayoutThrowsNamingItAndItsKind()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/experimental_list_layout");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await LayoutExecutor.ReadAsync(
                file, tree, context, new RowRange(0, file.RowCount), FieldMask.All));

        Assert.Equal("vortex.list", error.ComponentId);
        Assert.Equal(VortexComponentKind.Layout, error.Kind);
        Assert.Contains("vortex.list", error.Message, StringComparison.Ordinal);
        Assert.Contains("layout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownArrayEncodingOnAnUnprojectedFieldDoesNotThrow()
    {
        // {ints=i64, strs=utf8}; `strs` is stored with vortex.fsst, which Phase 1 does not decode.
        // Projecting `ints` alone must succeed, and the batch's schema must hold only that field.
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/editions_disabled");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        FieldMask ints = new FieldMaskBuilder().IncludeField(0).Build();
        int root = await LayoutExecutor.ReadAsync(file, tree, context, new RowRange(0, file.RowCount), ints);

        CanonicalNode node = context.Canonical.GetNode(root);
        Assert.Equal(CanonicalKind.Struct, node.Kind);
        Assert.Equal(1, node.FieldCount);
        Assert.Equal(file.RowCount, node.Length);

        DType projected = node.DType;
        Assert.Equal(1, projected.FieldCount);
        Assert.Equal("ints", projected.GetFieldName(0));
        Assert.Equal(file.Schema.GetField(0), projected.GetField(0));
    }

    [Fact]
    public async Task ProjectingTheUnknownArrayEncodingThrowsNamingIt()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/editions_disabled");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        FieldMask strs = new FieldMaskBuilder().IncludeField(1).Build();

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await LayoutExecutor.ReadAsync(
                file, tree, context, new RowRange(0, file.RowCount), strs));

        Assert.Equal(VortexComponentKind.Array, error.Kind);
        Assert.Equal("vortex.fsst", error.ComponentId);
    }

    [Fact]
    public async Task AProjectedFieldReadsTheSameValuesAsTheWholeRead()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/uncompressed_canonical");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        RowRange all = new RowRange(0, file.RowCount);

        byte[] whole;
        byte[] field1FromWhole;
        try
        {
            int root = await LayoutExecutor.ReadAsync(file, tree, context, all, FieldMask.All);
            CanonicalNode node = context.Canonical.GetNode(root);
            Assert.Equal(2, node.FieldCount);
            whole = CanonicalDigest.Of(context, node.GetFieldIndex(0));
            field1FromWhole = CanonicalDigest.Of(context, node.GetFieldIndex(1));
        }
        finally
        {
            context.ResetBatch();
        }

        foreach (int field in new[] { 0, 1 })
        {
            try
            {
                FieldMask mask = new FieldMaskBuilder().IncludeField(field).Build();
                int root = await LayoutExecutor.ReadAsync(file, tree, context, all, mask);
                CanonicalNode node = context.Canonical.GetNode(root);

                Assert.Equal(1, node.FieldCount);
                Assert.Equal(1, node.DType.FieldCount);
                Assert.Equal(file.Schema.GetFieldName(field), node.DType.GetFieldName(0));

                byte[] projected = CanonicalDigest.Of(context, node.GetFieldIndex(0));
                Assert.True(
                    (field == 0 ? whole : field1FromWhole).AsSpan().SequenceEqual(projected),
                    $"projecting field {field} changed its values.");
            }
            finally
            {
                context.ResetBatch();
            }
        }
    }

    [Fact]
    public async Task AnEmptyProjectionStillReportsTheRowCount()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/uncompressed_canonical");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        int root = await LayoutExecutor.ReadAsync(
            file, tree, context, new RowRange(0, file.RowCount), FieldMask.Empty);

        CanonicalNode node = context.Canonical.GetNode(root);
        Assert.Equal(file.RowCount, node.Length);
        Assert.Equal(0, node.FieldCount);
    }

    [Fact]
    public async Task OpeningAndParsingAFileWithAnUnknownLayoutNeverThrows()
    {
        // Contract §2.3: opening a file never throws for an unknown component, and parsing the
        // layout tree never throws for an unknown layout id.
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/experimental_list_layout");
        LayoutTree tree = LayoutTree.Parse(file);
        Assert.True(tree.NodeCount > 0);
    }
}
