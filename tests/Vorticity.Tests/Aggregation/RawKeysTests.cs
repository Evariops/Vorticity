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
/// A key of two to four fixed-width columns as the tuple of their values in one word
/// (PLAN-HIGH-CARDINALITY.md, H11): it groups as its tuples, with nulls, every NaN one value and both
/// zeros one, in 64 bits and in 128, on one lane and four, under a filter, and reads its components
/// back.
/// </summary>
public sealed partial class RawKeysTests
{
    private const int Rows = 60_000;

    public static TheoryData<int, bool> Cases => new TheoryData<int, bool>
    {
        { 1, false },
        { 1, true },
        { 4, false },
        { 4, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ARawKeyGroupsAsItsTuples(int degree, bool filtered)
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            IEnumerable<Row> kept = filtered ? rows.Where(r => r.Value > 20L) : rows;
            Scan<Row> Scan() => filtered ? file.Scan<Row>().Where(r => r.Value > 20L) : file.Scan<Row>();

            // Two columns in 64 bits: an int and a byte.
            AssertGroups(
                kept.GroupBy(r => (r.A, r.E)).Select(g => $"{g.Key.A}|{g.Key.E}|{g.Count()}|{g.Sum(r => r.Value)}"),
                (await ListAsync<IntByte>(Scan().GroupBy(r => (r.A, r.E)).Select(g => (g.Key.A, g.Key.E, g.Count(), g.Sum(r => r.Value))), typeof(RawKeys<ulong>)))
                    .Select(g => $"{g.A}|{g.E}|{g.Count}|{g.Sum}"));

            // A nullable short and a float with NaNs of several payloads and both zeros, in 64 bits.
            AssertGroups(
                kept.GroupBy(r => (r.C, r.D)).Select(g => $"{Text(g.Key.C)}|{Text(g.Key.D)}|{g.Count()}"),
                (await ListAsync<ShortFloat>(Scan().GroupBy(r => (r.C, r.D)).Select(g => (g.Key.C, g.Key.D, g.Count())), typeof(RawKeys<ulong>)))
                    .Select(g => $"{Text(g.C)}|{Text(g.D)}|{g.Count}"));

            // Two columns of 64 bits: 128.
            AssertGroups(
                kept.GroupBy(r => (r.B, r.F)).Select(g => $"{g.Key.B}|{g.Key.F}|{g.Count()}"),
                (await ListAsync<LongULong>(Scan().GroupBy(r => (r.B, r.F)).Select(g => (g.Key.B, g.Key.F, g.Count())), typeof(RawKeys<UInt128>)))
                    .Select(g => $"{g.B}|{g.F}|{g.Count}"));

            // Four columns, a nullable one among them: 88 bits and a null.
            AssertGroups(
                kept.GroupBy(r => (r.A, r.C, r.E, r.D)).Select(g => $"{g.Key.A}|{Text(g.Key.C)}|{g.Key.E}|{Text(g.Key.D)}|{g.Count()}"),
                (await ListAsync<Four>(Scan().GroupBy(r => (r.A, r.C, r.E, r.D)).Select(g => (g.Key.A, g.Key.C, g.Key.E, g.Key.D, g.Count())), typeof(RawKeys<UInt128>)))
                    .Select(g => $"{g.A}|{Text(g.C)}|{g.E}|{Text(g.D)}|{g.Count}"));

            // A nullable double and an int.
            AssertGroups(
                kept.GroupBy(r => (r.G, r.A)).Select(g => $"{Text(g.Key.G)}|{g.Key.A}|{g.Count()}"),
                (await ListAsync<DoubleInt>(Scan().GroupBy(r => (r.G, r.A)).Select(g => (g.Key.G, g.Key.A, g.Count())), typeof(RawKeys<UInt128>)))
                    .Select(g => $"{Text(g.G)}|{g.A}|{g.Count}"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // Columns the statistics bound to a product a table of groups holds keep their indexes and that
    // table: a byte of seven values and a short of fifty, nulls aside.
    [Fact]
    public async Task ABoundedSmallProductKeepsItsTableOfGroups()
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<ByteShort> groups = await ListAsync<ByteShort>(
                file.Scan<Row>().GroupBy(r => (r.E, r.C)).Select(g => (g.Key.E, g.Key.C, g.Count())),
                typeof(PackedKeys<ulong>));
            Assert.Equal(rows.GroupBy(r => (r.E, r.C)).Count(), groups.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // The key's columns come back in order, nulls last, as the column of each would order them.
    [Fact]
    public async Task ARawKeyOrdersByItsComponents()
    {
        Row[] rows = MakeRows();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<IntByte> ordered = await ListAsync<IntByte>(
                file.Scan<Row>().GroupBy(r => (r.A, r.E)).OrderBy(g => g.Key.A).ThenByDescending(g => g.Key.E).Select(g => (g.Key.A, g.Key.E, g.Count(), g.Sum(r => r.Value))),
                typeof(RawKeys<ulong>));
            List<IntByte> expected = [.. rows.GroupBy(r => (r.A, r.E)).OrderBy(g => g.Key.A).ThenByDescending(g => g.Key.E).Select(g => new IntByte(g.Key.A, g.Key.E, g.Count(), g.Sum(r => r.Value)))];
            Assert.Equal(expected, ordered);
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

    /// <summary>The groups, the lanes' keys checked to be <paramref name="raw"/> when it is given.</summary>
    private static async Task<List<T>> ListAsync<T>(Vorticity.Aggregation grouped, Type? raw)
        where T : IVortexRecord<T>
    {
        GroupKeys?[] lanes = [];
        grouped.Plan.Watch = partitions => lanes = [.. partitions.Select(partition => partition.Keys)];
        List<T> list = [];
        await foreach (T group in grouped.As<T>().ToRecordsAsync(Ct))
        {
            list.Add(group);
        }

        if (raw is not null)
        {
            Assert.NotEmpty(lanes);
            Assert.All(lanes, keys => Assert.IsType(raw, keys));
        }

        return list;
    }

    private static string Text(short? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Text(float value) => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(double? value) => value is not { } v ? "null" : v == 0 ? "0" : v.ToString("R", CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Values spread wide enough that no product of bounds fits a table of groups: an int of about a
    /// thousand values, a byte, a short with nulls, a float with NaNs of two payloads and both zeros,
    /// longs over their range, a double with nulls and NaNs.
    /// </summary>
    private static Row[] MakeRows()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int a = (int)((mix >> 10) % 1_000) * 1_000_003;
            byte e = (byte)((mix >> 20) % 7);
            short? c = row % 13 == 0 ? null : (short)((mix >> 30) % 50 - 25);
            float d = ((mix >> 40) % 9) switch
            {
                0 => float.NaN,
                1 => BitConverter.Int32BitsToSingle(0x7FC0_0001),
                2 => -0.0f,
                3 => 0.0f,
                _ => (float)((mix >> 44) % 5) / 4,
            };
            long b = (long)(((mix >> 3) % 300) * 0x0101_0101_0101_0101UL);
            ulong f = ((mix >> 13) % 3) * 0x8000_0000_0000_0001UL;
            double? g = row % 11 == 0 ? null : ((mix >> 50) % 6) == 0 ? double.NaN : (double)((mix >> 52) % 40);
            rows[row] = new Row(a, b, c, d, e, f, g, (long)((mix >> 8) % 100));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "raw-keys");
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
    public partial record struct Row(int A, long B, short? C, float D, byte E, ulong F, double? G, long Value);

    [VortexRecord]
    public partial record struct IntByte(int A, byte E, long Count, long Sum);

    [VortexRecord]
    public partial record struct ShortFloat(short? C, float D, long Count);

    [VortexRecord]
    public partial record struct LongULong(long B, ulong F, long Count);

    [VortexRecord]
    public partial record struct Four(int A, short? C, byte E, float D, long Count);

    [VortexRecord]
    public partial record struct DoubleInt(double? G, int A, long Count);

    [VortexRecord]
    public partial record struct ByteShort(byte E, short? C, long Count);
}
