// Real metadata payloads, copied out of the `metadata_b64` fields of the golden corpus sidecars in
// tests/Vorticity.Conformance/corpus. Every one was written by Vortex 0.86.1, so a byte-exact
// re-serialization is the strongest available statement that this codec agrees with upstream on
// both the tag numbers and the implicit/explicit presence rules.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class CorpusMetadataTests
{
    private delegate void BodyWriter(ref ProtoWriter writer);

    private static byte[] Serialize(BodyWriter write)
    {
        ProtoWriter writer = new ProtoWriter(64);
        try
        {
            write(ref writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void AssertExact(string base64, BodyWriter write)
    {
        byte[] expected = Convert.FromBase64String(base64);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(Serialize(write)));
    }

    // ------------------------------------------------------------------ vortex.bool

    [Theory]
    [InlineData("", 0u)]                        // encodings/dict_nullable_codes_r1023
    [InlineData("CAM=", 3u)]                    // encodings/bool_bit_offset3_r1025
    [InlineData("CAU=", 5u)]                    // encodings/bool_bit_offset_straddle_r1
    [InlineData("CAc=", 7u)]                    // encodings/bool_bit_offset7 — the only offset 7
    public void BoolCorpus(string base64, uint expectedOffset)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        BoolMetadata value = BoolMetadata.Read(metadata);
        Assert.Equal(expectedOffset, value.Offset);
        AssertExact(base64, (ref ProtoWriter w) => BoolMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.decimal

    [Theory]
    [InlineData("", DecimalStorageType.I8)]     // types/decimal2_1_nullable_r1024
    [InlineData("CAE=", DecimalStorageType.I16)]
    [InlineData("CAI=", DecimalStorageType.I32)]
    [InlineData("CAM=", DecimalStorageType.I64)]
    [InlineData("CAQ=", DecimalStorageType.I128)]
    [InlineData("CAU=", DecimalStorageType.I256)]   // types/decimal40_10_nonnull_r1
    public void DecimalCorpus(string base64, DecimalStorageType expected)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        DecimalMetadata value = DecimalMetadata.Read(metadata);
        Assert.Equal(expected, value.ValuesType);
        AssertExact(base64, (ref ProtoWriter w) => DecimalMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.dict

    [Theory]
    [InlineData("CAUQARgBIAA=", 5u, PType.U16, true, false)]   // encodings/dict_nullable_codes_r1023
    [InlineData("CAUQAxgAIAA=", 5u, PType.U64, false, false)]  // encodings/dict_u64_codes_r1023
    [InlineData("CAUQAhgAIAA=", 5u, PType.U32, false, false)]  // encodings/dict
    [InlineData("CMgBGAAgAA==", 200u, PType.U8, false, false)] // encodings/dict_u8_codes
    [InlineData("CIkBGAAgAQ==", 137u, PType.U8, false, true)]  // types/utf8_nonnull_r1025
    public void DictCorpus(string base64, uint valuesLength, PType codes, bool nullableCodes, bool allReferenced)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        DictMetadata value = DictMetadata.Read(metadata);
        Assert.Equal(valuesLength, value.ValuesLength);
        Assert.Equal(codes, value.CodesPType);
        Assert.Equal(nullableCodes, value.IsNullableCodes);
        Assert.Equal(allReferenced, value.AllValuesReferenced);
        AssertExact(base64, (ref ProtoWriter w) => DictMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.list / listview / varbin

    [Theory]
    [InlineData("CIAMEAY=", 1536UL, PType.I32)]   // encodings/list_r1025
    [InlineData("CP0LEAY=", 1533UL, PType.I32)]   // encodings/list_r1023
    [InlineData("CIAwEAY=", 6144UL, PType.I32)]   // encodings/list
    [InlineData("EAY=", 0UL, PType.I32)]          // encodings/list_r1
    [InlineData("CKwFEAE=", 684UL, PType.U16)]    // types/struct_nested_deep_nonnull_r1025
    [InlineData("", 0UL, PType.U8)]               // types/list_i32_nullable_r1
    public void ListCorpus(string base64, ulong elementsLength, PType offsets)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        ListMetadata value = ListMetadata.Read(metadata);
        Assert.Equal(elementsLength, value.ElementsLength);
        Assert.Equal(offsets, value.OffsetPType);
        AssertExact(base64, (ref ProtoWriter w) => ListMetadata.Write(ref w, in value));
    }

    [Theory]
    [InlineData("CIAwEAMYAw==", 6144UL, PType.U64, PType.U64)]  // encodings/listview
    [InlineData("CIAMEAMYAw==", 1536UL, PType.U64, PType.U64)]  // encodings/listview_r1025
    [InlineData("EAMYAw==", 0UL, PType.U64, PType.U64)]         // encodings/map_r1
    [InlineData("CP8fEAMYAw==", 4095UL, PType.U64, PType.U64)]  // encodings/map
    public void ListViewCorpus(string base64, ulong elementsLength, PType offsets, PType sizes)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        ListViewMetadata value = ListViewMetadata.Read(metadata);
        Assert.Equal(elementsLength, value.ElementsLength);
        Assert.Equal(offsets, value.OffsetPType);
        Assert.Equal(sizes, value.SizePType);
        AssertExact(base64, (ref ProtoWriter w) => ListViewMetadata.Write(ref w, in value));
    }

    [Theory]
    [InlineData("CAI=", PType.U32)]   // encodings/parquet_variant_r1025
    [InlineData("CAE=", PType.U16)]   // types/binary_nonnull_r1024
    [InlineData("", PType.U8)]        // types/binary_nonnull_r8193
    public void VarBinCorpus(string base64, PType offsets)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        VarBinMetadata value = VarBinMetadata.Read(metadata);
        Assert.Equal(offsets, value.OffsetsPType);
        AssertExact(base64, (ref ProtoWriter w) => VarBinMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.runend

    [Theory]
    [InlineData("CAEQEQ==", PType.U16, 17UL)]  // encodings/runend_r1025
    [InlineData("CAEQQA==", PType.U16, 64UL)]  // encodings/runend
    [InlineData("EAE=", PType.U8, 1UL)]        // encodings/runend_r1
    [InlineData("EAM=", PType.U8, 3UL)]        // distributions/huge_string_r16
    public void RunEndCorpus(string base64, PType ends, ulong runs)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        RunEndMetadata value = RunEndMetadata.Read(metadata);
        Assert.Equal(ends, value.EndsPType);
        Assert.Equal(runs, value.NumRuns);
        Assert.Equal(0UL, value.Offset);
        AssertExact(base64, (ref ProtoWriter w) => RunEndMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ fastlanes.bitpacked

    [Theory]
    [InlineData("CAo=", 10u)]     // encodings/fastlanes_bitpacked_r1023
    [InlineData("CAE=", 1u)]      // encodings/alprd_r1
    [InlineData("CDA=", 48u)]     // encodings/alprd_r1
    [InlineData("CDM=", 51u)]     // encodings/alprd
    [InlineData("CAQ=", 4u)]      // types/utf8_nonnull_r1025
    public void BitPackedCorpusWithoutPatches(string base64, uint bitWidth)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        BitPackedMetadata value = BitPackedMetadata.Read(metadata);
        Assert.Equal(bitWidth, value.BitWidth);
        Assert.Equal(0u, value.Offset);
        Assert.False(value.HasPatches);
        AssertExact(base64, (ref ProtoWriter w) => BitPackedMetadata.Write(ref w, in value));
    }

    [Fact]
    public void BitPackedCorpusWithPatchesButNoChunkOffsets()
    {
        // encodings/fastlanes_bitpacked_patched_no_chunk_offsets, nchildren = 2 (indices, values).
        const string Base64 = "CAoaBAgIGAM=";
        byte[] metadata = Convert.FromBase64String(Base64);
        BitPackedMetadata value = BitPackedMetadata.Read(metadata);
        Assert.Equal(10u, value.BitWidth);
        Assert.True(value.HasPatches);
        Assert.Equal(8UL, value.Patches.Length);
        Assert.Equal(PType.U64, value.Patches.IndicesPType);
        Assert.False(value.Patches.HasChunkOffsets);
        AssertExact(Base64, (ref ProtoWriter w) => BitPackedMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ fastlanes.rle

    [Theory]
    [InlineData("CAEQgAgYASABKAM=", 1UL, 1024UL, PType.U16, 1UL, PType.U64)]   // encodings/fastlanes_rle_r1
    [InlineData("CIACEIAgGAEgBCgD", 256UL, 4096UL, PType.U16, 4UL, PType.U64)] // encodings/fastlanes_rle
    [InlineData("CEAQgAgYASABKAM=", 64UL, 1024UL, PType.U16, 1UL, PType.U64)]  // encodings/fastlanes_rle_r1023
    [InlineData("CEEQgBAYASACKAM=", 65UL, 2048UL, PType.U16, 2UL, PType.U64)]  // encodings/fastlanes_rle_r1025
    public void RleCorpus(
        string base64, ulong values, ulong indices, PType indicesPType, ulong offsetsLength, PType offsetsPType)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        RleMetadata value = RleMetadata.Read(metadata);
        Assert.Equal(values, value.ValuesLength);
        Assert.Equal(indices, value.IndicesLength);
        Assert.Equal(indicesPType, value.IndicesPType);
        Assert.Equal(offsetsLength, value.ValuesIdxOffsetsLength);
        Assert.Equal(offsetsPType, value.ValuesIdxOffsetsPType);
        Assert.Equal(0UL, value.Offset);
        AssertExact(base64, (ref ProtoWriter w) => RleMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.sparse

    [Theory]
    [InlineData("CgQIQBgD", 64UL, PType.U64)]  // encodings/sparse
    [InlineData("CgQIDxgD", 15UL, PType.U64)]  // encodings/sparse_r1023
    [InlineData("CgQIEBgD", 16UL, PType.U64)]  // encodings/sparse_r1025
    [InlineData("CgQIYhgB", 98UL, PType.U16)]  // containers/zoned_many_zones_nulls
    public void SparseCorpus(string base64, ulong patchCount, PType indices)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        SparseMetadata value = SparseMetadata.Read(metadata);
        Assert.Equal(patchCount, value.Patches.Length);
        Assert.Equal(indices, value.Patches.IndicesPType);
        Assert.False(value.Patches.HasChunkOffsets);
        AssertExact(base64, (ref ProtoWriter w) => SparseMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.sequence

    [Theory]
    [InlineData("CgMY0A8SAhgO", 1000L, 7L)]    // encodings/sequence_r1023
    [InlineData("CgIYABICGA4=", 0L, 7L)]       // types/time_us_nonnull_r8193
    [InlineData("CgIYABICGAI=", 0L, 1L)]       // types/struct_nested_deep_nonnull_r1025
    [InlineData("CgIYDRICGAI=", -7L, 1L)]      // types/struct_flat_nonnull_r1024
    public void SequenceCorpusInt64(string base64, long expectedBase, long expectedMultiplier)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        SequenceMetadata value = SequenceMetadata.Read(metadata, new ScalarStore(), new DTypeArena());
        Assert.Equal(expectedBase, value.Base.AsInt64);
        Assert.Equal(expectedMultiplier, value.Multiplier.AsInt64);
        AssertExact(base64, (ref ProtoWriter w) => SequenceMetadata.Write(ref w, in value));
    }

    [Fact]
    public void SequenceCorpusUnsignedBase()
    {
        // types/map_utf8_i64_nonnull_r8191: base is uint64_value (tag 4), multiplier is int64_value.
        const string Base64 = "CgIgABICGAY=";
        byte[] metadata = Convert.FromBase64String(Base64);
        SequenceMetadata value = SequenceMetadata.Read(metadata, new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.UInt64, value.Base.Kind);
        Assert.Equal(0UL, value.Base.AsUInt64);
        Assert.Equal(3L, value.Multiplier.AsInt64);
        AssertExact(Base64, (ref ProtoWriter w) => SequenceMetadata.Write(ref w, in value));
    }

    [Fact]
    public void SequenceCorpusWideBase()
    {
        // types/timestamp_ns_tz_nonnull_r8193: a ten-byte zigzag base.
        const string Base64 = "CgoYgIDQ4sa/zpcvEgQYgol6";
        byte[] metadata = Convert.FromBase64String(Base64);
        SequenceMetadata value = SequenceMetadata.Read(metadata, new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.Int64, value.Base.Kind);
        AssertExact(Base64, (ref ProtoWriter w) => SequenceMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.alp / alprd

    [Fact]
    public void AlpCorpusWithChunkedPatches()
    {
        // encodings/alp_r1023, nchildren = 4 (encoded, indices, values, chunk offsets).
        const string Base64 = "CAkQBxoLCLYCGAMgASgDMAA=";
        byte[] metadata = Convert.FromBase64String(Base64);
        AlpMetadata value = AlpMetadata.Read(metadata);
        Assert.Equal(9u, value.ExponentE);
        Assert.Equal(7u, value.ExponentF);
        Assert.True(value.HasPatches);
        Assert.Equal(310UL, value.Patches.Length);
        Assert.Equal(PType.U64, value.Patches.IndicesPType);
        Assert.True(value.Patches.HasChunkOffsets);
        Assert.Equal(1UL, value.Patches.ChunkOffsetsLength);
        Assert.Equal(PType.U64, value.Patches.ChunkOffsetsPType);
        Assert.True(value.Patches.HasOffsetWithinChunk);
        Assert.Equal(0UL, value.Patches.OffsetWithinChunk);
        AssertExact(Base64, (ref ProtoWriter w) => AlpMetadata.Write(ref w, in value));
    }

    [Theory]
    [InlineData("", 0u, 0u, false)]                 // encodings/alp_no_patches
    [InlineData("GgQICBgD", 0u, 0u, true)]          // encodings/alp_patched_no_chunk_offsets
    [InlineData("CBEQDw==", 17u, 15u, false)]       // encodings/alp_r1
    public void AlpCorpus(string base64, uint exponentE, uint exponentF, bool hasPatches)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        AlpMetadata value = AlpMetadata.Read(metadata);
        Assert.Equal(exponentE, value.ExponentE);
        Assert.Equal(exponentF, value.ExponentF);
        Assert.Equal(hasPatches, value.HasPatches);
        AssertExact(base64, (ref ProtoWriter w) => AlpMetadata.Write(ref w, in value));
    }

    [Theory]
    [InlineData("CDAQARoC438gAQ==", 48u, 1u, PType.U16)]    // encodings/alprd_r1
    [InlineData("CDMQAhoE/A/9DyAB", 51u, 2u, PType.U16)]    // encodings/alprd
    [InlineData("CDQQAhoE/gf/ByAB", 52u, 2u, PType.U16)]    // distributions/denormal_heavy_f64_r8193
    public void AlpRdCorpus(string base64, uint rightBitWidth, uint dictionaryLength, PType leftParts)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        Assert.Equal((int)dictionaryLength, AlpRdMetadata.CountDictionaryEntries(metadata));

        Span<uint> dictionary = stackalloc uint[AlpRdMetadata.TypicalDictionaryLength];
        AlpRdMetadata value = AlpRdMetadata.Read(metadata, dictionary);
        Assert.Equal(rightBitWidth, value.RightBitWidth);
        Assert.Equal(dictionaryLength, value.DictionaryLength);
        Assert.Equal((int)dictionaryLength, value.DictionaryEntryCount);
        Assert.Equal(leftParts, value.LeftPartsPType);
        Assert.False(value.HasPatches);

        uint[] entries = dictionary[..value.DictionaryEntryCount].ToArray();
        AssertExact(base64, (ref ProtoWriter w) => AlpRdMetadata.Write(ref w, in value, entries));
    }

    // ------------------------------------------------------------------ vortex.fsst / onpair

    [Theory]
    [InlineData("CAYQBg==", PType.I32, PType.I32)]  // encodings/fsst
    [InlineData("EAE=", PType.U8, PType.U16)]       // types/map_utf8_i64_nonnull_r8191
    [InlineData("", PType.U8, PType.U8)]            // types/struct_flat_nonnull_r8193
    [InlineData("EAI=", PType.U8, PType.U32)]       // distributions/high_cardinality_utf8_r8193
    public void FsstCorpus(string base64, PType lengths, PType offsets)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        FsstMetadata value = FsstMetadata.Read(metadata);
        Assert.Equal(lengths, value.UncompressedLengthsPType);
        Assert.Equal(offsets, value.CodesOffsetsPType);
        AssertExact(base64, (ref ProtoWriter w) => FsstMetadata.Write(ref w, in value));
    }

    [Theory]
    [InlineData("CAIYvAIg9RAoAjABOAI=", PType.U32, 316u, 2165UL)]   // encodings/onpair_r1023
    [InlineData("CAIYgAIgESgCMAE4Ag==", PType.U32, 256u, 17UL)]     // encodings/onpair_r1
    [InlineData("GPECINoRKAEwATgB", PType.U8, 369u, 2266UL)]        // types/utf8_nonnull_r1025
    public void OnPairCorpus(string base64, PType lengths, uint dictionarySize, ulong codesLength)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        OnPairMetadata value = OnPairMetadata.Read(metadata);
        Assert.Equal(lengths, value.UncompressedLengthsPType);
        Assert.Equal(dictionarySize, value.DictionarySize);
        Assert.Equal(codesLength, value.CodesLength);
        AssertExact(base64, (ref ProtoWriter w) => OnPairMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ datetimeparts / decimal byte parts

    [Theory]
    [InlineData("CAcQBhgG", PType.I64, PType.I32, PType.I32)]  // encodings/datetimeparts
    [InlineData("CAEQAhgB", PType.U16, PType.U32, PType.U16)]  // types/timestamp_ms_nullable_r8193
    public void DateTimePartsCorpus(string base64, PType days, PType seconds, PType subseconds)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        DateTimePartsMetadata value = DateTimePartsMetadata.Read(metadata);
        Assert.Equal(days, value.DaysPType);
        Assert.Equal(seconds, value.SecondsPType);
        Assert.Equal(subseconds, value.SubsecondsPType);
        AssertExact(base64, (ref ProtoWriter w) => DateTimePartsMetadata.Write(ref w, in value));
    }

    [Theory]
    [InlineData("CAc=", PType.I64)]  // encodings/decimal_byte_parts_r1
    [InlineData("CAY=", PType.I32)]  // types/decimal9_2_nonnull_r8191
    [InlineData("CAU=", PType.I16)]  // types/decimal4_2_nonnull_r8192
    public void DecimalBytePartsCorpus(string base64, PType zerothChild)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        DecimalBytePartsMetadata value = DecimalBytePartsMetadata.Read(metadata);
        Assert.Equal(zerothChild, value.ZerothChildPType);
        AssertExact(base64, (ref ProtoWriter w) => DecimalBytePartsMetadata.Write(ref w, in value));
    }

    // ------------------------------------------------------------------ vortex.zstd

    [Theory]
    [InlineData("EgcI4P4BEIAIEgQIHxAB", 2)]   // encodings/zstd_r1025
    [InlineData("EgcIwP4BEP8H", 1)]           // encodings/zstd_r1023
    [InlineData("EgQIHxAB", 1)]               // encodings/zstd_r1
    [InlineData("EgcI4P4BEIAIEgcI4P4BEIAIEgcI4P4BEIAIEgcI4P4BEIAI", 4)]  // encodings/zstd
    public void ZstdCorpus(string base64, int frameCount)
    {
        byte[] metadata = Convert.FromBase64String(base64);
        Assert.Equal(frameCount, ZstdMetadata.CountFrames(metadata));

        Span<ZstdFrameMetadata> frames = stackalloc ZstdFrameMetadata[8];
        ZstdMetadata value = ZstdMetadata.Read(metadata, frames);
        Assert.Equal(0u, value.DictionarySize);
        Assert.Equal(frameCount, value.FrameCount);

        ZstdFrameMetadata[] copy = frames[..value.FrameCount].ToArray();
        AssertExact(base64, (ref ProtoWriter w) => ZstdMetadata.Write(ref w, in value, copy));
    }

    [Fact]
    public void ZstdCorpusFrameValues()
    {
        // encodings/zstd_r1025: a 32608-byte frame of 1024 values followed by a 31-byte frame of 1.
        byte[] metadata = Convert.FromBase64String("EgcI4P4BEIAIEgQIHxAB");
        Span<ZstdFrameMetadata> frames = stackalloc ZstdFrameMetadata[2];
        ZstdMetadata value = ZstdMetadata.Read(metadata, frames);
        Assert.Equal(2, value.FrameCount);
        Assert.Equal(32608UL, frames[0].UncompressedSize);
        Assert.Equal(1024UL, frames[0].ValueCount);
        Assert.Equal(31UL, frames[1].UncompressedSize);
        Assert.Equal(1UL, frames[1].ValueCount);
    }

    // ------------------------------------------------------------------ empty-metadata encodings

    [Theory]
    [InlineData("vortex.null")]
    [InlineData("vortex.primitive")]
    [InlineData("vortex.varbinview")]
    [InlineData("vortex.struct")]
    [InlineData("vortex.chunked")]
    [InlineData("vortex.masked")]
    [InlineData("vortex.fixed_size_list")]
    [InlineData("vortex.ext")]
    [InlineData("vortex.bytebool")]
    [InlineData("vortex.zigzag")]
    public void EmptyMetadataEncodingsAreEmptyInTheCorpus(string encodingId)
    {
        // Every corpus node with one of these ids reports metadata_len = 0.
        EncodingMetadata.RequireEmpty(Array.Empty<byte>(), encodingId);
    }
}
