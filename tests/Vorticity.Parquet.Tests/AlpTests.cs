using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Columns;
using Vorticity.Parquet.Encodings;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// ALP read as the standard makes it normative: the standard's own worked example, the test suite's
/// file bit for bit against the same values stored PLAIN, a NaN's payload kept, every malformed page
/// refused, and the powers of ten held to their exactly rounded values.
/// </summary>
public sealed class AlpTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The standard's worked example: four doubles, a NaN among them, in 31 bytes of vector.</summary>
    [Fact]
    public void DecodesTheStandardsWorkedExample()
    {
        const long Nan = 0x7FF8_0000_0000_0001;
        byte[] page = Example(Nan);
        Assert.Equal(Alp.HeaderBytes + 4 + 31, page.Length);

        double[] decoded = new double[4];
        Alp.Decode(page, decoded, new ulong[Alp.VectorSize(page)]);
        Assert.Equal(1500.0, decoded[0]);
        Assert.Equal(Nan, BitConverter.DoubleToInt64Bits(decoded[1]));
        Assert.Equal(2500.0, decoded[2]);
        Assert.Equal(333.5, decoded[3]);
    }

    [Fact]
    public void RefusesAPageItsLayoutDoesNotHold()
    {
        double[] four = new double[4];
        ulong[] scratch = new ulong[1024];
        Malformed(page => BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(3), 5));
        Malformed(page => page[2] = 2);
        Malformed(page => page[2] = 16);
        Malformed(page => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(Alp.HeaderBytes), 8));
        Malformed(page => page[Alp.HeaderBytes + 4] = 19);
        Malformed(page => page[Alp.HeaderBytes + 5] = 5);
        Malformed(page => page[Alp.HeaderBytes + 4 + 12] = 65);
        Malformed(page => BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(Alp.HeaderBytes + 4 + 2), 5));
        Malformed(page => BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(Alp.HeaderBytes + 4 + 13 + 8), 4));
        Assert.Throws<ParquetFormatException>(() => Alp.Decode([.. Example(0), 0], four, scratch));
        Assert.Throws<ParquetFormatException>(() => Alp.Decode(Example(0)[..^1], four, scratch));
        Assert.Throws<ParquetFormatException>(() => Alp.Decode(Example(0)[..5], four, scratch));
        Assert.Throws<ParquetFormatException>(() => Alp.Decode(Example(0), new double[3], scratch));

        byte[] mode = Example(0);
        mode[0] = 1;
        Assert.Throws<ParquetUnsupportedException>(() => Alp.Decode(mode, four, scratch));

        void Malformed(Action<byte[]> change)
        {
            byte[] page = Example(0);
            change(page);
            Assert.Throws<ParquetFormatException>(() => Alp.Decode(page, four, scratch));
        }
    }

    /// <summary>
    /// The test suite's file: every ALP column, at vectors of 32, 1 024 and 4 096 values, the very
    /// bits of the same values stored PLAIN, nulls in the same rows.
    /// </summary>
    [Fact]
    public async Task ReadsTheSuitesFileBitForBit()
    {
        string? path = Root is null ? null : Directory.EnumerateFiles(Root, "alp_extended.zstd.parquet", SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(path is null, "VORTICITY_PARQUET_DATA holds no alp_extended.zstd.parquet.");
        await using ParquetFile file = await ParquetFile.OpenAsync(path!, Ct);
        Assert.Contains("ALP", file.Metadata.RowGroups[0].Chunks.Single(chunk => chunk.Column == "double_alp_1024").Encodings);

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                Compare<float>(batch, "float_plain", ["float_alp_1024", "float_alp_4096", "float_alp_32"]);
                Compare<double>(batch, "double_plain", ["double_alp_1024", "double_alp_4096", "double_alp_32"]);
                rows += batch.RowCount;
            }
        }

        Assert.Equal(9_032, rows);

        static void Compare<T>(RecordBatch batch, string plain, string[] alp)
            where T : unmanaged
        {
            VortexColumn reference = batch.Column(System.Text.Encoding.UTF8.GetBytes(plain));
            ReadOnlySpan<byte> expected = System.Runtime.InteropServices.MemoryMarshal.AsBytes(reference.AsPrimitive<T>().Values);
            foreach (string name in alp)
            {
                VortexColumn column = batch.Column(System.Text.Encoding.UTF8.GetBytes(name));
                ReadOnlySpan<byte> actual = System.Runtime.InteropServices.MemoryMarshal.AsBytes(column.AsPrimitive<T>().Values);
                int width = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
                for (int r = 0; r < batch.RowCount; r++)
                {
                    Assert.Equal(reference.IsValid(r), column.IsValid(r));
                    if (reference.IsValid(r))
                    {
                        Assert.True(expected.Slice(r * width, width).SequenceEqual(actual.Slice(r * width, width)), $"{name} row {batch.StartRow + r}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every power of ten and its reciprocal ALP multiplies by is the value IEEE 754's conversion of
    /// the decimal literal gives, correctly rounded: held to the exact rationals, never trusted to a
    /// compiler's parsing of a literal.
    /// </summary>
    [Fact]
    public void ThePowersOfTenAreCorrectlyRounded()
    {
        for (int k = 0; k < AlpTables.F10Double.Length; k++)
        {
            Assert.True(Rounds(AlpTables.F10Double[k], BigInteger.Pow(10, k), BigInteger.One), $"1e{k} as a double");
            Assert.True(Rounds(AlpTables.If10Double[k], BigInteger.One, BigInteger.Pow(10, k)), $"1e-{k} as a double");
        }

        for (int k = 0; k < AlpTables.F10Single.Length; k++)
        {
            Assert.True(Rounds(AlpTables.F10Single[k], BigInteger.Pow(10, k), BigInteger.One), $"1e{k} as a float");
            Assert.True(Rounds(AlpTables.If10Single[k], BigInteger.One, BigInteger.Pow(10, k)), $"1e-{k} as a float");
        }

        // The ranges the standard gives readers are within the tables.
        Assert.True(AlpTables.F10Single.Length > 10 && AlpTables.If10Single.Length > 10);
        Assert.True(AlpTables.F10Double.Length > 18 && AlpTables.If10Double.Length > 18);
    }

    /// <summary>Whether <paramref name="value"/> is the double nearest <paramref name="numerator"/> / <paramref name="denominator"/>, ties to even.</summary>
    private static bool Rounds(double value, BigInteger numerator, BigInteger denominator) =>
        Within(Exact(value), Exact(Math.BitDecrement(value)), Exact(Math.BitIncrement(value)), numerator, denominator, (BitConverter.DoubleToInt64Bits(value) & 1) == 0);

    /// <summary>Whether <paramref name="value"/> is the float nearest <paramref name="numerator"/> / <paramref name="denominator"/>, ties to even.</summary>
    private static bool Rounds(float value, BigInteger numerator, BigInteger denominator) =>
        Within(Exact(value), Exact(MathF.BitDecrement(value)), Exact(MathF.BitIncrement(value)), numerator, denominator, (BitConverter.SingleToInt32Bits(value) & 1) == 0);

    /// <summary>
    /// Whether the exact <paramref name="numerator"/> / <paramref name="denominator"/> rounds to
    /// <paramref name="value"/>: it lies between the midpoints of the value and its neighbours, and on
    /// one of them only when the value is the even of the two, as 10^23, whose 5^23 takes 54 bits, lies.
    /// </summary>
    private static bool Within(
        (BigInteger N, BigInteger D) value, (BigInteger N, BigInteger D) below, (BigInteger N, BigInteger D) above, BigInteger numerator, BigInteger denominator, bool even)
    {
        (BigInteger N, BigInteger D) low = Midpoint(below, value);
        (BigInteger N, BigInteger D) high = Midpoint(value, above);
        int fromLow = BigInteger.Compare(low.N * denominator, numerator * low.D);
        int toHigh = BigInteger.Compare(numerator * high.D, high.N * denominator);
        return (fromLow < 0 || (fromLow == 0 && even)) && (toHigh < 0 || (toHigh == 0 && even));

        static (BigInteger N, BigInteger D) Midpoint((BigInteger N, BigInteger D) a, (BigInteger N, BigInteger D) b) =>
            ((a.N * b.D) + (b.N * a.D), 2 * a.D * b.D);
    }

    /// <summary>A positive finite double as an exact fraction.</summary>
    private static (BigInteger N, BigInteger D) Exact(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        exponent -= 1075;
        return exponent >= 0 ? (new BigInteger(mantissa) << exponent, BigInteger.One) : (new BigInteger(mantissa), BigInteger.One << -exponent);
    }

    /// <summary>A positive finite float as an exact fraction: a float converts to a double exactly.</summary>
    private static (BigInteger N, BigInteger D) Exact(float value) => Exact((double)value);

    /// <summary>The standard's worked example, its NaN of <paramref name="nan"/>'s bits: a page of four doubles in one vector of 1 024.</summary>
    private static byte[] Example(long nan)
    {
        List<byte> page = [0, 0, 10];
        page.AddRange(BitConverter.GetBytes(4));
        page.AddRange(BitConverter.GetBytes(4u));

        // AlpInfo: exponent 4, factor 3, one exception; ForInfo: 3335, 15 bits.
        page.AddRange([4, 3, 1, 0]);
        page.AddRange(BitConverter.GetBytes(3335L));
        page.Add(15);
        byte[] packed = new byte[8];
        BitPacking.Pack64([11665, 11665, 21665, 0], 15, packed);
        page.AddRange(packed);
        page.AddRange(BitConverter.GetBytes((ushort)1));
        page.AddRange(BitConverter.GetBytes(nan));
        return [.. page];
    }
}
