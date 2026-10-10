using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Real files from the data disk, as other writers wrote them: the January 2023 yellow taxi trips,
/// by Arrow's C++ library, and the first part of ClickBench's hits. Each holds to what it says of
/// itself, reads the same rows mapped and by positional reads, and the trips, written again by this
/// writer sorted on their pickup time, read back in that order as they lie.
/// </summary>
/// <remarks>Run only where <c>VORTICITY_REAL_DATA</c> names the directory that holds them.</remarks>
public sealed partial class RealDataTests : IDisposable
{
    private const string Trips = "nyc-taxi/yellow_tripdata_2023-01.parquet";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vx-real-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Trip(
        long? VendorID,
        [VortexColumn(Unit = TimeUnit.Microseconds)] DateTime? tpep_pickup_datetime,
        [VortexColumn(Unit = TimeUnit.Microseconds)] DateTime? tpep_dropoff_datetime,
        double? passenger_count,
        double? trip_distance,
        double? RatecodeID,
        string? store_and_fwd_flag,
        long? PULocationID,
        long? DOLocationID,
        long? payment_type,
        double? fare_amount,
        double? extra,
        double? mta_tax,
        double? tip_amount,
        double? tolls_amount,
        double? improvement_surcharge,
        double? total_amount,
        double? congestion_surcharge,
        double? airport_fee);

    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_REAL_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Files => [Trips, "clickbench/hits_0.parquet"];

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [MemberData(nameof(Files))]
    public async Task HoldsToItselfAndReadsTheSameRowsByAnyRead(string relative)
    {
        string path = Require(relative);
        await using VortexSession mapped = VortexSession.Create(options => options.MapFiles = true);
        await using VortexSession positional = VortexSession.Create(options => options.MapFiles = false);
        await using ParquetFile inPlace = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, mapped, Ct);
        await using ParquetFile read = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, positional, Ct);

        Assert.Empty(await inPlace.VerifyAsync(Ct));
        (long rows, ulong digest) = await DigestAsync(inPlace.Scan().ToBatchesAsync(Ct));
        Assert.Equal(inPlace.Metadata.RowCount, rows);
        Assert.Equal((rows, digest), await DigestAsync(read.Scan().ToBatchesAsync(Ct)));
    }

    [Fact]
    public async Task WritesTheTripsSortedOnTheirPickupTimeAndReadsThemSo()
    {
        string path = Require(Trips);
        ulong sorted;
        long rows;
        await using (ParquetFile file = await ParquetFile.OpenAsync(path, Ct))
        {
            Assert.Equal(nameof(KeySourceKind.InMemory), (await file.Scan<Trip>().OrderBy(t => t.tpep_pickup_datetime).ExplainAsync(Ct)).Order!.Source);
            (rows, sorted) = await DigestAsync(file.Scan<Trip>().OrderBy(t => t.tpep_pickup_datetime).ToBatchesAsync(Ct));
            await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Trip>(_path, new ParquetWriteOptions
            {
                SortingColumns = [new ParquetSortingColumn("tpep_pickup_datetime")],
            });
            await foreach (RecordBatch batch in file.Scan<Trip>().OrderBy(t => t.tpep_pickup_datetime).ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        // Every row group declares the order and bears it out; ordered on it, the file streams as it
        // lies, the same rows in the same order as the sort of the original.
        await using ParquetFile rewritten = await ParquetFile.OpenAsync(_path, Ct);
        Assert.Empty(await rewritten.VerifyAsync(Ct));
        Assert.All(rewritten.Metadata.RowGroups, group => Assert.Equal([new ParquetSortingColumn("tpep_pickup_datetime")], group.SortingColumns));
        Assert.Equal(nameof(KeySourceKind.SortedColumn), (await rewritten.Scan<Trip>().OrderBy(t => t.tpep_pickup_datetime).ExplainAsync(Ct)).Order!.Source);
        Assert.Equal((rows, sorted), await DigestAsync(rewritten.Scan<Trip>().OrderBy(t => t.tpep_pickup_datetime).ToBatchesAsync(Ct)));
        Assert.Equal((rows, sorted), await DigestAsync(rewritten.Scan().ToBatchesAsync(Ct)));
    }

    /// <summary>The rows of the batches and a hash of every value of every row, rendered, in order.</summary>
    private static async Task<(long Rows, ulong Digest)> DigestAsync(IAsyncEnumerable<RecordBatch> batches)
    {
        XxHash64 hash = new();
        StringBuilder row = new();
        long rows = 0;
        await foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    row.Clear();
                    for (int c = 0; c < batch.Schema.Count; c++)
                    {
                        row.Append(Render.Row(batch, c, r)).Append('\u001f');
                    }

                    hash.Append(Encoding.UTF8.GetBytes(row.ToString()));
                    rows++;
                }
            }
        }

        return (rows, hash.GetCurrentHashAsUInt64());
    }

    private static string Require(string relative)
    {
        Assert.SkipWhen(Root is null || !System.IO.File.Exists(Path.Combine(Root!, relative)), $"VORTICITY_REAL_DATA names no directory holding {relative}.");
        return Path.Combine(Root!, relative);
    }
}
