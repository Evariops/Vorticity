using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Keys;

/// <summary>What a sorted column's zone map must say for the column to serve a key cursor.</summary>
public sealed partial class SortedColumnZoneMapTests
{
    [VortexRecord]
    public partial record struct MaybeKeyed(int? Key, long Value);

    /// <summary>
    /// A column of nulls alone states no maximum in any zone, and is served all the same: it has no entry, so nothing is
    /// sought in it. A map without maxima is refused only where there are entries to seek.
    /// </summary>
    [Fact]
    public async Task AColumnOfNullsAloneIsServedWithNoEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-null-keys-{Guid.NewGuid():N}.vortex");
        try
        {
            MaybeKeyed[] rows = new MaybeKeyed[10_000];
            for (int row = 0; row < rows.Length; row++)
            {
                rows[row] = new MaybeKeyed(null, row);
            }

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<MaybeKeyed>(path, new VortexWriteOptions { RowBlockSize = 1_024 }))
            {
                await writer.WriteAsync<MaybeKeyed>(rows, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            KeyPlan plan = await file.Scan<MaybeKeyed>().Keys(r => r.Key).ExplainAsync(ct);
            Assert.Equal(KeySourceKind.SortedColumn, plan.Source);
            Assert.Equal(0L, plan.EntryCount);

            await using KeyCursor<int?> cursor = await file.Scan<MaybeKeyed>().Keys(r => r.Key).OpenAsync(ct);
            Assert.False(await cursor.SeekFirstAsync(ct));
            Assert.False(await cursor.SeekAsync(5, SeekMode.AtOrAfter, ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
