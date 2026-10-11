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

    /// <summary>The values of each vector the writer cuts, the standard's default, and their log.</summary>
    internal const int WrittenVectorLog = 10;
    internal const int WrittenVector = 1 << WrittenVectorLog;

    /// <summary>
    /// The most bytes <see cref="Encode(ReadOnlySpan{float}, Span{byte}, Scratch)"/> writes for
    /// <paramref name="count"/> values of <paramref name="width"/> bytes: every vector's offset and
    /// header, every delta at the full width, and every value an exception besides.
    /// </summary>
    internal static int MaxSize(int count, int width)
    {
        int vectors = (count + WrittenVector - 1) / WrittenVector;
        return checked(HeaderBytes + (vectors * (sizeof(uint) + 5 + width)) + (count * width) + (count * (sizeof(ushort) + width)));
    }

    /// <summary>What the writer encodes a vector through: its integers, and its exceptions' rows and values.</summary>
    internal sealed class Scratch
    {
        internal readonly int[] Ints = new int[WrittenVector];
        internal readonly long[] Longs = new long[WrittenVector];
        internal readonly int[] Rows = new int[WrittenVector];
        internal readonly float[] Singles = new float[WrittenVector];
        internal readonly double[] Doubles = new double[WrittenVector];
    }

    /// <summary>
    /// Encodes <paramref name="values"/>, FLOAT, as an ALP page into <paramref name="destination"/>,
    /// which holds <see cref="MaxSize"/>; the bytes written. The page's exponents are the core's search
    /// over a sample of its values; each vector of <see cref="WrittenVector"/> keeps a value's integer
    /// where the standard's decode gives its very bits, and carries every other value, NaN, an
    /// infinity, -0 or a value of more digits, as an exception, its slot the first integer kept.
    /// </summary>
    internal static int Encode(ReadOnlySpan<float> values, Span<byte> destination, Scratch scratch)
    {
        int count = values.Length;
        int vectors = Header(destination, count);
        Span<byte> body = destination[HeaderBytes..];
        int at = vectors * sizeof(uint);
        (int e, int f) = global::Vorticity.Writing.AlpPlan.BestExponents(values);
        for (int v = 0; v < vectors; v++)
        {
            ReadOnlySpan<float> vector = values.Slice(v * WrittenVector, Math.Min(WrittenVector, count - (v * WrittenVector)));
            BinaryPrimitives.WriteUInt32LittleEndian(body[(v * sizeof(uint))..], (uint)at);
            Span<int> encoded = scratch.Ints.AsSpan(0, vector.Length);
            int exceptions = global::Vorticity.Writing.AlpPlan.Encode(vector, e, f, encoded, scratch.Rows, scratch.Singles);
            (int least, int most) = Extent(encoded);
            uint span = unchecked((uint)most - (uint)least);
            int width = 32 - System.Numerics.BitOperations.LeadingZeroCount(span);
            Span<byte> output = body[at..];
            output[0] = (byte)e;
            output[1] = (byte)f;
            BinaryPrimitives.WriteUInt16LittleEndian(output[2..], (ushort)exceptions);
            BinaryPrimitives.WriteInt32LittleEndian(output[4..], least);
            output[8] = (byte)width;
            Span<uint> deltas = MemoryMarshal.Cast<int, uint>(encoded);
            AddReference(deltas, unchecked(0u - (uint)least));
            int packed = (int)BitPacking.PackedBytes(vector.Length, width);
            BitPacking.Pack32(deltas, width, output.Slice(9, packed));
            Span<byte> positions = output.Slice(9 + packed, exceptions * sizeof(ushort));
            Span<byte> patches = output.Slice(9 + packed + positions.Length, exceptions * sizeof(float));
            for (int j = 0; j < exceptions; j++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(positions[(j * sizeof(ushort))..], (ushort)scratch.Rows[j]);
                BinaryPrimitives.WriteInt32LittleEndian(patches[(j * sizeof(float))..], BitConverter.SingleToInt32Bits(scratch.Singles[j]));
            }

            at += 9 + packed + positions.Length + patches.Length;
        }

        return HeaderBytes + at;
    }

    /// <summary>Encodes <paramref name="values"/>, DOUBLE, as an ALP page: as <see cref="Encode(ReadOnlySpan{float}, Span{byte}, Scratch)"/> does FLOAT's.</summary>
    internal static int Encode(ReadOnlySpan<double> values, Span<byte> destination, Scratch scratch)
    {
        int count = values.Length;
        int vectors = Header(destination, count);
        Span<byte> body = destination[HeaderBytes..];
        int at = vectors * sizeof(uint);
        (int e, int f) = global::Vorticity.Writing.AlpPlan.BestExponents(values);
        for (int v = 0; v < vectors; v++)
        {
            ReadOnlySpan<double> vector = values.Slice(v * WrittenVector, Math.Min(WrittenVector, count - (v * WrittenVector)));
            BinaryPrimitives.WriteUInt32LittleEndian(body[(v * sizeof(uint))..], (uint)at);
            Span<long> encoded = scratch.Longs.AsSpan(0, vector.Length);
            int exceptions = global::Vorticity.Writing.AlpPlan.Encode(vector, e, f, encoded, scratch.Rows, scratch.Doubles);
            (long least, long most) = Extent(encoded);
            ulong span = unchecked((ulong)most - (ulong)least);
            int width = 64 - System.Numerics.BitOperations.LeadingZeroCount(span);
            Span<byte> output = body[at..];
            output[0] = (byte)e;
            output[1] = (byte)f;
            BinaryPrimitives.WriteUInt16LittleEndian(output[2..], (ushort)exceptions);
            BinaryPrimitives.WriteInt64LittleEndian(output[4..], least);
            output[12] = (byte)width;
            Span<ulong> deltas = MemoryMarshal.Cast<long, ulong>(encoded);
            AddReference(deltas, unchecked(0UL - (ulong)least));
            int packed = (int)BitPacking.PackedBytes(vector.Length, width);
            BitPacking.Pack64(deltas, width, output.Slice(13, packed));
            Span<byte> positions = output.Slice(13 + packed, exceptions * sizeof(ushort));
            Span<byte> patches = output.Slice(13 + packed + positions.Length, exceptions * sizeof(double));
            for (int j = 0; j < exceptions; j++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(positions[(j * sizeof(ushort))..], (ushort)scratch.Rows[j]);
                BinaryPrimitives.WriteInt64LittleEndian(patches[(j * sizeof(double))..], BitConverter.DoubleToInt64Bits(scratch.Doubles[j]));
            }

            at += 13 + packed + positions.Length + patches.Length;
        }

        return HeaderBytes + at;
    }

    /// <summary>Writes the header of a page of <paramref name="count"/> values in vectors of <see cref="WrittenVector"/>; their number.</summary>
    private static int Header(Span<byte> destination, int count)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = WrittenVectorLog;
        BinaryPrimitives.WriteInt32LittleEndian(destination[3..], count);
        return (count + WrittenVector - 1) / WrittenVector;
    }

    /// <summary>The least and the greatest of <paramref name="values"/>, not empty: a vector at a time.</summary>
    private static (T Least, T Most) Extent<T>(ReadOnlySpan<T> values)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
    {
        T least = values[0];
        T most = values[0];
        int i = 0;
        if (Vector256.IsHardwareAccelerated && values.Length >= Vector256<T>.Count)
        {
            ref T value = ref MemoryMarshal.GetReference(values);
            Vector256<T> low = Vector256.Create(T.MaxValue);
            Vector256<T> high = Vector256.Create(T.MinValue);
            for (; i <= values.Length - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                Vector256<T> lanes = Vector256.LoadUnsafe(ref value, (nuint)i);
                low = Vector256.Min(low, lanes);
                high = Vector256.Max(high, lanes);
            }

            for (int lane = 0; lane < Vector256<T>.Count; lane++)
            {
                least = T.Min(least, low.GetElement(lane));
                most = T.Max(most, high.GetElement(lane));
            }
        }

        for (; i < values.Length; i++)
        {
            least = T.Min(least, values[i]);
            most = T.Max(most, values[i]);
        }

        return (least, most);
    }

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
