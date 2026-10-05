using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Keys;

/// <summary>
/// A filtered scan weighs asking an exact source only where the file may hold one: the one column
/// the filter tests is sorted by the file's statistics, or indexed. Anywhere else the source would
/// decline, and the weighing, a walk of the whole file's zones before the first batch, would cost
/// for nothing. Where the gate opens, the cover is there; where it closes, it is not.
/// </summary>
public sealed partial class ExactCoverGateTests
{
    private const int Rows = 20_000;

    [Fact]
    public async Task AnExactSourceIsWeighedOnlyWhereTheFileMayHoldOne()
    {
        string plain = await WriteAsync(IndexPolicy.None);
        string indexed = await WriteAsync(IndexPolicy.None.SortedRuns(nameof(Row.Noise), required: true));
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(plain, Ct);

            // The key, sorted as the file's statistics say: the column serves, indexes or not.
            Assert.True(ExactCover.MayExist(file, Above(nameof(Row.Key), 5), indexes: true));
            Assert.True(ExactCover.MayExist(file, Above(nameof(Row.Key), 5), indexes: false));
            await using (ExactCover? cover = await ExactCover.TryCreateAsync(file, Above(nameof(Row.Key), 5), indexes: true, Ct))
            {
                Assert.NotNull(cover);
            }

            // The noise, neither sorted nor indexed, and a filter on two columns: nothing serves.
            Assert.False(ExactCover.MayExist(file, Above(nameof(Row.Noise), 5), indexes: true));
            Assert.False(ExactCover.MayExist(file, Expr.And(Above(nameof(Row.Key), 5), Above(nameof(Row.Noise), 5)), indexes: true));
            Assert.Null(await ExactCover.TryCreateAsync(file, Above(nameof(Row.Noise), 5), indexes: true, Ct));

            // The noise indexed: its runs serve, unless the scan leaves the indexes out.
            await using VortexFile runs = await VortexFile.OpenAsync(indexed, Ct);
            Assert.True(ExactCover.MayExist(runs, Above(nameof(Row.Noise), 5), indexes: true));
            Assert.False(ExactCover.MayExist(runs, Above(nameof(Row.Noise), 5), indexes: false));
            await using (ExactCover? cover = await ExactCover.TryCreateAsync(runs, Above(nameof(Row.Noise), 5), indexes: true, Ct))
            {
                Assert.NotNull(cover);
            }

            // Either way, the scan keeps the rows the filter keeps.
            long expected = 0;
            for (int row = 0; row < Rows; row++)
            {
                expected += Noise(row) > 5 ? 1 : 0;
            }

            Assert.Equal(expected, await CountAsync(file));
            Assert.Equal(expected, await CountAsync(runs));
        }
        finally
        {
            System.IO.File.Delete(plain);
            System.IO.File.Delete(indexed);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VortexExpr Above(string column, long value) =>
        Expr.Gt(Expr.Field(column), Expr.Literal(FilterLiteral.From(value)));

    private static long Noise(int row) => (long)((ulong)row * 0x9E3779B97F4A7C15UL >> 57);

    /// <summary>The rows a scan of <paramref name="file"/> filtered on the noise delivers, batch by batch.</summary>
    private static async Task<long> CountAsync(VortexFile file)
    {
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(Above(nameof(Row.Noise), 5)).ExecuteAsync().WithCancellation(Ct))
        {
            rows += batch.SelectedRows;
        }

        return rows;
    }

    private static async Task<string> WriteAsync(IndexPolicy indexes)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "exact-cover-gate");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Row(row, Noise(row));
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { Indexes = indexes }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(long Key, long Noise);
}
