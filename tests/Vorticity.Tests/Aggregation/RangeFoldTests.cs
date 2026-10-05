using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A key in runs of a few rows folds a batch's ranges in one call per aggregate: a count by the
/// ranges' lengths, a run-end or a constant column by its runs, a dictionary or a canonical one row
/// by row; the answers .NET gives, at every degree, the rows filtered or not.
/// </summary>
public sealed partial class RangeFoldTests
{
    private const int Rows = 200_000;

    private static readonly string[] Names = ["ash", "beech", "cedar", "elm", "fir", "oak"];

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task ShortRangesFoldAsTheirRows(int degree, bool filtered)
    {
        Tree[] rows = Trees();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // A filter on a column that is no key's leaves a mask on every batch.
            Scan<Tree> scan = filtered ? file.Scan<Tree>().Where(r => r.Coded != 20) : file.Scan<Tree>();
            List<Totals> totals = await ListAsync(scan
                .GroupBy(r => r.Name)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count(), g.Sum(r => r.Run), g.Sum(r => r.Constant), g.Sum(r => r.Coded), g.Sum(r => r.Value), g.Max(r => r.Run)))
                .As<Totals>());

            IEnumerable<Tree> kept = filtered ? rows.Where(r => r.Coded != 20) : rows;
            Assert.Equal(
                kept.GroupBy(r => r.Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new Totals(
                    g.Key, g.Count(), g.Sum(r => r.Run), g.Sum(r => r.Constant), g.Sum(r => r.Coded), g.Sum(r => r.Value), g.Max(r => r.Run))),
                totals);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Names in runs of seven rows; a long in runs of a thousand; a constant; a long of three
    /// values in no run; a float in quarters, null every fifty rows, whose sums are exact.
    /// </summary>
    private static Tree[] Trees()
    {
        Tree[] rows = new Tree[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Tree(Names[row / 7 % Names.Length], row / 1_000, 7, row % 3 * 10, row % 50 == 0 ? null : row % 400 * 0.25);
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

    private static async Task<string> WriteAsync(Tree[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "range-folds");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"trees-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Tree>(path))
        {
            await writer.WriteAsync<Tree>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Tree(string Name, long Run, long Constant, long Coded, double? Value);

    [VortexRecord]
    public partial record struct Totals(string Name, long Count, long Run, long Constant, long Coded, double? Value, long Latest);
}
