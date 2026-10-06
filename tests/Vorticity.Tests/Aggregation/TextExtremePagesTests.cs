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
/// The extremes of a text keep their values in pages the slot shares among its groups
/// (PLAN-HIGH-CARDINALITY.md, H1, reduction 5): a page for many groups, not an object a group.
/// Values that grow move and leave their room behind, which a compaction takes back; a value longer
/// than a quarter of a page takes one of its own. The answers are the rows', at one lane and four.
/// </summary>
public sealed partial class TextExtremePagesTests
{
    private const int Groups = 20_000;

    private const int Rows = 200_000;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheExtremesOfATextShareTheirPages(int degree)
    {
        Row[] rows = Samples();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Min(r => r.Text), g.Max(r => r.Text)));
            List<BytesExtremeSlot<string>> slots = [];
            grouped.Plan.Watch = partitions =>
            {
                foreach (AggregationPartition partition in partitions)
                {
                    slots.AddRange(partition.Slots.OfType<BytesExtremeSlot<string>>());
                }
            };

            Dictionary<int, Extremes> read = [];
            await foreach (Extremes group in grouped.As<Extremes>().ToRecordsAsync(Ct))
            {
                read.Add(group.Key, group);
            }

            Assert.Equal(Groups, read.Count);
            foreach (IGrouping<int, Row> group in rows.GroupBy(r => r.Key))
            {
                string[] texts = [.. group.Select(r => r.Text).OfType<string>()];
                Assert.Equal(texts.Length == 0 ? null : texts.Min(StringComparer.Ordinal), read[group.Key].Least);
                Assert.Equal(texts.Length == 0 ? null : texts.Max(StringComparer.Ordinal), read[group.Key].Most);
            }

            // Twenty thousand groups' values in a few pages a slot, the long values' own included,
            // in every lane that read rows.
            Assert.Contains(slots, slot => slot.Pages > 0);
            Assert.All(slots, slot => Assert.InRange(slot.Pages, 0, 200));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The slot counts the bytes of its pages as it takes them (PLAN-HIGH-CARDINALITY.md, H1, the
    /// footprint): what the thread allocates, the headers of the arrays aside. A thousand values of
    /// 104 bytes' room fill two pages; a value longer than a quarter of a page takes one of its own.
    /// </summary>
    [Fact]
    public void ThePagesAreCountedAsTheyAreTaken()
    {
        BytesExtremeSlot<string> slot = new BytesExtremeSlot<string>(null!, max: true);
        slot.EnsureGroups(1_000);
        byte[] value = new byte[100];
        byte[] longest = new byte[30_000];
        longest[0] = byte.MaxValue;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int g = 0; g < 1_000; g++)
        {
            value[0] = (byte)g;
            slot.Offer(g, value);
        }

        slot.Offer(0, longest);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((2 * BytesExtremeSlot<string>.PageBytes) + 30_000, slot.PagedBytes);
        Assert.Equal(3, slot.Pages);
        Assert.InRange(allocated - slot.PagedBytes, 0, 256);
        Assert.InRange(slot.Footprint - slot.PagedBytes, 1_000 * (sizeof(long) + sizeof(int)), 3_000 * (sizeof(long) + sizeof(int)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Ten rows a group, its rows apart in the file: a value that grows by three characters from one
    /// row of a group to the next, which the maximum moves for; every thousandth group, one value
    /// longer than a quarter of a page; nulls, and the last ten groups nothing else.
    /// </summary>
    private static Row[] Samples()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int key = row % Groups;
            int round = row / Groups;
            string? text = key >= Groups - 10 || row % 7 == 3
                ? null
                : $"v{row:D7}{new string('x', round * 3)}{(key % 1_000 == 0 && round == 5 ? new string('y', 20_000) : string.Empty)}";
            rows[row] = new Row(key, text);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "text-extreme-pages");
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
    public partial record struct Row(int Key, string? Text);

    [VortexRecord]
    public partial record struct Extremes(int Key, string? Least, string? Most);
}
