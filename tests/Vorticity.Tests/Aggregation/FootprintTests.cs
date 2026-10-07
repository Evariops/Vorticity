using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// What the groups of a run hold, gathered as its merge ends, its footprint: an int key, a count and a sum of a long take some tens of bytes a group, the index,
/// the keys and the records at their capacity; four lanes hold more, each its groups and the merge
/// its own.
/// </summary>
public sealed partial class FootprintTests
{
    private const int Groups = 100_000;

    [Fact]
    public async Task ARunCountsTheBytesOfItsGroups()
    {
        string path = await WriteAsync();
        try
        {
            long one = await StateBytesAsync(path, degree: 1);
            long four = await StateBytesAsync(path, degree: 4);
            Assert.InRange(one / Groups, 20, 100);
            Assert.True(four > one, $"{four} bytes at four lanes, {one} at one");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<long> StateBytesAsync(string path, int degree)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        Vorticity.Aggregation grouped = file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
        long groups = 0;
        await foreach (Total total in grouped.As<Total>().ToRecordsAsync(Ct))
        {
            groups++;
        }

        Assert.Equal(Groups, groups);
        return grouped.Plan.LastRun!.StateBytes;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three rows a key, the keys spread in no order over the file.</summary>
    private static async Task<string> WriteAsync()
    {
        Row[] rows = [.. Enumerable.Range(0, 3 * Groups).Select(row => new Row((int)((long)row * 7_919 % Groups), row % 101))];
        string directory = Path.Combine(AppContext.BaseDirectory, "footprint");
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
