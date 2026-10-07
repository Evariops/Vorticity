using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class GroupHashTests
{
    private const int CrowdedKeys = 4_096;

    // Every value a * (2^32 + 1) folds to the same 32 bits, 0: a column of them would pile into one
    // chain of the key table and make a group-by quadratic, but past MaxChain links the table takes a seed.
    [Fact]
    public void FixedKeysCrowdedIntoOneSlotAreRehashedUnderASeed()
    {
        KeyTable<long> table = new KeyTable<long>();
        for (int a = 0; a < CrowdedKeys; a++)
        {
            Assert.Equal(a, table.GetOrAdd(Crowded(a), a));
        }

        Assert.True(table.Seeded);
        for (int a = 0; a < CrowdedKeys; a++)
        {
            Assert.Equal(a, table.GetOrAdd(Crowded(a), -1));
        }
    }

    // The merge's hash of bytes inlines XXH3's paths of 16 bytes and less: the same bits as the
    // library's, at every length on both sides of each path's edge, under any seed.
    [Fact]
    public void AShortKeyHashesAsTheLibraryHashesIt()
    {
        Random random = new Random(20261007);
        ulong[] seeds = [MergeHash.Seed, 0, 1, ulong.MaxValue, 0x8000_0000_0000_0000UL, 0x0000_0000_FFFF_FFFFUL];
        byte[] bytes = new byte[40];
        foreach (ulong seed in seeds.Concat(Enumerable.Range(0, 32).Select(_ => (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63))))
        {
            for (int length = 0; length <= bytes.Length; length++)
            {
                for (int draw = 0; draw < 16; draw++)
                {
                    random.NextBytes(bytes);
                    ReadOnlySpan<byte> key = bytes.AsSpan(0, length);
                    Assert.Equal(XxHash3.HashToUInt64(key, unchecked((long)seed)), MergeHash.Of(key, seed));
                }
            }
        }
    }

    // Keys that share their home fill its line, then its chain: every one keeps its group, and a
    // chain shorter than MaxChain takes no seed.
    [Fact]
    public void KeysPastAFullLineKeepTheirGroupsInItsChain()
    {
        KeyTable<long> table = new KeyTable<long>();
        for (int a = 0; a < 40; a++)
        {
            Assert.Equal(a, table.GetOrAdd(Crowded(a), a));
        }

        Assert.False(table.Seeded);
        for (int a = 0; a < 40; a++)
        {
            Assert.Equal(a, table.GetOrAdd(Crowded(a), -1));
        }
    }

    // Keys of a structure the identity keeps: a stride of 2^22 (identifiers whose sequence field is
    // zero), a permutation of the integers, hot keys in a row among a range folded onto them modulo a
    // prime a little smaller than it. A probe over the whole table walked their runs and took a seed;
    // a line and its chain take none, and number the keys as they come.
    [Theory]
    [InlineData("strided")]
    [InlineData("permutation")]
    [InlineData("drift")]
    public void StructuredKeysTakeNoSeed(string pattern)
    {
        const int Rows = 400_000;
        KeyTable<long> table = new KeyTable<long>();
        Dictionary<long, int> oracle = [];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = Mix((ulong)row);
            long key = pattern switch
            {
                "strided" => (long)(mix % 100_000) << 22,
                "permutation" => unchecked((uint)row * 2_654_435_761u),
                _ => (mix & 1) == 0 ? (row / (Rows / 16) * 1_000) + (long)((mix >> 1) % 1_000) : 100_000 + (long)((mix >> 32) % 100_000),
            };
            if (!oracle.TryGetValue(key, out int expected))
            {
                expected = oracle.Count;
                oracle.Add(key, expected);
            }

            Assert.Equal(expected, table.GetOrAdd(key, table.Count));
        }

        Assert.False(table.Seeded);
        Assert.Equal(oracle.Count, table.Count);
    }

    // The index is rehashed while the groups fill: every group keeps its rows through it.
    [Fact]
    public async Task AGroupByOverCrowdedKeysCountsEveryGroup()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "groups");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"crowded-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Keyed>(path))
            {
                Keyed[] rows = new Keyed[2 * CrowdedKeys];
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = new Keyed(Crowded(i % CrowdedKeys));
                }

                await writer.WriteAsync<Keyed>(rows, TestContext.Current.CancellationToken);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Dictionary<long, long> counts = [];
            Scan<KeyCount> groups = file.Scan<Keyed>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count())).As<KeyCount>();
            await foreach ((long key, long count) in groups.ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                counts.Add(key, count);
            }

            Assert.Equal(CrowdedKeys, counts.Count);
            for (int a = 0; a < CrowdedKeys; a++)
            {
                Assert.Equal(2, counts[Crowded(a)]);
            }
        }
        finally
        {
            global::System.IO.File.Delete(path);
        }
    }

    // Keys in no order, in a row and at a regular stride keep the identity: a row of keys lands in a
    // row of slots, a stride spreads over the prime.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_024)]
    public void SpreadFixedKeysKeepTheIdentity(long stride)
    {
        KeyTable<long> table = new KeyTable<long>();
        Random random = new Random(18);
        long[] keys = new long[100_000];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = stride == 0 ? random.NextInt64() : i * stride;
            Assert.Equal(i, table.GetOrAdd(keys[i], i));
        }

        Assert.False(table.Seeded);
        Assert.Equal(keys.Length, table.Count);
        Assert.Equal(77, table.GetOrAdd(keys[77], -1));
    }

    [Fact]
    public void ASeededTableStillGroupsEqualFloatsTogether()
    {
        KeyTable<double> table = new KeyTable<double>();
        for (int a = 1; a <= 4_096; a++)
        {
            table.GetOrAdd(BitConverter.UInt64BitsToDouble((ulong)a * ((1UL << 32) + 1)), table.Count);
        }

        Assert.True(table.Seeded);
        int zero = table.GetOrAdd(0.0, table.Count);
        Assert.Equal(zero, table.GetOrAdd(-0.0, -1));
        int nan = table.GetOrAdd(double.NaN, table.Count);
        Assert.Equal(nan, table.GetOrAdd(BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001), -1));
    }

    // Keys whose XxHash3 under the process's seed share their low bits, built by whoever learns the
    // seed, all land in one slot of the table's probe sequence: the cluster grows by one each insert
    // until the table takes a seed of its own.
    [Fact]
    public void ByteKeysCraftedToShareASlotReseedTheTable()
    {
        List<byte[]> keys = Crowded();
        ByteKeyTable table = new ByteKeyTable();
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(i, table.GetOrAdd(keys[i], out bool added));
            Assert.True(added);
        }

        Assert.True(table.Reseeded);
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(i, table.GetOrAdd(keys[i], out bool added));
            Assert.False(added);
        }
    }

    // The null of a text key is an entry no key finds:
    // numbered with the keys, kept by Retain, passed over when the table rehashes or takes a seed;
    // the empty key is a key of its own.
    [Fact]
    public void ADetachedEntryNumbersAGroupNoKeyFinds()
    {
        List<byte[]> keys = Crowded();
        ByteKeyTable table = new ByteKeyTable();
        Assert.Equal(0, table.GetOrAdd(keys[0], out bool added));
        Assert.Equal(1, table.AddDetached());
        Assert.Equal(2, table.GetOrAdd([], out added));
        Assert.True(added);
        for (int i = 1; i < keys.Count; i++)
        {
            Assert.Equal(i + 2, table.GetOrAdd(keys[i], out added));
            Assert.True(added);
        }

        Assert.True(table.Reseeded);
        Assert.Equal(2, table.GetOrAdd([], out added));
        Assert.False(added);

        // Every entry kept but the first: the detached one numbered 0 and found by no key.
        table.Retain([.. Enumerable.Range(1, table.Count - 1)]);
        Assert.Equal(1, table.GetOrAdd([], out added));
        Assert.False(added);
        for (int i = 1; i < keys.Count; i++)
        {
            Assert.Equal(i + 1, table.GetOrAdd(keys[i], out added));
            Assert.False(added);
        }

        Assert.Equal(keys.Count + 1, table.GetOrAdd(keys[0], out added));
        Assert.True(added);
    }

    [Fact]
    public void OrdinaryByteKeysKeepTheProcessHash()
    {
        ByteKeyTable table = new ByteKeyTable();
        for (int i = 0; i < 100_000; i++)
        {
            table.GetOrAdd(Encoding.ASCII.GetBytes("key-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), out _);
        }

        Assert.False(table.Reseeded);
    }

    // A key's length lies ahead of its bytes in 7-bit groups: one byte below 128, more past it, and
    // none of it moves a key's bytes through growths and a Retain.
    [Fact]
    public void ByteKeysOfEveryLengthKeepTheirBytes()
    {
        int[] lengths = [0, 1, 127, 128, 300, 16_383, 16_384, 20_000];
        ByteKeyTable table = new ByteKeyTable();
        List<byte[]> keys = [];
        for (int i = 0; i < 400; i++)
        {
            byte[] key = new byte[lengths[i % lengths.Length] + (i / lengths.Length)];
            new Random(i).NextBytes(key);
            keys.Add(key);
            Assert.Equal(i, table.GetOrAdd(key, out bool added));
            Assert.True(added);
        }

        table.Retain([.. Enumerable.Range(0, keys.Count).Where(i => i % 2 == 1)]);
        for (int i = 0; i < table.Count; i++)
        {
            byte[] key = keys[(2 * i) + 1];
            Assert.True(table.KeyOf(i).SequenceEqual(key));
            Assert.Equal(i, table.GetOrAdd(key, out bool added));
            Assert.False(added);
        }
    }

    // A text key is hashed once: the hash its table keeps is the one a merge cuts the parts by, while
    // the table has no seed of its own, through growths and a Retain.
    [Fact]
    public void AByteKeyKeepsTheHashTheMergeCutsBy()
    {
        ByteKeyTable table = new ByteKeyTable();
        for (int i = 0; i < 5_000; i++)
        {
            table.GetOrAdd(Encoding.ASCII.GetBytes("key-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), out _);
        }

        table.Retain([.. Enumerable.Range(0, table.Count).Where(i => i % 3 != 0)]);
        Assert.False(table.Reseeded);
        for (int i = 0; i < table.Count; i++)
        {
            Assert.Equal(MergeHash.Of(table.KeyOf(i), MergeHash.Seed), table.HashOf(i));
            Assert.Equal(i, table.GetOrAdd(table.KeyOf(i).ToArray(), out bool added));
            Assert.False(added);
        }
    }

    /// <summary>Key <paramref name="a"/> of a column whose every key has the default hash 0.</summary>
    private static long Crowded(int a) => a * ((1L << 32) + 1);

    /// <summary>SplitMix64, as the queries bench draws its keys.</summary>
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>A thousand text keys whose XxHash3 under the process's seed share their low eleven bits.</summary>
    private static List<byte[]> Crowded()
    {
        List<byte[]> keys = [];
        for (int i = 0; keys.Count < 1_024; i++)
        {
            byte[] key = Encoding.ASCII.GetBytes("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if ((XxHash3.HashToUInt64(key, unchecked((long)MergeHash.Seed)) & 0x7FF) == 0)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>One row: a key, written by hand as the generator would.</summary>
    internal readonly record struct Keyed(long Key) : IVortexRecord<Keyed>
    {
        public static VortexSchema Schema { get; } = [("Key", VortexType.Int64)];

        public static void ReadRows(Columns<Keyed> columns, Span<Keyed> rows)
        {
            ReadOnlySpan<long> keys = columns.Column<long>(0).Values;
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Keyed(keys[i]);
            }
        }

        public static void WriteRows(ColumnsBuilder<Keyed> builder, ReadOnlySpan<Keyed> rows)
        {
            ColumnBuilder<long> keys = builder.Column<long>(0);
            for (int i = 0; i < rows.Length; i++)
            {
                keys.Append(rows[i].Key);
            }
        }
    }
}

internal static class KeyedSymbols
{
    extension(Probe<GroupHashTests.Keyed> r)
    {
        public Sym<long> Key => r.Column<long>(0);
    }
}
