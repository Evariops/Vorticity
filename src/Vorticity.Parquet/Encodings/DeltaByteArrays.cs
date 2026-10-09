using System;
using System.Runtime.InteropServices;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// The two delta encodings of byte arrays, read and written. DELTA_LENGTH_BYTE_ARRAY: the lengths,
/// DELTA_BINARY_PACKED, then the values' bytes back to back. DELTA_BYTE_ARRAY: per value, the length
/// of the prefix it shares with the value before it and the length of the rest, both
/// DELTA_BINARY_PACKED, then the rests back to back.
/// </summary>
/// <remarks>
/// A length-prefixed page's bytes are its values' already, so a reader cuts views over them where
/// they lie; a front-coded page's values exist only once rebuilt, each from the one before it, into
/// a heap of their own whose size the lengths give before anything is copied.
/// </remarks>
internal static class DeltaByteArrays
{
    /// <summary>
    /// Reads a DELTA_LENGTH_BYTE_ARRAY page's lengths into all of <paramref name="lengths"/>; where
    /// its values' bytes start, which run to the page's end and which the lengths exactly tile.
    /// </summary>
    /// <exception cref="ParquetFormatException">A length is negative, or they do not tile the page's bytes.</exception>
    internal static int DecodeLengths(ReadOnlySpan<byte> data, Span<int> lengths)
    {
        int start = DeltaBinaryPacked.Decode32(data, lengths);
        long total = Sum(lengths, "DELTA_LENGTH_BYTE_ARRAY");
        if (total != data.Length - start)
        {
            ParquetThrow.Format($"A DELTA_LENGTH_BYTE_ARRAY page's lengths come to {total} bytes where it holds {data.Length - start}.");
        }

        return start;
    }

    /// <summary>
    /// Reads a DELTA_BYTE_ARRAY page's prefix and suffix lengths, each value's prefix checked against
    /// the value before it; the bytes its values take rebuilt, and where its suffixes' bytes start.
    /// </summary>
    /// <exception cref="ParquetFormatException">A length is negative, a prefix is longer than the value before it, or the suffixes do not tile the page's bytes.</exception>
    internal static long DecodePrefixes(ReadOnlySpan<byte> data, Span<int> prefixes, Span<int> suffixes, out int suffixStart)
    {
        int at = DeltaBinaryPacked.Decode32(data, prefixes);
        at += DeltaBinaryPacked.Decode32(data[at..], suffixes);
        long suffixBytes = Sum(suffixes, "DELTA_BYTE_ARRAY");
        if (suffixBytes != data.Length - at)
        {
            ParquetThrow.Format($"A DELTA_BYTE_ARRAY page's suffixes come to {suffixBytes} bytes where it holds {data.Length - at}.");
        }

        long total = 0;
        int previous = 0;
        for (int i = 0; i < prefixes.Length; i++)
        {
            int prefix = prefixes[i];
            if (prefix < 0 || prefix > previous)
            {
                ParquetThrow.Format($"Value {i} of a DELTA_BYTE_ARRAY page shares {prefix} bytes with a value of {previous}.");
            }

            previous = prefix + suffixes[i];
            total += previous;
        }

        suffixStart = at;
        return total;
    }

    /// <summary>
    /// Rebuilds a DELTA_BYTE_ARRAY page's values back to back into <paramref name="heap"/>, which
    /// holds the bytes <see cref="DecodePrefixes"/> counted, and writes each value's length.
    /// </summary>
    internal static void Rebuild(ReadOnlySpan<byte> suffixData, ReadOnlySpan<int> prefixes, ReadOnlySpan<int> suffixes, Span<byte> heap, Span<int> lengths)
    {
        int written = 0;
        int previousStart = 0;
        int read = 0;
        for (int i = 0; i < prefixes.Length; i++)
        {
            int prefix = prefixes[i];
            int suffix = suffixes[i];

            // The prefix is the head of the value before, already in the heap: the copy runs forward
            // and never overlaps the bytes it reads.
            heap.Slice(previousStart, prefix).CopyTo(heap.Slice(written, prefix));
            suffixData.Slice(read, suffix).CopyTo(heap.Slice(written + prefix, suffix));
            previousStart = written;
            lengths[i] = prefix + suffix;
            written += prefix + suffix;
            read += suffix;
        }
    }

    /// <summary>The bytes <see cref="EncodeLengths"/> writes: the lengths' encoding and the values' bytes.</summary>
    internal static int SizeLengths(ReadOnlySpan<int> lengths, int dataBytes) => DeltaBinaryPacked.Size32(lengths) + dataBytes;

    /// <summary>Writes a DELTA_LENGTH_BYTE_ARRAY page: <paramref name="lengths"/> encoded, then <paramref name="data"/>; the bytes written.</summary>
    internal static int EncodeLengths(ReadOnlySpan<int> lengths, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int written = DeltaBinaryPacked.Encode32(lengths, destination);
        data.CopyTo(destination[written..]);
        return written + data.Length;
    }

    /// <summary>
    /// Splits values, given by their <paramref name="lengths"/> over <paramref name="data"/>, into the
    /// prefix each shares with the value before it and the length of its rest; the rests' bytes.
    /// </summary>
    internal static int Prefixes(ReadOnlySpan<byte> data, ReadOnlySpan<int> lengths, Span<int> prefixes, Span<int> suffixes)
    {
        int start = 0;
        int previousStart = 0;
        int previousLength = 0;
        int rest = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            ReadOnlySpan<byte> value = data.Slice(start, length);
            int shared = value.CommonPrefixLength(data.Slice(previousStart, previousLength));
            prefixes[i] = shared;
            suffixes[i] = length - shared;
            rest += length - shared;
            previousStart = start;
            previousLength = length;
            start += length;
        }

        return rest;
    }

    /// <summary>The bytes <see cref="EncodePrefixes"/> writes.</summary>
    internal static int SizePrefixes(ReadOnlySpan<int> prefixes, ReadOnlySpan<int> suffixes, int suffixBytes) =>
        DeltaBinaryPacked.Size32(prefixes) + DeltaBinaryPacked.Size32(suffixes) + suffixBytes;

    /// <summary>Writes a DELTA_BYTE_ARRAY page: the prefixes, the suffix lengths, then each value's rest; the bytes written.</summary>
    internal static int EncodePrefixes(ReadOnlySpan<byte> data, ReadOnlySpan<int> lengths, ReadOnlySpan<int> prefixes, ReadOnlySpan<int> suffixes, Span<byte> destination)
    {
        int written = DeltaBinaryPacked.Encode32(prefixes, destination);
        written += DeltaBinaryPacked.Encode32(suffixes, destination[written..]);
        int start = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            data.Slice(start + prefixes[i], suffixes[i]).CopyTo(destination[written..]);
            written += suffixes[i];
            start += lengths[i];
        }

        return written;
    }

    private static long Sum(ReadOnlySpan<int> lengths, string encoding)
    {
        long total = 0;
        foreach (int length in lengths)
        {
            if (length < 0)
            {
                ParquetThrow.Format($"A {encoding} page holds a negative length.");
            }

            total += length;
        }

        return total;
    }
}
