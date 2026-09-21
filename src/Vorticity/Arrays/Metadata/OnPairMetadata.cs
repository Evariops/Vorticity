using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.onpair</c> metadata.
/// </summary>
/// <remarks>
/// <code>
/// message OnPairMetadata {
///   PType  uncompressed_lengths_ptype = 1;
///   // tag 2 unused — a removed field. Do not renumber.
///   uint32 dict_size                  = 3;   // dict_offsets has length dict_size + 1
///   uint64 codes_len                  = 4;
///   PType  dict_offsets_ptype         = 5;
///   PType  codes_ptype                = 6;
///   PType  codes_offsets_ptype        = 7;
/// }
/// </code>
/// Tag 2 is skipped by the ordinary unknown-field path, which is exactly right: a future reuse of
/// the number must not fail the read.
/// </remarks>
internal readonly struct OnPairMetadata : IEquatable<OnPairMetadata>
{
    private const string MessageName = "OnPairMetadata";

    /// <summary>Creates on-pair metadata.</summary>
    /// <param name="uncompressedLengthsPType">Physical type of the uncompressed-lengths child (tag 1).</param>
    /// <param name="dictionarySize">Number of dictionary tokens (tag 3).</param>
    /// <param name="codesLength">Length of the codes child (tag 4).</param>
    /// <param name="dictionaryOffsetsPType">Physical type of the dictionary-offsets child (tag 5).</param>
    /// <param name="codesPType">Physical type of the codes child (tag 6).</param>
    /// <param name="codesOffsetsPType">Physical type of the code-offsets child (tag 7).</param>
    /// <exception cref="ArgumentOutOfRangeException">A physical type is undefined.</exception>
    public OnPairMetadata(
        PType uncompressedLengthsPType,
        uint dictionarySize,
        ulong codesLength,
        PType dictionaryOffsetsPType,
        PType codesPType,
        PType codesOffsetsPType)
    {
        Require(uncompressedLengthsPType, nameof(uncompressedLengthsPType));
        Require(dictionaryOffsetsPType, nameof(dictionaryOffsetsPType));
        Require(codesPType, nameof(codesPType));
        Require(codesOffsetsPType, nameof(codesOffsetsPType));
        UncompressedLengthsPType = uncompressedLengthsPType;
        DictionarySize = dictionarySize;
        CodesLength = codesLength;
        DictionaryOffsetsPType = dictionaryOffsetsPType;
        CodesPType = codesPType;
        CodesOffsetsPType = codesOffsetsPType;
    }

    /// <summary>Physical type of the per-row uncompressed-lengths child (tag 1).</summary>
    public PType UncompressedLengthsPType { get; }

    /// <summary>Number of dictionary tokens (tag 3); the offsets child has <c>DictionarySize + 1</c> entries.</summary>
    public uint DictionarySize { get; }

    /// <summary>Length of the codes child (tag 4).</summary>
    public ulong CodesLength { get; }

    /// <summary>Physical type of the dictionary-offsets child (tag 5).</summary>
    public PType DictionaryOffsetsPType { get; }

    /// <summary>Physical type of the codes child (tag 6).</summary>
    public PType CodesPType { get; }

    /// <summary>Physical type of the code-offsets child (tag 7).</summary>
    public PType CodesOffsetsPType { get; }

    /// <summary>Reads a <c>vortex.onpair</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or a physical type is undefined.</exception>
    public static OnPairMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        PType lengths = PType.U8;
        uint dictionarySize = 0;
        ulong codesLength = 0;
        PType dictionaryOffsets = PType.U8;
        PType codes = PType.U8;
        PType codesOffsets = PType.U8;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    lengths = MetadataProto.ReadPType(ref reader, wire, MessageName, "uncompressed_lengths_ptype");
                    break;
                case 3:
                    dictionarySize = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "dict_size");
                    break;
                case 4:
                    codesLength = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "codes_len");
                    break;
                case 5:
                    dictionaryOffsets =
                        MetadataProto.ReadPType(ref reader, wire, MessageName, "dict_offsets_ptype");
                    break;
                case 6:
                    codes = MetadataProto.ReadPType(ref reader, wire, MessageName, "codes_ptype");
                    break;
                case 7:
                    codesOffsets = MetadataProto.ReadPType(ref reader, wire, MessageName, "codes_offsets_ptype");
                    break;
                default:
                    // Tag 2 lands here, along with every genuinely unknown number.
                    reader.SkipField(wire);
                    break;
            }
        }

        return new OnPairMetadata(lengths, dictionarySize, codesLength, dictionaryOffsets, codes, codesOffsets);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in OnPairMetadata value)
    {
        writer.WriteEnum(1, (int)value.UncompressedLengthsPType);
        writer.WriteUInt32(3, value.DictionarySize);
        writer.WriteUInt64(4, value.CodesLength);
        writer.WriteEnum(5, (int)value.DictionaryOffsetsPType);
        writer.WriteEnum(6, (int)value.CodesPType);
        writer.WriteEnum(7, (int)value.CodesOffsetsPType);
    }

    /// <inheritdoc/>
    public bool Equals(OnPairMetadata other) =>
        UncompressedLengthsPType == other.UncompressedLengthsPType
        && DictionarySize == other.DictionarySize
        && CodesLength == other.CodesLength
        && DictionaryOffsetsPType == other.DictionaryOffsetsPType
        && CodesPType == other.CodesPType
        && CodesOffsetsPType == other.CodesOffsetsPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is OnPairMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            UncompressedLengthsPType, DictionarySize, CodesLength,
            DictionaryOffsetsPType, CodesPType, CodesOffsetsPType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(OnPairMetadata left, OnPairMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(OnPairMetadata left, OnPairMetadata right) => !left.Equals(right);

    private static void Require(PType ptype, string parameterName)
    {
        if (!PTypeExtensions.IsDefined(ptype))
        {
            throw new ArgumentOutOfRangeException(parameterName, ptype, "Undefined PType.");
        }
    }
}
