using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A key of two to eight columns, its parts numbered by indexes of their own and the numbers packed
/// into a word: the groups .NET makes, nulls in a part included; through the table of the parts'
/// numbers and past it, where the hash alone serves; blocking and streaming, where each batch closed
/// renumbers the parts; at every degree.
/// </summary>
public sealed partial class PackedKeysTests
{
    private const int Rows = 150_000;

    private static readonly string[] Names = [.. Enumerable.Range(0, 50).Select(i => $"name-{i:D2}")];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task APackedKeyGroupsAsItsTuples(int degree)
    {
        Item[] rows = Items();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // Two parts, one of them text with nulls: fifteen thousand groups, through the table.
            Assert.Equal(
                rows.GroupBy(r => (r.Small, r.Name)).Select(g => new SmallName(g.Key.Small, g.Key.Name, g.Count(), g.Sum(r => r.Value))).OrderBy(g => $"{g.Small:D4}|{g.Name}"),
                (await ListAsync(file.Scan<Item>().GroupBy(r => (r.Small, r.Name)).Select(g => (g.Key.Small, g.Key.Name, g.Count(), g.Sum(r => r.Value))).As<SmallName>()))
                    .OrderBy(g => $"{g.Small:D4}|{g.Name}"));

            // Two parts of a group nearly a row: past the table, the hash alone.
            Assert.Equal(
                rows.GroupBy(r => (r.Small, r.Wide)).Select(g => new SmallWide(g.Key.Small, g.Key.Wide, g.Count())).OrderBy(g => g.Small).ThenBy(g => g.Wide),
                (await ListAsync(file.Scan<Item>().GroupBy(r => (r.Small, r.Wide)).Select(g => (g.Key.Small, g.Key.Wide, g.Count())).As<SmallWide>()))
                    .OrderBy(g => g.Small).ThenBy(g => g.Wide));

            // Three parts, a nullable boolean among them: a word of 128 bits.
            Assert.Equal(
                rows.GroupBy(r => (r.Small, r.Name, r.Flag)).Select(g => new SmallNameFlag(g.Key.Small, g.Key.Name, g.Key.Flag, g.Count())).OrderBy(g => $"{g.Small:D4}|{g.Name}|{g.Flag}"),
                (await ListAsync(file.Scan<Item>().GroupBy(r => (r.Small, r.Name, r.Flag)).Select(g => (g.Key.Small, g.Key.Name, g.Key.Flag, g.Count())).As<SmallNameFlag>()))
                    .OrderBy(g => $"{g.Small:D4}|{g.Name}|{g.Flag}"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AStreamingKeyOfFourPartsRenumbersItsPartsAsItsGroupsClose(int degree)
    {
        Item[] rows = Items();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation days = file.Scan<Item>()
                .GroupBy(r => (r.Day, r.Small, r.Name, r.Flag))
                .Select(g => (g.Key.Day, g.Key.Small, g.Key.Name, g.Key.Flag, g.Count(), g.Sum(r => r.Value)));
            Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)days.Query) >= 0);
            List<DayItem> streamed = await ListAsync(days.As<DayItem>());

            List<DayItem> expected = [.. rows.GroupBy(r => (r.Day, r.Small, r.Name, r.Flag))
                .Select(g => new DayItem(g.Key.Day, g.Key.Small, g.Key.Name, g.Key.Flag, g.Count(), g.Sum(r => r.Value)))];
            Assert.Equal(expected.Count, streamed.Count);
            Assert.Equal(expected.OrderBy(Order), streamed.OrderBy(Order));

            // The groups come out by day, the component that streams; the parts held no more than
            // the open days' values at a time.
            Assert.Equal(streamed.Select(d => d.Day).Order(), streamed.Select(d => d.Day));
            Assert.True(((AggregationQuery)days.Query).PeakGroups < expected.Count, $"{((AggregationQuery)days.Query).PeakGroups} groups held");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AKeyOfFiveToEightPartsPacksIntoAWordOfFour(int degree)
    {
        // The items twice, half a file apart, a day running over both: five parts, texts with nulls among
        // them, each tuple met twice, by two lanes at four; eight, the day first, a group a row. The
        // tuples of their numbers in a word of 256 bits; ordered by a text.
        Item[] items = Items();
        Wide8[] rows = new Wide8[2 * items.Length];
        for (int row = 0; row < rows.Length; row++)
        {
            int at = row % items.Length;
            Item item = items[at];
            ulong mix = (ulong)at * 0xC2B2_AE3D_27D4_EB4FUL;
            rows[row] = new Wide8(row / 1_000, item.Small, item.Name, item.Wide, item.Flag, at % 7 == 0 ? null : Names[(int)((mix >> 30) % 20)], (short)((mix >> 50) % 5), (int)((mix >> 20) % 3), item.Value);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "packed-keys", $"wide-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide8>(path))
        {
            await writer.WriteAsync<Wide8>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            static string Five(FiveCount g) => $"{g.Small:D4}|{g.Name}|{g.Wide:D7}|{g.Flag}|{g.Code}";
            Assert.Equal(
                rows.GroupBy(r => (r.Small, r.Name, r.Wide, r.Flag, r.Code)).Select(g => new FiveCount(g.Key.Small, g.Key.Name, g.Key.Wide, g.Key.Flag, g.Key.Code, g.Count(), g.Sum(r => r.Value))).OrderBy(Five, StringComparer.Ordinal),
                (await ListAsync(file.Scan<Wide8>().GroupBy(r => (r.Small, r.Name, r.Wide, r.Flag, r.Code)).Select(g => (g.Key.Small, g.Key.Name, g.Key.Wide, g.Key.Flag, g.Key.Code, g.Count(), g.Sum(r => r.Value))).As<FiveCount>()))
                    .OrderBy(Five, StringComparer.Ordinal));

            static string Eight(EightCount g) => $"{g.Day:D6}|{g.Small:D4}|{g.Name}|{g.Wide:D7}|{g.Flag}|{g.Code}|{g.Tiny}|{g.Bucket}";
            Assert.Equal(
                rows.GroupBy(r => (r.Day, r.Small, r.Name, r.Wide, r.Flag, r.Code, r.Tiny, r.Bucket))
                    .Select(g => new EightCount(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Key.Item4, g.Key.Item5, g.Key.Item6, g.Key.Item7, g.Key.Item8, g.Count()))
                    .OrderBy(Eight, StringComparer.Ordinal),
                (await ListAsync(file.Scan<Wide8>().GroupBy(r => (r.Day, r.Small, r.Name, r.Wide, r.Flag, r.Code, r.Tiny, r.Bucket))
                    .Select(g => (g.Key.Day, g.Key.Small, g.Key.Name, g.Key.Wide, g.Key.Flag, g.Key.Code, g.Key.Tiny, g.Key.Bucket, g.Count())).As<EightCount>()))
                    .OrderBy(Eight, StringComparer.Ordinal));

            // Nine, past the word: their values encoded into bytes.
            static string Nine(NineCount g) => $"{g.Day:D6}|{g.Small:D4}|{g.Name}|{g.Wide:D7}|{g.Flag}|{g.Code}|{g.Tiny}|{g.Bucket}|{g.Value:D3}";
            Assert.Equal(
                rows.GroupBy(r => (r.Day, r.Small, r.Name, r.Wide, r.Flag, r.Code, r.Tiny, r.Bucket, r.Value))
                    .Select(g => new NineCount(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Key.Item4, g.Key.Item5, g.Key.Item6, g.Key.Item7, g.Key.Item8, g.Key.Item9, g.Count()))
                    .OrderBy(Nine, StringComparer.Ordinal),
                (await ListAsync(file.Scan<Wide8>().GroupBy(r => (r.Day, r.Small, r.Name, r.Wide, r.Flag, r.Code, r.Tiny, r.Bucket, r.Value))
                    .Select(g => (g.Key.Day, g.Key.Small, g.Key.Name, g.Key.Wide, g.Key.Flag, g.Key.Code, g.Key.Tiny, g.Key.Bucket, g.Key.Value, g.Count())).As<NineCount>()))
                    .OrderBy(Nine, StringComparer.Ordinal));

            // Ordered by a text part, then the others: the parts compare as their columns would.
            List<FiveCount> ordered = await ListAsync(file.Scan<Wide8>().GroupBy(r => (r.Small, r.Name, r.Wide, r.Flag, r.Code))
                .OrderBy(g => g.Key.Code).ThenBy(g => g.Key.Wide).ThenBy(g => g.Key.Small).ThenBy(g => g.Key.Name).ThenBy(g => g.Key.Flag).Take(500)
                .Select(g => (g.Key.Small, g.Key.Name, g.Key.Wide, g.Key.Flag, g.Key.Code, g.Count(), g.Sum(r => r.Value))).As<FiveCount>());
            Assert.Equal(
                rows.GroupBy(r => (r.Small, r.Name, r.Wide, r.Flag, r.Code)).Select(g => new FiveCount(g.Key.Small, g.Key.Name, g.Key.Wide, g.Key.Flag, g.Key.Code, g.Count(), g.Sum(r => r.Value)))
                    .OrderBy(g => g.Code is null ? 1 : 0).ThenBy(g => g.Code, StringComparer.Ordinal).ThenBy(g => g.Wide).ThenBy(g => g.Small).ThenBy(g => g.Name is null ? 1 : 0).ThenBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Flag is null ? 1 : 0).ThenBy(g => g.Flag).Take(500),
                ordered);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Order(DayItem item) => $"{item.Day:D6}|{item.Small:D4}|{item.Name}|{item.Flag}";

    /// <summary>
    /// A few hundred small numbers; fifty names, null in one row of eleven; a number of a million
    /// values; a nullable boolean; a day in runs of five hundred rows, which is sorted.
    /// </summary>
    private static Item[] Items()
    {
        Item[] rows = new Item[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int small = (int)((mix >> 20) % 300);
            string? name = row % 11 == 0 ? null : Names[(int)((mix >> 40) % (ulong)Names.Length)];
            bool? flag = row % 13 == 0 ? null : (mix >> 50 & 1) == 1;
            rows[row] = new Item(row / 500, small, name, (long)(mix >> 12) % 1_000_000, flag, row % 97);
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

    private static async Task<string> WriteAsync(Item[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "packed-keys");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"items-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Item>(path))
        {
            await writer.WriteAsync<Item>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Item(int Day, int Small, string? Name, long Wide, bool? Flag, long Value);

    [VortexRecord]
    public partial record struct DayItem(int Day, int Small, string? Name, bool? Flag, long Count, long Sum);

    [VortexRecord]
    public partial record struct SmallName(int Small, string? Name, long Count, long Sum);

    [VortexRecord]
    public partial record struct SmallWide(int Small, long Wide, long Count);

    [VortexRecord]
    public partial record struct SmallNameFlag(int Small, string? Name, bool? Flag, long Count);

    [VortexRecord]
    public partial record struct Wide8(int Day, int Small, string? Name, long Wide, bool? Flag, string? Code, short Tiny, int Bucket, long Value);

    [VortexRecord]
    public partial record struct FiveCount(int Small, string? Name, long Wide, bool? Flag, string? Code, long Count, long Sum);

    [VortexRecord]
    public partial record struct EightCount(int Day, int Small, string? Name, long Wide, bool? Flag, string? Code, short Tiny, int Bucket, long Count);

    [VortexRecord]
    public partial record struct NineCount(int Day, int Small, string? Name, long Wide, bool? Flag, string? Code, short Tiny, int Bucket, long Value, long Count);
}
