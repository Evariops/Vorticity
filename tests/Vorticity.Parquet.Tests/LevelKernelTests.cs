using System;
using Vorticity.Parquet.Reading;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The kernels that assemble nested nodes from levels, against plain transcriptions that walk the
/// levels an entry at a time: random levels at every width around the kernels' 64-level masks.
/// </summary>
public sealed class LevelKernelTests
{
    private static readonly int[] Lengths = [0, 1, 7, 8, 9, 31, 32, 33, 63, 64, 65, 127, 128, 129, 1_023, 1_024, 1_025, 8_191, 8_192, 8_193];

    [Fact]
    public void FindsTheSlotsOfAFieldAsAWalkDoes()
    {
        Random random = new(17);
        foreach (int entries in Lengths)
        {
            for (int trial = 0; trial < 20; trial++)
            {
                (byte[] rep, byte[] def) = Levels(random, entries, maxRepetition: 2, maxDefinition: 4);
                int repetition = random.Next(3);
                int definition = random.Next(3);
                int definedAt = definition + random.Next(2);
                int expected = SlotsByWalk(rep, def, repetition, definition, definedAt, out byte[] bits, out int validByWalk);
                byte[] present = new byte[(expected + 7) / 8];
                Array.Fill(present, (byte)0xA5);
                int slots = LevelKernels.Slots(rep, def, repetition, definition, definedAt, present, expected, out int valid);
                Assert.Equal(expected, slots);
                Assert.Equal(validByWalk, valid);
                for (int i = 0; i < expected; i++)
                {
                    Assert.Equal(Bit(bits, i), Bit(present, i));
                }

                // Every byte is written, the bits past the slots cleared.
                for (int i = expected; i < present.Length * 8; i++)
                {
                    Assert.False(Bit(present, i));
                }

                if (expected > 0)
                {
                    Assert.True(LevelKernels.Slots(rep, def, repetition, definition, definedAt, new byte[present.Length], expected - 1, out _) > expected - 1);
                }
            }
        }
    }

    [Fact]
    public void FindsTheSlotsAndElementsOfAListAsAWalkDoes()
    {
        Random random = new(23);
        foreach (int entries in Lengths)
        {
            for (int trial = 0; trial < 20; trial++)
            {
                (byte[] rep, byte[] def) = Levels(random, entries, maxRepetition: 2, maxDefinition: 5);
                int repetition = random.Next(2);
                int definition = random.Next(3);
                int definedAt = definition + random.Next(2);
                int elementsAt = definedAt + 1;
                int expected = ListsByWalk(
                    rep, def, repetition, definition, definedAt, repetition + 1, elementsAt,
                    out int[] offsetsByWalk, out int[] sizesByWalk, out byte[] bits, out int validByWalk, out int totalByWalk, out bool strayByWalk);
                byte[] present = new byte[(expected + 7) / 8];
                int[] offsets = new int[expected];
                int[] sizes = new int[expected];
                int slots = LevelKernels.Lists(
                    rep, def, repetition, definition, definedAt, repetition + 1, elementsAt, present, offsets, sizes, new int[2 * entries],
                    out int valid, out int total, out bool stray);
                Assert.Equal(expected, slots);
                Assert.Equal(strayByWalk, stray);
                Assert.Equal(totalByWalk, total);
                Assert.Equal(validByWalk, valid);
                if (!stray)
                {
                    Assert.Equal(offsetsByWalk, offsets);
                    Assert.Equal(sizesByWalk, sizes);
                    for (int i = 0; i < expected; i++)
                    {
                        Assert.Equal(Bit(bits, i), Bit(present, i));
                    }
                }
            }
        }
    }

    [Fact]
    public void FindsTheListsOfWellFormedLevelsWithoutStrays()
    {
        // Levels a writer makes: rows of zero to five elements, a list null or empty now and then.
        Random random = new(29);
        foreach (int rows in Lengths)
        {
            System.Collections.Generic.List<byte> rep = [];
            System.Collections.Generic.List<byte> def = [];
            int[] expectedSizes = new int[rows];
            for (int row = 0; row < rows; row++)
            {
                int size = random.Next(6);
                int kind = random.Next(8);
                if (kind == 0 || size == 0)
                {
                    rep.Add(0);
                    def.Add((byte)(kind == 0 ? 0 : 1));
                    continue;
                }

                expectedSizes[row] = size;
                for (int k = 0; k < size; k++)
                {
                    rep.Add((byte)(k == 0 ? 0 : 1));
                    def.Add((byte)(2 + random.Next(2)));
                }
            }

            int[] offsets = new int[rows];
            int[] sizes = new int[rows];
            byte[] present = new byte[(rows + 7) / 8];
            int slots = LevelKernels.Lists([.. rep], [.. def], 0, 0, 1, 1, 2, present, offsets, sizes, new int[2 * rep.Count], out int valid, out int total, out bool stray);
            Assert.Equal(rows, slots);
            Assert.False(stray);
            Assert.Equal(expectedSizes, sizes);
            int at = 0;
            for (int row = 0; row < rows; row++)
            {
                Assert.Equal(at, offsets[row]);
                at += sizes[row];
            }

            Assert.Equal(at, total);
            Assert.True(valid <= rows);
        }
    }

    [Fact]
    public void FindsTheZeroThatStartsARow()
    {
        Random random = new(31);
        foreach (int entries in Lengths)
        {
            byte[] levels = new byte[entries];
            for (int i = 0; i < entries; i++)
            {
                levels[i] = (byte)(random.Next(4) == 0 ? 0 : random.Next(1, 3));
            }

            int zeros = 0;
            for (int i = 0; i < entries; i++)
            {
                if (levels[i] == 0)
                {
                    Assert.Equal(i, LevelKernels.NthZero(levels, zeros));
                    zeros++;
                }
            }

            Assert.Equal(-1, LevelKernels.NthZero(levels, zeros));
        }
    }

    private static (byte[] Rep, byte[] Def) Levels(Random random, int entries, int maxRepetition, int maxDefinition)
    {
        byte[] rep = new byte[entries];
        byte[] def = new byte[entries];
        for (int i = 0; i < entries; i++)
        {
            rep[i] = (byte)random.Next(maxRepetition + 1);
            def[i] = (byte)random.Next(maxDefinition + 1);
        }

        return (rep, def);
    }

    /// <summary>The slots a walk an entry at a time finds, and their bits.</summary>
    private static int SlotsByWalk(byte[] rep, byte[] def, int repetition, int definition, int definedAt, out byte[] bits, out int valid)
    {
        bits = new byte[(def.Length + 7) / 8];
        valid = 0;
        int slots = 0;
        for (int e = 0; e < def.Length; e++)
        {
            if (rep[e] <= repetition && def[e] >= definition)
            {
                if (def[e] >= definedAt)
                {
                    bits[slots >> 3] |= (byte)(1 << (slots & 7));
                    valid++;
                }

                slots++;
            }
        }

        return slots;
    }

    /// <summary>A list's slots, offsets and sizes as a walk an entry at a time finds them.</summary>
    private static int ListsByWalk(
        byte[] rep, byte[] def, int repetition, int definition, int definedAt, int repeatedAt, int elementsAt,
        out int[] offsets, out int[] sizes, out byte[] bits, out int valid, out int total, out bool stray)
    {
        System.Collections.Generic.List<int> offsetList = [];
        System.Collections.Generic.List<int> sizeList = [];
        bits = new byte[(def.Length + 7) / 8];
        valid = 0;
        total = 0;
        stray = false;
        bool open = false;
        for (int e = 0; e < def.Length; e++)
        {
            int r = rep[e];
            int d = def[e];
            if (r <= repetition)
            {
                open = d >= definition;
                if (!open)
                {
                    continue;
                }

                int slot = offsetList.Count;
                if (d >= definedAt)
                {
                    bits[slot >> 3] |= (byte)(1 << (slot & 7));
                    valid++;
                }

                int size = d >= elementsAt ? 1 : 0;
                offsetList.Add(total);
                sizeList.Add(size);
                total += size;
            }
            else if (r == repeatedAt)
            {
                if (d < elementsAt)
                {
                    stray = true;
                }
                else if (!open)
                {
                    stray = true;
                    total++;
                }
                else
                {
                    sizeList[^1]++;
                    total++;
                }
            }
        }

        offsets = [.. offsetList];
        sizes = [.. sizeList];
        return offsets.Length;
    }

    private static bool Bit(byte[] bits, int i) => (bits[i >> 3] & (1 << (i & 7))) != 0;
}
