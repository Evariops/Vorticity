// The invariants the readers owe their caller, rather than the values they produce.
//
//   * REGISTER THEN EXECUTE. RegisterSegments must register exactly the segments Execute reads, and
//     Execute must read only registered ones. Break it and either the scan throws "segment not
//     populated" or it silently issues a second read and the scan's I/O-count test fails.
//   * The table holds six readers and each one's IdUtf8 resolves back to its own slot.
//   * The tree is immutable after Parse, so concurrent scans over one file agree.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LayoutReaderContractTests
{
    [Fact]
    public void EveryLayoutEncodingHasAReaderWhoseIdResolvesBack()
    {
        LayoutEncodingId[] all =
        [
            LayoutEncodingId.Flat,
            LayoutEncodingId.Chunked,
            LayoutEncodingId.Struct,
            LayoutEncodingId.Dict,
            LayoutEncodingId.Zoned,
            LayoutEncodingId.Stats,
        ];

        foreach (LayoutEncodingId id in all)
        {
            Assert.True(LayoutReaderTable.IsImplemented(id), $"{id} has no reader.");
            LayoutReader reader = LayoutReaderTable.Get(id, id.ToString());
            Assert.Equal(id, reader.EncodingId);
            Assert.Equal(id, EncodingRegistry.ResolveLayout(reader.IdUtf8));
        }

        Assert.False(LayoutReaderTable.IsImplemented(LayoutEncodingId.Unknown));
    }

    [Fact]
    public void GetOnAnUnknownLayoutThrowsWithItsIdAndKind()
    {
        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => LayoutReaderTable.Get(LayoutEncodingId.Unknown, "acme.custom"));

        Assert.Equal("acme.custom", error.ComponentId);
        Assert.Equal(VortexComponentKind.Layout, error.Kind);
    }

    [Theory]
    [InlineData("containers/uncompressed_canonical")]
    [InlineData("containers/chunked_stream_3")]
    [InlineData("distributions/all_null_i64_r1024")]
    [InlineData("types/struct_field_names")]
    public async Task ExecuteReadsOnlyTheSegmentsRegisterAskedFor(string id)
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync(id);
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        LayoutNode root = tree.Root;
        LayoutReader reader = LayoutReaderTable.Get(root.Encoding, root.EncodingIdText);
        RowRange rows = new RowRange(0, file.RowCount);
        FieldMask all = FieldMask.All;

        reader.RegisterSegments(in root, rows, in all, context.Segments);
        int registered = context.Segments.Count;

        await file.Segments.ReadManyAsync(context.Segments, TestContext.Current.CancellationToken);
        reader.Execute(in root, rows, in all, context);

        // A new segment after Complete() would have thrown; this catches the subtler case of a
        // reader that registers MORE than it reads, which wastes I/O silently.
        Assert.Equal(registered, context.Segments.Count);

        HashSet<int> used = new HashSet<int>();
        for (int slot = 0; slot < context.Segments.Count; slot++)
        {
            Assert.True(context.Segments.IsFilled(slot));
            used.Add(slot);
        }

        Assert.Equal(registered, used.Count);
    }

    [Fact]
    public async Task ProjectingFewerFieldsRegistersFewerSegments()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/uncompressed_canonical");
        LayoutTree tree = LayoutTree.Parse(file);

        int whole = await RegisteredCountAsync(file, tree, FieldMask.All);
        int oneField = await RegisteredCountAsync(file, tree, new FieldMaskBuilder().IncludeField(0).Build());
        int noField = await RegisteredCountAsync(file, tree, FieldMask.Empty);

        Assert.True(oneField < whole, $"one field registered {oneField} segments, all registered {whole}.");
        Assert.True(noField < oneField, $"no field registered {noField} segments, one field {oneField}.");
    }

    [Fact]
    public async Task TwoConcurrentScansOfOneFileAgree()
    {
        // VortexFile is thread-safe and so is a parsed LayoutTree; a ScanContext is not, so each
        // scan gets its own. This is the test that the projected-struct
        // dtypes really are built in per-scan arenas.
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/uncompressed_canonical");
        LayoutTree tree = LayoutTree.Parse(file);

        Task<byte[]> first = Task.Run(() => ScanAsync(file, tree, new FieldMaskBuilder().IncludeField(0).Build()));
        Task<byte[]> second = Task.Run(() => ScanAsync(file, tree, new FieldMaskBuilder().IncludeField(0).Build()));
        Task<byte[]> third = Task.Run(() => ScanAsync(file, tree, FieldMask.All));

        byte[][] results = await Task.WhenAll(first, second, third);
        Assert.True(results[0].AsSpan().SequenceEqual(results[1]), "two identical concurrent scans disagreed.");
        Assert.True(results[2].Length > results[0].Length, "the full scan read no more than the projected one.");
    }

    private static async Task<byte[]> ScanAsync(VortexFile file, LayoutTree tree, FieldMask fields)
    {
        using ScanContext context = new ScanContext(file);
        int root = await LayoutExecutor.ReadAsync(file, tree, context, new RowRange(0, file.RowCount), fields);
        return CanonicalDigest.Of(context, root);
    }

    private static async ValueTask<int> RegisteredCountAsync(VortexFile file, LayoutTree tree, FieldMask fields)
    {
        using ScanContext context = new ScanContext(file);
        LayoutNode root = tree.Root;
        LayoutReader reader = LayoutReaderTable.Get(root.Encoding, root.EncodingIdText);
        reader.RegisterSegments(in root, new RowRange(0, file.RowCount), in fields, context.Segments);

        int count = context.Segments.Count;
        await file.Segments.ReadManyAsync(context.Segments, default);
        return count;
    }
}
