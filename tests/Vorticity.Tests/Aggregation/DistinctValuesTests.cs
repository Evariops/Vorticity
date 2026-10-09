using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The values of a distinct count of one group, a count over the whole scan: each held once in its
/// slot, the value of zero bits apart, a float's equal values as
/// one, and a merge by parts finding every value of every lane in its part's run of slots, once.
/// </summary>
public sealed partial class DistinctValuesTests
{
    [Fact]
    public void AValueOfZeroBitsIsHeldApartAndCountedOnce()
    {
        DistinctValues<int> values = new DistinctValues<int>();
        Assert.True(values.Add(0));
        Assert.False(values.Add(0));
        Assert.True(values.Add(1));
        Assert.True(values.Add(-1));
        Assert.Equal(3, values.Count);

        DistinctValues<int> other = new DistinctValues<int>();
        other.Add(0);
        other.Add(2);
        Assert.Equal(1, values.MergeAll(other));
        Assert.Equal(4, values.Count);
        Assert.True(values.HoldsZero);
    }

    [Fact]
    public void AFloatsEqualValuesAreOneValue()
    {
        // Every NaN one value, both zeros one, whatever their bits.
        DistinctValues<double> values = new DistinctValues<double>();
        Assert.True(values.Add(double.NaN));
        Assert.False(values.Add(BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0001UL)));
        Assert.True(values.Add(-0.0));
        Assert.False(values.Add(0.0));
        Assert.True(values.Add(1.5));
        Assert.Equal(3, values.Count);

        DistinctValues<float> singles = new DistinctValues<float>();
        Assert.True(singles.Add(0.0f));
        Assert.False(singles.Add(-0.0f));
        Assert.True(singles.Add(float.NaN));
        Assert.False(singles.Add(BitConverter.UInt32BitsToSingle(0xFFC0_0001U)));
        Assert.Equal(2, singles.Count);
    }

    [Fact]
    public void ValuesOfManyBytesAreHeldBitForBit()
    {
        DistinctValues<UInt128> values = new DistinctValues<UInt128>();
        for (int i = 0; i < 5_000; i++)
        {
            Assert.True(values.Add(new UInt128((ulong)i * 31, (ulong)i)));
        }

        Assert.False(values.Add(new UInt128(31, 1)));
        Assert.True(values.Add(new UInt128(1, 31)));
        Assert.Equal(5_001, values.Count);
    }

    [Fact]
    public void AReservedSetKeepsItsValuesAndGrowsNoMore()
    {
        DistinctValues<long> values = new DistinctValues<long>();
        for (long i = 0; i < 1_000; i++)
        {
            values.Add(i * 7_919);
        }

        values.Reserve(100_000);
        long reserved = values.Footprint;
        Assert.Equal(DistinctValues<long>.FootprintOf(100_000), reserved);
        for (long i = 0; i < 100_000; i++)
        {
            Assert.Equal(i >= 1_000, values.Add(i * 7_919));
        }

        Assert.Equal(100_000, values.Count);
        Assert.Equal(reserved, values.Footprint);
    }

    [Fact]
    public void ASlotOfOneGroupReservesTheValuesItsFirstRowsForetell()
    {
        // 65 536 rows of a column of a million values: about 63 500 distinct, which foretell 865 000
        // over two million rows. The set takes them at once, and grows no more.
        long[] column = new long[2_000_000];
        for (int row = 0; row < column.Length; row++)
        {
            column[row] = (long)((((ulong)row * 0x9E37_79B9_7F4A_7C15UL) >> 20) % 1_000_000);
        }

        CanonicalArena arena = new CanonicalArena();
        try
        {
            FixedDistinctSlot<long> slot = new FixedDistinctSlot<long>(StorageKind.Primitive);
            slot.Ungrouped();
            slot.EnsureGroups(1);
            slot.StepRange(Input(arena, column.AsSpan(0, 65_536), 1), 0, 65_536, 0);
            slot.Foretell(65_536, column.Length, memory: null, lanes: 1);
            long reserved = slot.Footprint;
            Assert.True(reserved >= DistinctValues<long>.FootprintOf(800_000), $"{reserved} bytes reserved");

            slot.StepRange(Input(arena, column.AsSpan(65_536), 2), 0, column.Length - 65_536, 0);
            Assert.Equal(column.Distinct().Count(), slot.Result(0));
            Assert.Equal(reserved, slot.Footprint);
        }
        finally
        {
            arena.Reset();
        }
    }

    [Theory]
    [InlineData(2, 2_047)]
    [InlineData(8, 2_047)]
    [InlineData(64, 2_047)]
    [InlineData(64, 15)]
    [InlineData(16, 100_000)]
    public void EveryPartTakesTheValuesOfItsRunOfEveryLaneOnce(int parts, int perLane)
    {
        // Lanes about half full, whose runs of probed slots cross the parts' runs and wrap past the
        // last slot, sharing a third of their values; and lanes of fewer slots than there are parts. A
        // part's values share the top bits of their hash: placed by them, they filled one run of the
        // part's slots, a part's share of them, which probing walked to its end, from 1.0 s to minutes.
        int bits = System.Numerics.BitOperations.Log2((uint)parts);
        DistinctValues<long>[] lanes = new DistinctValues<long>[3];
        HashSet<long> all = [];
        for (int lane = 0; lane < lanes.Length; lane++)
        {
            lanes[lane] = new DistinctValues<long>();
            for (int i = 0; i < perLane; i++)
            {
                long value = i % 3 == 0 ? i : ((long)(lane + 1) << 40) + (i * 7_919L);
                lanes[lane].Add(value);
                all.Add(value);
            }
        }

        all.Remove(0);
        Dictionary<int, int> expected = all.GroupBy(v => DistinctValues<long>.PartOf(v, bits)).ToDictionary(g => g.Key, g => g.Count());
        for (int part = 0; part < parts; part++)
        {
            DistinctValues<long> distinct = new DistinctValues<long>(skip: bits);
            foreach (DistinctValues<long> lane in lanes)
            {
                distinct.AddPart(lane, part, bits);
            }

            Assert.Equal(expected.GetValueOrDefault(part), distinct.Count);
            Assert.True(distinct.Farthest() < 128, $"a value of part {part} landed {distinct.Farthest()} slots past its home");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task ACountOverTheScanCountsEachValueOnceWhateverItsMerge(int degree)
    {
        // A hundred thousand values a column over the lanes, zero and a float's equal values among
        // them: the lanes' values, many, merge by parts, the value of zero bits counted once.
        const int Rows = 240_000;
        Sample[] rows = new Sample[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int number = (int)((mix >> 20) % 100_000) - 50_000;
            double real = (row % 5) switch
            {
                0 => double.NaN,
                1 => row % 2 == 0 ? 0.0 : -0.0,
                _ => number / 4.0,
            };
            rows[row] = new Sample(number, real);
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "distinct-values");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Sample>(path))
            {
                await writer.WriteAsync<Sample>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Assert.Equal(rows.Select(r => r.Number).Distinct().Count(), await file.Scan<Sample>().CountDistinctAsync(r => r.Number, Ct));
            Assert.Equal(rows.Select(r => r.Real).Distinct().Count(), await file.Scan<Sample>().CountDistinctAsync(r => r.Real, Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A batch of one column of <c>long</c>s, laid in <paramref name="arena"/> as a scan lays it.</summary>
    private static BatchInput Input(CanonicalArena arena, ReadOnlySpan<long> column, long batch)
    {
        DType dtype = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(column.Length * sizeof(long), 8, out Span<byte> values);
        MemoryMarshal.AsBytes(column).CopyTo(values);
        int node = arena.AddPrimitive(dtype, column.Length, Validity.NonNullable, PType.I64, buffer);
        return new BatchInput(batch, arena, node, column.Length, default);
    }

    [VortexRecord]
    public partial record struct Sample(int Number, double Real);
}
