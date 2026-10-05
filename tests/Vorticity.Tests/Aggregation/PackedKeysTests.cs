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
/// A key of two to four columns, its parts numbered by indexes of their own and the numbers packed
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
}
