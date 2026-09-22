// Read/write round trips, with the absent-versus-present-and-default distinction asserted
// explicitly for every field that has explicit presence.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class RoundTripMetadataTests
{
    private delegate void BodyWriter(ref ProtoWriter writer);

    private static byte[] Serialize(BodyWriter write)
    {
        // A plain local plus try/finally: CS1657 forbids passing a `using` variable as `ref`.
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

    private static byte[] SerializeFlat(in FlatLayoutMetadata value)
    {
        // FlatLayoutMetadata is a ref struct, so it cannot be captured by the Serialize lambda.
        ProtoWriter writer = new ProtoWriter(64);
        try
        {
            FlatLayoutMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(7u)]
    public void BoolRoundTrips(uint offset)
    {
        BoolMetadata value = new BoolMetadata(offset);
        byte[] bytes = Serialize((ref ProtoWriter w) => BoolMetadata.Write(ref w, in value));
        Assert.Equal(value, BoolMetadata.Read(bytes));
    }

    [Fact]
    public void BoolOffsetEightIsRejectedOnReadAndOnConstruction()
    {
        Assert.Throws<VortexFormatException>(
            () => { _ = BoolMetadata.Read(new WireBuilder().VarintField(1, 8).ToArray()); });
        Assert.Throws<VortexFormatException>(
            () => { _ = BoolMetadata.Read(new WireBuilder().VarintField(1, uint.MaxValue).ToArray()); });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoolMetadata(8));
    }

    [Theory]
    [InlineData(DecimalStorageType.I8)]
    [InlineData(DecimalStorageType.I16)]
    [InlineData(DecimalStorageType.I32)]
    [InlineData(DecimalStorageType.I64)]
    [InlineData(DecimalStorageType.I128)]
    [InlineData(DecimalStorageType.I256)]
    internal void DecimalRoundTrips(DecimalStorageType storage)
    {
        DecimalMetadata value = new DecimalMetadata(storage);
        byte[] bytes = Serialize((ref ProtoWriter w) => DecimalMetadata.Write(ref w, in value));
        Assert.Equal(value, DecimalMetadata.Read(bytes));
        Assert.Equal(storage, DecimalMetadata.Read(bytes).ValuesType);
    }

    public static TheoryData<bool?, bool?> OptionalBoolPairs =>
        new TheoryData<bool?, bool?>
        {
            { null, null },
            { false, null },
            { null, false },
            { true, false },
            { false, true },
            { true, true },
        };

    [Theory]
    [MemberData(nameof(OptionalBoolPairs))]
    public void DictRoundTripsIncludingAbsentVersusFalse(bool? isNullableCodes, bool? allValuesReferenced)
    {
        DictMetadata value = new DictMetadata(5, PType.U16, isNullableCodes, allValuesReferenced);
        byte[] bytes = Serialize((ref ProtoWriter w) => DictMetadata.Write(ref w, in value));
        DictMetadata read = DictMetadata.Read(bytes);
        Assert.Equal(value, read);
        Assert.Equal(isNullableCodes, read.IsNullableCodes);
        Assert.Equal(allValuesReferenced, read.AllValuesReferenced);
    }

    [Fact]
    public void DictAbsentOptionalIsNotEqualToPresentAndFalse()
    {
        DictMetadata absent = new DictMetadata(5, PType.U16, null, null);
        DictMetadata presentFalse = new DictMetadata(5, PType.U16, false, false);
        Assert.NotEqual(absent, presentFalse);

        byte[] absentBytes = Serialize((ref ProtoWriter w) => DictMetadata.Write(ref w, in absent));
        byte[] falseBytes = Serialize((ref ProtoWriter w) => DictMetadata.Write(ref w, in presentFalse));
        Assert.NotEqual(absentBytes.Length, falseBytes.Length);
        Assert.Null(DictMetadata.Read(absentBytes).IsNullableCodes);
        Assert.False(DictMetadata.Read(falseBytes).IsNullableCodes);
    }

    [Theory]
    [MemberData(nameof(OptionalBoolPairs))]
    public void DictLayoutRoundTripsIncludingAbsentVersusFalse(bool? isNullableCodes, bool? allValuesReferenced)
    {
        DictLayoutMetadata value = new DictLayoutMetadata(PType.U32, isNullableCodes, allValuesReferenced);
        byte[] bytes = Serialize((ref ProtoWriter w) => DictLayoutMetadata.Write(ref w, in value));
        DictLayoutMetadata read = DictLayoutMetadata.Read(bytes);
        Assert.Equal(value, read);
        Assert.Equal(isNullableCodes, read.IsNullableCodes);
        Assert.Equal(allValuesReferenced, read.AllValuesReferenced);
    }

    [Fact]
    public void PatchesRoundTripsWithoutChunkOffsets()
    {
        PatchesMetadata value = PatchesMetadata.Create(310, 7, PType.U32);
        byte[] bytes = Serialize((ref ProtoWriter w) => PatchesMetadata.WriteBody(ref w, in value));
        ProtoReader reader = new ProtoReader(bytes);
        PatchesMetadata read = PatchesMetadata.ReadBody(ref reader);
        Assert.Equal(value, read);
        Assert.False(read.HasChunkOffsets);
        Assert.False(read.HasChunkOffsetsLength);
        Assert.False(read.HasOffsetWithinChunk);
        Assert.Equal(0UL, read.ChunkOffsetsLength);
    }

    [Fact]
    public void PatchesRoundTripsWithChunkOffsetsAndZeroOffsetWithinChunk()
    {
        // offset_within_chunk = 0 present must not collapse into absent: it has explicit presence.
        PatchesMetadata value = PatchesMetadata.CreateChunked(310, 0, PType.U64, 1, PType.U64, 0);
        byte[] bytes = Serialize((ref ProtoWriter w) => PatchesMetadata.WriteBody(ref w, in value));
        ProtoReader reader = new ProtoReader(bytes);
        PatchesMetadata read = PatchesMetadata.ReadBody(ref reader);
        Assert.Equal(value, read);
        Assert.True(read.HasChunkOffsets);
        Assert.True(read.HasOffsetWithinChunk);
        Assert.Equal(0UL, read.OffsetWithinChunk);

        PatchesMetadata withoutTag6 = PatchesMetadata.CreateChunked(310, 0, PType.U64, 1, PType.U64, null);
        Assert.NotEqual(value, withoutTag6);
    }

    [Fact]
    public void PatchesRejectsSignedIndicesPType()
    {
        // Upstream refuses them too: "Patch indices must be unsigned integers".
        foreach (PType signed in new[] { PType.I8, PType.I16, PType.I32, PType.I64, PType.F32, PType.F64 })
        {
            byte[] bytes = new WireBuilder().VarintField(1, 4).VarintField(3, (ulong)signed).ToArray();
            Assert.Throws<VortexFormatException>(
                () => { _ = SparseMetadata.Read(new WireBuilder().BytesField(1, bytes).ToArray()); });
            Assert.Throws<ArgumentOutOfRangeException>(() => PatchesMetadata.Create(4, 0, signed));
        }
    }

    [Fact]
    public void PatchesChunkOffsetsPTypePresenceAloneDrivesHasChunkOffsets()
    {
        // Upstream's discriminator is chunk_offsets_dtype().is_some(), i.e. tag 5 alone, and the
        // child layout follows it. Tag 4 without tag 5 must therefore not claim chunk offsets.
        byte[] tag4Only = new WireBuilder()
            .VarintField(1, 4).VarintField(3, (ulong)PType.U32).VarintField(4, 9).ToArray();
        ProtoReader r1 = new ProtoReader(tag4Only);
        PatchesMetadata onlyLength = PatchesMetadata.ReadBody(ref r1);
        Assert.False(onlyLength.HasChunkOffsets);
        Assert.True(onlyLength.HasChunkOffsetsLength);
        Assert.Equal(9UL, onlyLength.ChunkOffsetsLength);

        byte[] tag5Only = new WireBuilder()
            .VarintField(1, 4).VarintField(3, (ulong)PType.U32).VarintField(5, (ulong)PType.U16).ToArray();
        ProtoReader r2 = new ProtoReader(tag5Only);
        PatchesMetadata onlyPType = PatchesMetadata.ReadBody(ref r2);
        Assert.True(onlyPType.HasChunkOffsets);
        Assert.False(onlyPType.HasChunkOffsetsLength);
        Assert.Equal(0UL, onlyPType.ChunkOffsetsLength);
    }

    [Fact]
    public void SparseRejectsAbsentPatches()
    {
        Assert.Throws<VortexFormatException>(() => { _ = SparseMetadata.Read(Array.Empty<byte>()); });
        Assert.Throws<VortexFormatException>(
            () => { _ = SparseMetadata.Read(new WireBuilder().VarintField(9, 1).ToArray()); });
    }

    [Fact]
    public void SparseAcceptsAnEmptyPatchesMessage()
    {
        // Present-but-empty is a legal prost `required` message: every field defaults.
        SparseMetadata value = SparseMetadata.Read(new WireBuilder().BytesField(1, Array.Empty<byte>()).ToArray());
        Assert.Equal(0UL, value.Patches.Length);
        Assert.Equal(PType.U8, value.Patches.IndicesPType);
    }

    [Fact]
    public void BitPackedRoundTripsWithAndWithoutPatches()
    {
        BitPackedMetadata bare = new BitPackedMetadata(10, 512);
        byte[] bareBytes = Serialize((ref ProtoWriter w) => BitPackedMetadata.Write(ref w, in bare));
        Assert.Equal(bare, BitPackedMetadata.Read(bareBytes));
        Assert.False(BitPackedMetadata.Read(bareBytes).HasPatches);

        PatchesMetadata patches = PatchesMetadata.Create(8, 0, PType.U64);
        BitPackedMetadata patched = new BitPackedMetadata(10, 512, in patches);
        byte[] patchedBytes = Serialize((ref ProtoWriter w) => BitPackedMetadata.Write(ref w, in patched));
        BitPackedMetadata read = BitPackedMetadata.Read(patchedBytes);
        Assert.True(read.HasPatches);
        Assert.Equal(patched, read);
        Assert.NotEqual(bare, patched);

        // An all-default patch descriptor is still "present", and that is the whole point of the flag.
        PatchesMetadata zeroed = PatchesMetadata.Create(0, 0, PType.U8);
        BitPackedMetadata zeroPatched = new BitPackedMetadata(10, 512, in zeroed);
        byte[] zeroBytes = Serialize((ref ProtoWriter w) => BitPackedMetadata.Write(ref w, in zeroPatched));
        Assert.True(BitPackedMetadata.Read(zeroBytes).HasPatches);
        Assert.NotEqual(bare, zeroPatched);
    }

    [Fact]
    public void BitPackedOffsetIsBoundedByTheFastLanesBlock()
    {
        Assert.Throws<VortexFormatException>(
            () => { _ = BitPackedMetadata.Read(new WireBuilder().VarintField(2, 1024).ToArray()); });
        Assert.Throws<VortexFormatException>(
            () => { _ = BitPackedMetadata.Read(new WireBuilder().VarintField(2, uint.MaxValue).ToArray()); });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitPackedMetadata(3, 1024));
        Assert.Equal(1023u, BitPackedMetadata.Read(new WireBuilder().VarintField(2, 1023).ToArray()).Offset);
    }

    [Fact]
    public void RleRoundTripsAndOffsetHasNoTriState()
    {
        RleMetadata value = new RleMetadata(1, 1024, PType.U16, 1, PType.U64, 12);
        byte[] bytes = Serialize((ref ProtoWriter w) => RleMetadata.Write(ref w, in value));
        Assert.Equal(value, RleMetadata.Read(bytes));

        RleMetadata zeroOffset = new RleMetadata(1, 1024, PType.U16, 1, PType.U64, 0);
        byte[] zeroBytes = Serialize((ref ProtoWriter w) => RleMetadata.Write(ref w, in zeroOffset));
        Assert.Equal(zeroOffset, RleMetadata.Read(zeroBytes));
        Assert.Equal(zeroOffset, RleMetadata.Read(new WireBuilder()
            .VarintField(1, 1).VarintField(2, 1024).VarintField(3, (ulong)PType.U16)
            .VarintField(4, 1).VarintField(5, (ulong)PType.U64).VarintField(6, 0).ToArray()));
    }

    [Fact]
    public void ListListViewVarBinRunEndRoundTrip()
    {
        ListMetadata list = new ListMetadata(1536, PType.I32);
        Assert.Equal(list, ListMetadata.Read(Serialize((ref ProtoWriter w) => ListMetadata.Write(ref w, in list))));

        ListViewMetadata view = new ListViewMetadata(6144, PType.U64, PType.U64);
        Assert.Equal(
            view, ListViewMetadata.Read(Serialize((ref ProtoWriter w) => ListViewMetadata.Write(ref w, in view))));

        VarBinMetadata varbin = new VarBinMetadata(PType.U32);
        Assert.Equal(
            varbin, VarBinMetadata.Read(Serialize((ref ProtoWriter w) => VarBinMetadata.Write(ref w, in varbin))));

        RunEndMetadata runend = new RunEndMetadata(PType.U16, 17, 3);
        Assert.Equal(
            runend, RunEndMetadata.Read(Serialize((ref ProtoWriter w) => RunEndMetadata.Write(ref w, in runend))));
    }

    [Fact]
    public void SequenceRoundTripsThroughTheScalarStore()
    {
        ScalarStore store = new ScalarStore();
        DTypeArena arena = new DTypeArena();
        byte[] wire = new WireBuilder()
            .BytesField(1, new WireBuilder().VarintField(3, 2000).ToArray())
            .BytesField(2, new WireBuilder().VarintField(3, 13).ToArray())
            .ToArray();

        SequenceMetadata value = SequenceMetadata.Read(wire, store, arena);
        Assert.Equal(1000L, value.Base.AsInt64);
        Assert.Equal(-7L, value.Multiplier.AsInt64);

        byte[] again = Serialize((ref ProtoWriter w) => SequenceMetadata.Write(ref w, in value));
        SequenceMetadata reread = SequenceMetadata.Read(again, new ScalarStore(), new DTypeArena());
        Assert.Equal(1000L, reread.Base.AsInt64);
        Assert.Equal(-7L, reread.Multiplier.AsInt64);
    }

    [Fact]
    public void SequenceRejectsMissingOrEmptyValues()
    {
        ScalarStore store = new ScalarStore();
        DTypeArena arena = new DTypeArena();

        byte[] noMultiplier = new WireBuilder()
            .BytesField(1, new WireBuilder().VarintField(3, 2).ToArray()).ToArray();
        Assert.Throws<VortexFormatException>(() => { _ = SequenceMetadata.Read(noMultiplier, store, arena); });

        byte[] emptyBase = new WireBuilder()
            .BytesField(1, Array.Empty<byte>())
            .BytesField(2, new WireBuilder().VarintField(3, 2).ToArray())
            .ToArray();
        Assert.Throws<VortexFormatException>(() => { _ = SequenceMetadata.Read(emptyBase, store, arena); });

        Assert.Throws<VortexFormatException>(
            () => { _ = SequenceMetadata.Read(Array.Empty<byte>(), store, arena); });
    }

    [Fact]
    public void FlatLayoutDistinguishesAbsentFromEmpty()
    {
        FlatLayoutMetadata absent = FlatLayoutMetadata.Read(Array.Empty<byte>());
        Assert.False(absent.HasArrayEncodingTree);
        Assert.True(absent.ArrayEncodingTree.IsEmpty);

        byte[] emptyPresent = new WireBuilder().BytesField(1, Array.Empty<byte>()).ToArray();
        FlatLayoutMetadata present = FlatLayoutMetadata.Read(emptyPresent);
        Assert.True(present.HasArrayEncodingTree);
        Assert.True(present.ArrayEncodingTree.IsEmpty);

        byte[] roundTripped = SerializeFlat(in present);
        Assert.Equal(emptyPresent, roundTripped);
        Assert.True(FlatLayoutMetadata.Read(roundTripped).HasArrayEncodingTree);

        byte[] absentBytes = SerializeFlat(in absent);
        Assert.Empty(absentBytes);

        byte[] tree = new byte[] { 4, 0, 0, 0, 9 };
        FlatLayoutMetadata inlined = new FlatLayoutMetadata(tree);
        byte[] inlinedBytes = SerializeFlat(in inlined);
        Assert.True(FlatLayoutMetadata.Read(inlinedBytes).ArrayEncodingTree.SequenceEqual(tree));
    }

    [Fact]
    public void ListLayoutRoundTrips()
    {
        ListLayoutMetadata value = new ListLayoutMetadata(PType.I32);
        Assert.Equal(
            value,
            ListLayoutMetadata.Read(Serialize((ref ProtoWriter w) => ListLayoutMetadata.Write(ref w, in value))));
    }

    [Fact]
    public void Phase2MessagesRoundTrip()
    {
        PatchesMetadata patches = PatchesMetadata.CreateChunked(310, 1, PType.U64, 1, PType.U64, 0);

        AlpMetadata alp = new AlpMetadata(9, 7, in patches);
        Assert.Equal(alp, AlpMetadata.Read(Serialize((ref ProtoWriter w) => AlpMetadata.Write(ref w, in alp))));
        AlpMetadata alpBare = new AlpMetadata(17, 15);
        Assert.Equal(
            alpBare, AlpMetadata.Read(Serialize((ref ProtoWriter w) => AlpMetadata.Write(ref w, in alpBare))));
        Assert.False(
            AlpMetadata.Read(Serialize((ref ProtoWriter w) => AlpMetadata.Write(ref w, in alpBare))).HasPatches);

        PatchesMetadata? none = null;
        AlpRdMetadata alprd = new AlpRdMetadata(51, 2, 2, PType.U16, in none);
        uint[] dictionary = new uint[] { 2044, 2045 };
        byte[] alprdBytes = Serialize(
            (ref ProtoWriter w) => AlpRdMetadata.Write(ref w, in alprd, dictionary));
        Assert.Equal(2, AlpRdMetadata.CountDictionaryEntries(alprdBytes));
        Span<uint> readBack = stackalloc uint[8];
        AlpRdMetadata alprdRead = AlpRdMetadata.Read(alprdBytes, readBack);
        Assert.Equal(alprd, alprdRead);
        Assert.Equal(2044u, readBack[0]);
        Assert.Equal(2045u, readBack[1]);

        FsstMetadata fsst = new FsstMetadata(PType.I32, PType.I32);
        Assert.Equal(fsst, FsstMetadata.Read(Serialize((ref ProtoWriter w) => FsstMetadata.Write(ref w, in fsst))));

        OnPairMetadata onpair = new OnPairMetadata(PType.U32, 316, 2165, PType.U32, PType.U16, PType.U32);
        Assert.Equal(
            onpair, OnPairMetadata.Read(Serialize((ref ProtoWriter w) => OnPairMetadata.Write(ref w, in onpair))));

        DateTimePartsMetadata dtp = new DateTimePartsMetadata(PType.I64, PType.I32, PType.I32);
        Assert.Equal(
            dtp,
            DateTimePartsMetadata.Read(Serialize((ref ProtoWriter w) => DateTimePartsMetadata.Write(ref w, in dtp))));

        DecimalBytePartsMetadata dbp = new DecimalBytePartsMetadata(PType.I64);
        Assert.Equal(
            dbp,
            DecimalBytePartsMetadata.Read(
                Serialize((ref ProtoWriter w) => DecimalBytePartsMetadata.Write(ref w, in dbp))));

        ZstdFrameMetadata[] frames =
        {
            new ZstdFrameMetadata(32608, 1024),
            new ZstdFrameMetadata(31, 1),
        };
        ZstdMetadata zstd = new ZstdMetadata(128, 2);
        byte[] zstdBytes = Serialize((ref ProtoWriter w) => ZstdMetadata.Write(ref w, in zstd, frames));
        Assert.Equal(2, ZstdMetadata.CountFrames(zstdBytes));
        Span<ZstdFrameMetadata> readFrames = stackalloc ZstdFrameMetadata[2];
        Assert.Equal(zstd, ZstdMetadata.Read(zstdBytes, readFrames));
        Assert.Equal(frames[0], readFrames[0]);
        Assert.Equal(frames[1], readFrames[1]);
    }

    [Fact]
    public void DecimalBytePartsRejectsANonZeroLowerPartCount()
    {
        // The format requires a reader to reject a non-zero lower_part_count.
        byte[] bytes = new WireBuilder().VarintField(1, (ulong)PType.I64).VarintField(2, 1).ToArray();
        Assert.Throws<VortexFormatException>(() => { _ = DecimalBytePartsMetadata.Read(bytes); });
    }

    [Fact]
    public void AlpRdRejectsADictionaryLongerThanTheDestination()
    {
        byte[] bytes = new WireBuilder()
            .BytesField(3, new WireBuilder().Varint(1).Varint(2).Varint(3).ToArray())
            .ToArray();
        Assert.Throws<VortexFormatException>(
            () =>
            {
                Span<uint> destination = stackalloc uint[2];
                _ = AlpRdMetadata.Read(bytes, destination);
            });
    }

    [Fact]
    public void AlpRdRejectsADeclaredLengthAboveTheEntriesPresent()
    {
        // Upstream slices dict[0..dict_len] and would panic; we reject.
        byte[] bytes = new WireBuilder()
            .VarintField(2, 4)
            .BytesField(3, new WireBuilder().Varint(1).Varint(2).ToArray())
            .ToArray();
        Assert.Throws<VortexFormatException>(
            () =>
            {
                Span<uint> destination = stackalloc uint[8];
                _ = AlpRdMetadata.Read(bytes, destination);
            });
    }

    [Fact]
    public void AlpRdRejectsADictionaryEntryWiderThanU16()
    {
        byte[] bytes = new WireBuilder()
            .VarintField(2, 1)
            .BytesField(3, new WireBuilder().Varint(65536).ToArray())
            .ToArray();
        Assert.Throws<VortexFormatException>(
            () =>
            {
                Span<uint> destination = stackalloc uint[8];
                _ = AlpRdMetadata.Read(bytes, destination);
            });
    }

    [Fact]
    public void AlpRdAcceptsTheUnpackedSpellingOfARepeatedField()
    {
        // proto3 packs repeated numerics by default, but a reader must accept the unpacked form.
        byte[] unpacked = new WireBuilder()
            .VarintField(2, 2).VarintField(3, 2044).VarintField(3, 2045).VarintField(4, (ulong)PType.U16)
            .ToArray();
        Span<uint> destination = stackalloc uint[8];
        AlpRdMetadata value = AlpRdMetadata.Read(unpacked, destination);
        Assert.Equal(2, value.DictionaryEntryCount);
        Assert.Equal(2044u, destination[0]);
        Assert.Equal(2045u, destination[1]);
        Assert.Equal(2, AlpRdMetadata.CountDictionaryEntries(unpacked));
    }

    [Fact]
    public void ZstdRejectsMoreFramesThanTheDestinationHolds()
    {
        byte[] bytes = new WireBuilder()
            .BytesField(2, new WireBuilder().VarintField(1, 1).ToArray())
            .BytesField(2, new WireBuilder().VarintField(1, 2).ToArray())
            .ToArray();
        Assert.Throws<VortexFormatException>(
            () =>
            {
                Span<ZstdFrameMetadata> frames = stackalloc ZstdFrameMetadata[1];
                _ = ZstdMetadata.Read(bytes, frames);
            });
    }
}
