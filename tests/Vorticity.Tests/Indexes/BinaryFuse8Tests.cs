// A binary fuse filter of 8-bit fingerprints, the presence filter of a column sheet.
//
// WHAT IS HELD: every key a filter was built from is found, whatever the set's size, empty and one key included; keys it
// was not built from are found about one time in 256, under 0.5 %; a large set takes about 1.125 bytes a key and never
// more than 1.25; the same keys make the same bytes; and what a filter writes reads back as the same filter.
using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class BinaryFuse8Tests
{
    public static TheoryData<int> Sizes() => [0, 1, 2, 3, 10, 100, 1_000, 10_000, 100_000, 1_000_000];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryKeyBuiltFromIsFound(int size)
    {
        ulong[] keys = Keys(size, seed: 3);
        BinaryFuse8 filter = BinaryFuse8.Build(keys)!;
        Assert.NotNull(filter);
        foreach (ulong key in keys)
        {
            Assert.True(filter.MayContain(key), $"key {key:X} of {size} is not found");
        }

        BinaryFuse8 read = RoundTrip(filter);
        foreach (ulong key in keys)
        {
            Assert.True(read.MayContain(key));
        }
    }

    [Fact]
    public void AKeyItWasNotBuiltFromIsFoundUnderHalfAPercentOfTheTime()
    {
        ulong[] keys = Keys(100_000, seed: 5);
        BinaryFuse8 filter = BinaryFuse8.Build(keys)!;
        HashSet<ulong> held = [.. keys];
        Random random = new Random(7);
        int asked = 0;
        int found = 0;
        while (asked < 1_000_000)
        {
            ulong key = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63);
            if (held.Contains(key))
            {
                continue;
            }

            asked++;
            found += filter.MayContain(key) ? 1 : 0;
        }

        double rate = (double)found / asked;
        Assert.True(rate < 0.005, $"{rate:P3} of keys not built from are found");
        Assert.True(rate > 0.002, $"{rate:P3} of keys not built from are found, far under one in 256: the fingerprints are not 8 bits");
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void ALargeSetTakesAboutAByteAndAnEighthAKey(int size)
    {
        BinaryFuse8 filter = BinaryFuse8.Build(Keys(size, seed: 11))!;
        double perKey = (double)(filter.Bytes - BinaryFuse8.HeaderBytes) / size;
        Assert.InRange(perKey, 1.125, 1.25);
    }

    [Fact]
    public void TheSameKeysMakeTheSameBytes()
    {
        ulong[] keys = Keys(50_000, seed: 13);
        ulong[] shuffled = (ulong[])keys.Clone();
        new Random(17).Shuffle(shuffled);
        Assert.Equal(Bytes(BinaryFuse8.Build(keys)!), Bytes(BinaryFuse8.Build(keys)!));
        Assert.Equal(BinaryFuse8.Build(keys)!.Bytes, BinaryFuse8.Build(shuffled)!.Bytes);
    }

    [Fact]
    public void BytesThatDoNotHoldAFilterAreRefused()
    {
        byte[] bytes = Bytes(BinaryFuse8.Build(Keys(1_000, seed: 19))!);
        Assert.Throws<VortexFormatException>(() => BinaryFuse8.Read(bytes.AsSpan(0, 10), out _));
        Assert.Throws<VortexFormatException>(() => BinaryFuse8.Read(bytes.AsSpan(0, bytes.Length - 1), out _));
        bytes[8] = 3;
        Assert.Throws<VortexFormatException>(() => BinaryFuse8.Read(bytes, out _));

        // No segment, and the two a filter always adds: a key's third slot would land past them.
        byte[] none = new byte[20 + 2 * 1_024];
        BitConverter.TryWriteBytes(none.AsSpan(8), 1_024u);
        BitConverter.TryWriteBytes(none.AsSpan(12), 0u);
        BitConverter.TryWriteBytes(none.AsSpan(16), 2 * 1_024u);
        Assert.Throws<VortexFormatException>(() => BinaryFuse8.Read(none, out _));
    }

    [Fact]
    public void ASketchsRanksAndGapsAreHeldToItsRegisters()
    {
        const int Registers = 1 << 12;
        byte[] ranks = new byte[Registers];
        ranks[5] = 3;
        byte[] dense = HyperLogLog.Encode(ranks.Select((r, i) => (byte)(i % 7)).ToArray());
        Assert.Equal(1, dense[0]);

        // A rank of 63 packed in six bits, past the 53 that twelve bits of index leave.
        dense[1] |= 63;
        Assert.Throws<VortexFormatException>(() => HyperLogLog.Decode(dense, new byte[Registers]));
        Assert.Throws<VortexFormatException>(() => HyperLogLog.Raise(dense, new byte[Registers]));

        // Sparse: one register at a gap past 2^63, which once turned the index negative; then a rank past 53.
        byte[] wrapped = [0, 1, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01, 3];
        Assert.Throws<VortexFormatException>(() => HyperLogLog.Decode(wrapped, new byte[Registers]));
        byte[] high = [0, 1, 5, 60];
        Assert.Throws<VortexFormatException>(() => HyperLogLog.Decode(high, new byte[Registers]));
        byte[] fine = [0, 1, 5, 53];
        byte[] decoded = new byte[Registers];
        HyperLogLog.Decode(fine, decoded);
        Assert.Equal(53, decoded[5]);
    }

    /// <summary><paramref name="size"/> distinct keys drawn from <paramref name="seed"/>.</summary>
    private static ulong[] Keys(int size, int seed)
    {
        Random random = new Random(seed);
        HashSet<ulong> keys = new HashSet<ulong>(size);
        while (keys.Count < size)
        {
            keys.Add((ulong)random.NextInt64() ^ ((ulong)random.Next() << 63));
        }

        return [.. keys];
    }

    private static byte[] Bytes(BinaryFuse8 filter)
    {
        byte[] bytes = new byte[filter.Bytes];
        filter.Write(bytes);
        return bytes;
    }

    private static BinaryFuse8 RoundTrip(BinaryFuse8 filter)
    {
        byte[] bytes = Bytes(filter);
        BinaryFuse8 read = BinaryFuse8.Read(bytes, out int length);
        Assert.Equal(bytes.Length, length);
        Assert.Equal(bytes, Bytes(read));
        return read;
    }
}
