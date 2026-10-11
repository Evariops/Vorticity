using System;
using System.Buffers.Binary;

namespace Vorticity.ObjectStorage.Hashing;

/// <summary>
/// CRC-64/NVME: the polynomial <c>0xAD93D23594C93659</c>, reflected, all ones in and out. S3 names it
/// <c>CRC64NVME</c> and Azure's CRC-64 is the same, so one implementation serves both.
/// <c>System.IO.Hashing.Crc64</c> is ECMA-182's, another polynomial.
/// </summary>
/// <remarks>
/// Eight bytes a step through eight tables, the slicing a portable CRC uses. Two CRCs combine into
/// the CRC of the concatenation without the bytes (<see cref="Combine"/>), which is how the parts of
/// an upload, sent in parallel, give the whole object's.
/// </remarks>
internal static class Crc64Nvme
{
    /// <summary>The polynomial, reflected.</summary>
    internal const ulong Polynomial = 0x9A6C9329AC4BC9B5;

    private static readonly ulong[] s_tables = BuildTables();

    /// <summary>The CRC of <paramref name="data"/>.</summary>
    internal static ulong Hash(ReadOnlySpan<byte> data) => ~Update(ulong.MaxValue, data);

    /// <summary>
    /// The register after <paramref name="data"/>, from <paramref name="crc"/>: start from
    /// <see cref="ulong.MaxValue"/>, and complement the last register for the CRC.
    /// </summary>
    internal static ulong Update(ulong crc, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<ulong> t = s_tables;
        while (data.Length >= 8)
        {
            ulong word = crc ^ BinaryPrimitives.ReadUInt64LittleEndian(data);
            crc = t[(7 * 256) + (int)(word & 0xFF)]
                ^ t[(6 * 256) + (int)((word >> 8) & 0xFF)]
                ^ t[(5 * 256) + (int)((word >> 16) & 0xFF)]
                ^ t[(4 * 256) + (int)((word >> 24) & 0xFF)]
                ^ t[(3 * 256) + (int)((word >> 32) & 0xFF)]
                ^ t[(2 * 256) + (int)((word >> 40) & 0xFF)]
                ^ t[256 + (int)((word >> 48) & 0xFF)]
                ^ t[(int)(word >> 56)];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = t[(int)((crc ^ b) & 0xFF)] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>
    /// The CRC of A followed by B, from the CRC of A, the CRC of B and B's length: the CRC of A moved
    /// past B's bits, a multiplication by <c>x^(8 * lengthOfB)</c> modulo the polynomial, then the CRC
    /// of B added. The all-ones conditioning of both cancels, as zlib's combination relies on.
    /// </summary>
    internal static ulong Combine(ulong crcOfA, ulong crcOfB, long lengthOfB)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lengthOfB);
        return lengthOfB == 0 ? crcOfA : MultiplyModulo(PowerOfXModulo(lengthOfB), crcOfA) ^ crcOfB;
    }

    /// <summary><c>a * b</c> modulo the polynomial, both reflected: bit 63 is <c>x^0</c>.</summary>
    private static ulong MultiplyModulo(ulong a, ulong b)
    {
        ulong product = 0;
        ulong m = 1UL << 63;
        for (int i = 0; i < 64; i++, m >>= 1)
        {
            if ((a & m) != 0)
            {
                product ^= b;
            }

            b = (b & 1) != 0 ? (b >> 1) ^ Polynomial : b >> 1;
        }

        return product;
    }

    /// <summary><c>x^(8 * bytes)</c> modulo the polynomial, reflected, by squaring.</summary>
    private static ulong PowerOfXModulo(long bytes)
    {
        // x^1 reflected is bit 62; x^(2^k) is squared from it, and x^(8 n) is the product of the
        // squares the bits of 8 n name.
        ulong result = 1UL << 63;
        ulong square = 1UL << 62;
        ulong n = (ulong)bytes << 3;
        while (n != 0)
        {
            if ((n & 1) != 0)
            {
                result = MultiplyModulo(square, result);
            }

            square = MultiplyModulo(square, square);
            n >>= 1;
        }

        return result;
    }

    private static ulong[] BuildTables()
    {
        ulong[] tables = new ulong[8 * 256];
        for (int b = 0; b < 256; b++)
        {
            ulong crc = (ulong)b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            tables[b] = crc;
        }

        for (int slice = 1; slice < 8; slice++)
        {
            for (int b = 0; b < 256; b++)
            {
                ulong previous = tables[((slice - 1) * 256) + b];
                tables[(slice * 256) + b] = tables[(int)(previous & 0xFF)] ^ (previous >> 8);
            }
        }

        return tables;
    }
}
