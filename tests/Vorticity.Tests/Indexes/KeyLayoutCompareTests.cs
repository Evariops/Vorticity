// Two keys of an index's layout ordered as their values, for every integer width signed and unsigned: keys drawn at
// random, keys that share their high bytes, the ends of each type and equal keys; the sign alone is compared.
using System;
using System.Buffers.Binary;
using Vorticity.Indexes;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class KeyLayoutCompareTests
{
    [Theory]
    [InlineData((int)PType.I64)]
    [InlineData((int)PType.U64)]
    [InlineData((int)PType.I32)]
    [InlineData((int)PType.U32)]
    [InlineData((int)PType.I16)]
    [InlineData((int)PType.U16)]
    [InlineData((int)PType.I8)]
    [InlineData((int)PType.U8)]
    public void KeysAreOrderedAsTheirValues(int type)
    {
        PType ptype = (PType)type;
        Assert.True(KeyLayout.TryOf(new DTypeArena().Primitive(ptype, Nullability.NonNullable), out KeyLayout layout));
        int width = ptype.ByteWidth();
        bool signed = ptype.IsSignedInteger();
        Random random = new Random((int)ptype + 9);
        long[] edges = signed
            ? [Least(width), Least(width) + 1, -1, 0, 1, Most(width) - 1, Most(width)]
            : [0, 1, 255, 256, (long)(Most(width) >> 1), Most(width) - 1];
        for (int i = 0; i < 2_000; i++)
        {
            long a = i < edges.Length * edges.Length ? edges[i % edges.Length] : Draw(random, width, signed, i);
            long b = i < edges.Length * edges.Length ? edges[i / edges.Length % edges.Length] : (i % 3 == 0 ? a : Draw(random, width, signed, i + 1));
            byte[] left = Bytes(a, width);
            byte[] right = Bytes(b, width);
            int expected = signed ? Math.Sign(a.CompareTo(b)) : Math.Sign(((ulong)a & Mask(width)).CompareTo((ulong)b & Mask(width)));
            Assert.True(expected == Math.Sign(layout.Compare(left, right)), $"{ptype}: {a} against {b}");
        }
    }

    private static long Draw(Random random, int width, bool signed, int salt)
    {
        // Every other key shares its high bytes with a rising base.
        long value = salt % 2 == 0 ? random.NextInt64() : 0x1234_5678_9ABC_0000L + random.Next(65_536);
        return signed ? (value << (64 - (8 * width))) >> (64 - (8 * width)) : (long)((ulong)value & Mask(width));
    }

    private static long Least(int width) => width == 8 ? long.MinValue : -(1L << ((8 * width) - 1));

    private static long Most(int width) => width == 8 ? long.MaxValue : (1L << ((8 * width) - 1)) - 1;

    private static ulong Mask(int width) => width == 8 ? ulong.MaxValue : (1UL << (8 * width)) - 1;

    private static byte[] Bytes(long value, int width)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes[..width];
    }
}
