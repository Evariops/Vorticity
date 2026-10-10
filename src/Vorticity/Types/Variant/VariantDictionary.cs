using System;

namespace Vorticity.Types.Variant;

/// <summary>
/// A variant's metadata read in place: the header, then the dictionary of strings an object's
/// field ids index.
/// </summary>
/// <remarks>
/// Nothing is copied: a string is a slice of the metadata, its offsets read when it is asked for.
/// The offsets of the whole dictionary are checked to fit when it is read, and a string's own
/// bounds when it is read, so that a malformed entry fails the value that names it and no other.
/// </remarks>
internal readonly ref struct VariantDictionary
{
    /// <summary>The only metadata version the encoding defines.</summary>
    private const int Version = 1;

    private readonly ReadOnlySpan<byte> _offsets;
    private readonly ReadOnlySpan<byte> _strings;
    private readonly int _width;

    private VariantDictionary(ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> strings, int width, int count, bool sorted)
    {
        _offsets = offsets;
        _strings = strings;
        _width = width;
        Count = count;
        Sorted = sorted;
    }

    /// <summary>How many strings the dictionary holds.</summary>
    internal int Count { get; }

    /// <summary>Whether the writer declared the strings unique and sorted, which a lookup then searches by halves.</summary>
    internal bool Sorted { get; }

    /// <summary>Reads <paramref name="metadata"/>'s header and checks that its offsets fit.</summary>
    /// <exception cref="VortexFormatException">The metadata is malformed.</exception>
    /// <exception cref="VortexUnsupportedException">The version is not 1.</exception>
    internal static VariantDictionary Read(ReadOnlySpan<byte> metadata)
    {
        if (metadata.IsEmpty)
        {
            throw new VortexFormatException("A variant's metadata is empty.");
        }

        byte header = metadata[0];
        int version = header & 0x0F;
        if (version != Version)
        {
            throw new VortexUnsupportedException(
                "vortex.parquet.variant",
                VortexComponentKind.Array,
                $"the variant metadata declares version {version}; only version 1 is defined.");
        }

        int width = ((header >> 6) & 0x03) + 1;
        if (metadata.Length < 1 + width)
        {
            throw new VortexFormatException(
                $"A variant's metadata is {metadata.Length} bytes; its {width}-byte dictionary size does not fit.");
        }

        long count = ParquetVariant.Unsigned(metadata.Slice(1, width));
        long strings = 1L + width + ((count + 1) * width);
        if (strings > metadata.Length)
        {
            throw new VortexFormatException(
                $"A variant's metadata declares {count} dictionary entries, whose offsets do not fit in {metadata.Length} bytes.");
        }

        return new VariantDictionary(
            metadata[(1 + width)..(int)strings], metadata[(int)strings..], width, (int)count, (header & 0x10) != 0);
    }

    /// <summary>String <paramref name="id"/>, as the UTF-8 bytes the metadata holds.</summary>
    /// <exception cref="VortexFormatException">The id is out of range, or the string's offsets are.</exception>
    internal ReadOnlySpan<byte> this[int id]
    {
        get
        {
            if ((uint)id >= (uint)Count)
            {
                throw new VortexFormatException($"A variant names field id {(uint)id} of a dictionary of {Count} strings.");
            }

            uint start = ParquetVariant.Unsigned(_offsets.Slice(id * _width, _width));
            uint end = ParquetVariant.Unsigned(_offsets.Slice((id + 1) * _width, _width));
            if (start > end || end > (uint)_strings.Length)
            {
                throw new VortexFormatException(
                    $"A variant's dictionary string {id} spans [{start}, {end}) of {_strings.Length} bytes.");
            }

            return _strings[(int)start..(int)end];
        }
    }

    /// <summary>The id of <paramref name="name"/>, or -1 when the dictionary holds no such string.</summary>
    /// <remarks>
    /// A sorted dictionary is searched by halves, in the unsigned order of the strings' bytes the
    /// standard sorts by; an unsorted one from its first string, so that of two equal strings the
    /// first is found.
    /// </remarks>
    internal int Find(ReadOnlySpan<byte> name)
    {
        if (Sorted)
        {
            int low = 0;
            int high = Count - 1;
            while (low <= high)
            {
                int middle = (int)((uint)(low + high) >> 1);
                int order = this[middle].SequenceCompareTo(name);
                if (order == 0)
                {
                    return middle;
                }

                if (order < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return -1;
        }

        for (int id = 0; id < Count; id++)
        {
            if (this[id].SequenceEqual(name))
            {
                return id;
            }
        }

        return -1;
    }
}
