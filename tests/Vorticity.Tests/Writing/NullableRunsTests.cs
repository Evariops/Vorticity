// The run search over a column with nulls, sixty-four rows a word: the runs the row-by-row walk finds, in the same order,
// whatever the nulls, the values, the kind of the column, the bit its validity starts at, and the ceiling it gives up at.
using System;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class NullableRunsTests
{
    [Theory]
    [InlineData(0.02, 0)]
    [InlineData(0.5, 3)]
    [InlineData(0.98, 5)]
    [InlineData(0.9, 0)]
    public void TheWordWalkFindsTheRowWalksRuns(double valid, int bitOffset)
    {
        Random random = new Random((int)(valid * 1_000) + bitOffset);
        foreach (int length in (int[])[1, 63, 64, 65, 1_000, 10_007])
        {
            // Long stretches of nulls and of values, values in runs, so that whole words of each come up.
            bool[] present = new bool[length];
            long[] numbers = new long[length];
            string[] texts = new string[length];
            bool[] flags = new bool[length];
            for (int row = 0; row < length;)
            {
                int stretch = random.Next(1, random.Next(2) == 0 ? 8 : 200);
                bool isValid = random.NextDouble() < valid;
                long number = random.Next(4);
                for (int i = 0; i < stretch && row < length; i++, row++)
                {
                    present[row] = isValid;
                    numbers[row] = random.Next(10) == 0 ? random.Next(4) : number;
                    texts[row] = numbers[row] == 3 ? "a value longer than twelve bytes " + numbers[row] : "v" + numbers[row];
                    flags[row] = numbers[row] % 2 == 0;
                }
            }

            CanonicalArena arena = new CanonicalArena();
            DTypeArena types = new DTypeArena();
            int[] columns =
            [
                Numbers(arena, types, numbers, Bitmap(arena, types, present, bitOffset)),
                Texts(arena, types, texts, Bitmap(arena, types, present, bitOffset)),
                arena.AddBool(types.Bool(Nullability.Nullable), length, Bitmap(arena, types, present, bitOffset), Bits(arena, flags), 0),
            ];

            foreach (int column in columns)
            {
                foreach (int ceiling in (int[])[length, length / 4, 3])
                {
                    int[] wordStarts = new int[ceiling + 1];
                    int[] wordEnds = new int[ceiling + 1];
                    int[] rowStarts = new int[ceiling + 1];
                    int[] rowEnds = new int[ceiling + 1];
                    int words = ColumnCompressor.RunsOf(arena, column, compared: false, wordStarts, wordEnds);
                    int rows = ColumnCompressor.RunsOf(arena, column, compared: true, rowStarts, rowEnds);
                    Assert.Equal(rows, words);
                    Assert.Equal(rowStarts, wordStarts);
                    Assert.Equal(rowEnds[..Math.Max(0, Math.Min(rows, ceiling + 1) - 1)], wordEnds[..Math.Max(0, Math.Min(words, ceiling + 1) - 1)]);
                }
            }
        }
    }

    private static int Numbers(CanonicalArena arena, DTypeArena types, long[] numbers, Validity validity)
    {
        VortexBuffer buffer = arena.Allocate(numbers.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        MemoryMarshal.Cast<long, byte>(numbers).CopyTo(bytes);
        return arena.AddPrimitive(types.Primitive(PType.I64, Nullability.Nullable), numbers.Length, validity, PType.I64, buffer);
    }

    private static int Texts(CanonicalArena arena, DTypeArena types, string[] texts, Validity validity)
    {
        int heapBytes = 0;
        foreach (string text in texts)
        {
            heapBytes += Encoding.UTF8.GetByteCount(text);
        }

        VortexBuffer views = arena.Allocate(texts.Length * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = arena.Allocate(Math.Max(heapBytes, 1), 8, out Span<byte> heapSpan);
        viewBytes.Clear();
        int at = 0;
        for (int row = 0; row < texts.Length; row++)
        {
            byte[] value = Encoding.UTF8.GetBytes(texts[row]);
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            MemoryMarshal.Write(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..]);
                MemoryMarshal.Write(view[8..], 0);
                MemoryMarshal.Write(view[12..], at);
                value.CopyTo(heapSpan[at..]);
                at += value.Length;
            }
        }

        return arena.AddVarBinView(types.Utf8(Nullability.Nullable), texts.Length, validity, views, [heap]);
    }

    private static VortexBuffer Bits(CanonicalArena arena, bool[] flags)
    {
        VortexBuffer buffer = arena.Allocate((flags.Length + 7) / 8, 8, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < flags.Length; i++)
        {
            bytes[i >> 3] |= flags[i] ? (byte)(1 << (i & 7)) : (byte)0;
        }

        return buffer;
    }

    /// <summary>A validity of <paramref name="present"/> from bit <paramref name="bitOffset"/>, the bits around set.</summary>
    private static Validity Bitmap(CanonicalArena arena, DTypeArena types, bool[] present, int bitOffset)
    {
        VortexBuffer buffer = arena.Allocate((present.Length + bitOffset + 7) / 8, 1, out Span<byte> bits);
        bits.Fill(0xFF);
        for (int i = 0; i < present.Length; i++)
        {
            int at = bitOffset + i;
            if (!present[i])
            {
                bits[at >> 3] &= (byte)~(1 << (at & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), present.Length, Validity.NonNullable, buffer, bitOffset));
    }
}
