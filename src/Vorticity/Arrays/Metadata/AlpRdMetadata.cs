using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.alprd</c> metadata.
/// </summary>
/// <remarks>
/// <code>
/// message ALPRDMetadata {
///   uint32 right_bit_width  = 1;
///   uint32 dict_len         = 2;
///   repeated uint32 dict    = 3;
///   PType left_parts_ptype  = 4;
///   PatchesMetadata patches = 5;   // optional, though it carries no `optional` keyword
/// }
/// </code>
/// The dictionary is written into a caller-supplied span rather than an array owned by this struct:
/// the entry count is file-supplied, and no cap is invented here. Size the destination with
/// <see cref="CountDictionaryEntries"/>, or with <see cref="TypicalDictionaryLength"/> when reading
/// a file a conformant writer produced.
/// </remarks>
public readonly struct AlpRdMetadata : IEquatable<AlpRdMetadata>
{
    private const string MessageName = "ALPRDMetadata";

    /// <summary>
    /// The dictionary length every observed writer emits: the ALP-RD left-parts dictionary holds at
    /// most eight entries. A hint for sizing a <c>stackalloc</c>, not a validated bound.
    /// </summary>
    public const int TypicalDictionaryLength = 8;

    private readonly PatchesMetadata _patches;

    /// <summary>Creates ALP-RD metadata.</summary>
    /// <param name="rightBitWidth">Width of the right parts in bits (tag 1).</param>
    /// <param name="dictionaryLength">Declared number of usable dictionary entries (tag 2).</param>
    /// <param name="dictionaryEntryCount">Number of entries actually present in tag 3.</param>
    /// <param name="leftPartsPType">Physical type of the left-parts child (tag 4).</param>
    /// <param name="patches">The patch descriptor (tag 5), or null when absent.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="leftPartsPType"/> is undefined.</exception>
    public AlpRdMetadata(
        uint rightBitWidth,
        uint dictionaryLength,
        int dictionaryEntryCount,
        PType leftPartsPType,
        in PatchesMetadata? patches)
    {
        if (!PTypeExtensions.IsDefined(leftPartsPType))
        {
            throw new ArgumentOutOfRangeException(nameof(leftPartsPType), leftPartsPType, "Undefined PType.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dictionaryEntryCount);
        RightBitWidth = rightBitWidth;
        DictionaryLength = dictionaryLength;
        DictionaryEntryCount = dictionaryEntryCount;
        LeftPartsPType = leftPartsPType;
        _patches = patches.GetValueOrDefault();
        HasPatches = patches.HasValue;
    }

    /// <summary>Width of the right parts in bits (tag 1).</summary>
    public uint RightBitWidth { get; }

    /// <summary>
    /// Declared number of usable dictionary entries (tag 2). Only the first
    /// <c>dict_len</c> entries are read, so this must not exceed <see cref="DictionaryEntryCount"/>.
    /// </summary>
    public uint DictionaryLength { get; }

    /// <summary>Number of entries the repeated <c>dict</c> field actually carried (tag 3).</summary>
    public int DictionaryEntryCount { get; }

    /// <summary>Physical type of the left-parts child (tag 4).</summary>
    public PType LeftPartsPType { get; }

    /// <summary>True when tag 5 was present.</summary>
    public bool HasPatches { get; }

    /// <summary>The patch descriptor (tag 5). Meaningful only when <see cref="HasPatches"/> is true.</summary>
    public PatchesMetadata Patches => _patches;

    /// <summary>Counts the <c>dict</c> entries of a payload without decoding the rest of it.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed.</exception>
    public static int CountDictionaryEntries(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        int count = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 3)
            {
                count += MetadataProto.CountRepeatedUInt32(ref reader, wire);
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        return count;
    }

    /// <summary>Reads a <c>vortex.alprd</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <param name="dictionary">
    /// Receives the left-parts dictionary. Must hold at least <see cref="CountDictionaryEntries"/>
    /// elements.
    /// </param>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, the dictionary does not fit in <paramref name="dictionary"/>,
    /// <c>dict_len</c> exceeds the number of entries present, or an entry does not fit in a
    /// <see cref="ushort"/>, which the dictionary's codes are required to do.
    /// </exception>
    public static AlpRdMetadata Read(ReadOnlySpan<byte> metadata, Span<uint> dictionary)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint rightBitWidth = 0;
        uint dictionaryLength = 0;
        int dictionaryCount = 0;
        PType leftPartsPType = PType.U8;
        PatchesMetadata patches = default;
        bool hasPatches = false;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    rightBitWidth = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "right_bit_width");
                    break;
                case 2:
                    dictionaryLength = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "dict_len");
                    break;
                case 3:
                    MetadataProto.ReadRepeatedUInt32(
                        ref reader, wire, MessageName, "dict", dictionary, ref dictionaryCount);
                    break;
                case 4:
                    leftPartsPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "left_parts_ptype");
                    break;
                case 5:
                    MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "patches");
                    patches = PatchesMetadata.Read(ref reader);
                    hasPatches = true;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (dictionaryLength > (uint)dictionaryCount)
        {
            MetadataProto.ThrowOutOfDomain(
                MessageName, "dict_len",
                $"{dictionaryLength} entries are declared but only {dictionaryCount} are present.");
        }

        for (int i = 0; i < dictionaryCount; i++)
        {
            if (dictionary[i] > ushort.MaxValue)
            {
                MetadataProto.ThrowOutOfDomain(
                    MessageName, "dict", $"entry {i} is {dictionary[i]}, which does not fit in u16.");
            }
        }

        PatchesMetadata? optionalPatches = hasPatches ? patches : null;
        return new AlpRdMetadata(
            rightBitWidth, dictionaryLength, dictionaryCount, leftPartsPType, in optionalPatches);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    /// <param name="dictionary">
    /// The dictionary entries to emit; its length must equal <see cref="DictionaryEntryCount"/>.
    /// </param>
    /// <exception cref="ArgumentException">The dictionary length disagrees with the metadata.</exception>
    public static void Write(ref ProtoWriter writer, in AlpRdMetadata value, ReadOnlySpan<uint> dictionary)
    {
        if (dictionary.Length != value.DictionaryEntryCount)
        {
            throw new ArgumentException(
                $"Expected {value.DictionaryEntryCount} dictionary entries, got {dictionary.Length}.",
                nameof(dictionary));
        }

        writer.WriteUInt32(1, value.RightBitWidth);
        writer.WriteUInt32(2, value.DictionaryLength);
        if (dictionary.Length != 0)
        {
            // A repeated numeric field goes out packed, as one length-delimited run of varints.
            ProtoWriter.MessageScope scope = writer.BeginMessage(3);
            for (int i = 0; i < dictionary.Length; i++)
            {
                writer.WriteVarint(dictionary[i]);
            }

            scope.End();
        }

        writer.WriteEnum(4, (int)value.LeftPartsPType);
        if (value.HasPatches)
        {
            PatchesMetadata patches = value._patches;
            PatchesMetadata.Write(ref writer, 5, in patches);
        }
    }

    /// <inheritdoc/>
    public bool Equals(AlpRdMetadata other) =>
        RightBitWidth == other.RightBitWidth
        && DictionaryLength == other.DictionaryLength
        && DictionaryEntryCount == other.DictionaryEntryCount
        && LeftPartsPType == other.LeftPartsPType
        && HasPatches == other.HasPatches
        && (!HasPatches || _patches.Equals(other._patches));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AlpRdMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            RightBitWidth, DictionaryLength, DictionaryEntryCount, LeftPartsPType, HasPatches,
            HasPatches ? _patches.GetHashCode() : 0);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(AlpRdMetadata left, AlpRdMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AlpRdMetadata left, AlpRdMetadata right) => !left.Equals(right);
}
