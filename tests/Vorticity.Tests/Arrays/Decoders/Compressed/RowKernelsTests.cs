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

    [Theory]
    [InlineData(PType.U16, 16, true)]
    [InlineData(PType.U16, 16, false)]
    [InlineData(PType.U8, 8, false)]
    [InlineData(PType.I32, 4, true)]
    [InlineData(PType.I16, 2, false)]
    internal void NullableCodesGatherAsRowByRow(PType codesPType, int width, bool valuesAllValid)
    {
        Random random = new Random((width * 31) + (int)codesPType + (valuesAllValid ? 1 : 0));
        byte[] values = new byte[Entries * width];
        random.NextBytes(values);
        bool[] codeValid = new bool[Rows];
        bool[] valueValid = new bool[Entries];
        int[] codes = new int[Rows];
        for (int row = 0; row < Rows; row++)
        {
            codeValid[row] = random.Next(10) != 0;

            // A null row's code is whatever the file holds, past the dictionary included.
            codes[row] = codeValid[row] ? random.Next(Entries) : Entries + random.Next(100);
        }

        for (int entry = 0; entry < Entries; entry++)
        {
            valueValid[entry] = random.Next(5) != 0;
        }

        byte[] destination = new byte[Rows * width];
        destination.AsSpan().Fill(0xCD);
        byte[] output = new byte[(Rows + 7) / 8];
        int fault = RowKernels.GatherMasked(
            Codes(codes, codesPType), codesPType, values, width, Entries, destination, Rows,
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
