using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Keys;

/// <summary>A sorted timestamp column, the sorted column of most datasets of logs and metrics, as a key cursor serves it.</summary>
public sealed partial class SortedTimestampTests
{
    private const int Rows = 20_000;

    private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [VortexRecord]
    public partial record struct Tick(DateTime At, long Value);

    /// <summary>
    /// The column is bisected in its storage, through the extension, as an integer column is: every seek, rank and count
    /// lands where the instants say, across the zones and between two of them.
    /// </summary>
    [Fact]
    public async Task ASortedTimestampColumnIsSoughtThroughItsExtension()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-ticks-{Guid.NewGuid():N}.vortex");
        try
        {
            // Every instant on two rows, a second apart, over zones of 1 024 rows.
            Tick[] ticks = new Tick[Rows];
            for (int row = 0; row < Rows; row++)
            {
                ticks[row] = new Tick(At(row / 2), row);
            }

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Tick>(path, new VortexWriteOptions { RowBlockSize = 1_024 }))
            {
                await writer.WriteAsync<Tick>(ticks, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            KeyPlan plan = await file.Scan<Tick>().Keys(r => r.At).ExplainAsync(ct);
            Assert.Equal(KeySourceKind.SortedColumn, plan.Source);

            await using KeyCursor<DateTime> cursor = await file.Scan<Tick>().Keys(r => r.At).OpenAsync(ct);
            foreach (int second in (int[])[0, 1, 511, 512, 1_234, 5_000, (Rows / 2) - 1])
            {
                Assert.True(await cursor.SeekAsync(At(second), SeekMode.Exact, ct));
                Assert.Equal(2L * second, cursor.Row);
                Assert.Equal(At(second), cursor.Key);
                Assert.Equal(2L, await cursor.CountAtKeyAsync(ct));
                Assert.Equal(2L * second, await cursor.RankAsync(At(second), ct));

                // Half a second on: no entry holds it, the next instant's first row is at or after it.
                DateTime between = At(second).AddMilliseconds(500);
                Assert.False(await cursor.SeekAsync(between, SeekMode.Exact, ct));
                Assert.Equal(second + 1 < Rows / 2, await cursor.SeekAsync(between, SeekMode.AtOrAfter, ct));
                Assert.True(await cursor.SeekAsync(between, SeekMode.AtOrBefore, ct));
                Assert.Equal((2L * second) + 1, cursor.Row);
            }

            Assert.False(await cursor.SeekAsync(At(Rows), SeekMode.AtOrAfter, ct));
            Assert.True(await cursor.SeekAsync(Epoch.AddDays(-1), SeekMode.AtOrAfter, ct));
            Assert.Equal(0, cursor.Row);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static DateTime At(int second) => Epoch.AddSeconds(second);
}
