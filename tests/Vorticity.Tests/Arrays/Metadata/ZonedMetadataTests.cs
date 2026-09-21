// vortex.zoned: the version-byte trap, the aggregate registry, and two byte-exact goldens lifted
// out of real corpus files.
using System;
using Vorticity.Arrays.Metadata;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class ZonedMetadataTests
{
    private static byte[] SkipNansOptions => new byte[] { 0x08, 0x01 };

    private static byte[] BoundOptions(ulong maxBytes)
    {
        byte[] bytes = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(maxBytes >> (8 * i));
        }

        return bytes;
    }

    private static byte[] Spec(ReadOnlySpan<byte> id, ReadOnlySpan<byte> options)
    {
        WireBuilder inner = new WireBuilder().BytesField(1, id);
        if (!options.IsEmpty)
        {
            inner.BytesField(2, options);
        }

        return inner.ToArray();
    }

    private static byte[] Zoned(byte version, uint zoneLength, params byte[][] specs)
    {
        WireBuilder tail = new WireBuilder();
        if (zoneLength != 0)
        {
            tail.VarintField(1, zoneLength);
        }

        foreach (byte[] spec in specs)
        {
            tail.BytesField(2, spec);
        }

        return WireBuilder.InsertAt(tail.ToArray(), 0, new[] { version });
    }

    [Fact]
    public void MissingVersionByteIsRejected()
    {
        AggregateSpecList specs = new AggregateSpecList();
        Assert.Throws<VortexFormatException>(
            () => { _ = ZonedMetadata.Read(Array.Empty<byte>(), specs); });
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)255)]
    public void UnsupportedVersionIsRejected(byte version)
    {
        AggregateSpecList specs = new AggregateSpecList();
        byte[] metadata = Zoned(version, 8192, Spec("vortex.min"u8, SkipNansOptions));
        Assert.Throws<VortexFormatException>(() => { _ = ZonedMetadata.Read(metadata, specs); });
    }

    [Fact]
    public void ZeroLengthProtoTailIsRejected()
    {
        AggregateSpecList specs = new AggregateSpecList();
        Assert.Throws<VortexFormatException>(
            () => { _ = ZonedMetadata.Read(new byte[] { ZonedMetadata.SupportedVersion }, specs); });
    }

    [Fact]
    public void NullSpecListIsACallerError()
    {
        Assert.Throws<ArgumentNullException>(
            () => ZonedMetadata.Read(MetadataCatalog.ZonedMetadataBytes, null!));
    }

    [Fact]
    public void UnknownAggregateIdResolvesToUnknownWithoutThrowing()
    {
        AggregateSpecList specs = new AggregateSpecList();
        byte[] metadata = Zoned(
            1,
            4096,
            Spec("vortex.min"u8, SkipNansOptions),
            Spec("acme.histogram"u8, new byte[] { 1, 2, 3 }),
            Spec("vortex.null_count"u8, Array.Empty<byte>()));

        ZonedMetadata value = ZonedMetadata.Read(metadata, specs);
        Assert.Equal(4096u, value.ZoneLength);
        Assert.Equal(3, value.AggregateSpecCount);
        Assert.Equal(AggregateId.Min, specs.GetAggregate(0));
        Assert.Equal(AggregateId.Unknown, specs.GetAggregate(1));
        Assert.Equal(AggregateId.NullCount, specs.GetAggregate(2));
        Assert.True(specs.GetIdUtf8(1).SequenceEqual("acme.histogram"u8));
        Assert.True(specs.GetOptions(1).SequenceEqual(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void AnAggregateSpecWithoutAnIdDegradesToUnknownRatherThanFailingTheRead()
    {
        // prost's `string id` has implicit presence, so absent and "" are the same bytes, and
        // upstream resolves an id it does not recognise to Ok(None) and disables that aggregate's
        // pruning. Degrading is mandatory: a pruning hint may never fail a read.
        AggregateSpecList specs = new AggregateSpecList();
        byte[] metadata = Zoned(1, 1024, Array.Empty<byte>());
        ZonedMetadata value = ZonedMetadata.Read(metadata, specs);
        Assert.Equal(1, value.AggregateSpecCount);
        Assert.Equal(AggregateId.Unknown, specs.GetAggregate(0));
        Assert.True(specs.GetIdUtf8(0).IsEmpty);

        byte[] emptyId = Zoned(1, 1024, new WireBuilder().BytesField(1, Array.Empty<byte>()).ToArray());
        Assert.Equal(AggregateId.Unknown, ZonedMetadata.Read(emptyId, specs).Specs!.GetAggregate(0));
    }

    [Fact]
    public void ZoneLengthZeroIsAccepted()
    {
        // Upstream keeps such a layout and disables pruning rather than failing the read
        // (vortex-layout-0.86.1/src/layouts/zoned/mod.rs, "Backward compat: older files may
        // encode zone_len == 0").
        AggregateSpecList specs = new AggregateSpecList();
        byte[] metadata = Zoned(1, 0, Spec("vortex.max"u8, SkipNansOptions));
        ZonedMetadata value = ZonedMetadata.Read(metadata, specs);
        Assert.Equal(0u, value.ZoneLength);
        Assert.Equal(1, value.AggregateSpecCount);
    }

    [Fact]
    public void ReadClearsTheSpecListFirst()
    {
        AggregateSpecList specs = new AggregateSpecList();
        specs.Add("stale"u8, "stale"u8);
        byte[] metadata = Zoned(1, 64, Spec("vortex.max"u8, SkipNansOptions));
        ZonedMetadata value = ZonedMetadata.Read(metadata, specs);
        Assert.Equal(1, specs.Count);
        Assert.Equal(1, value.AggregateSpecCount);
        Assert.Equal(AggregateId.Max, specs.GetAggregate(0));
    }

    [Fact]
    public void ZonedMatchesTheBytesInContainersZonedLayout()
    {
        // Reconstructed from containers/zoned_layout.jsonl's zone_map line, and verified byte for
        // byte against the .vortex itself: the 61-byte sequence occurs verbatim in the file, which
        // is also the metadata_bytes the sidecar records for that vortex.zoned layout node.
        byte[] expected = Zoned(
            1,
            8192,
            Spec("vortex.max"u8, SkipNansOptions),
            Spec("vortex.min"u8, SkipNansOptions),
            Spec("vortex.null_count"u8, Array.Empty<byte>()));
        Assert.Equal(61, expected.Length);
        Assert.Equal(
            "0108804012100A0A766F727465782E6D61781202080112100A0A766F727465782E6D696E1202080112130A11" +
            "766F727465782E6E756C6C5F636F756E74",
            Convert.ToHexString(expected));

        AggregateSpecList specs = new AggregateSpecList();
        ZonedMetadata value = ZonedMetadata.Read(expected, specs);
        Assert.Equal(8192u, value.ZoneLength);
        Assert.Equal(3, value.AggregateSpecCount);
        Assert.Equal(AggregateId.Max, specs.GetAggregate(0));
        Assert.Equal(AggregateId.Min, specs.GetAggregate(1));
        Assert.Equal(AggregateId.NullCount, specs.GetAggregate(2));

        Assert.Equal(expected, ZonedMetadata.Serialize(in value));
    }

    [Fact]
    public void ZonedMatchesTheBytesInDistributionsHugeStringR16()
    {
        // distributions/huge_string_r16: vortex.bounded_max(64), vortex.bounded_min(64),
        // vortex.null_count(). 89 bytes, again verbatim in the .vortex.
        byte[] expected = Zoned(
            1,
            8192,
            Spec("vortex.bounded_max"u8, BoundOptions(64)),
            Spec("vortex.bounded_min"u8, BoundOptions(64)),
            Spec("vortex.null_count"u8, Array.Empty<byte>()));
        Assert.Equal(89, expected.Length);

        AggregateSpecList specs = new AggregateSpecList();
        ZonedMetadata value = ZonedMetadata.Read(expected, specs);
        Assert.Equal(3, value.AggregateSpecCount);
        Assert.Equal(AggregateId.BoundedMax, specs.GetAggregate(0));
        Assert.Equal(AggregateId.BoundedMin, specs.GetAggregate(1));

        Assert.True(specs.TryGetBoundLength(0, out uint maxBound));
        Assert.Equal(64u, maxBound);
        Assert.True(specs.TryGetBoundLength(1, out uint minBound));
        Assert.Equal(64u, minBound);
        Assert.False(specs.TryGetBoundLength(2, out _));

        Assert.Equal(expected, ZonedMetadata.Serialize(in value));
    }

    [Fact]
    public void ZonedTailIsSweptForUnknownFieldsAndGroups()
    {
        string expected = MetadataCatalog.DescribeZoned(MetadataCatalog.ZonedMetadataBytes);
        byte[] tail = MetadataCatalog.ZonedProtoTail;
        foreach (byte[] unknown in WireBuilder.UnknownFields())
        {
            foreach (int offset in WireBuilder.FieldBoundaries(tail))
            {
                byte[] mutatedTail = WireBuilder.InsertAt(tail, offset, unknown);
                byte[] mutated = WireBuilder.InsertAt(mutatedTail, 0, new[] { ZonedMetadata.SupportedVersion });
                Assert.Equal(expected, MetadataCatalog.DescribeZoned(mutated));
            }
        }

        byte[] group = WireBuilder.GroupTag();
        foreach (int offset in WireBuilder.FieldBoundaries(tail))
        {
            byte[] mutatedTail = WireBuilder.InsertAt(tail, offset, group);
            byte[] mutated = WireBuilder.InsertAt(mutatedTail, 0, new[] { ZonedMetadata.SupportedVersion });
            Assert.Throws<VortexFormatException>(() => { _ = MetadataCatalog.DescribeZoned(mutated); });
        }
    }

    [Fact]
    public void ZonedTruncationNeverEscapesVortexFormatException()
    {
        byte[] valid = MetadataCatalog.ZonedMetadataBytes;
        for (int length = 0; length < valid.Length; length++)
        {
            byte[] truncated = valid.AsSpan(0, length).ToArray();
            try
            {
                _ = MetadataCatalog.DescribeZoned(truncated);
            }
            catch (VortexFormatException)
            {
            }
        }
    }

    [Theory]
    [InlineData("vortex.min", AggregateId.Min)]
    [InlineData("vortex.max", AggregateId.Max)]
    [InlineData("vortex.bounded_min", AggregateId.BoundedMin)]
    [InlineData("vortex.bounded_max", AggregateId.BoundedMax)]
    [InlineData("vortex.nan_count", AggregateId.NanCount)]
    [InlineData("vortex.null_count", AggregateId.NullCount)]
    [InlineData("", AggregateId.Unknown)]
    [InlineData("vortex.mix", AggregateId.Unknown)]
    [InlineData("vortex.mia", AggregateId.Unknown)]
    [InlineData("vortex.bounded_mix", AggregateId.Unknown)]
    [InlineData("vortex.bounded_mia", AggregateId.Unknown)]
    [InlineData("vortex.nan_counts", AggregateId.Unknown)]
    [InlineData("vortex.null_countx", AggregateId.Unknown)]
    [InlineData("VORTEX.MIN", AggregateId.Unknown)]
    [InlineData("vortex.mi", AggregateId.Unknown)]
    public void AggregateIdsResolve(string id, AggregateId expected)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(id);
        Assert.Equal(expected, AggregateRegistry.Resolve(utf8));
    }

    [Fact]
    public void BoundLengthDecodesOnlyForBoundedAggregatesWithEightUsableBytes()
    {
        Assert.True(AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMin, BoundOptions(64), out uint n));
        Assert.Equal(64u, n);

        Assert.False(AggregateRegistry.TryGetBoundLength(AggregateId.Min, BoundOptions(64), out _));
        Assert.False(AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMin, Array.Empty<byte>(), out _));
        Assert.False(AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMin, new byte[7], out _));
        Assert.False(AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMin, new byte[9], out _));
        Assert.False(AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMax, BoundOptions(0), out _));
        Assert.False(
            AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMax, BoundOptions(0x1_0000_0000UL), out _));
        Assert.True(
            AggregateRegistry.TryGetBoundLength(AggregateId.BoundedMax, BoundOptions(uint.MaxValue), out uint max));
        Assert.Equal(uint.MaxValue, max);
    }

    [Fact]
    public void SpecListGrowsAndRecyclesWithoutLosingEntries()
    {
        AggregateSpecList specs = new AggregateSpecList(1);
        for (int round = 0; round < 3; round++)
        {
            specs.Clear();
            for (int i = 0; i < 40; i++)
            {
                byte[] id = System.Text.Encoding.UTF8.GetBytes("agg." + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                specs.Add(id, new byte[i % 7]);
            }

            Assert.Equal(40, specs.Count);
            for (int i = 0; i < 40; i++)
            {
                byte[] id = System.Text.Encoding.UTF8.GetBytes("agg." + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(specs.GetIdUtf8(i).SequenceEqual(id));
                Assert.Equal(i % 7, specs.GetOptions(i).Length);
                Assert.Equal(AggregateId.Unknown, specs.GetAggregate(i));
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = specs.GetIdUtf8(40).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = specs.GetOptions(-1).Length; });
    }

    [Fact]
    public void ManyAggregateSpecsAreBoundedByTheInputLength()
    {
        // The only allocation this component sizes from file bytes. Each spec costs at least four
        // bytes on the wire (tag, length, id tag, id length), so a 4 KiB metadata cannot ask for
        // more than ~1000 entries: no unbounded allocation is reachable.
        WireBuilder tail = new WireBuilder().VarintField(1, 8);
        for (int i = 0; i < 1000; i++)
        {
            tail.BytesField(2, new WireBuilder().BytesField(1, "x"u8).ToArray());
        }

        byte[] metadata = WireBuilder.InsertAt(tail.ToArray(), 0, new[] { ZonedMetadata.SupportedVersion });
        AggregateSpecList specs = new AggregateSpecList();
        ZonedMetadata value = ZonedMetadata.Read(metadata, specs);
        Assert.Equal(1000, value.AggregateSpecCount);
        Assert.True(metadata.Length > 4000);
    }
}
