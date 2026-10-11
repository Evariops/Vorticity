using System;

namespace Vorticity.Types.Variant;

/// <summary>
/// A variant object or array read in place: its count, an object's field ids, the offsets of its
/// values, and the values they address.
/// </summary>
/// <remarks>
/// An object's offsets follow its fields' name order, not the order of its values, so they need
/// not increase: a value's end is not the next offset but what its own encoding says, and
/// <see cref="ValueAt"/> reads it there.
/// </remarks>
internal readonly ref struct VariantNested
{
    private readonly ReadOnlySpan<byte> _ids;
    private readonly ReadOnlySpan<byte> _offsets;
    private readonly ReadOnlySpan<byte> _values;
    private readonly int _idWidth;
    private readonly int _offsetWidth;

    private VariantNested(ReadOnlySpan<byte> ids, ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> values, int idWidth, int offsetWidth, int count, int size)
    {
        _ids = ids;
        _offsets = offsets;
        _values = values;
        _idWidth = idWidth;
        _offsetWidth = offsetWidth;
        Count = count;
        Size = size;
    }

    /// <summary>How many fields or elements it holds.</summary>
    internal int Count { get; }

    /// <summary>The bytes the whole value takes, from its header to the end of its values.</summary>
    internal int Size { get; }

    /// <summary>Reads the object or the array <paramref name="value"/> starts with.</summary>
    /// <param name="value">A value whose basic type is an object or an array.</param>
    /// <exception cref="VortexFormatException">The value is cut short.</exception>
    internal static VariantNested Read(ReadOnlySpan<byte> value)
    {
        byte header = value[0];
        int flags = header >> 2;
        bool isObject = (header & 0x03) == ParquetVariant.BasicObject;
        int offsetWidth = (flags & 0x03) + 1;
        int idWidth = isObject ? ((flags >> 2) & 0x03) + 1 : 0;
        int countWidth = ((isObject ? flags >> 4 : flags >> 2) & 0x01) != 0 ? 4 : 1;
        if (value.Length < 1 + countWidth)
        {
            throw Cut(value.Length, 1L + countWidth);
        }

        long count = ParquetVariant.Unsigned(value.Slice(1, countWidth));
        long offsetsAt = 1L + countWidth + (count * idWidth);
        long valuesAt = offsetsAt + ((count + 1) * offsetWidth);
        if (valuesAt > value.Length)
        {
            throw Cut(value.Length, valuesAt);
        }

        ReadOnlySpan<byte> offsets = value[(int)offsetsAt..(int)valuesAt];
        long size = valuesAt + ParquetVariant.Unsigned(offsets.Slice((int)count * offsetWidth, offsetWidth));
        if (size > value.Length)
        {
            throw Cut(value.Length, size);
        }

        return new VariantNested(
            value[(1 + countWidth)..(int)offsetsAt], offsets, value[(int)valuesAt..(int)size], idWidth, offsetWidth, (int)count, (int)size);
    }

    /// <summary>The dictionary id of an object's field <paramref name="index"/>.</summary>
    internal int IdAt(int index) => (int)ParquetVariant.Unsigned(_ids.Slice(index * _idWidth, _idWidth));

    /// <summary>The value of field or element <paramref name="index"/>, as many bytes as its encoding takes.</summary>
    /// <exception cref="VortexFormatException">Its offset or its encoding runs past the values.</exception>
    internal ReadOnlySpan<byte> ValueAt(int index)
    {
        uint start = ParquetVariant.Unsigned(_offsets.Slice(index * _offsetWidth, _offsetWidth));
        if (start >= (uint)_values.Length)
        {
            throw new VortexFormatException(
                $"A variant's value {index} starts at {start}, past the {_values.Length} bytes of its values.");
        }

        ReadOnlySpan<byte> rest = _values[(int)start..];
        return rest[..ParquetVariant.SizeOf(rest)];
    }

    private static VortexFormatException Cut(int length, long needed) =>
        new($"A variant object or array of {length} bytes is cut short of the {needed} its header declares.");
}
