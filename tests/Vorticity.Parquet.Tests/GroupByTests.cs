using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A group by on an integer key of a Parquet file: its row groups' statistics bound the key, so that
/// the engine numbers its groups by value rather than hashing them, to the same groups.
/// </summary>
public sealed partial class GroupByTests : IDisposable
{
    private const int Rows = 200_000;

    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-groups-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Sale(int Store, long Units);

    [VortexRecord]
    public partial record struct StoreUnits(int Store, long Count, long Units);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task NumbersAnIntegerKeyByTheBoundsOfItsStatistics()
    {
        Random random = new(53);
        Sale[] rows = [.. Enumerable.Range(0, Rows).Select(_ => new Sale(1_000 + random.Next(5_000), random.Next(100)))];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Sale>(_path, new ParquetWriteOptions { RowGroupRows = 32_768 }))
        {
            await writer.WriteAsync<Sale>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Vorticity.Aggregation byStore = file.Scan<Sale>().GroupBy(s => s.Store).Select(g => (g.Key, g.Count(), g.Sum(s => s.Units)));

        // The key's values, from the least to the greatest the row groups' statistics give.
        ScanPlan plan = await byStore.ExplainAsync(Ct);
        Assert.Equal(rows.Max(r => r.Store) - rows.Min(r => r.Store) + 1, plan.Grouping!.Keys[0].ValueRange);

        List<StoreUnits> groups = [];
        await foreach (StoreUnits group in byStore.As<StoreUnits>().ToRecordsAsync(Ct))
        {
            groups.Add(group);
        }

        Assert.Equal(
            rows.GroupBy(r => r.Store).Select(g => new StoreUnits(g.Key, g.Count(), g.Sum(r => r.Units))).OrderBy(g => g.Store),
            groups.OrderBy(g => g.Store));
    }
}
