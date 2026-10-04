using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class GroupHashTests
{
    private const int CrowdedKeys = 4_096;

    // Every value a * (2^32 + 1) has the same 64-bit hash, 0: a column of them would pile into one
    // bucket and make a group-by quadratic.
    [Fact]
    public void FixedKeysCrowdedIntoOneBucketAreRehashedUnderASeed()
    {
        GroupIndex<long> index = new GroupIndex<long>();
        for (int a = 0; a < CrowdedKeys; a++)
        {
            Assert.Equal(a, Group(ref index, Crowded(a), a));
        }

        Assert.True(index.Hardened);
        for (int a = 0; a < CrowdedKeys; a++)
        {
            Assert.Equal(a, Group(ref index, Crowded(a), -1));
        }
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

    [Fact]
    public void SpreadFixedKeysKeepTheDefaultHash()
    {
        GroupIndex<long> index = new GroupIndex<long>();
        Random random = new Random(18);
        for (int i = 0; i < 100_000; i++)
        {
            Group(ref index, random.NextInt64(), index.Count);
        }

        Assert.False(index.Hardened);
    }

    [Fact]
    public void AHardenedIndexStillGroupsEqualFloatsTogether()
    {
        GroupIndex<double> index = new GroupIndex<double>();
        for (int a = 1; a <= 4_096; a++)
        {
            Group(ref index, BitConverter.UInt64BitsToDouble((ulong)a * ((1UL << 32) + 1)), index.Count);
        }

        Assert.True(index.Hardened);
        int zero = Group(ref index, 0.0, index.Count);
        Assert.Equal(zero, Group(ref index, -0.0, -1));
        int nan = Group(ref index, double.NaN, index.Count);
        Assert.Equal(nan, Group(ref index, BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001), -1));
    }

    // Keys whose unseeded XxHash3 share their low bits all land in one slot of the table's probe
    // sequence: the cluster grows by one each insert until the table takes a seed.
    [Fact]
    public void ByteKeysCraftedToShareASlotReseedTheTable()
    {
        List<byte[]> keys = [];
        for (int i = 0; keys.Count < 1_024; i++)
        {
            byte[] key = Encoding.ASCII.GetBytes("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if ((XxHash3.HashToUInt64(key) & 0x7FF) == 0)
            {
                keys.Add(key);
            }
        }

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

    [Fact]
    public void OrdinaryByteKeysKeepTheUnseededHash()
    {
        ByteKeyTable table = new ByteKeyTable();
        for (int i = 0; i < 100_000; i++)
        {
            table.GetOrAdd(Encoding.ASCII.GetBytes("key-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), out _);
        }

        Assert.False(table.Reseeded);
    }

    /// <summary>Key <paramref name="a"/> of a column whose every key has the default hash 0.</summary>
    private static long Crowded(int a) => a * ((1L << 32) + 1);

    /// <summary>
    /// The group of <paramref name="value"/>, numbered <paramref name="next"/> when it is new; the
    /// index is told when its keys double, as its owner tells it.
    /// </summary>
    private static int Group<TValue>(ref GroupIndex<TValue> index, TValue value, int next)
        where TValue : unmanaged, IEquatable<TValue>
    {
        ref int group = ref index.Slot(value, out bool exists);
        if (exists)
        {
            return group;
        }

        group = next;
        int count = index.Count;
        if ((count & (count - 1)) == 0)
        {
            index.Doubled();
        }

        return next;
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
