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
/// The numbers of a whole partition's groups, which a merge takes them all by, are shared by every
/// thread up to a bound, and past it the merge's own (PLAN-HIGH-CARDINALITY.md, H1, reduction 7): a
/// merge of lanes of more groups than that gives the rows' answers, keys in no order merged in series
/// and keys in order followed range after range, and leaves the shared numbers within their bound.
/// </summary>
public sealed partial class SharedNumbersTests
{
    private const int Groups = 150_000;

    private const int Rows = 3 * Groups;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMergeOfMoreGroupsThanTheSharedNumbersNumbersItsOwn(bool ordered)
    {
        Row[] rows = Samples(ordered);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
            grouped.Plan.MergeInParts = false;
            Dictionary<int, long> totals = [];
            await foreach (Total group in grouped.As<Total>().ToRecordsAsync(Ct))
            {
                Assert.Equal(3, group.Count);
                totals.Add(group.Key, group.Sum);
            }

            Assert.Equal(Groups, totals.Count);
            foreach (IGrouping<int, Row> group in rows.GroupBy(r => r.Key))
            {
                Assert.Equal(group.Sum(r => r.Value), totals[group.Key]);
            }

            Assert.InRange(Numbers.SharedCount, 0, Numbers.Shared);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three rows a key: the keys in order, or spread in no order over the whole file.</summary>
    private static Row[] Samples(bool ordered) =>
        [.. Enumerable.Range(0, Rows).Select(row => new Row(ordered ? row / 3 : (int)((long)row * 7_919 % Groups), row % 101))];

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shared-numbers");
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
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct Total(int Key, long Count, long Sum);
}
