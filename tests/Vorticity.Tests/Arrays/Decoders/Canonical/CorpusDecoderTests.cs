// The oracle. Every one of these files was written by Vortex 0.86.1 and carries a JSONL sidecar of
// expected values; agreeing with our own encoder would prove nothing.
//
// The set is exactly the corpus entries whose root layout is a single `vortex.flat` leaf, so the
// blob is reachable without a layout reader. Each row count the corpus uses - 1, 1023, 1025 and the
// 4096 default - appears here, which covers the FastLanes block (1024) and row block (8192)
// boundaries.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class CorpusDecoderTests
{
    public static TheoryData<string> Entries()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (string entry in CorpusEntries.All)
        {
            data.Add(entry);
        }

        return data;
    }

    [Fact]
    public void TheListIsEverySingleFlatLeafEntryOfTheManifest()
    {
        SortedSet<string> expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (CorpusEntry entry in CorpusManifest.All)
        {
            if (entry.LayoutIds is ["vortex.flat"])
            {
                expected.Add(entry.Id);
            }
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected, new SortedSet<string>(CorpusEntries.All, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task DecodesEveryRowAsTheSidecarSays(string entry)
    {
        Sidecar? sidecar = Sidecar.TryLoad(entry);
        Assert.NotNull(sidecar);

        await using DecodedCorpusFile file = await DecodedCorpusFile.OpenAsync(entry, sidecar);

        Assert.Equal(sidecar.RowCount, file.RowCount);
        Assert.Equal(file.RowCount, sidecar.Rows.Count);

        for (int row = 0; row < file.RowCount; row++)
        {
            JsonElement expected = sidecar.Rows[row];
            CanonicalValueAssert.AssertRow(file.Scan, file.RootIndex, file.Schema, row, expected);
        }
    }

    [Fact]
    public async Task AllNullColumnCollapsesToAllInvalid()
    {
        // containers/all_null_i64_explicit_validity_r1025 is one of the two corpus files that carry
        // a materialized all-zero validity child rather than a folded vortex.constant node. It
        // must still report AllInvalid, not Bitmap.
        const string Entry = "containers/all_null_i64_explicit_validity_r1025";
        Sidecar? sidecar = Sidecar.TryLoad(Entry);
        Assert.NotNull(sidecar);

        await using DecodedCorpusFile file = await DecodedCorpusFile.OpenAsync(Entry, sidecar);

        CanonicalNode root = file.Scan.Canonical.GetNode(file.RootIndex);
        Assert.Equal(CanonicalKind.Struct, root.Kind);

        CanonicalNode field = file.Scan.Canonical.GetNode(root.GetFieldIndex(0));
        Assert.Equal(ValidityKind.AllInvalid, field.Validity.Kind);
    }

    [Theory]
    [InlineData("encodings/bool_bit_offset3")]
    [InlineData("encodings/bool_bit_offset7")]
    [InlineData("encodings/bool_bit_offset_straddle")]
    public async Task BoolBitOffsetSurvivesIntoTheCanonicalNode(string entry)
    {
        Sidecar? sidecar = Sidecar.TryLoad(entry);
        Assert.NotNull(sidecar);

        await using DecodedCorpusFile file = await DecodedCorpusFile.OpenAsync(entry, sidecar);

        CanonicalNode root = file.Scan.Canonical.GetNode(file.RootIndex);
        Assert.Equal(CanonicalKind.Bool, root.Kind);

        // The bitmap is never shifted: the offset travels with it, so a non-zero offset here is
        // the whole point of the fixture.
        Assert.InRange(root.BitOffset, 0, 7);
        Assert.NotEqual(0, root.BitOffset);
    }

    [Fact]
    public async Task ConstantNodeCarriesExactlyOneBufferAndNoMetadata()
    {
        // The scalar is buffer 0 and the metadata is empty. The sidecar records metadata_len 0 for
        // every constant node in the corpus; this asserts the shape we decode.
        const string Entry = "encodings/constant";
        Sidecar? sidecar = Sidecar.TryLoad(Entry);
        Assert.NotNull(sidecar);

        await using DecodedCorpusFile file = await DecodedCorpusFile.OpenAsync(Entry, sidecar);

        ArrayNode node = file.Scan.Nodes.Root;
        Assert.Equal(ArrayEncodingId.Constant, node.Encoding);
        Assert.Equal(1, node.BufferCount);
        Assert.True(node.Metadata.IsEmpty);
        Assert.Equal(0, node.ChildCount);
    }

    [Fact]
    public async Task VarBinAndVarBinViewAgreeOnTheSameValues()
    {
        // encodings/varbin and encodings/varbinview hold the same utf8 column in the two shapes;
        // both must canonicalize to VarBinView and produce identical values.
        Sidecar? a = Sidecar.TryLoad("encodings/varbin");
        Sidecar? b = Sidecar.TryLoad("encodings/varbinview");
        Assert.NotNull(a);
        Assert.NotNull(b);

        await using DecodedCorpusFile varbin = await DecodedCorpusFile.OpenAsync("encodings/varbin", a);
        await using DecodedCorpusFile view = await DecodedCorpusFile.OpenAsync("encodings/varbinview", b);

        Assert.Equal(
            CanonicalKind.VarBinView,
            varbin.Scan.Canonical.GetNode(varbin.RootIndex).Kind);
        Assert.Equal(
            CanonicalKind.VarBinView,
            view.Scan.Canonical.GetNode(view.RootIndex).Kind);
    }

    [Fact]
    public async Task ChunkedWithEmptyChunksConcatenatesToOneNode()
    {
        const string Entry = "encodings/chunked_empty_chunks";
        Sidecar? sidecar = Sidecar.TryLoad(Entry);
        Assert.NotNull(sidecar);

        await using DecodedCorpusFile file = await DecodedCorpusFile.OpenAsync(Entry, sidecar);

        CanonicalNode root = file.Scan.Canonical.GetNode(file.RootIndex);
        Assert.Equal(CanonicalKind.Struct, root.Kind);

        CanonicalNode column = file.Scan.Canonical.GetNode(root.GetFieldIndex(0));
        Assert.Equal(CanonicalKind.Primitive, column.Kind);
        Assert.Equal(file.RowCount, column.Length);
    }
}
