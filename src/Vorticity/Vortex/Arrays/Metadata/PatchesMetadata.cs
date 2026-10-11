using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// The patch descriptor shared by every encoding that carries exceptions out of line: bit packing,
/// the two floating-point encodings and the sparse encoding all embed this same message.
/// </summary>
/// <remarks>
/// <code>
/// message PatchesMetadata {
///   uint64 len                          = 1;
///   uint64 offset                       = 2;
///   PType  indices_ptype                = 3;
///   optional uint64 chunk_offsets_len   = 4;
///   optional PType  chunk_offsets_ptype = 5;
///   optional uint64 offset_within_chunk = 6;
/// }
/// </code>
/// </remarks>
internal readonly struct PatchesMetadata : IEquatable<PatchesMetadata>
{
    private const string MessageName = "PatchesMetadata";

    /// <summary>
    /// One patch index offset is stored per chunk of this many rows, which is what makes patch
    /// lookup constant time.
    /// </summary>
    public const int ChunkSize = 1024;

    private readonly ulong _length;
    private readonly ulong _offset;
    private readonly ulong _chunkOffsetsLength;
    private readonly ulong _offsetWithinChunk;
    private readonly PType _indicesPType;
    private readonly PType _chunkOffsetsPType;
    private readonly bool _hasChunkOffsetsPType;
    private readonly bool _hasChunkOffsetsLength;
    private readonly bool _hasOffsetWithinChunk;

    private PatchesMetadata(
        ulong length,
        ulong offset,
        PType indicesPType,
        bool hasChunkOffsetsLength,
        ulong chunkOffsetsLength,
        bool hasChunkOffsetsPType,
        PType chunkOffsetsPType,
        bool hasOffsetWithinChunk,
        ulong offsetWithinChunk)
    {
        _length = length;
        _offset = offset;
        _indicesPType = indicesPType;
        _hasChunkOffsetsLength = hasChunkOffsetsLength;
        _chunkOffsetsLength = chunkOffsetsLength;
        _hasChunkOffsetsPType = hasChunkOffsetsPType;
        _chunkOffsetsPType = chunkOffsetsPType;
        _hasOffsetWithinChunk = hasOffsetWithinChunk;
        _offsetWithinChunk = offsetWithinChunk;
    }

    /// <summary>Builds a descriptor without chunk offsets.</summary>
    /// <param name="length">Number of patches (tag 1).</param>
    /// <param name="offset">Row offset the patch indices are relative to (tag 2).</param>
    /// <param name="indicesPType">Physical type of the indices child; must be unsigned (tag 3).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="indicesPType"/> is undefined or signed.</exception>
    public static PatchesMetadata Create(ulong length, ulong offset, PType indicesPType)
    {
        RequireUnsignedArgument(indicesPType);
        return new PatchesMetadata(length, offset, indicesPType, false, 0, false, default, false, 0);
    }

    /// <summary>Builds a descriptor with chunk offsets.</summary>
    /// <param name="length">Number of patches (tag 1).</param>
    /// <param name="offset">Row offset the patch indices are relative to (tag 2).</param>
    /// <param name="indicesPType">Physical type of the indices child; must be unsigned (tag 3).</param>
    /// <param name="chunkOffsetsLength">Length of the chunk-offsets child (tag 4).</param>
    /// <param name="chunkOffsetsPType">Physical type of the chunk-offsets child (tag 5).</param>
    /// <param name="offsetWithinChunk">Offset of the first row within its chunk (tag 6), or null.</param>
    /// <exception cref="ArgumentOutOfRangeException">A physical type is undefined, or the indices type is signed.</exception>
    public static PatchesMetadata CreateChunked(
        ulong length,
        ulong offset,
        PType indicesPType,
        ulong chunkOffsetsLength,
        PType chunkOffsetsPType,
        ulong? offsetWithinChunk)
    {
        RequireUnsignedArgument(indicesPType);
        if (!PTypeExtensions.IsDefined(chunkOffsetsPType))
        {
            throw new ArgumentOutOfRangeException(nameof(chunkOffsetsPType), chunkOffsetsPType, "Undefined PType.");
        }

        return new PatchesMetadata(
            length, offset, indicesPType,
            true, chunkOffsetsLength,
            true, chunkOffsetsPType,
            offsetWithinChunk.HasValue, offsetWithinChunk.GetValueOrDefault());
    }

    /// <summary>Number of patches (tag 1, <c>len</c>).</summary>
    public ulong Length => _length;

    /// <summary>Row offset the stored patch indices are relative to (tag 2).</summary>
    public ulong Offset => _offset;

    /// <summary>
    /// Physical type of the indices child (tag 3). Always one of U8, U16, U32, U64: patch indices
    /// must be unsigned.
    /// </summary>
    public PType IndicesPType => _indicesPType;

    /// <summary>
    /// True when the patches carry a chunk-offsets child.
    /// </summary>
    /// <remarks>
    /// This is the presence of <b>tag 5</b>, <c>chunk_offsets_ptype</c>, because the number of
    /// children a patched node carries is decided by that field and not by tag 4. Tag 4 is read
    /// independently and defaults to zero when absent. Every writer emits the two together, so the
    /// two possible readings agree on every real file; see <see cref="HasChunkOffsetsLength"/> when
    /// the difference matters.
    /// </remarks>
    public bool HasChunkOffsets => _hasChunkOffsetsPType;

    /// <summary>True when tag 4 was present, independently of tag 5.</summary>
    public bool HasChunkOffsetsLength => _hasChunkOffsetsLength;

    /// <summary>Length of the chunk-offsets child (tag 4); zero when the field is absent.</summary>
    public ulong ChunkOffsetsLength => _chunkOffsetsLength;

    /// <summary>
    /// Physical type of the chunk-offsets child (tag 5). Meaningful only when
    /// <see cref="HasChunkOffsets"/> is true.
    /// </summary>
    public PType ChunkOffsetsPType => _chunkOffsetsPType;

    /// <summary>True when tag 6 was present.</summary>
    public bool HasOffsetWithinChunk => _hasOffsetWithinChunk;

    /// <summary>Offset of the first row within its patch chunk (tag 6); zero when absent.</summary>
    public ulong OffsetWithinChunk => _offsetWithinChunk;

    /// <summary>
    /// Reads the nested <c>PatchesMetadata</c> message that the tag just returned by
    /// <see cref="ProtoReader.TryReadTag"/> introduces: the reader must be positioned on the
    /// field's length prefix, and this method consumes the prefix and the whole body.
    /// </summary>
    /// <param name="reader">Reader positioned immediately after a length-delimited field tag.</param>
    /// <exception cref="VortexFormatException">
    /// The body is truncated, a known field carries the wrong wire type, or
    /// <c>indices_ptype</c> is undefined or signed.
    /// </exception>
    public static PatchesMetadata Read(ref ProtoReader reader)
    {
        ProtoReader body = reader.ReadMessage();
        return ReadBody(ref body);
    }

    /// <summary>Reads a <c>PatchesMetadata</c> body that has already been unwrapped.</summary>
    /// <param name="body">A reader over the message body alone, with no length prefix.</param>
    /// <exception cref="VortexFormatException">The body is malformed.</exception>
    public static PatchesMetadata ReadBody(ref ProtoReader body)
    {
        ulong length = 0;
        ulong offset = 0;
        PType indicesPType = PType.U8;
        bool hasChunkOffsetsLength = false;
        ulong chunkOffsetsLength = 0;
        bool hasChunkOffsetsPType = false;
        PType chunkOffsetsPType = PType.U8;
        bool hasOffsetWithinChunk = false;
        ulong offsetWithinChunk = 0;

        while (body.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    length = MetadataProto.ReadUInt64(ref body, wire, MessageName, "len");
                    break;
                case 2:
                    offset = MetadataProto.ReadUInt64(ref body, wire, MessageName, "offset");
                    break;
                case 3:
                    indicesPType = MetadataProto.ReadPType(ref body, wire, MessageName, "indices_ptype");
                    break;
                case 4:
                    chunkOffsetsLength =
                        MetadataProto.ReadUInt64(ref body, wire, MessageName, "chunk_offsets_len");
                    hasChunkOffsetsLength = true;
                    break;
                case 5:
                    chunkOffsetsPType =
                        MetadataProto.ReadPType(ref body, wire, MessageName, "chunk_offsets_ptype");
                    hasChunkOffsetsPType = true;
                    break;
                case 6:
                    offsetWithinChunk =
                        MetadataProto.ReadUInt64(ref body, wire, MessageName, "offset_within_chunk");
                    hasOffsetWithinChunk = true;
                    break;
                default:
                    body.SkipField(wire);
                    break;
            }
        }

        // The indices ptype decides how many bytes per patch index the decoder reads out of the
        // child buffer, so a signed type is rejected rather than reinterpreted.
        if (!indicesPType.IsUnsignedInteger())
        {
            MetadataProto.ThrowOutOfDomain(
                MessageName, "indices_ptype", $"{indicesPType.Name()} is not unsigned; patch indices must be.");
        }

        return new PatchesMetadata(
            length, offset, indicesPType,
            hasChunkOffsetsLength, chunkOffsetsLength,
            hasChunkOffsetsPType, chunkOffsetsPType,
            hasOffsetWithinChunk, offsetWithinChunk);
    }

    /// <summary>
    /// Writes this descriptor as a nested message under <paramref name="fieldNumber"/>.
    /// </summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="fieldNumber">The enclosing message's field number for this descriptor.</param>
    /// <param name="value">The descriptor.</param>
    public static void Write(ref ProtoWriter writer, int fieldNumber, in PatchesMetadata value)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(fieldNumber);
        WriteBody(ref writer, in value);
        scope.End();
    }

    /// <summary>Writes the descriptor body with no tag and no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The descriptor.</param>
    public static void WriteBody(ref ProtoWriter writer, in PatchesMetadata value)
    {
        writer.WriteUInt64(1, value._length);
        writer.WriteUInt64(2, value._offset);
        writer.WriteEnum(3, (int)value._indicesPType);
        if (value._hasChunkOffsetsLength)
        {
            writer.WriteUInt64Always(4, value._chunkOffsetsLength);
        }

        if (value._hasChunkOffsetsPType)
        {
            writer.WriteEnumAlways(5, (int)value._chunkOffsetsPType);
        }

        if (value._hasOffsetWithinChunk)
        {
            writer.WriteUInt64Always(6, value._offsetWithinChunk);
        }
    }

    /// <inheritdoc/>
    public bool Equals(PatchesMetadata other) =>
        _length == other._length
        && _offset == other._offset
        && _indicesPType == other._indicesPType
        && _hasChunkOffsetsLength == other._hasChunkOffsetsLength
        && _chunkOffsetsLength == other._chunkOffsetsLength
        && _hasChunkOffsetsPType == other._hasChunkOffsetsPType
        && (!_hasChunkOffsetsPType || _chunkOffsetsPType == other._chunkOffsetsPType)
        && _hasOffsetWithinChunk == other._hasOffsetWithinChunk
        && _offsetWithinChunk == other._offsetWithinChunk;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PatchesMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(_length);
        hash.Add(_offset);
        hash.Add(_indicesPType);
        hash.Add(_hasChunkOffsetsLength);
        hash.Add(_chunkOffsetsLength);
        hash.Add(_hasChunkOffsetsPType);
        hash.Add(_hasChunkOffsetsPType ? _chunkOffsetsPType : default);
        hash.Add(_hasOffsetWithinChunk);
        hash.Add(_offsetWithinChunk);
        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PatchesMetadata left, PatchesMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PatchesMetadata left, PatchesMetadata right) => !left.Equals(right);

    private static void RequireUnsignedArgument(PType indicesPType)
    {
        if (!PTypeExtensions.IsDefined(indicesPType) || !indicesPType.IsUnsignedInteger())
        {
            throw new ArgumentOutOfRangeException(
                nameof(indicesPType), indicesPType, "Patch indices must be an unsigned integer PType.");
        }
    }
}
