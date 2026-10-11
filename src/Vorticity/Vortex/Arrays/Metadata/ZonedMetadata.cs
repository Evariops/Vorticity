using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.zoned</c> layout metadata: a version byte followed by
/// <c>message ZonedMetadataProto { uint32 zone_len = 1; repeated AggregateSpecProto aggregate_specs = 2; }</c>.
/// The payload is not a bare message, so a missing version byte, an unsupported version and an
/// empty Protobuf tail are three distinct rejections.
/// </summary>
/// <remarks>
/// The aggregate specs live in a caller-owned <see cref="AggregateSpecList"/> rather than in this
/// struct, so decoding a zone map allocates nothing once the list has warmed up. The struct keeps
/// a reference to that list: see the warning on <see cref="AggregateSpecList"/> about reusing one
/// list for two zone maps.
/// </remarks>
internal readonly struct ZonedMetadata
{
    private const string MessageName = "ZonedMetadataProto";

    /// <summary>The only supported value of the leading version byte.</summary>
    public const byte SupportedVersion = 1;

    private readonly AggregateSpecList? _specs;

    private ZonedMetadata(uint zoneLength, AggregateSpecList? specs, int specCount)
    {
        ZoneLength = zoneLength;
        _specs = specs;
        AggregateSpecCount = specCount;
    }

    /// <summary>
    /// Number of rows per zone (tag 1). A value of <c>0</c> means there is no usable zone map: the
    /// reader falls back to the data child rather than failing, so it is not rejected here.
    /// </summary>
    public uint ZoneLength { get; }

    /// <summary>Number of aggregate specs, i.e. the number of entries this read put in the list.</summary>
    public int AggregateSpecCount { get; }

    /// <summary>The list the specs were decoded into, or null for a default-constructed value.</summary>
    public AggregateSpecList? Specs => _specs;

    /// <summary>
    /// Reads <c>[u8 version] ++ ZonedMetadataProto</c> from the raw layout metadata bytes,
    /// replacing the contents of <paramref name="specs"/>.
    /// </summary>
    /// <param name="layoutMetadata">The raw layout metadata bytes.</param>
    /// <param name="specs">
    /// Receives the aggregate specs. <b>Cleared first</b>, so any earlier zone map's specs are lost.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="specs"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The payload is empty (no version byte), the version is not
    /// <see cref="SupportedVersion"/>, the Protobuf tail is zero-length, or the tail is malformed.
    /// </exception>
    public static ZonedMetadata Read(ReadOnlySpan<byte> layoutMetadata, AggregateSpecList specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        specs.Clear();

        if (layoutMetadata.IsEmpty)
        {
            ThrowMissingVersion();
        }

        byte version = layoutMetadata[0];
        if (version != SupportedVersion)
        {
            ThrowUnsupportedVersion(version);
        }

        ReadOnlySpan<byte> tail = layoutMetadata.Slice(1);
        if (tail.IsEmpty)
        {
            ThrowMissingProto();
        }

        ProtoReader reader = new ProtoReader(tail);
        uint zoneLength = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    zoneLength = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "zone_len");
                    break;
                case 2:
                {
                    MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "aggregate_specs");
                    AggregateSpec spec = AggregateSpec.Read(ref reader);
                    specs.Add(spec.IdUtf8, spec.Options);
                    break;
                }

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ZonedMetadata(zoneLength, specs, specs.Count);
    }

    /// <summary>Writes the Protobuf tail only: no version byte, no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata, whose <see cref="Specs"/> supply the aggregate list.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="value"/> declares specs but carries no list to read them from.
    /// </exception>
    public static void Write(ref ProtoWriter writer, in ZonedMetadata value)
    {
        writer.WriteUInt32(1, value.ZoneLength);

        int count = value.AggregateSpecCount;
        if (count == 0)
        {
            return;
        }

        AggregateSpecList? specs = value._specs;
        if (specs is null || specs.Count < count)
        {
            ThrowDetachedSpecs();
        }

        for (int i = 0; i < count; i++)
        {
            AggregateSpec spec = new AggregateSpec(specs.GetIdUtf8(i), specs.GetOptions(i));
            AggregateSpec.Write(ref writer, 2, in spec);
        }
    }

    /// <summary>
    /// Serializes the complete layout metadata: the version byte followed by the Protobuf tail.
    /// </summary>
    /// <param name="value">The metadata.</param>
    /// <returns>A freshly allocated byte array; write paths only, never a read path.</returns>
    public static byte[] Serialize(in ZonedMetadata value)
    {
        // A plain local plus try/finally, not `using`: CS1657 forbids passing a `using` variable as
        // a `ref` argument.
        ProtoWriter writer = new ProtoWriter(128);
        try
        {
            Write(ref writer, in value);
            ReadOnlySpan<byte> tail = writer.WrittenSpan;
            byte[] result = new byte[tail.Length + 1];
            result[0] = SupportedVersion;
            tail.CopyTo(result.AsSpan(1));
            return result;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Builds a value that writes <paramref name="specs"/> back out.</summary>
    /// <param name="zoneLength">Number of rows per zone.</param>
    /// <param name="specs">The aggregate specs to emit; the list is captured, not copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="specs"/> is null.</exception>
    public static ZonedMetadata Create(uint zoneLength, AggregateSpecList specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        return new ZonedMetadata(zoneLength, specs, specs.Count);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingVersion() =>
        throw new VortexFormatException("vortex.zoned metadata is empty: the protobuf version byte is missing.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnsupportedVersion(byte version) =>
        throw new VortexFormatException(
            $"Unsupported vortex.zoned metadata version {version}; only version {SupportedVersion} is defined.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingProto() =>
        throw new VortexFormatException(
            "vortex.zoned metadata carries a version byte and nothing else: the protobuf tail is missing.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDetachedSpecs() =>
        throw new InvalidOperationException(
            "This ZonedMetadata declares aggregate specs but its AggregateSpecList no longer holds them; " +
            "the list was cleared or reused for another zone map.");
}
