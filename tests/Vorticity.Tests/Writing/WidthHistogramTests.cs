// The ingest pass's bit-width histograms against a row-at-a-time oracle.
//
// With AVX-512 the widths are counted from vectors: a leading-zero count per lane narrowed to a
// byte, then each width of the block's band counted a compare at a time, or a byte at a time when
// the band is wide. So the cases cover every integer type, lengths either side of a vector and of
// the 1 024-row chunk, narrow and full-range values (a narrow band and a wide one), and nulls at
// every bit offset -- the histograms must be the oracle's exactly, a null counted at width zero in
// both domains and nowhere else.
using System;
using System.Buffers.Binary;
using System.Numerics;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class WidthHistogramTests
{
    [Theory]
    [InlineData(PType.I8)]
    [InlineData(PType.I16)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    internal void TheHistogramsAreTheRowsWidths(PType ptype)
    {
        Random random = new Random((int)ptype);
        int width = ptype.ByteWidth();
        bool signed = ptype.IsSignedInteger();
        foreach (int rows in new[] { 1, 63, 64, 65, 1_000, 1_025, 3_000 })
        {
            foreach (string shape in new[] { "narrow", "full", "mixed" })
            {
                foreach (int offset in new[] { -1, 0, 5 })
                {
                    CanonicalArena arena = new CanonicalArena();
                    DTypeArena types = new DTypeArena();
                    byte[] values = new byte[rows * width];
                    random.NextBytes(values);
                    for (int i = 0; i < rows; i++)
                    {
                        Span<byte> slot = values.AsSpan(i * width, width);
                        if (shape == "narrow" || (shape == "mixed" && random.Next(3) != 0))
                        {
                            slot.Clear();
                            slot[0] = (byte)random.Next(256);
                            if (width > 1 && shape == "mixed")
                            {
                                slot[1] = (byte)random.Next(4);
                            }

                            if (signed && random.Next(2) == 0)
                            {
                                // A small negative: every high bit set.
                                slot.Fill(0xFF);
                                slot[0] = (byte)random.Next(256);
                            }
                        }
                    }

                    bool[] valid = new bool[rows];
                    for (int i = 0; i < rows; i++)
                    {
                        valid[i] = offset < 0 || random.Next(5) != 0;
                    }

                    int node = Primitive(arena, types, ptype, values, valid, offset);
                    int[] histograms = new int[BitPackWidths.Length];
                    BlockStatsPass.Widths(arena, node, 0, rows, histograms);

                    int[] expected = new int[BitPackWidths.Length];
                    ulong top = width == 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
                    for (int i = 0; i < rows; i++)
                    {
                        if (!valid[i])
                        {
                            expected[0]++;
                            expected[BitPackWidths.ZigZagOffset]++;
                            continue;
                        }

                        ulong bits = Read(values, i, width) & top;
                        expected[64 - BitOperations.LeadingZeroCount(bits)]++;
                        if (signed)
                        {
                            ulong zig = ((bits << 1) ^ (0UL - ((bits >> (width * 8 - 1)) & 1))) & top;
                            expected[BitPackWidths.ZigZagOffset + 64 - BitOperations.LeadingZeroCount(zig)]++;
                        }
                    }

                    if (!signed)
                    {
                        // An unsigned column's zigzag half is not counted, beyond its nulls.
                        for (int w = 1; w < BitPackWidths.Domain; w++)
                        {
                            expected[BitPackWidths.ZigZagOffset + w] = 0;
                        }
                    }

                    Assert.True(
                        expected.AsSpan().SequenceEqual(histograms),
                        $"{ptype} rows={rows} {shape} offset={offset}: {string.Join(',', histograms)} expected {string.Join(',', expected)}");
                }
            }
        }
    }

    private static ulong Read(byte[] values, int row, int width)
    {
        ReadOnlySpan<byte> at = values.AsSpan(row * width, width);
        return width switch
        {
            1 => at[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(at),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(at),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(at),
        };
    }

    /// <summary>
    /// The lane counter with a frame of reference, as the bit-pack plan takes it: each row's width
    /// is its value less the reference, wrapping at the element width, and its zigzag width is of
    /// the value itself; a null row is counted nowhere, the caller counting it at width zero.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void TheFramedWidthsAreTheRowsLessTheReference(int elementBits)
    {
        if (!BlockStatsPass.LanesCountWidths)
        {
            return;
        }

        switch (elementBits)
        {
            case 8:
                Framed<byte>(elementBits);
                break;
            case 16:
                Framed<ushort>(elementBits);
                break;
            case 32:
                Framed<uint>(elementBits);
                break;
            default:
                Framed<ulong>(elementBits);
                break;
        }
    }

    private static void Framed<T>(int elementBits)
        where T : unmanaged, IBinaryInteger<T>
    {
        Random random = new Random(elementBits * 31);
        ulong top = elementBits == 64 ? ulong.MaxValue : (1UL << elementBits) - 1;
        foreach (int rows in new[] { 64, 65, 1_000, 1_025, 3_000 })
        {
            foreach (bool masked in new[] { false, true })
            {
                ulong reference = (ulong)random.NextInt64() & top;
                T[] values = new T[rows];
                for (int i = 0; i < rows; i++)
                {
                    // Near the reference mostly, so the band is narrow; below it now and then, so
                    // the subtraction wraps.
                    ulong near = random.Next(4) == 0 ? (ulong)random.NextInt64() : reference + (ulong)random.Next(300);
                    values[i] = T.CreateTruncating(near & top);
                }

                byte[] bits = new byte[(rows + 7 + 3) / 8];
                random.NextBytes(bits);
                int[] widths = new int[BitPackWidths.Length];
                BlockStatsPass.CountLaneWidths<T>(values, masked ? bits : default, 3, masked, zigzag: true, widths, reference);

                int[] expected = new int[BitPackWidths.Length];
                for (int i = 0; i < rows; i++)
                {
                    int at = 3 + i;
                    if (masked && ((bits[at >> 3] >> (at & 7)) & 1) == 0)
                    {
                        continue;
                    }

                    ulong value = ulong.CreateTruncating(values[i]) & top;
                    ulong framed = (value - reference) & top;
                    ulong zig = ((value << 1) ^ (0UL - ((value >> (elementBits - 1)) & 1))) & top;
                    expected[64 - BitOperations.LeadingZeroCount(framed)]++;
                    expected[BitPackWidths.ZigZagOffset + 64 - BitOperations.LeadingZeroCount(zig)]++;
                }

                Assert.Equal(expected, widths);
            }
        }
    }

    private static int Primitive(CanonicalArena arena, DTypeArena types, PType ptype, byte[] values, bool[] valid, int offset)
    {
        VortexBuffer buffer = arena.Allocate(values.Length, ptype.ByteWidth(), out Span<byte> bytes);
        values.CopyTo(bytes);
        Validity validity = Validity.NonNullable;
        if (offset >= 0)
        {
            VortexBuffer bitmap = arena.Allocate((valid.Length + offset + 7) / 8, 1, out Span<byte> bits);
            bits.Fill(0xFF);
            for (int i = 0; i < valid.Length; i++)
            {
                if (!valid[i])
                {
                    int at = offset + i;
                    bits[at >> 3] &= (byte)~(1 << (at & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), valid.Length, Validity.NonNullable, bitmap, offset));
        }

        return arena.AddPrimitive(
            types.Primitive(ptype, offset >= 0 ? Nullability.Nullable : Nullability.NonNullable), valid.Length, validity, ptype, buffer);
    }
}
