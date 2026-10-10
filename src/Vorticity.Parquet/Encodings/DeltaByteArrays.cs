using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

    /// <summary>The bytes past a page's values a heap holds for <see cref="Rebuild"/> to copy its last values a vector at a time too.</summary>
    internal const int RebuildSlack = 16;

    /// <summary>
    /// Rebuilds a DELTA_BYTE_ARRAY page's values back to back into <paramref name="heap"/>, which
    /// holds the bytes <see cref="DecodePrefixes"/> counted, and writes each value's length. The
    /// lengths must be those <see cref="DecodePrefixes"/> checked.
    /// </summary>
    /// <remarks>
    /// A value's parts are copied sixteen bytes at a time, up to sixteen past their end, which the
    /// next part writes over, where the heap and the page hold sixteen bytes past them: its prefix
    /// from the head of the value before it, whose bytes it needs lie before the copy's destination,
    /// so that none is written before it is read, and its suffix from the page, its first sixteen
    /// bytes whatever its length. Nearer either end, the parts are copied as they are;
    /// <see cref="RebuildSlack"/> bytes past the heap's values spare its last ones that. A page of
    /// short values took 6.3 ns a value copied part by part as spans, a call for each, and 3.5 this
    /// way; copying 32 bytes of each part whatever its length took 4.7, the prefix's second load
    /// reading bytes the stores before it had not yet written to the cache.
    /// </remarks>
    internal static unsafe void Rebuild(ReadOnlySpan<byte> suffixData, ReadOnlySpan<int> prefixes, ReadOnlySpan<int> suffixes, Span<byte> heap, Span<int> lengths)
    {
        int count = prefixes.Length;
        if (suffixes.Length < count || lengths.Length < count)
        {
            throw new ArgumentException("A value has no suffix or length to take.", nameof(suffixes));
        }

        fixed (byte* heapStart = heap)
        fixed (byte* data = suffixData)
        {
            byte* written = heapStart;
            byte* previous = heapStart;
            byte* heapEnd = heapStart + heap.Length;
            byte* read = data;
            byte* readEnd = data + suffixData.Length;
            ref int prefixRef = ref MemoryMarshal.GetReference(prefixes);
            ref int suffixRef = ref MemoryMarshal.GetReference(suffixes);
            ref int lengthRef = ref MemoryMarshal.GetReference(lengths);
            for (int i = 0; i < count; i++)
            {
                nint prefix = Unsafe.Add(ref prefixRef, i);
                nint suffix = Unsafe.Add(ref suffixRef, i);
                byte* into = written + prefix;
                if (heapEnd - written >= prefix + suffix + RebuildSlack && readEnd - read >= suffix + RebuildSlack)
                {
                    for (nint k = 0; k < prefix; k += 16)
                    {
                        Vector128.Store(Vector128.Load(previous + k), written + k);
                    }

                    Vector128.Store(Vector128.Load(read), into);
                    for (nint k = 16; k < suffix; k += 16)
                    {
                        Vector128.Store(Vector128.Load(read + k), into + k);
                    }
                }
                else
                {
                    if (heapEnd - written < prefix + suffix || readEnd - read < suffix)
                    {
                        throw new ArgumentException("The heap or the suffixes' bytes are shorter than the lengths take.", nameof(heap));
                    }

                    Buffer.MemoryCopy(previous, written, prefix, prefix);
                    Buffer.MemoryCopy(read, into, suffix, suffix);
                }

                previous = written;
                Unsafe.Add(ref lengthRef, i) = (int)(prefix + suffix);
                written = into + suffix;
                read += suffix;
            }
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
