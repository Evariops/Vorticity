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
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// An integer key the file statistics bound to a few thousand values is grouped through a table
/// from the value to its group, a value hashed once: the groups .NET makes, negative values and
/// nulls included, at every degree; a keep renumbers the table with the groups.
/// </summary>
public sealed partial class DirectGroupKeysTests
{
    private const int Rows = 120_000;

    private static readonly DTypeArena Types = new DTypeArena();

    [Fact]
    public void ATableOfGroupsFollowsTheGroupsAKeepRenumbers()
    {
        GroupKeys keys = new FixedKeys<int>(Shape(VortexType.Int32), sorted: false, new KeyBounds(-5, 5));
        AssertGroupedAs(keys, [3, -5, 3, 0, 5, -5], [0, 1, 0, 2, 3, 1]);

        // The groups of -5 and 5 kept, numbered 0 and 1: the table forgets 3 and 0.
        keys.Keep([1, 3]);
        AssertGroupedAs(keys, [5, -5, 0, 3, 5], [1, 0, 2, 3, 1]);

        // A value past the bounds, which exact statistics never give, is grouped by the index alone.
        AssertGroupedAs(keys, [9, 5, 9, -6], [4, 1, 4, 5]);
        Assert.Equal(6, keys.Count);
    }

    // A span numbered whole: each value's group is its number from the start, a value past the bounds a
    // group past the span; the values no row met are dropped once the rows are folded, the others kept in
    // their order, and the keys number values as they first come from then on.
    [Fact]
    public void ASpanNumberedWholeDropsTheValuesNoRowMet()
    {
        GroupKeys keys = new FixedKeys<int>(Shape(VortexType.Int32), sorted: false, new KeyBounds(-5, 5));
        Assert.True(keys.NumberWhole(groupBytes: 16));
        AssertGroupedAs(keys, [3, -5, 3, 0, 5, -5], [8, 0, 8, 5, 10, 0]);
        AssertGroupedAs(keys, [9, 0], [11, 5]);
        Assert.Equal(12, keys.Count);

        int[] met = keys.Met()!;
        Assert.Equal([0, 5, 8, 10, 11], met);
        keys.Keep(met);
        AssertGroupedAs(keys, [5, 1, -5, 9], [3, 5, 0, 4]);
        Assert.Equal(6, keys.Count);
        Assert.Null(keys.Met());
    }

    // Every value of a span numbered whole met, a row's group is its number with no page read, a value past
    // the bounds left for the lookup, in vectors and then row by row; nothing is dropped. A span whose
    // groups would not fit the private cache is numbered as values come.
    [Fact]
    public void ASpanMetWholeNumbersItsRowsByTheirValue()
    {
        GroupKeys keys = new FixedKeys<int>(Shape(VortexType.Int32), sorted: false, new KeyBounds(100, 163));
        Assert.True(keys.NumberWhole(groupBytes: 16));
        AssertGroupedAs(keys, [.. Enumerable.Range(100, 64)], [.. Enumerable.Range(0, 64)]);
        int[] values = [.. Enumerable.Range(0, 37).Select(i => i == 20 ? 99 : i == 33 ? 500 : 100 + (i * 7 % 64))];
        AssertGroupedAs(keys, values, [.. values.Select(value => value == 99 ? 64 : value == 500 ? 65 : value - 100)]);
        Assert.Null(keys.Met());

        GroupKeys wide = new FixedKeys<int>(Shape(VortexType.Int32), sorted: false, new KeyBounds(0, (1 << 16) - 1));
        Assert.True(((FixedKeys<int>)wide).ByValue);
        Assert.False(wide.NumberWhole(groupBytes: 16));
    }

    // A span numbered whole whose rows meet one value in three, a null in thirteen among them, under a filter
    // and not: the groups .NET makes at every degree, as numbering values as they come makes them.
    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    public async Task ASpanNumberedWholeGroupsAsTheRowsDo(int degree, bool whole)
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            foreach (bool filtered in (bool[])[false, true])
            {
                Scan<Reading> scan = filtered ? file.Scan<Reading>().Where(r => r.Value > 3) : file.Scan<Reading>();
                Vorticity.Aggregation grouped = scan.GroupBy(r => r.Sparse).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
                grouped.Plan.NumberWhole = whole;
                List<SparseTotal> totals = await ListAsync(grouped.As<SparseTotal>());
                Assert.Equal(
                    rows.Where(r => !filtered || r.Value > 3).GroupBy(r => r.Sparse).Select(g => new SparseTotal(g.Key, g.Count(), g.Sum(r => r.Value)))
                        .OrderBy(t => t.Sparse ?? int.MinValue),
                    totals.OrderBy(t => t.Sparse ?? int.MinValue));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ABoundedKeyGroupsAsTheRowsDo(int degree)
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Assert.True(file.HasFileStatistics);

            List<Total> bySmall = await ListAsync(file.Scan<Reading>().GroupBy(r => r.Small).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).As<Total>());
            Assert.Equal(
                rows.GroupBy(r => r.Small).OrderBy(g => g.Key).Select(g => new Total(g.Key, g.Count(), g.Sum(r => r.Value))),
                bySmall);

            // Nulls in the key, and unsigned values; under a filter of rows.
            List<MaybeTotal> byMaybe = await ListAsync(file.Scan<Reading>().Where(r => r.Value > 3).GroupBy(r => r.Maybe).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).As<MaybeTotal>());
            Assert.Equal(
                rows.Where(r => r.Value > 3).GroupBy(r => r.Maybe).OrderBy(g => g.Key is null).ThenBy(g => g.Key)
                    .Select(g => new MaybeTotal(g.Key, g.Count(), g.Sum(r => r.Value))),
                byMaybe);
            List<UnsignedTotal> byUnsigned = await ListAsync(file.Scan<Reading>().GroupBy(r => r.Unsigned).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).As<UnsignedTotal>());
            Assert.Equal(
                rows.GroupBy(r => r.Unsigned).OrderBy(g => g.Key).Select(g => new UnsignedTotal(g.Key, g.Count(), g.Sum(r => r.Value))),
                byUnsigned);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertGroupedAs(GroupKeys keys, int[] values, int[] groups)
    {
        CanonicalArena batch = new CanonicalArena();
        DType dtype = Types.Primitive(PType.I32, Nullability.NonNullable);
        VortexBuffer buffer = batch.Allocate(values.Length * sizeof(int), sizeof(int), out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, int>(bytes));
        int node = batch.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.I32, buffer);
        int[] rowGroups = new int[values.Length];

        Assert.False(keys.Assign(batch, [node], values.Length, [], rowGroups, new GroupRanges()));
        Assert.Equal(groups, rowGroups);
        Func<int, int> key = keys.Reader<int>(0);
        Assert.Equal(values, rowGroups.Select(group => key(group)));
    }

    private static ColumnShape Shape(VortexType type) =>
        new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, []));

    /// <summary>Keys scattered over a few hundred values: negative ones, nullable ones, unsigned ones, one value in three of their span.</summary>
    private static Reading[] Readings()
    {
        Reading[] rows = new Reading[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int scattered = (int)((uint)(row * 2_654_435_761u) >> 23);
            rows[row] = new Reading(
                scattered - 256,
                scattered % 9 == 0 ? null : (short)(scattered % 40 - 20),
                (uint)(scattered % 300) + 4_000_000_000u,
                row % 7,
                row % 13 == 0 ? null : (scattered % 200 * 3) - 300);
        }

        return rows;
    }

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Reading[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "direct-keys");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"readings-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            await writer.WriteAsync<Reading>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Reading(int Small, short? Maybe, uint Unsigned, long Value, int? Sparse);

    [VortexRecord]
    public partial record struct SparseTotal(int? Sparse, long Count, long Sum);

    [VortexRecord]
    public partial record struct Total(int Small, long Count, long Sum);

    [VortexRecord]
    public partial record struct MaybeTotal(short? Maybe, long Count, long Sum);

    [VortexRecord]
    public partial record struct UnsignedTotal(uint Unsigned, long Count, long Sum);
}
