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
using Vorticity.Tests.File;
using Vorticity.Types;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LazyResolutionTests
{
    /// <summary>The id the fixture's `strs` column is renamed to. Same length as `vortex.fsst`.</summary>
    private const string ForgedId = "vortex.zzzz";

    [Fact]
    public async Task AnUnknownLayoutOnAnUnprojectedFieldDoesNotThrow()
    {
        // A FORGED layout id, not a real one. This test used containers/experimental_list_layout
        // until `vortex.list` gained a reader, which is the fourth time an "unknown component"
        // example has expired by being implemented. `vortex.zzzzz` is registered nowhere and cannot
        // gain a reader, so the fixture outlives the coverage work.
        await using VortexFile file = await LayoutExecutor.OpenForgedAsync("negative/unknown_layout_id.vortex");
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
        await using VortexFile file = await LayoutExecutor.OpenForgedAsync("negative/unknown_layout_id.vortex");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await LayoutExecutor.ReadAsync(
                file, tree, context, new RowRange(0, file.RowCount), FieldMask.All));

        Assert.Equal("vortex.zzzzz", error.ComponentId);
        Assert.Equal(VortexComponentKind.Layout, error.Kind);
        Assert.Contains("vortex.zzzzz", error.Message, StringComparison.Ordinal);
        Assert.Contains("layout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownArrayEncodingOnAnUnprojectedFieldDoesNotThrow()
    {
        // {ints=i64, strs=utf8}, with `strs`'s encoding id renamed to one nothing resolves.
        // Projecting `ints` alone must succeed, and the batch's schema must hold only that field.
        //
        // THE HALF THAT GETS DELETED, so it is forged rather than borrowed from a real unsupported
        // encoding: when this test named vortex.fsst, landing that decoder turned it green for the
        // opposite reason -- the column became readable, and the "it was never asked for" property
        // it exists to guard went untested with nothing to say so.
        await using VortexFile file = await OpenForged();
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
        await using VortexFile file = await OpenForged();
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        FieldMask strs = new FieldMaskBuilder().IncludeField(1).Build();

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await LayoutExecutor.ReadAsync(
                file, tree, context, new RowRange(0, file.RowCount), strs));

        Assert.Equal(VortexComponentKind.Array, error.Kind);
        Assert.Equal(ForgedId, error.ComponentId);
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

    /// <summary>
    /// <c>containers/editions_disabled</c> ({ints=i64, strs=utf8}) with the encoding id of the
    /// `strs` column renamed to one no build resolves.
    /// </summary>
    private static async Task<VortexFile> OpenForged()
    {
        LayoutExecutor.EnsureDecoders();
        byte[] bytes = ForgedEncodingId.Patch(
            "containers/editions_disabled", "vortex.fsst"u8, "vortex.zzzz"u8);
        return await VortexFile.OpenAsync(
            new TestSegmentSource(bytes), VortexOpenOptions.Default, System.Threading.CancellationToken.None);
    }

    [Fact]
    public async Task OpeningAndParsingAFileWithAnUnknownLayoutNeverThrows()
    {
        // Contract §2.3: opening a file never throws for an unknown component, and parsing the
        // layout tree never throws for an unknown layout id.
        await using VortexFile file = await LayoutExecutor.OpenForgedAsync("negative/unknown_layout_id.vortex");
        LayoutTree tree = LayoutTree.Parse(file);
        Assert.True(tree.NodeCount > 0);
    }
}
