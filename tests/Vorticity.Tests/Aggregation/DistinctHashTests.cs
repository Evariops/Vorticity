using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The pairs a distinct count keeps are hashed under multipliers of the process: values built to
/// share one default hash, or one bucket of a prime-sized set, spread as any values do; equal values
/// hash alike; and every value is counted once per group.
/// </summary>
public sealed class DistinctHashTests
{
    private const int Count = 4_096;

    /// <summary>A prime bucket count, as a set's are.</summary>
    private const int Buckets = 8_419;

    /// <summary>
    /// A chain that 4,096 pairs hashed at random into 8,419 buckets all but never reach; forged
    /// values under the default hash make one chain of them all.
    /// </summary>
    private const int LongestChain = 32;

    /// <summary>The values of a forged column.</summary>
    public enum Forgery
    {
        /// <summary>(k << 32) | k, whose default hash folds its halves together into 0.</summary>
        EqualHalves,

        /// <summary>The multiples of the bucket count, which a hash that is the value takes to one bucket.</summary>
        PrimeMultiples,

        /// <summary>1, 2, 3 and on.</summary>
        CountingUp,
    }

    [Theory]
    [InlineData(Forgery.EqualHalves)]
    [InlineData(Forgery.PrimeMultiples)]
    [InlineData(Forgery.CountingUp)]
    public void ForgedValuesSpreadOverTheBuckets(Forgery forgery)
    {
        int[] chains = new int[Buckets];
        HashSet<int> hashes = [];
        for (long k = 1; k <= Count; k++)
        {
            int hash = new DistinctEntry<long>(0, Forged(forgery, k)).GetHashCode();
            hashes.Add(hash);
            chains[(uint)hash % Buckets]++;
        }

        Assert.InRange(chains.Max(), 1, LongestChain);
        Assert.True(hashes.Count > Count - 16, $"{hashes.Count} hashes for {Count} values");
    }

    // One value seen by every group is a pair per group: the group is part of the hash, or they would share one chain.
    [Fact]
    public void OneValueInManyGroupsHashesApart()
    {
        HashSet<int> hashes = [];
        for (int group = 0; group < Count; group++)
        {
            hashes.Add(new DistinctEntry<long>(group, 42).GetHashCode());
        }

        Assert.Equal(Count, hashes.Count);
    }

    // Every NaN is every other, and both zeros are one value: the entries are equal, so their hashes are.
    [Fact]
    public void EqualFloatsHashAlike()
    {
        EqualAndHashAlike(
            new DistinctEntry<double>(3, double.NaN), new DistinctEntry<double>(3, BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001)));
        EqualAndHashAlike(new DistinctEntry<double>(3, 0.0), new DistinctEntry<double>(3, -0.0));
        EqualAndHashAlike(
            new DistinctEntry<float>(3, float.NaN), new DistinctEntry<float>(3, BitConverter.Int32BitsToSingle(0x7FC0_0001)));
        EqualAndHashAlike(new DistinctEntry<float>(3, 0f), new DistinctEntry<float>(3, -0f));
        EqualAndHashAlike(new DistinctEntry<Half>(3, Half.NaN), new DistinctEntry<Half>(3, BitConverter.UInt16BitsToHalf(0x7E01)));
        EqualAndHashAlike(new DistinctEntry<Half>(3, Half.Zero), new DistinctEntry<Half>(3, Half.NegativeZero));
    }

    [Fact]
    public void ACountOverForgedIntegersCountsEachOnce()
    {
        long[] column = new long[2 * Count];
        for (int row = 0; row < column.Length; row++)
        {
            long k = (row % Count) + 1;
            column[row] = (k << 32) | k;
        }

        Assert.Equal([Count], CountDistinct(column, PType.I64, groups: null));
    }

    [Fact]
    public void ACountOverFloatsTakesEveryNaNAndBothZerosAsOneValueEach()
    {
        double[] column =
        [
            double.NaN, BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001), BitConverter.Int64BitsToDouble(unchecked((long)0xFFF0_0000_0000_0002)),
            0.0, -0.0, 1.5, 1.5,
        ];

        Assert.Equal([3], CountDistinct(column, PType.F64, groups: null));
    }

    // Rows of three groups, each group's values counted apart: the same value in two groups is two pairs.
    [Fact]
    public void RowsOfSeveralGroupsAreCountedGroupByGroup()
    {
        int[] groups = new int[3 * Count];
        long[] column = new long[groups.Length];
        for (int row = 0; row < column.Length; row++)
        {
            groups[row] = row % 3;
            column[row] = (row / 3) % (100 * (groups[row] + 1));
        }

        Assert.Equal([100, 200, 300], CountDistinct(column, PType.I64, groups));
    }

    // A merge adds the other set's pairs to this one's, under the map of its groups.
    [Fact]
    public void AMergeCountsThePairsOfBothOnce()
    {
        FixedDistinctSlot<long> left = Counted([1, 2, 3]);
        FixedDistinctSlot<long> right = Counted([3, 4]);
        left.MergeFrom(right, [0]);

        Assert.Equal(4, left.Result(0));
    }

    [Fact]
    public async Task ADistinctCountThroughAQueryCountsForgedValuesOnce()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "distinct");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"forged-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Grouped>(path))
            {
                Grouped[] rows = new Grouped[4 * Count];
                for (int i = 0; i < rows.Length; i++)
                {
                    long k = (i % Count) + 1;
                    rows[i] = new Grouped(i % 2, (k << 32) | k);
                }

                await writer.WriteAsync<Grouped>(rows, TestContext.Current.CancellationToken);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Dictionary<long, long> counts = [];
            Aggregation<(long, long)> distinct = file.Scan<Grouped>()
                .GroupBy(r => r.Group)
                .AggAsync(g => (g.Key, g.CountDistinct(r => r.Value)));
            await foreach ((long group, long count) in distinct.WithCancellation(TestContext.Current.CancellationToken))
            {
                counts.Add(group, count);
            }

            // Rows alternate between the groups and Count is even, so each group sees half the values.
            Assert.Equal(2, counts.Count);
            Assert.Equal(Count / 2, counts[0]);
            Assert.Equal(Count / 2, counts[1]);
        }
        finally
        {
            global::System.IO.File.Delete(path);
        }
    }

    private static long Forged(Forgery forgery, long k) => forgery switch
    {
        Forgery.EqualHalves => (k << 32) | k,
        Forgery.PrimeMultiples => k * Buckets,
        _ => k,
    };

    private static void EqualAndHashAlike<TValue>(DistinctEntry<TValue> a, DistinctEntry<TValue> b)
        where TValue : unmanaged, IEquatable<TValue>
    {
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    private static FixedDistinctSlot<long> Counted(long[] column)
    {
        CanonicalArena arena = new CanonicalArena();
        try
        {
            FixedDistinctSlot<long> slot = new FixedDistinctSlot<long>(StorageKind.Primitive);
            slot.EnsureGroups(1);
            slot.StepRange(Input(arena, column, PType.I64, 1), 0, column.Length, 0);
            return slot;
        }
        finally
        {
            arena.Reset();
        }
    }

    /// <summary>The distinct count of each group, every row in group 0 when <paramref name="groups"/> is null.</summary>
    private static long[] CountDistinct<TValue>(TValue[] column, PType ptype, int[]? groups)
        where TValue : unmanaged, IEquatable<TValue>
    {
        CanonicalArena arena = new CanonicalArena();
        try
        {
            int count = groups is null ? 1 : groups.Max() + 1;
            FixedDistinctSlot<TValue> slot = new FixedDistinctSlot<TValue>(StorageKind.Primitive);
            slot.EnsureGroups(count);
            BatchInput input = Input(arena, column, ptype, 1);
            if (groups is null)
            {
                slot.StepRange(input, 0, column.Length, 0);
            }
            else
            {
                slot.StepRows(input, groups);
            }

            long[] results = new long[count];
            for (int group = 0; group < count; group++)
            {
                results[group] = slot.Result(group);
            }

            return results;
        }
        finally
        {
            arena.Reset();
        }
    }

    private static BatchInput Input<TValue>(CanonicalArena arena, TValue[] column, PType ptype, long batch)
        where TValue : unmanaged
    {
        DType dtype = new DTypeArena().Primitive(ptype, Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(column.Length * Unsafe.SizeOf<TValue>(), 8, out Span<byte> values);
        MemoryMarshal.AsBytes(column.AsSpan()).CopyTo(values);
        int node = arena.AddPrimitive(dtype, column.Length, Validity.NonNullable, ptype, buffer);
        return new BatchInput(batch, arena, node, column.Length, default);
    }

    /// <summary>One row: a group and a value, written by hand as the generator would.</summary>
    internal readonly record struct Grouped(long Group, long Value) : IVortexRecord<Grouped>
    {
        public static VortexSchema Schema { get; } = [("Group", VortexType.Int64), ("Value", VortexType.Int64)];

        public static void ReadRows(Columns<Grouped> columns, Span<Grouped> rows)
        {
            ReadOnlySpan<long> groups = columns.Column<long>(0).Values;
            ReadOnlySpan<long> values = columns.Column<long>(1).Values;
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Grouped(groups[i], values[i]);
            }
        }

        public static void WriteRows(ColumnsBuilder<Grouped> builder, ReadOnlySpan<Grouped> rows)
        {
            ColumnBuilder<long> groups = builder.Column<long>(0);
            ColumnBuilder<long> values = builder.Column<long>(1);
            for (int i = 0; i < rows.Length; i++)
            {
                groups.Append(rows[i].Group);
                values.Append(rows[i].Value);
            }
        }
    }
}

internal static class GroupedSymbols
{
    extension(Probe<DistinctHashTests.Grouped> r)
    {
        public Sym<long> Group => r.Column<long>(0);

        public Sym<long> Value => r.Column<long>(1);
    }
}
