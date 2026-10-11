using System;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>
/// A column chunk's <c>BloomFilterHeader</c>, which its bitset follows: the standard's split-block
/// filter of xxHash64, uncompressed, the one layout the standard defines.
/// </summary>
internal static class BloomFilterHeader
{
    /// <summary>The fewest bytes a bitset holds: one block.</summary>
    internal const int MinimumBytes = 32;

    /// <summary>The most bytes a bitset may hold, as the standard bounds it.</summary>
    internal const int MaximumBytes = 128 << 20;

    /// <summary>
    /// Reads a header at the head of <paramref name="bytes"/>: its length and its bitset's, when it
    /// is the split-block filter of xxHash64, uncompressed, of a power of two of bytes the standard
    /// allows. False for any other filter, which says nothing a reader may use.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> bytes, out int headerLength, out int bitsetBytes)
    {
        ThriftCompactReader reader = new(bytes);
        headerLength = 0;
        bitsetBytes = -1;
        bool block = false;
        bool xxHash = false;
        bool uncompressed = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1 when type == ThriftType.I32:
                    bitsetBytes = reader.ReadI32();
                    break;
                case 2 when type == ThriftType.Struct:
                    block = Member(ref reader, 1);
                    break;
                case 3 when type == ThriftType.Struct:
                    xxHash = Member(ref reader, 1);
                    break;
                case 4 when type == ThriftType.Struct:
                    uncompressed = Member(ref reader, 1);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        headerLength = reader.Position;
        return block && xxHash && uncompressed
            && bitsetBytes is >= MinimumBytes and <= MaximumBytes
            && (bitsetBytes & (bitsetBytes - 1)) == 0;
    }

    /// <summary>Writes the header of a split-block filter of xxHash64, uncompressed, of <paramref name="bitsetBytes"/> bytes.</summary>
    internal static void Write(ref ThriftCompactWriter writer, int bitsetBytes)
    {
        short saved = writer.BeginStruct();
        writer.WriteI32Field(1, bitsetBytes);
        EmptyMember(ref writer, 2);
        EmptyMember(ref writer, 3);
        EmptyMember(ref writer, 4);
        writer.EndStruct(saved);
    }

    /// <summary>A union field holding the empty struct of its member 1.</summary>
    private static void EmptyMember(ref ThriftCompactWriter writer, short field)
    {
        short union = writer.BeginStructField(field);
        short member = writer.BeginStructField(1);
        writer.EndStruct(member);
        writer.EndStruct(union);
    }

    /// <summary>Whether a union holds the member <paramref name="expected"/> and no other.</summary>
    private static bool Member(ref ThriftCompactReader reader, short expected)
    {
        bool found = false;
        bool other = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id == expected)
            {
                found = true;
            }
            else
            {
                other = true;
            }

            reader.Skip(type);
        }

        reader.ExitStruct(saved);
        return found && !other;
    }
}
