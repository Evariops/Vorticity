// The order a locating run is written in: ChunkKeys.Ranked against the comparer it replaced.
//
// Ranked sorts fixed widths as integers and bytes eight at a time, and both are claimed to give the
// comparer's order exactly -- a claim the written bytes rest on, since a run's keys are laid out in
// that order. The oracle is the comparer itself, over keys built to break a windowed sort: shared
// prefixes longer than a window, keys that end on a window boundary, trailing zeros, and for the
// fixed widths every sign and every float oddity.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ChunkKeysTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BytesAreRankedInTheComparersOrder(int seed)
    {
        Random random = new Random(seed);
        byte[] alphabet = [0, 0, 1, 97, 255];
        HashSet<string> seen = [];
        List<byte[]> keys = [];
        byte[] shared = new byte[37];
        random.NextBytes(shared);
        while (keys.Count < 5_000)
        {
            // Half the keys start with the same 37 bytes, past four windows.
            int prefix = random.Next(2) == 0 ? random.Next(shared.Length + 1) : 0;
            int length = prefix + (random.Next(3) == 0 ? random.Next(3) * 8 : random.Next(20));
            byte[] key = new byte[length];
            shared.AsSpan(0, prefix).CopyTo(key);
            for (int i = prefix; i < length; i++)
            {
                key[i] = alphabet[random.Next(alphabet.Length)];
            }

            if (seen.Add(Convert.ToHexString(key)))
            {
                keys.Add(key);
            }
        }

        AssertRanked(new KeyLayout(KeyShape.Bytes, 0, default), keys);
    }

    [Theory]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    [InlineData(PType.I8)]
    [InlineData(PType.I16)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.F16)]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    internal void FixedWidthsAreRankedInTheComparersOrder(PType ptype)
    {
        Assert.True(KeyLayout.TryOf(new DTypeArena().Primitive(ptype, Nullability.NonNullable), out KeyLayout layout));
        int width = layout.Width;
        Random random = new Random((int)ptype);
        HashSet<ulong> seen = [];
        List<byte[]> keys = [];
        List<ulong> special = [0, ulong.MaxValue, 1, 1UL << ((width * 8) - 1), (1UL << ((width * 8) - 1)) - 1];
        if (ptype == PType.F64)
        {
            foreach (double d in new[] { 0.0, -0.0, double.NaN, -double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon })
            {
                special.Add(BitConverter.DoubleToUInt64Bits(d));
            }
        }
        else if (ptype == PType.F32)
        {
            foreach (float f in new[] { 0.0f, -0.0f, float.NaN, -float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                special.Add(BitConverter.SingleToUInt32Bits(f));
            }
        }

        ulong mask = width == 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
        int wanted = width == 1 ? 256 : 3_000;
        int index = 0;
        Span<byte> wide = stackalloc byte[8];
        while (keys.Count < wanted)
        {
            ulong bits = index < special.Count ? special[index] : (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);
            index++;
            bits &= mask;
            if (seen.Add(bits))
            {
                byte[] key = new byte[width];
                BinaryPrimitives.WriteUInt64LittleEndian(wide, bits);
                wide[..width].CopyTo(key);
                keys.Add(key);
            }
        }

        AssertRanked(layout, keys);
    }

    private static void AssertRanked(KeyLayout layout, List<byte[]> keys)
    {
        using ChunkKeys table = new ChunkKeys();
        foreach (byte[] key in keys)
        {
            int id = table.Intern(key);
            table.Note(id, id);
        }

        List<byte[]> expected = [.. keys];
        expected.Sort((a, b) => layout.Compare(a, b));
        int count = table.Ranked(layout, out int[] ranked);
        Assert.Equal(expected.Count, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(
                table.KeyBytes(ranked[i]).SequenceEqual(expected[i]),
                $"rank {i}: {Convert.ToHexString(table.KeyBytes(ranked[i]))}, expected {Convert.ToHexString(expected[i])}");
        }

        System.Buffers.ArrayPool<int>.Shared.Return(ranked);
    }
}
