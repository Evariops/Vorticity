using System;
using System.Buffers.Binary;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// The masked gather with nullable codes, which gathers a word of rows at a time, held to the rows
/// gathered one by one: bitmaps that start mid-byte, a last word that is not whole, codes past the
/// dictionary under null rows, and values with and without validity of their own.
/// </summary>
public sealed class RowKernelsTests
{
    private const int Rows = 1_000;

    private const int Entries = 37;

    /// <remarks>
    /// The dictionary is a few dozen values, whose validity the gather expands to a byte each, or
    /// more than a quarter of the rows, whose validity it reads in the bitmap at each code.
    /// </remarks>
    [Theory]
    [InlineData(PType.U16, 16, true, Entries)]
    [InlineData(PType.U16, 16, false, Entries)]
    [InlineData(PType.U8, 8, false, Entries)]
    [InlineData(PType.I32, 4, true, Entries)]
    [InlineData(PType.I16, 2, false, Entries)]
    [InlineData(PType.U16, 16, false, 251)]
    [InlineData(PType.U16, 8, false, 5_000)]
    [InlineData(PType.I32, 4, false, 5_000)]
    [InlineData(PType.I16, 2, false, 5_000)]
    internal void NullableCodesGatherAsRowByRow(PType codesPType, int width, bool valuesAllValid, int entries)
    {
        Random random = new Random((width * 31) + (int)codesPType + (valuesAllValid ? 1 : 0) + entries);
        byte[] values = new byte[entries * width];
        random.NextBytes(values);
        bool[] codeValid = new bool[Rows];
        bool[] valueValid = new bool[entries];
        int[] codes = new int[Rows];
        for (int row = 0; row < Rows; row++)
        {
            codeValid[row] = random.Next(10) != 0;

            // A null row's code is whatever the file holds, past the dictionary included.
            codes[row] = codeValid[row] ? random.Next(entries) : entries + random.Next(100);
        }

        for (int entry = 0; entry < entries; entry++)
        {
            valueValid[entry] = random.Next(5) != 0;
        }

        byte[] destination = new byte[Rows * width];
        destination.AsSpan().Fill(0xCD);
        byte[] output = new byte[(Rows + 7) / 8];
        int fault = RowKernels.GatherMasked(
            Codes(codes, codesPType), codesPType, values, width, entries, destination, Rows,
            Bits(codeValid, offset: 3), 3, valuesAllValid ? default : Bits(valueValid, offset: 5), 5,
            valuesAllValid, output);

        Assert.Equal(-1, fault);
        for (int row = 0; row < Rows; row++)
        {
            ReadOnlySpan<byte> gathered = destination.AsSpan(row * width, width);
            if (codeValid[row])
            {
                Assert.True(values.AsSpan(codes[row] * width, width).SequenceEqual(gathered), $"row {row}");
            }
            else
            {
                Assert.True(gathered.IndexOfAnyExcept((byte)0) < 0, $"row {row}");
            }

            bool expected = codeValid[row] && (valuesAllValid || valueValid[codes[row]]);
            Assert.Equal(expected, ((output[row >> 3] >> (row & 7)) & 1) != 0);
        }
    }

    /// <summary>A code past the dictionary is a fault under a valid row, and nothing under a null one.</summary>
    [Fact]
    internal void ACodePastTheDictionaryIsAFaultOnlyUnderAValidRow()
    {
        int[] codes = new int[Rows];
        bool[] valid = new bool[Rows];
        for (int row = 0; row < Rows; row++)
        {
            valid[row] = row % 7 != 0;
            codes[row] = valid[row] ? row % Entries : 60_000;
        }

        byte[] values = new byte[Entries * 16];
        int fault = RowKernels.GatherMasked(
            Codes(codes, PType.U16), PType.U16, values, 16, Entries, new byte[Rows * 16], Rows,
            Bits(valid, offset: 0), 0, default, 0, valuesAllValid: true, new byte[(Rows + 7) / 8]);
        Assert.Equal(-1, fault);

        codes[701] = Entries;
        fault = RowKernels.GatherMasked(
            Codes(codes, PType.U16), PType.U16, values, 16, Entries, new byte[Rows * 16], Rows,
            Bits(valid, offset: 0), 0, default, 0, valuesAllValid: true, new byte[(Rows + 7) / 8]);
        Assert.Equal(701, fault);
    }

    /// <summary>
    /// The gather eight rows a step, held to the rows gathered one by one: every width it has a
    /// case for and one it has not, and a row count past the last whole step.
    /// </summary>
    [Theory]
    [InlineData(PType.U8, 1)]
    [InlineData(PType.U8, 16)]
    [InlineData(PType.U16, 2)]
    [InlineData(PType.I32, 4)]
    [InlineData(PType.U16, 8)]
    [InlineData(PType.I16, 32)]
    [InlineData(PType.U8, 12)]
    internal void CodesGatherAsRowByRow(PType codesPType, int width)
    {
        const int rows = Rows + 3;
        Random random = new Random((width * 31) + (int)codesPType);
        byte[] values = new byte[Entries * width];
        random.NextBytes(values);
        int[] codes = new int[rows];
        for (int row = 0; row < rows; row++)
        {
            codes[row] = random.Next(Entries);
        }

        byte[] destination = new byte[rows * width];
        int fault = RowKernels.Gather(Codes(codes, codesPType), codesPType, values, width, Entries, destination, rows);

        Assert.Equal(-1, fault);
        for (int row = 0; row < rows; row++)
        {
            Assert.True(
                values.AsSpan(codes[row] * width, width).SequenceEqual(destination.AsSpan(row * width, width)), $"row {row}");
        }
    }

    /// <summary>
    /// A code past the dictionary is reported at its own row wherever it falls in a step of eight,
    /// in the rows past the last step, and when it is negative.
    /// </summary>
    [Theory]
    [InlineData(PType.U16, 0, Entries)]
    [InlineData(PType.U16, 7, Entries)]
    [InlineData(PType.U16, 701, 60_000)]
    [InlineData(PType.U16, Rows + 2, Entries)]
    [InlineData(PType.I16, 356, -1)]
    [InlineData(PType.I32, 999, -40)]
    internal void ACodePastTheDictionaryIsReportedAtItsRow(PType codesPType, int row, int code)
    {
        const int rows = Rows + 3;
        int[] codes = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            codes[i] = i % Entries;
        }

        codes[row] = code;
        int fault = RowKernels.Gather(
            Codes(codes, codesPType), codesPType, new byte[Entries * 4], 4, Entries, new byte[rows * 4], rows);
        Assert.Equal(row, fault);
    }

    /// <summary>
    /// Byte codes through a dictionary small enough to be permuted, where there are 512-bit
    /// permutes: every width that has a permute, dictionaries either side of what one and two
    /// registers hold, row counts either side of a block; and a code past the dictionary reported
    /// at its own row wherever it falls in a block, the rows before it gathered.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    internal void ByteCodesThroughASmallDictionaryGatherAsRowByRow(int width)
    {
        Random random = new Random(width * 977);
        foreach (int entries in new[] { 1, 8, 9, 16, 17, 32, 33, 64, 65, 128, 129 })
        {
            byte[] values = new byte[entries * width];
            random.NextBytes(values);
            foreach (int rows in new[] { 0, 7, 8, 63, 64, 65, 1003 })
            {
                int[] codes = new int[rows];
                for (int row = 0; row < rows; row++)
                {
                    codes[row] = random.Next(entries);
                }

                byte[] destination = new byte[rows * width];
                int fault = RowKernels.Gather(Codes(codes, PType.U8), PType.U8, values, width, entries, destination, rows);
                Assert.Equal(-1, fault);
                for (int row = 0; row < rows; row++)
                {
                    Assert.True(
                        values.AsSpan(codes[row] * width, width).SequenceEqual(destination.AsSpan(row * width, width)),
                        $"entries {entries}, rows {rows}, row {row}");
                }

                foreach (int bad in new[] { 0, 5, 63, 64, 500, rows - 1 })
                {
                    if (bad < 0 || bad >= rows || entries > 255)
                    {
                        continue;
                    }

                    int[] faulty = (int[])codes.Clone();
                    faulty[bad] = random.Next(2) == 0 ? entries : 255;
                    Array.Clear(destination);
                    fault = RowKernels.Gather(Codes(faulty, PType.U8), PType.U8, values, width, entries, destination, rows);
                    Assert.Equal(bad, fault);
                }
            }
        }
    }

    /// <summary><paramref name="codes"/> as <paramref name="ptype"/>, little-endian.</summary>
    private static byte[] Codes(int[] codes, PType ptype)
    {
        int size = ptype.ByteWidth();
        byte[] bytes = new byte[codes.Length * size];
        for (int i = 0; i < codes.Length; i++)
        {
            Span<byte> slot = bytes.AsSpan(i * size, size);
            switch (size)
            {
                case 1:
                    slot[0] = unchecked((byte)codes[i]);
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(slot, unchecked((ushort)codes[i]));
                    break;
                default:
                    BinaryPrimitives.WriteInt32LittleEndian(slot, codes[i]);
                    break;
            }
        }

        return bytes;
    }

    /// <summary>A bitmap of <paramref name="set"/> starting <paramref name="offset"/> bits in.</summary>
    private static byte[] Bits(bool[] set, int offset)
    {
        byte[] bits = new byte[((set.Length + offset) / 8) + 1];
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i])
            {
                int at = i + offset;
                bits[at >> 3] |= (byte)(1 << (at & 7));
            }
        }

        return bits;
    }
}
