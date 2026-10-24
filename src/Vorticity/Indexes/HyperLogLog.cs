using System;

namespace Vorticity.Indexes;

/// <summary>
/// The count a HyperLogLog's registers estimate, by Ertl's improved estimator, which needs no table of bias corrections
/// and is the same bits on every machine: the engine's sketch of distinct values and a column sheet's share it.
/// </summary>
/// <remarks>
/// O. Ertl, <i>New cardinality estimation algorithms for HyperLogLog sketches</i> (2017), algorithm 6.
/// </remarks>
internal static class HyperLogLog
{
    /// <summary>1 / (2 ln 2), written out: a platform's logarithm need not round alike, a literal does.</summary>
    private const double AlphaInfinity = 0.7213475204444817;

    /// <summary>The rank of a 64-bit hash under <paramref name="precision"/> bits of register index: its leading zeros past them, plus one.</summary>
    internal static byte Rank(ulong hash, int precision) =>
        (byte)(System.Numerics.BitOperations.LeadingZeroCount((hash << precision) | (1UL << (precision - 1))) + 1);

    /// <summary>The register a 64-bit hash raises under <paramref name="precision"/> bits of index: its top bits.</summary>
    internal static int Register(ulong hash, int precision) => (int)(hash >> (64 - precision));

    /// <summary>The estimate of registers <paramref name="ranks"/>, one a byte, of <paramref name="precision"/> bits of index.</summary>
    internal static double Estimate(ReadOnlySpan<byte> ranks, int precision)
    {
        Span<int> counts = stackalloc int[66 - precision];
        foreach (byte rank in ranks)
        {
            counts[rank]++;
        }

        return Estimate(counts, precision);
    }

    /// <summary>Packs registers of six bits, <paramref name="ranks"/>, a multiple of four, four in three bytes into <paramref name="into"/>; the bytes written.</summary>
    internal static int Pack(ReadOnlySpan<byte> ranks, Span<byte> into)
    {
        int at = 0;
        for (int r = 0; r < ranks.Length; r += 4)
        {
            uint word = (uint)ranks[r] | ((uint)ranks[r + 1] << 6) | ((uint)ranks[r + 2] << 12) | ((uint)ranks[r + 3] << 18);
            into[at++] = (byte)word;
            into[at++] = (byte)(word >> 8);
            into[at++] = (byte)(word >> 16);
        }

        return at;
    }

    /// <summary>The registers <see cref="Pack"/> wrote, into <paramref name="ranks"/>.</summary>
    internal static void Unpack(ReadOnlySpan<byte> packed, Span<byte> ranks)
    {
        for (int r = 0, at = 0; r < ranks.Length; r += 4, at += 3)
        {
            uint word = packed[at] | ((uint)packed[at + 1] << 8) | ((uint)packed[at + 2] << 16);
            ranks[r] = (byte)(word & 63);
            ranks[r + 1] = (byte)((word >> 6) & 63);
            ranks[r + 2] = (byte)((word >> 12) & 63);
            ranks[r + 3] = (byte)((word >> 18) & 63);
        }
    }

    /// <summary>
    /// Registers <paramref name="ranks"/> as the shorter of two forms: 0, the registers set, and each one's index as a gap
    /// from the one before and its rank, while few are set; or 1 and every register packed (<see cref="Pack"/>).
    /// </summary>
    internal static byte[] Encode(ReadOnlySpan<byte> ranks)
    {
        int set = ranks.Length - ranks.Count((byte)0);
        int packed = 1 + (ranks.Length * 3 / 4);
        if (set * 3 + Serialization.Varint.MaxLength32 < packed)
        {
            byte[] sparse = new byte[1 + Serialization.Varint.MaxLength32 + (set * (Serialization.Varint.MaxLength32 + 1))];
            int at = 1;
            at += Serialization.Varint.Write(sparse.AsSpan(at), (ulong)set);
            int previous = 0;
            for (int r = 0; r < ranks.Length; r++)
            {
                if (ranks[r] != 0)
                {
                    at += Serialization.Varint.Write(sparse.AsSpan(at), (ulong)(r - previous));
                    sparse[at++] = ranks[r];
                    previous = r;
                }
            }

            return sparse.AsSpan(0, at).ToArray();
        }

        byte[] dense = new byte[packed];
        dense[0] = 1;
        Pack(ranks, dense.AsSpan(1));
        return dense;
    }

    /// <summary>The registers <see cref="Encode"/> wrote, into <paramref name="ranks"/>.</summary>
    /// <exception cref="VortexFormatException">The bytes are no registers of that many.</exception>
    internal static void Decode(ReadOnlySpan<byte> bytes, Span<byte> ranks)
    {
        ranks.Clear();
        if (bytes.Length == 1 + (ranks.Length * 3 / 4) && bytes[0] == 1)
        {
            Unpack(bytes[1..], ranks);
            return;
        }

        int at = 1;
        ulong set = bytes.Length > 0 && bytes[0] == 0
            ? Serialization.Varint.Read64<Errors>(bytes, ref at)
            : throw new VortexFormatException("Registers of a sketch are in no form this library reads.");
        long register = 0;
        for (ulong i = 0; i < set; i++)
        {
            register += (long)Serialization.Varint.Read64<Errors>(bytes, ref at);
            if (register >= ranks.Length || at >= bytes.Length)
            {
                throw new VortexFormatException("Registers of a sketch name one past their count.");
            }

            ranks[(int)register] = bytes[at++];
        }
    }

    private readonly struct Errors : Serialization.IVarintErrors
    {
        public static ulong Truncated(int position) => throw new VortexFormatException($"Registers of a sketch end inside a varint at {position}.");

        public static ulong Malformed(int position, byte value) => throw new VortexFormatException($"Registers of a sketch hold a varint too wide at {position}.");
    }

    /// <summary>
    /// The estimate of the registers of <paramref name="precision"/> bits of index, counted by rank:
    /// <paramref name="counts"/>[r] the registers of rank r, [0] the empty ones, up to rank 65 − precision.
    /// </summary>
    internal static double Estimate(ReadOnlySpan<int> counts, int precision)
    {
        double m = 1 << precision;
        int q = 64 - precision;
        double z = m * Tau(1 - (counts[q + 1] / m));
        for (int k = q; k >= 1; k--)
        {
            z = 0.5 * (z + counts[k]);
        }

        z += m * Sigma(counts[0] / m);
        return AlphaInfinity * m * m / z;
    }

    private static double Sigma(double x)
    {
        if (x == 1)
        {
            return double.PositiveInfinity;
        }

        double y = 1;
        double z = x;
        while (true)
        {
            x *= x;
            double previous = z;
            z += x * y;
            y += y;
            if (z == previous)
            {
                return z;
            }
        }
    }

    private static double Tau(double x)
    {
        if (x == 0 || x == 1)
        {
            return 0;
        }

        double y = 1;
        double z = 1 - x;
        while (true)
        {
            x = Math.Sqrt(x);
            double previous = z;
            y *= 0.5;
            z -= (1 - x) * (1 - x) * y;
            if (z == previous)
            {
                return z / 3;
            }
        }
    }
}
