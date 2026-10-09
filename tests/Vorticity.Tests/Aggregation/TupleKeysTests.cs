using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A key of five to eight text and integer columns as the tuple of its values: it groups as its values,
/// texts short, long and empty, nulls in texts and integers, integers of every width and sign, on one
/// lane, four and fourteen, under a filter, in the lanes' tables and in the core; its columns come back in
/// order; a key a tuple cannot hold keeps the numbers of its columns.
/// </summary>
public sealed partial class TupleKeysTests
{
    private const int Rows = 60_000;

    public static TheoryData<int, bool, bool> Cases => new TheoryData<int, bool, bool>
    {
        { 1, false, false },
        { 4, true, false },
        { 14, false, false },
        { 1, false, true },
        { 4, true, true },
        { 14, false, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ATupleKeyGroupsAsItsValues(int degree, bool filtered, bool core)
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            IEnumerable<Row> kept = filtered ? rows.Where(r => r.Value > 20L) : rows;
            Scan<Row> Scan() => filtered ? file.Scan<Row>().Where(r => r.Value > 20L) : file.Scan<Row>();

            // Two texts, short, long, empty and null, and four integers, two of them nullable.
            AssertGroups(
                kept.GroupBy(r => (r.Name, r.Code, r.Int, r.Long, r.Short, r.Tiny))
                    .Select(g => $"{Text(g.Key.Name)}|{g.Key.Code}|{g.Key.Int}|{Text(g.Key.Long)}|{Text(g.Key.Short)}|{g.Key.Tiny}|{g.Count()}|{g.Sum(r => r.Value)}"),
                (await ListAsync<Six>(Scan().GroupBy(r => (r.Name, r.Code, r.Int, r.Long, r.Short, r.Tiny)).Select(g => (g.Key.Name, g.Key.Code, g.Key.Int, g.Key.Long, g.Key.Short, g.Key.Tiny, g.Count(), g.Sum(r => r.Value))), core))
                    .Select(g => $"{Text(g.Name)}|{g.Code}|{g.Int}|{Text(g.Long)}|{Text(g.Short)}|{g.Tiny}|{g.Count}|{g.Sum}"));

            // Eight integers, every width, signed and not, the high bit of the unsigned ones set.
            AssertGroups(
                kept.GroupBy(r => (r.Tiny, r.Byte, r.Short, r.UShort, r.Int, r.UInt, r.Long, r.ULong))
                    .Select(g => $"{g.Key.Tiny}|{g.Key.Byte}|{Text(g.Key.Short)}|{g.Key.UShort}|{g.Key.Int}|{g.Key.UInt}|{Text(g.Key.Long)}|{g.Key.ULong}|{g.Count()}"),
                (await ListAsync<Eight>(Scan().GroupBy(r => (r.Tiny, r.Byte, r.Short, r.UShort, r.Int, r.UInt, r.Long, r.ULong)).Select(g => (g.Key.Tiny, g.Key.Byte, g.Key.Short, g.Key.UShort, g.Key.Int, g.Key.UInt, g.Key.Long, g.Key.ULong, g.Count())), core))
                    .Select(g => $"{g.Tiny}|{g.Byte}|{Text(g.Short)}|{g.UShort}|{g.Int}|{g.UInt}|{Text(g.Long)}|{g.ULong}|{g.Count}"));

            // Three texts, one always longer than a word: every lane numbers the long ones alike.
            AssertGroups(
                kept.GroupBy(r => (r.Name, r.Code, r.Label, r.Byte, r.UShort)).Select(g => $"{Text(g.Key.Name)}|{g.Key.Code}|{g.Key.Label}|{g.Key.Byte}|{g.Key.UShort}|{g.Count()}"),
                (await ListAsync<Texts>(Scan().GroupBy(r => (r.Name, r.Code, r.Label, r.Byte, r.UShort)).Select(g => (g.Key.Name, g.Key.Code, g.Key.Label, g.Key.Byte, g.Key.UShort, g.Count())), core))
                    .Select(g => $"{Text(g.Name)}|{g.Code}|{g.Label}|{g.Byte}|{g.UShort}|{g.Count}"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // The key's columns come back in order, each as its column orders it, nulls last both ways.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATupleKeyOrdersByItsComponents(bool core)
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Six> ordered = await ListAsync<Six>(
                file.Scan<Row>().GroupBy(r => (r.Name, r.Code, r.Int, r.Long, r.Short, r.Tiny))
                    .OrderBy(g => g.Key.Name).ThenByDescending(g => g.Key.Long).ThenBy(g => g.Key.Code).ThenBy(g => g.Key.Int).ThenByDescending(g => g.Key.Short).ThenBy(g => g.Key.Tiny)
                    .Select(g => (g.Key.Name, g.Key.Code, g.Key.Int, g.Key.Long, g.Key.Short, g.Key.Tiny, g.Count(), g.Sum(r => r.Value))),
                core);
            Comparer<string?> texts = Comparer<string?>.Create((a, b) => a is null || b is null ? (a is null).CompareTo(b is null) : string.CompareOrdinal(a, b));
            Comparer<long?> longsDown = Comparer<long?>.Create((a, b) => a is null || b is null ? (a is null).CompareTo(b is null) : b.Value.CompareTo(a.Value));
            Comparer<short?> shortsDown = Comparer<short?>.Create((a, b) => a is null || b is null ? (a is null).CompareTo(b is null) : b.Value.CompareTo(a.Value));
            List<Six> expected =
            [
                .. rows.GroupBy(r => (r.Name, r.Code, r.Int, r.Long, r.Short, r.Tiny))
                    .OrderBy(g => g.Key.Name, texts).ThenBy(g => g.Key.Long, longsDown).ThenBy(g => g.Key.Code, StringComparer.Ordinal).ThenBy(g => g.Key.Int).ThenBy(g => g.Key.Short, shortsDown).ThenBy(g => g.Key.Tiny)
                    .Select(g => new Six(g.Key.Name, g.Key.Code, g.Key.Int, g.Key.Long, g.Key.Short, g.Key.Tiny, g.Count(), g.Sum(r => r.Value))),
            ];
            Assert.Equal(expected, ordered);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // A run numbers its long texts once for all its lanes, the 30 labels and the 80 long names, under its
    // memory, which it gives back with its result.
    [Fact]
    public async Task ARunNumbersItsLongTextsOnceForAllItsLanes()
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = file.Scan<Row>().GroupBy(r => (r.Name, r.Code, r.Label, r.Byte, r.UShort))
                .Select(g => (g.Key.Name, g.Key.Code, g.Key.Label, g.Key.Byte, g.Key.UShort, g.Count()));
            grouped.Plan.CoreOnNew = false;
            TupleLayout[] layouts = [];
            grouped.Plan.Watch = partitions => layouts = [.. partitions.Select(partition => ((TupleKeys)partition.Keys!).Layout)];
            long groups = 0;
            await foreach (Texts group in grouped.As<Texts>().ToRecordsAsync(Ct))
            {
                groups++;
            }

            Assert.Equal(rows.GroupBy(r => (r.Name, r.Code, r.Label, r.Byte, r.UShort)).Count(), groups);
            Assert.True(layouts.Length > 1, $"{layouts.Length} lanes");
            Assert.Single(layouts.Distinct());
            Assert.Equal(110, layouts[0].LongTexts);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // A float among the columns, or four texts, which pass a tuple's 63 bytes: the numbers of the columns.
    [Fact]
    public async Task AKeyNoTupleHoldsKeepsTheNumbersOfItsColumns()
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<WithReal> real = await ListAsync<WithReal>(
                file.Scan<Row>().GroupBy(r => (r.Name, r.Code, r.Int, r.Tiny, r.Real)).Select(g => (g.Key.Name, g.Key.Code, g.Key.Int, g.Key.Tiny, g.Key.Real, g.Count())),
                core: false,
                typeof(PackedKeys<PackedTuple>));
            Assert.Equal(rows.GroupBy(r => (r.Name, r.Code, r.Int, r.Tiny, r.Real)).Count(), real.Count);
            List<FourTexts> four = await ListAsync<FourTexts>(
                file.Scan<Row>().GroupBy(r => (r.Name, r.Code, r.Label, r.Note, r.Byte)).Select(g => (g.Key.Name, g.Key.Code, g.Key.Label, g.Key.Note, g.Key.Byte, g.Count())),
                core: false,
                typeof(PackedKeys<PackedTuple>));
            Assert.Equal(rows.GroupBy(r => (r.Name, r.Code, r.Label, r.Note, r.Byte)).Count(), four.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void AssertGroups(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        List<string> left = [.. expected.Order(StringComparer.Ordinal)];
        List<string> right = [.. actual.Order(StringComparer.Ordinal)];
        Assert.True(left.Count > 100, $"{left.Count} groups");
        Assert.Equal(left, right);
    }

    /// <summary>
    /// The groups: in the core, its caches tiny so that every batch flushes and its tables split, when
    /// <paramref name="core"/>; in the lanes' tables of <paramref name="keys"/> (tuples by default) otherwise.
    /// </summary>
    private static async Task<List<T>> ListAsync<T>(Vorticity.Aggregation grouped, bool core, Type? keys = null)
        where T : IVortexRecord<T>
    {
        AggregationPlan plan = grouped.Plan;
        GroupKeys?[] lanes = [];
        if (core)
        {
            plan.Core = true;
            plan.CoreLanes = 1;
            plan.CoreCapacity = 96;
            plan.CoreFloor = 16;
            plan.CoreTableGroups = 40;
            plan.CoreBatchEntries = 8;
        }
        else
        {
            plan.CoreOnNew = false;
            plan.Watch = partitions => lanes = [.. partitions.Select(partition => partition.Keys)];
        }

        List<T> list = [];
        await foreach (T group in grouped.As<T>().ToRecordsAsync(Ct))
        {
            list.Add(group);
        }

        if (core)
        {
            Assert.NotNull(plan.LastRun!.Core);
        }
        else
        {
            Assert.Null(plan.LastRun!.Core);
            Assert.NotEmpty(lanes);
            Assert.All(lanes, partitionKeys => Assert.IsType(keys ?? typeof(TupleKeys), partitionKeys));
        }

        return list;
    }

    private static string Text(string? value) => value ?? "null";

    private static string Text(short? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Text(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// Names of 400, a fifth longer than a word, null one row in 17; a code and a label of the name, the
    /// code empty for some, the label always long; a fourth text; integers of a few values each, of every
    /// width, the unsigned ones past their signed range, two nullable; a float.
    /// </summary>
    private static Row[] MakeRows()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = Mix((ulong)row);
            int k = (int)(mix % 400);
            string? name = row % 17 == 0 ? null : k % 5 == 0 ? $"a-longer-name-{k:D5}" : $"n{k}";
            string code = k % 41 == 40 ? "" : $"c{k % 41}";
            string label = $"label-of-some-length-{k % 30:D4}";
            string note = $"note-{(mix >> 9) % 3}";
            sbyte tiny = (sbyte)((int)((mix >> 12) % 3) - 1);
            byte small = (byte)((mix >> 15) % 5 * 60);
            short? signed = row % 11 == 0 ? null : (short)((int)((mix >> 18) % 4) - 2);
            ushort wide = (ushort)((mix >> 21) % 6 * 13_000);
            int integer = ((int)((mix >> 24) % 5) * 1_000_003) - 2_000_000;
            uint unsigned = ((mix >> 27) % 3) switch { 0 => 7u, 1 => uint.MaxValue - 5, _ => 2_147_483_650u };
            long? large = row % 13 == 0 ? null : ((long)((mix >> 30) % 3) - 1) * (long.MaxValue / 3);
            ulong huge = ((mix >> 33) % 4) * 0x4000_0000_0000_0001UL;
            double real = (double)((mix >> 36) % 4) / 2;
            rows[row] = new Row(name, code, label, note, tiny, small, signed, wide, integer, unsigned, large, huge, real, (long)((mix >> 40) % 100));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "tuple-keys");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(
        string? Name, string Code, string Label, string Note, sbyte Tiny, byte Byte, short? Short, ushort UShort, int Int, uint UInt, long? Long, ulong ULong, double Real, long Value);

    [VortexRecord]
    public partial record struct Six(string? Name, string Code, int Int, long? Long, short? Short, sbyte Tiny, long Count, long Sum);

    [VortexRecord]
    public partial record struct Eight(sbyte Tiny, byte Byte, short? Short, ushort UShort, int Int, uint UInt, long? Long, ulong ULong, long Count);

    [VortexRecord]
    public partial record struct Texts(string? Name, string Code, string Label, byte Byte, ushort UShort, long Count);

    [VortexRecord]
    public partial record struct WithReal(string? Name, string Code, int Int, sbyte Tiny, double Real, long Count);

    [VortexRecord]
    public partial record struct FourTexts(string? Name, string Code, string Label, string Note, byte Byte, long Count);
}
