// Decoding the golden corpus, value for value against the sidecars. Agreeing with our own encoder
// proves nothing; these are real files written by Vortex 0.86.1.
//
// Every file here is a single-column `vortex.flat` layout over segment 0, so the layout reader
// (another component) is not needed: the root layout's `segments[0]` names the
// array blob directly. The `_r0` variants are `vortex.chunked` layouts with no segments at all and
// are left to the conformance component.
//
// The full value-by-value conformance sweep belongs to the conformance component; this is the
// narrow preview of it that covers the nine compressed encodings, and it is the only thing that
// proves the kernels.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class CorpusDecodeTests
{
    [Theory]
    // fastlanes.for over a bit-packed child: 75 corpus files contain a FoR node and every one of
    // them decodes to garbage if the metadata is read as empty.
    [InlineData("encodings/fastlanes_for")]
    [InlineData("encodings/fastlanes_for_r1")]
    [InlineData("encodings/fastlanes_for_r1023")]
    [InlineData("encodings/fastlanes_for_r1025")]

    // fastlanes.bitpacked, including the patched shape.
    [InlineData("encodings/fastlanes_bitpacked")]
    [InlineData("encodings/fastlanes_bitpacked_r1")]
    [InlineData("encodings/fastlanes_bitpacked_r1023")]
    [InlineData("encodings/fastlanes_bitpacked_r1025")]
    [InlineData("encodings/fastlanes_bitpacked_patched_no_chunk_offsets")]

    // fastlanes.rle: the chunked value windows.
    [InlineData("encodings/fastlanes_rle")]
    [InlineData("encodings/fastlanes_rle_r1")]
    [InlineData("encodings/fastlanes_rle_r1023")]
    [InlineData("encodings/fastlanes_rle_r1025")]

    [InlineData("encodings/zigzag")]
    [InlineData("encodings/zigzag_r1")]
    [InlineData("encodings/zigzag_r1023")]
    [InlineData("encodings/zigzag_r1025")]

    [InlineData("encodings/runend")]
    [InlineData("encodings/runend_r1")]
    [InlineData("encodings/runend_r1023")]
    [InlineData("encodings/runend_r1025")]

    [InlineData("encodings/sparse")]
    [InlineData("encodings/sparse_r1023")]
    [InlineData("encodings/sparse_r1025")]

    [InlineData("encodings/sequence")]
    [InlineData("encodings/sequence_r1")]
    [InlineData("encodings/sequence_r1023")]
    [InlineData("encodings/sequence_r1025")]

    [InlineData("encodings/bytebool")]
    [InlineData("encodings/bytebool_r1")]
    [InlineData("encodings/bytebool_r1023")]
    [InlineData("encodings/bytebool_r1025")]

    // vortex.dict over a varbinview dictionary, in every codes width and nullability the corpus
    // carries.
    [InlineData("encodings/dict")]
    [InlineData("encodings/dict_r1")]
    [InlineData("encodings/dict_r1023")]
    [InlineData("encodings/dict_r1025")]
    [InlineData("encodings/dict_u8_codes")]
    [InlineData("encodings/dict_u64_codes")]
    [InlineData("encodings/dict_nullable_codes")]
    [InlineData("encodings/dict_nullable_codes_r1")]
    [InlineData("encodings/dict_nullable_codes_r1023")]
    [InlineData("encodings/dict_nullable_codes_r1025")]
    [InlineData("encodings/dict_nullable_values_nonnull_codes")]
    [InlineData("encodings/dict_nullable_values_nonnull_codes_r1023")]
    public async Task DecodesACorpusFileValueForValue(string entryId)
    {
        string? root = Corpus.Root;
        Assert.SkipWhen(root is null, "the golden corpus is not on disk");

        string dataPath = Path.Combine(root!, entryId + ".vortex");
        string sidecarPath = Path.Combine(root!, entryId + ".jsonl");
        Assert.True(System.IO.File.Exists(dataPath), dataPath);

        TestDecoders.EnsureRegistered();

        byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(dataPath).ConfigureAwait(true);

        // 64-aligned, not merely pinned. The pinned object heap promises 8 bytes; the real segment
        // sources hand a decoder a 64-aligned base and the canonical decoders check
        // a buffer's REAL address - a varbinview views buffer at file offset 16k lands on an odd
        // multiple of 8 if the copy starts on one, and vortex.varbinview then rejects it.
        int pinnedOffset;
        byte[] pinned = GC.AllocateArray<byte>(fileBytes.Length + 64, pinned: true);
        unsafe
        {
            fixed (byte* origin = pinned)
            {
                pinnedOffset = (int)((64 - ((nuint)origin & 63)) & 63);
            }
        }

        fileBytes.CopyTo(pinned, pinnedOffset);

        VortexFile file = await VortexFile.OpenAsync(dataPath).ConfigureAwait(true);
        try
        {
            (int segmentIndex, long rowCount) = FlatLayoutOf(file);
            SegmentSpec spec = file.SegmentSpecs[segmentIndex];

            using ScanContext scan = new(file);
            VortexBuffer segment = VortexBuffer.FromPinned(
                pinned.AsSpan(pinnedOffset + (int)spec.Offset, (int)spec.Length),
                spec.AlignmentExponent);
            ArrayBlobReader.Load(scan.Nodes, segment, scan.ArrayEncodings);

            int decoded = scan.Decode.DecodeRoot(scan.Nodes.Root, file.Schema, (int)rowCount);
            CanonicalNode node = scan.Canonical.GetNode(decoded);

            Assert.Equal((int)rowCount, node.Length);
            CompareAgainstSidecar(scan, node, sidecarPath, (int)rowCount);
        }
        finally
        {
            await file.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static (int SegmentIndex, long RowCount) FlatLayoutOf(VortexFile file)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        LayoutView layout = LayoutView.Root(file.RootLayoutBytes.Span, ref budget);

        Assert.Equal(LayoutEncodingId.Flat, file.GetLayoutEncoding(layout.Encoding));
        Assert.True(layout.Metadata.IsEmpty, "this fixture has no inlined array tree");
        Assert.Single(layout.Segments.ToArray());

        return ((int)layout.Segments[0], (long)layout.RowCount);
    }

    private static void CompareAgainstSidecar(
        ScanContext scan, CanonicalNode node, string sidecarPath, int rowCount)
    {
        int compared = 0;
        foreach (string line in System.IO.File.ReadLines(sidecarPath))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement element = document.RootElement;
            if (element.GetProperty("kind").GetString() != "rows")
            {
                continue;
            }

            int from = element.GetProperty("from").GetInt32();
            JsonElement values = element.GetProperty("v");
            int index = from;
            foreach (JsonElement expected in values.EnumerateArray())
            {
                Assert.True(index < rowCount, $"sidecar row {index} is past the row count");
                AssertRow(scan, node, index, expected);
                index++;
                compared++;
            }
        }

        Assert.Equal(rowCount, compared);
    }

    private static void AssertRow(ScanContext scan, CanonicalNode node, int row, JsonElement expected)
    {
        bool valid = IsValid(scan, node, row);
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.False(valid, $"row {row} should be null");
            return;
        }

        Assert.True(valid, $"row {row} should not be null");

        switch (node.Kind)
        {
            case CanonicalKind.Bool:
            {
                int bit = node.BitOffset + row;
                bool actual = (node.Bits.Span[bit >> 3] & (1 << (bit & 7))) != 0;
                Assert.Equal(expected.GetBoolean(), actual);
                return;
            }

            case CanonicalKind.Primitive:
            {
                string actual = FormatPrimitive(node, row);
                Assert.Equal(expected.GetString(), actual);
                return;
            }

            case CanonicalKind.VarBinView:
            {
                byte[] want = Convert.FromBase64String(expected.GetProperty("b64").GetString()!);
                ReadOnlySpan<byte> got = ReadView(node, row);
                Assert.Equal(expected.GetProperty("len").GetInt32(), got.Length);
                Assert.True(want.AsSpan().SequenceEqual(got), $"row {row} bytes differ");
                return;
            }

            default:
                Assert.Fail($"row comparison for {node.Kind} is not implemented");
                return;
        }
    }

    private static bool IsValid(ScanContext scan, CanonicalNode node, int row)
    {
        switch (node.Validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return true;
            case ValidityKind.AllInvalid:
                return false;
            default:
            {
                CanonicalNode bits = scan.Canonical.GetNode(node.Validity.CanonicalNodeIndex);
                int bit = bits.BitOffset + row;
                return (bits.Bits.Span[bit >> 3] & (1 << (bit & 7))) != 0;
            }
        }
    }

    private static string FormatPrimitive(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> values = node.Values.Span;
        return node.PType switch
        {
            PType.U8 => values[row].ToString(CultureInfo.InvariantCulture),
            PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(values.Slice(row * 2, 2))
                .ToString(CultureInfo.InvariantCulture),
            PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(values.Slice(row * 4, 4))
                .ToString(CultureInfo.InvariantCulture),
            PType.U64 => BinaryPrimitives.ReadUInt64LittleEndian(values.Slice(row * 8, 8))
                .ToString(CultureInfo.InvariantCulture),
            PType.I8 => ((sbyte)values[row]).ToString(CultureInfo.InvariantCulture),
            PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(values.Slice(row * 2, 2))
                .ToString(CultureInfo.InvariantCulture),
            PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(values.Slice(row * 4, 4))
                .ToString(CultureInfo.InvariantCulture),
            PType.I64 => BinaryPrimitives.ReadInt64LittleEndian(values.Slice(row * 8, 8))
                .ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"no sidecar comparison for {node.PType}"),
        };
    }

    private static ReadOnlySpan<byte> ReadView(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (length <= 12)
        {
            return view.Slice(4, length);
        }

        int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(8, 4));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));
        return node.GetDataBuffer(buffer).Span.Slice(offset, length);
    }
}

/// <summary>Finds the checked-in golden corpus without hard-coding an absolute path.</summary>
internal static class Corpus
{
    private const string Relative = "tests/Vorticity.Conformance/corpus";

    internal static string? Root { get; } = Locate();

    private static string? Locate()
    {
        foreach (string start in new[] { SourceDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, Relative);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static string SourceDirectory([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
}
