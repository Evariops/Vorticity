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

    /// <summary>Keys scattered over a few hundred values: negative ones, nullable ones, unsigned ones.</summary>
    private static Reading[] Readings()
    {
        Reading[] rows = new Reading[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int scattered = (int)((uint)(row * 2_654_435_761u) >> 23);
            rows[row] = new Reading(scattered - 256, scattered % 9 == 0 ? null : (short)(scattered % 40 - 20), (uint)(scattered % 300) + 4_000_000_000u, row % 7);
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
    public partial record struct Reading(int Small, short? Maybe, uint Unsigned, long Value);

    [VortexRecord]
    public partial record struct Total(int Small, long Count, long Sum);

    [VortexRecord]
    public partial record struct MaybeTotal(short? Maybe, long Count, long Sum);

    [VortexRecord]
    public partial record struct UnsignedTotal(uint Unsigned, long Count, long Sum);
}
