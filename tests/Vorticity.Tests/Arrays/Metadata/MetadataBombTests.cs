// Inputs designed to be legal-looking and expensive. Phase 0's review found a 3.6 KB file that hung
// the DType parser because FlatBuffers sub-table sharing makes the graph a DAG; Protobuf has no
// sharing, so the equivalent risks here are unbounded recursion, unbounded allocation, and a growth
// loop that overflows. Each is pinned below.
using System;
using System.Diagnostics;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class MetadataBombTests
{
    [Fact]
    public void DeeplyNestedSequenceScalarIsRejectedRatherThanOverflowingTheStack()
    {
        // SequenceMetadata is the only codec here that delegates to a recursive parser: a wire
        // ScalarValue nests through list_value, variant_value and union_value. The payload below is
        // 512 nested ListValues in about 2 KB.
        byte[] inner = new WireBuilder().VarintField(3, 2).ToArray();
        for (int i = 0; i < 512; i++)
        {
            // ListValue { repeated ScalarValue values = 1 } wrapped in ScalarValue.list_value = 9.
            inner = new WireBuilder().BytesField(9, new WireBuilder().BytesField(1, inner).ToArray()).ToArray();
        }

        byte[] metadata = new WireBuilder()
            .BytesField(1, inner)
            .BytesField(2, new WireBuilder().VarintField(3, 2).ToArray())
            .ToArray();

        Assert.True(metadata.Length < 8192);
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => { _ = SequenceMetadata.Read(metadata, new ScalarStore(), new DTypeArena()); });

        // The rejection must come from the depth cap, not from an accidental shape error.
        Assert.Contains("nesting depth", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABreadthOnlyScalarListIsLinearInTheInput()
    {
        // The complement of the depth bomb: many siblings rather than deep nesting. Protobuf cannot
        // share a sub-message, so the work stays proportional to the bytes.
        WireBuilder list = new WireBuilder();
        for (int i = 0; i < 20000; i++)
        {
            list.BytesField(1, new WireBuilder().VarintField(3, (ulong)i).ToArray());
        }

        byte[] metadata = new WireBuilder()
            .BytesField(1, new WireBuilder().BytesField(9, list.ToArray()).ToArray())
            .BytesField(2, new WireBuilder().VarintField(3, 2).ToArray())
            .ToArray();

        Stopwatch clock = Stopwatch.StartNew();
        SequenceMetadata value = SequenceMetadata.Read(metadata, new ScalarStore(), new DTypeArena());
        clock.Stop();
        Assert.Equal(ScalarValueKind.List, value.Base.Kind);
        Assert.Equal(20000, value.Base.ListCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "parsing must stay linear in the input");
    }

    [Fact]
    public void ANestedMessageLengthThatEscapesTheBufferIsRejected()
    {
        // 0x7FFFFFFF bytes claimed by a two-byte payload. The check runs in 64-bit arithmetic
        // upstream of any access, so this can never become an out-of-range read.
        byte[] sparse = new WireBuilder().Tag(1, global::Vorticity.Serialization.Protobuf.ProtoWireType.LengthDelimited)
            .Varint(0x7FFFFFFF).Raw(new byte[] { 0x08, 0x01 }).ToArray();
        Assert.Throws<VortexFormatException>(() => { _ = SparseMetadata.Read(sparse); });

        byte[] bitpacked = new WireBuilder().Tag(3, global::Vorticity.Serialization.Protobuf.ProtoWireType.LengthDelimited)
            .Varint(ulong.MaxValue).ToArray();
        Assert.Throws<VortexFormatException>(() => { _ = BitPackedMetadata.Read(bitpacked); });
    }

    [Fact]
    public void ManyUnknownFieldsAreSkippedInLinearTime()
    {
        WireBuilder builder = new WireBuilder().VarintField(1, 7);
        for (int i = 0; i < 100000; i++)
        {
            builder.VarintField(4242, 0xFFFFFFFFUL);
        }

        byte[] metadata = builder.ToArray();
        Stopwatch clock = Stopwatch.StartNew();
        BoolMetadata value = BoolMetadata.Read(metadata);
        clock.Stop();
        Assert.Equal(7u, value.Offset);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "unknown-field skipping must stay linear");
    }

    [Fact]
    public void ARepeatedFieldRepeatedManyTimesIsLastWinsAndTerminates()
    {
        // A non-repeated field appearing many times is last-wins on the wire, not an error, and the
        // loop must still advance one field at a time.
        WireBuilder builder = new WireBuilder();
        for (uint i = 0; i < 50000; i++)
        {
            builder.VarintField(1, i % 8);
        }

        byte[] metadata = builder.ToArray();
        Assert.Equal((50000u - 1u) % 8u, BoolMetadata.Read(metadata).Offset);
    }

    [Fact]
    public void AZoneMapWithManySpecsAllocatesInProportionToItsInput()
    {
        // The AggregateSpecList is the only structure in this component whose size a file controls.
        // Every spec costs at least four wire bytes, so the entry count cannot outrun the payload.
        const int Count = 20000;
        WireBuilder tail = new WireBuilder().VarintField(1, 1024);
        for (int i = 0; i < Count; i++)
        {
            tail.BytesField(2, new WireBuilder().BytesField(1, "vortex.min"u8).ToArray());
        }

        byte[] metadata = WireBuilder.InsertAt(tail.ToArray(), 0, new[] { ZonedMetadata.SupportedVersion });
        AggregateSpecList specs = new AggregateSpecList();
        ZonedMetadata value = ZonedMetadata.Read(metadata, specs);
        Assert.Equal(Count, value.AggregateSpecCount);
        Assert.True(metadata.Length > Count * 4);
        Assert.Equal(AggregateId.Min, specs.GetAggregate(Count - 1));

        // And the list recycles: a second, smaller zone map must not inherit the first one's specs.
        byte[] small = WireBuilder.InsertAt(
            new WireBuilder().VarintField(1, 8).ToArray(), 0, new[] { ZonedMetadata.SupportedVersion });
        Assert.Equal(0, ZonedMetadata.Read(small, specs).AggregateSpecCount);
        Assert.Equal(0, specs.Count);
    }

    [Fact]
    public void APackedRepeatedFieldClaimingHugeLengthIsRejected()
    {
        byte[] metadata = new WireBuilder()
            .Tag(3, global::Vorticity.Serialization.Protobuf.ProtoWireType.LengthDelimited)
            .Varint(0x7FFFFFFF)
            .ToArray();
        Assert.Throws<VortexFormatException>(
            () =>
            {
                Span<uint> destination = stackalloc uint[8];
                _ = AlpRdMetadata.Read(metadata, destination);
            });
        Assert.Throws<VortexFormatException>(() => { _ = AlpRdMetadata.CountDictionaryEntries(metadata); });
    }

    [Fact]
    public void APackedRepeatedFieldFullOfTruncatedVarintsIsRejected()
    {
        // A packed payload whose last varint runs off the end of its own sub-message.
        byte[] packed = new byte[] { 0xFF, 0xFF, 0xFF };
        byte[] metadata = new WireBuilder().BytesField(3, packed).ToArray();
        Assert.Throws<VortexFormatException>(
            () =>
            {
                Span<uint> destination = stackalloc uint[8];
                _ = AlpRdMetadata.Read(metadata, destination);
            });
    }

    [Fact]
    public void AZonedTailFullOfEmptySpecsTerminates()
    {
        // Each spec is two bytes (tag + zero length): the smallest a repeated message can be.
        WireBuilder tail = new WireBuilder();
        for (int i = 0; i < 10000; i++)
        {
            tail.BytesField(2, Array.Empty<byte>());
        }

        byte[] metadata = WireBuilder.InsertAt(tail.ToArray(), 0, new[] { ZonedMetadata.SupportedVersion });
        AggregateSpecList specs = new AggregateSpecList();
        Assert.Equal(10000, ZonedMetadata.Read(metadata, specs).AggregateSpecCount);
        Assert.Equal(AggregateId.Unknown, specs.GetAggregate(0));
    }
}
