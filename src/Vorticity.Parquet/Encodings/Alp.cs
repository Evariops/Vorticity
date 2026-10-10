using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// ALP, read: a header of seven bytes, the offsets of the page's vectors, then each vector — its
/// exponent, its factor and its exceptions' count; its frame of reference and its bit width; its
/// deltas bit-packed least significant bit first; its exceptions' positions, then their values'
/// exact bits.
/// </summary>
/// <remarks>
/// A vector decodes as the standard makes normative: each integer, the frame of reference added in
/// wrapping arithmetic, converted to the column's float, then multiplied by 10^factor and by
/// 10^-exponent, two multiplications never fused, by the correctly rounded constants
/// <see cref="AlpTables"/> holds for the core's own ALP, whose order of operations is the same. The
/// exceptions then overwrite their positions with their stored bits, a NaN's payload kept. Every
/// offset, count, exponent and width is checked before it addresses anything: a page must hold its
/// vectors back to back, as the standard lays them, and nothing past the last.
/// </remarks>
internal static class Alp
{
    /// <summary>The header: the compression mode, the integer encoding, the vector size's log and the values.</summary>
    internal const int HeaderBytes = 7;

    /// <summary>The most a FLOAT vector's exponent may be, and a DOUBLE's.</summary>
    private const int MaxExponent32 = 10;
    private const int MaxExponent64 = 18;

    /// <summary>The values each vector of <paramref name="page"/> holds but the last: what a decode's scratch holds.</summary>
    /// <exception cref="ParquetFormatException">The header is cut short or declares a size the standard does not allow.</exception>
    internal static int VectorSize(ReadOnlySpan<byte> page)
    {
        if (page.Length < HeaderBytes)
        {
            ParquetThrow.Truncated("ALP page header");
        }

        int log = page[2];
        if (log is < 3 or > 15)
        {
            ParquetThrow.Format($"An ALP page declares vectors of 2^{log} values, outside the 2^3 to 2^15 the standard allows.");
        }

        return 1 << log;
    }

    /// <summary>Decodes an ALP page of FLOAT values, as many as <paramref name="destination"/> holds, through <paramref name="scratch"/>, a vector's worth.</summary>
    /// <exception cref="ParquetFormatException">The page is not a well-formed ALP page of that many values.</exception>
    internal static void Decode(ReadOnlySpan<byte> page, Span<float> destination, Span<uint> scratch)
    {
        int count = destination.Length;
        int size = VectorSize(page);
        int vectors = Vectors(page, count, size);
        ReadOnlySpan<byte> body = page[HeaderBytes..];
        int at = OffsetsEnd(body, vectors);
        for (int v = 0; v < vectors; v++)
        {
            int values = Math.Min(size, count - (v * size));
            ReadOnlySpan<byte> vector = Vector(body, v, at, 9);
            int exponent = vector[0];
            int factor = vector[1];
            int exceptions = BinaryPrimitives.ReadUInt16LittleEndian(vector[2..]);
            uint reference = BinaryPrimitives.ReadUInt32LittleEndian(vector[4..]);
            int width = vector[8];
            Check(exponent, factor, MaxExponent32, width, 32, exceptions, values);
            int packed = (int)BitPacking.PackedBytes(values, width);
            int length = 9 + packed + (exceptions * (sizeof(ushort) + sizeof(float)));
            if (length > vector.Length)
            {
                ParquetThrow.Truncated("ALP vector");
            }

            Span<uint> deltas = scratch[..values];
            BitPacking.Unpack32(vector.Slice(9, packed), width, deltas);
            AddReference(deltas, reference);
            Span<float> output = destination.Slice(v * size, values);
            AlpTables.DecodeSingle(MemoryMarshal.Cast<uint, int>(deltas), output, exponent, factor);

            ReadOnlySpan<byte> positions = vector.Slice(9 + packed, exceptions * sizeof(ushort));
            ReadOnlySpan<byte> patches = vector.Slice(9 + packed + (exceptions * sizeof(ushort)), exceptions * sizeof(float));
            for (int j = 0; j < exceptions; j++)
            {
                output[Position(positions, j, values)] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(patches[(j * sizeof(float))..]));
            }

            at += length;
        }

        End(body, at);
    }

    /// <summary>Decodes an ALP page of DOUBLE values, as many as <paramref name="destination"/> holds, through <paramref name="scratch"/>, a vector's worth.</summary>
    /// <exception cref="ParquetFormatException">The page is not a well-formed ALP page of that many values.</exception>
    internal static void Decode(ReadOnlySpan<byte> page, Span<double> destination, Span<ulong> scratch)
    {
        int count = destination.Length;
        int size = VectorSize(page);
        int vectors = Vectors(page, count, size);
        ReadOnlySpan<byte> body = page[HeaderBytes..];
        int at = OffsetsEnd(body, vectors);
        for (int v = 0; v < vectors; v++)
        {
            int values = Math.Min(size, count - (v * size));
            ReadOnlySpan<byte> vector = Vector(body, v, at, 13);
            int exponent = vector[0];
            int factor = vector[1];
            int exceptions = BinaryPrimitives.ReadUInt16LittleEndian(vector[2..]);
            ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(vector[4..]);
            int width = vector[12];
            Check(exponent, factor, MaxExponent64, width, 64, exceptions, values);
            int packed = (int)BitPacking.PackedBytes(values, width);
            int length = 13 + packed + (exceptions * (sizeof(ushort) + sizeof(double)));
            if (length > vector.Length)
            {
                ParquetThrow.Truncated("ALP vector");
            }

            Span<ulong> deltas = scratch[..values];
            BitPacking.Unpack64(vector.Slice(13, packed), width, deltas);
            AddReference(deltas, reference);
            Span<double> output = destination.Slice(v * size, values);
            AlpTables.DecodeDouble(MemoryMarshal.Cast<ulong, long>(deltas), output, exponent, factor);

            ReadOnlySpan<byte> positions = vector.Slice(13 + packed, exceptions * sizeof(ushort));
            ReadOnlySpan<byte> patches = vector.Slice(13 + packed + (exceptions * sizeof(ushort)), exceptions * sizeof(double));
            for (int j = 0; j < exceptions; j++)
            {
                output[Position(positions, j, values)] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(patches[(j * sizeof(double))..]));
            }

            at += length;
        }

        End(body, at);
    }

    /// <summary>The vectors of a page of <paramref name="count"/> values, its header checked: ALP's mode and FOR's integers, and that count.</summary>
    private static int Vectors(ReadOnlySpan<byte> page, int count, int size)
    {
        if (page[0] != 0)
        {
            throw new ParquetUnsupportedException(page[0].ToString(System.Globalization.CultureInfo.InvariantCulture), ParquetComponentKind.Encoding,
                $"An ALP page is in compression mode {page[0]}, which this reader does not decode; mode 0 is ALP itself.");
        }

        if (page[1] != 0)
        {
            throw new ParquetUnsupportedException(page[1].ToString(System.Globalization.CultureInfo.InvariantCulture), ParquetComponentKind.Encoding,
                $"An ALP page's integers are in encoding {page[1]}, which this reader does not decode; encoding 0 is a frame of reference and bit-packing.");
        }

        int declared = BinaryPrimitives.ReadInt32LittleEndian(page[3..]);
        if (declared != count)
        {
            ParquetThrow.Format($"An ALP page declares {declared} values where its page holds {count}.");
        }

        return (count + size - 1) / size;
    }

    /// <summary>Where the offsets of <paramref name="vectors"/> vectors end in the page's body: where the first vector starts.</summary>
    private static int OffsetsEnd(ReadOnlySpan<byte> body, int vectors)
    {
        long end = (long)vectors * sizeof(uint);
        if (end > body.Length)
        {
            ParquetThrow.Truncated("ALP offset array");
        }

        return (int)end;
    }

    /// <summary>Vector <paramref name="v"/>, which starts at <paramref name="at"/>, where its offset must place it, and holds its header.</summary>
    private static ReadOnlySpan<byte> Vector(ReadOnlySpan<byte> body, int v, int at, int header)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(body[(v * sizeof(uint))..]) != (uint)at)
        {
            ParquetThrow.Format($"ALP vector {v} does not start where the vectors before it end.");
        }

        if (body.Length - at < header)
        {
            ParquetThrow.Truncated("ALP vector header");
        }

        return body[at..];
    }

    private static void Check(int exponent, int factor, int maxExponent, int width, int maxWidth, int exceptions, int values)
    {
        if (exponent > maxExponent || factor > exponent)
        {
            ParquetThrow.Format($"An ALP vector declares exponent {exponent} and factor {factor}, outside 0 <= factor <= exponent <= {maxExponent}.");
        }

        if (width > maxWidth)
        {
            ParquetThrow.Format($"An ALP vector declares deltas {width} bits wide, past the {maxWidth} of its integers.");
        }

        if (exceptions > values)
        {
            ParquetThrow.Format($"An ALP vector declares {exceptions} exceptions among its {values} values.");
        }
    }

    /// <summary>Exception <paramref name="j"/>'s position, inside the vector's <paramref name="values"/>.</summary>
    private static int Position(ReadOnlySpan<byte> positions, int j, int values)
    {
        int position = BinaryPrimitives.ReadUInt16LittleEndian(positions[(j * sizeof(ushort))..]);
        if (position >= values)
        {
            ParquetThrow.Format($"An ALP exception at position {position} lies past its vector's {values} values.");
        }

        return position;
    }

    private static void End(ReadOnlySpan<byte> body, int at)
    {
        if (at != body.Length)
        {
            ParquetThrow.Format($"An ALP page holds {body.Length - at} bytes past its last vector.");
        }
    }

    /// <summary>Adds <paramref name="reference"/> to every delta, wrapping, as the standard computes them.</summary>
    private static void AddReference<T>(Span<T> deltas, T reference)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref T delta = ref MemoryMarshal.GetReference(deltas);
            Vector256<T> add = Vector256.Create(reference);
            for (; i <= deltas.Length - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                (Vector256.LoadUnsafe(ref delta, (nuint)i) + add).StoreUnsafe(ref delta, (nuint)i);
            }
        }

        for (; i < deltas.Length; i++)
        {
            deltas[i] = unchecked(deltas[i] + reference);
        }
    }
}
