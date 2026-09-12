// Sweeps that run over every message in MetadataCatalog, so a codec added later cannot quietly
// skip them. Phase 1 contract §6 "Tests must cover".
using System;
using System.Collections.Generic;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class AdversarialMetadataTests
{
    public static TheoryData<string> CaseNames
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();
            foreach (MetadataCase c in MetadataCatalog.All)
            {
                data.Add(c.Name);
            }

            return data;
        }
    }

    private static MetadataCase Case(string name)
    {
        foreach (MetadataCase c in MetadataCatalog.All)
        {
            if (c.Name == name)
            {
                return c;
            }
        }

        throw new InvalidOperationException(name);
    }

    [Fact]
    public void CatalogCoversEveryMessage()
    {
        // A tripwire: bumping the catalogue is what makes every sweep below cover a new codec.
        Assert.Equal(21, MetadataCatalog.All.Count);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ValidPayloadParses(string name)
    {
        MetadataCase c = Case(name);
        Assert.False(string.IsNullOrEmpty(c.Describe(c.Valid)));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EmptyPayloadNeverThrowsAnythingButFormat(string name)
    {
        MetadataCase c = Case(name);

        // An empty proto3 message is legal and means "every field defaulted". A message with a
        // genuinely required field (SparseMetadata.patches, SequenceMetadata.base) rejects it, and
        // that rejection must be a VortexFormatException and nothing else.
        try
        {
            c.Describe(Array.Empty<byte>());
        }
        catch (VortexFormatException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void UnknownFieldsAreSkippedAtEveryBoundary(string name)
    {
        MetadataCase c = Case(name);
        string expected = c.Describe(c.Valid);
        int[] boundaries = WireBuilder.FieldBoundaries(c.Valid);
        Assert.True(boundaries.Length >= 2, "the sample payload must have at least one field");

        foreach (byte[] unknown in WireBuilder.UnknownFields())
        {
            foreach (int offset in boundaries)
            {
                byte[] mutated = WireBuilder.InsertAt(c.Valid, offset, unknown);
                Assert.Equal(expected, c.Describe(mutated));
            }
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GroupTagIsRejectedAtEveryBoundary(string name)
    {
        MetadataCase c = Case(name);
        byte[] group = WireBuilder.GroupTag();
        foreach (int offset in WireBuilder.FieldBoundaries(c.Valid))
        {
            byte[] mutated = WireBuilder.InsertAt(c.Valid, offset, group);
            Assert.Throws<VortexFormatException>(() => c.Describe(mutated));
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TruncationNeverEscapesVortexFormatException(string name)
    {
        MetadataCase c = Case(name);
        for (int length = 0; length < c.Valid.Length; length++)
        {
            byte[] truncated = c.Valid.AsSpan(0, length).ToArray();
            try
            {
                c.Describe(truncated);
            }
            catch (VortexFormatException)
            {
            }
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryByteFlipNeverEscapesVortexFormatException(string name)
    {
        // A cheap deterministic fuzz: every single-byte substitution of a valid payload must either
        // parse or raise VortexFormatException. Nothing else may reach the caller.
        MetadataCase c = Case(name);
        byte[] mutated = new byte[c.Valid.Length];
        ReadOnlySpan<byte> substitutions = new byte[] { 0x00, 0x01, 0x03, 0x0B, 0x7F, 0x80, 0xFF };
        for (int i = 0; i < c.Valid.Length; i++)
        {
            for (int s = 0; s < substitutions.Length; s++)
            {
                c.Valid.CopyTo(mutated, 0);
                mutated[i] = substitutions[s];
                try
                {
                    c.Describe(mutated);
                }
                catch (VortexFormatException)
                {
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TrailingGarbageIsEitherSkippedOrRejected(string name)
    {
        MetadataCase c = Case(name);
        byte[] mutated = WireBuilder.InsertAt(c.Valid, c.Valid.Length, new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Throws<VortexFormatException>(() => c.Describe(mutated));
    }

    public static TheoryData<int> UndefinedPTypeValues =>
        new TheoryData<int> { 11, 12, 127, 255, 1000, int.MaxValue };

    [Theory]
    [MemberData(nameof(UndefinedPTypeValues))]
    public void UndefinedPTypeIsRejectedRatherThanCoercedToU8(int raw)
    {
        // prost coerces an unknown `enumeration` varint to the enum's zero value, and PType::U8 is
        // zero. That silently reads a 1-byte-wide child where the file meant something else, so
        // every PType field in this namespace rejects instead.
        Assert.Throws<VortexFormatException>(
            () => VarBinMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => ListMetadata.Read(new WireBuilder().VarintField(2, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => DictMetadata.Read(new WireBuilder().VarintField(2, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => RunEndMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => DictLayoutMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => ListLayoutMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => FsstMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => DateTimePartsMetadata.Read(new WireBuilder().VarintField(2, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => OnPairMetadata.Read(new WireBuilder().VarintField(6, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => RleMetadata.Read(new WireBuilder().VarintField(5, (ulong)(uint)raw).ToArray()));
        Assert.Throws<VortexFormatException>(
            () => DecimalBytePartsMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativePTypeIsRejected(int raw)
    {
        // prost writes a negative `enumeration` as a ten-byte sign-extended varint; truncating it to
        // 32 bits must not alias a defined tag.
        byte[] payload = new WireBuilder().NegativeEnumField(1, raw).ToArray();
        Assert.Throws<VortexFormatException>(() => VarBinMetadata.Read(payload));
    }

    public static TheoryData<int> UndefinedDecimalTypeValues =>
        new TheoryData<int> { 6, 7, 127, 255, 256, int.MaxValue };

    [Theory]
    [MemberData(nameof(UndefinedDecimalTypeValues))]
    public void UndefinedDecimalTypeIsRejected(int raw)
    {
        // 256 is the interesting one: narrowing to byte first would turn it into I8 = 0.
        Assert.Throws<VortexFormatException>(
            () => DecimalMetadata.Read(new WireBuilder().VarintField(1, (ulong)(uint)raw).ToArray()));
    }

    [Fact]
    public void NegativeDecimalTypeIsRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => DecimalMetadata.Read(new WireBuilder().NegativeEnumField(1, -1).ToArray()));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void WrongWireTypeOnAKnownFieldIsRejected(string name)
    {
        // A recognized field number carrying the wrong framing is malformed, exactly as prost's
        // "invalid wire type" is. It must not be silently skipped like an unknown number.
        MetadataCase c = Case(name);
        ProtoReader reader = new ProtoReader(c.Valid);
        List<int> knownFields = new List<int>();
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            knownFields.Add(field);
            reader.SkipField(wire);
        }

        foreach (int field in knownFields)
        {
            byte[] bad = new WireBuilder().Fixed32Field(field, 0x01020304).ToArray();
            Assert.Throws<VortexFormatException>(() => c.Describe(bad));
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TruncatedLengthDelimitedFieldIsRejected(string name)
    {
        MetadataCase c = Case(name);

        // A length-delimited field number that no message defines still has to be skipped safely,
        // and a length that escapes the buffer is malformed rather than an out-of-range read.
        byte[] lying = new WireBuilder().Tag(4242, ProtoWireType.LengthDelimited).Varint(0x7FFFFFFF).ToArray();
        Assert.Throws<VortexFormatException>(() => c.Describe(lying));
    }

    [Fact]
    public void ElevenByteVarintIsRejected()
    {
        byte[] payload = new byte[]
        {
            0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01,
        };
        Assert.Throws<VortexFormatException>(() => BoolMetadata.Read(payload));
    }

    [Fact]
    public void FieldNumberZeroIsRejected()
    {
        Assert.Throws<VortexFormatException>(() => BoolMetadata.Read(new byte[] { 0x00, 0x01 }));
    }
}
