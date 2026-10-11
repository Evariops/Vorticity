using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// One frame of a <c>vortex.zstd</c> array:
/// <c>message ZstdFrameMetadata { uint64 uncompressed_size = 1; uint64 n_values = 2; }</c>.
/// </summary>
internal readonly struct ZstdFrameMetadata : IEquatable<ZstdFrameMetadata>
{
    private const string MessageName = "ZstdFrameMetadata";

    /// <summary>Creates frame metadata.</summary>
    /// <param name="uncompressedSize">Uncompressed byte size of the frame (tag 1).</param>
    /// <param name="valueCount">Number of values stored in the frame (tag 2).</param>
    public ZstdFrameMetadata(ulong uncompressedSize, ulong valueCount)
    {
        UncompressedSize = uncompressedSize;
        ValueCount = valueCount;
    }

    /// <summary>
    /// Uncompressed byte size of this frame (tag 1). The value comes from the file, so the decoder
    /// must check it against <see cref="VortexLimits.DefaultMaxDecompressedBytes"/> <b>before</b>
    /// allocating.
    /// </summary>
    public ulong UncompressedSize { get; }

    /// <summary>Number of values stored in this frame (tag 2, <c>n_values</c>).</summary>
    public ulong ValueCount { get; }

    /// <summary>
    /// Reads the nested <c>ZstdFrameMetadata</c> that the tag just returned by
    /// <see cref="ProtoReader.TryReadTag"/> introduces, consuming its length prefix and body.
    /// </summary>
    /// <param name="reader">Reader positioned immediately after a length-delimited field tag.</param>
    /// <exception cref="VortexFormatException">The body is malformed.</exception>
    public static ZstdFrameMetadata Read(ref ProtoReader reader)
    {
        ProtoReader body = reader.ReadMessage();
        ulong uncompressedSize = 0;
        ulong valueCount = 0;
        while (body.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    uncompressedSize =
                        MetadataProto.ReadUInt64(ref body, wire, MessageName, "uncompressed_size");
                    break;
                case 2:
                    valueCount = MetadataProto.ReadUInt64(ref body, wire, MessageName, "n_values");
                    break;
                default:
                    body.SkipField(wire);
                    break;
            }
        }

        return new ZstdFrameMetadata(uncompressedSize, valueCount);
    }

    /// <summary>Writes this frame as a nested message under <paramref name="fieldNumber"/>.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="fieldNumber">The enclosing message's field number for this frame.</param>
    /// <param name="value">The frame.</param>
    public static void Write(ref ProtoWriter writer, int fieldNumber, in ZstdFrameMetadata value)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(fieldNumber);
        writer.WriteUInt64(1, value.UncompressedSize);
        writer.WriteUInt64(2, value.ValueCount);
        scope.End();
    }

    /// <inheritdoc/>
    public bool Equals(ZstdFrameMetadata other) =>
        UncompressedSize == other.UncompressedSize && ValueCount == other.ValueCount;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZstdFrameMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(UncompressedSize, ValueCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ZstdFrameMetadata left, ZstdFrameMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ZstdFrameMetadata left, ZstdFrameMetadata right) => !left.Equals(right);
}

/// <summary>
/// <c>vortex.zstd</c> metadata:
/// <c>message ZstdMetadata { uint32 dictionary_size = 1; repeated ZstdFrameMetadata frames = 2; }</c>.
/// </summary>
/// <remarks>
/// The frames go into a caller-supplied span. The frame count is file-supplied, so no cap is
/// invented here; size the destination with <see cref="CountFrames"/>, which is bounded by the
/// metadata length because every frame costs at least two bytes on the wire.
/// </remarks>
internal readonly struct ZstdMetadata : IEquatable<ZstdMetadata>
{
    private const string MessageName = "ZstdMetadata";

    /// <summary>Creates zstd metadata.</summary>
    /// <param name="dictionarySize">Dictionary size in bytes, or 0 when there is no dictionary (tag 1).</param>
    /// <param name="frameCount">Number of frames read into the caller's span (tag 2).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is negative.</exception>
    public ZstdMetadata(uint dictionarySize, int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        DictionarySize = dictionarySize;
        FrameCount = frameCount;
    }

    /// <summary>Dictionary size in bytes; 0 when the array carries no dictionary (tag 1).</summary>
    public uint DictionarySize { get; }

    /// <summary>Number of frames the payload carried (tag 2).</summary>
    public int FrameCount { get; }

    /// <summary>Counts the frames of a payload without decoding them.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed.</exception>
    public static int CountFrames(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        int count = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 2 && wire == ProtoWireType.LengthDelimited)
            {
                reader.ReadLengthDelimited();
                count++;
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        return count;
    }

    /// <summary>Reads a <c>vortex.zstd</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <param name="frames">Receives the frames; must hold at least <see cref="CountFrames"/> entries.</param>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, or it carries more frames than <paramref name="frames"/> holds.
    /// </exception>
    public static ZstdMetadata Read(ReadOnlySpan<byte> metadata, Span<ZstdFrameMetadata> frames)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint dictionarySize = 0;
        int count = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    dictionarySize = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "dictionary_size");
                    break;
                case 2:
                {
                    MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "frames");
                    ZstdFrameMetadata frame = ZstdFrameMetadata.Read(ref reader);
                    if ((uint)count >= (uint)frames.Length)
                    {
                        MetadataProto.ThrowTooManyElements(MessageName, "frames", frames.Length);
                    }

                    frames[count++] = frame;
                    break;
                }

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ZstdMetadata(dictionarySize, count);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    /// <param name="frames">The frames to emit; its length must equal <see cref="FrameCount"/>.</param>
    /// <exception cref="ArgumentException">The frame count disagrees with the metadata.</exception>
    public static void Write(ref ProtoWriter writer, in ZstdMetadata value, ReadOnlySpan<ZstdFrameMetadata> frames)
    {
        if (frames.Length != value.FrameCount)
        {
            throw new ArgumentException(
                $"Expected {value.FrameCount} frames, got {frames.Length}.", nameof(frames));
        }

        writer.WriteUInt32(1, value.DictionarySize);
        for (int i = 0; i < frames.Length; i++)
        {
            ZstdFrameMetadata frame = frames[i];
            ZstdFrameMetadata.Write(ref writer, 2, in frame);
        }
    }

    /// <inheritdoc/>
    public bool Equals(ZstdMetadata other) =>
        DictionarySize == other.DictionarySize && FrameCount == other.FrameCount;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZstdMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DictionarySize, FrameCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ZstdMetadata left, ZstdMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ZstdMetadata left, ZstdMetadata right) => !left.Equals(right);
}
