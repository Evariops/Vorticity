using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The tests that count every thread's allocations, which anything else the process ran beside them
/// would land in.
/// </summary>
[CollectionDefinition(nameof(ParquetAllocationCollection), DisableParallelization = true)]
public sealed class ParquetAllocationCollection
{
}

/// <summary>
/// A scan of a Parquet file allocates nothing per batch: a full scan of a row group of 64 batches
/// costs what one of 16 does, once warm, under every codec, for pages v1 and v2, and over every
/// encoding the writer makes, a nested list's among them.
/// </summary>
/// <remarks>
/// Not an equality: the 48 batches more would show as 1 152 bytes at the least, the smallest object
/// the runtime allocates being 24 bytes, and what is left under 256 is the shared pools' own state,
/// a rent that hits in one run and misses in the other, as the core's gates allow.
/// </remarks>
[Collection(nameof(ParquetAllocationCollection))]
public sealed class ScanAllocationTests : IDisposable
{
    private const int BatchRows = 8_192;

    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ParquetCompression.Uncompressed, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Snappy, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Lz4Raw, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Brotli, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Gzip, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V1)]
    [InlineData(ParquetCompression.Gzip, DataPageVersion.V1)]
    public async Task AScanAllocatesNothingPerBatch(ParquetCompression compression, DataPageVersion pages)
    {
        DebuggableAttribute? debuggable = typeof(ParquetFile).Assembly.GetCustomAttribute<DebuggableAttribute>();
        Assert.SkipWhen(debuggable is { IsJITOptimizerDisabled: true }, "Allocation figures are Release figures: run `dotnet test -c Release`.");

        await using ParquetFile few = await ParquetFile.OpenAsync(await WriteAsync(16 * BatchRows, compression, pages), Ct);
        await using ParquetFile many = await ParquetFile.OpenAsync(await WriteAsync(64 * BatchRows, compression, pages), Ct);
        for (int warm = 0; warm < 3; warm++)
        {
            await MeasureAsync(few, 16 * BatchRows);
            await MeasureAsync(many, 64 * BatchRows);
        }

        // Alternately, so that the shared pools are in the same state for both, then floored.
        List<long> fewBytes = [];
        List<long> manyBytes = [];
        for (int i = 0; i < 5; i++)
        {
            fewBytes.Add(await MeasureAsync(few, 16 * BatchRows));
            manyBytes.Add(await MeasureAsync(many, 64 * BatchRows));
        }

        fewBytes.Sort();
        manyBytes.Sort();
        long drift = Math.Abs(manyBytes[0] - fewBytes[0]);
        Assert.True(
            drift <= 256,
            string.Create(CultureInfo.InvariantCulture, $"48 batches more moved a scan's allocations by {drift} bytes ({manyBytes[0]} against {fewBytes[0]})"));
    }

    /// <summary>What a full scan of <paramref name="file"/> allocates, on every thread.</summary>
    private static async Task<long> MeasureAsync(ParquetFile file, long rows)
    {
        long seen = 0;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await foreach (BatchView batch in file.Scan().WithCancellation(Ct))
        {
            seen += batch.RowCount;
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(rows, seen);
        return allocated;
    }

    /// <summary>
    /// A row group of <paramref name="rows"/> rows of every encoding: deltas, split floats, a
    /// dictionary, prefixed and plain text, runs of booleans, nulls and lists.
    /// </summary>
    private async Task<string> WriteAsync(int rows, ParquetCompression compression, DataPageVersion pages)
    {
        VortexSchema schema =
        [
            ("id", VortexType.Int64),
            ("measure", VortexType.Float64),
            ("label", VortexType.Utf8),
            ("word", VortexType.Utf8),
            ("blob", VortexType.Binary),
            ("flag", VortexType.Bool),
            ("sparse", VortexType.Int32.Nullable),
            ("tags", VortexType.List(VortexType.Int32)),
        ];
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-alloc-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, new ParquetWriteOptions
        {
            Compression = compression,
            DataPageVersion = pages,
            BlockRows = BatchRows,
            RowGroupRows = 64 * BatchRows,
        });
        ColumnsBuilder builder = writer.Builder();
        Random random = new(41);
        for (int i = 0; i < rows; i++)
        {
            builder.Column<long>(0).Append(1_000_000L + i);
            builder.Column<double>(1).Append(20.0 + (5.0 * Math.Sin(i / 300.0)));
            builder.Column<string>(2).Append($"label-{i % 30}");
            builder.Column<string>(3).Append($"https://example.org/section-{i / 500:D4}/item-{i:D7}");
            byte[] blob = new byte[random.Next(0, 12)];
            random.NextBytes(blob);
            builder.Column<ReadOnlyMemory<byte>>(4).Append(blob);
            builder.Column<bool>(5).Append(i % 100 < 90);
            if (i % 5 == 0)
            {
                builder.Column<int?>(6).AppendNull();
            }
            else
            {
                builder.Column<int?>(6).Append(random.Next());
            }

            ColumnBuilder<ReadOnlyMemory<int>> tags = builder.Column<ReadOnlyMemory<int>>(7);
            tags.BeginList();
            for (int t = 0; t < i % 3; t++)
            {
                tags.Elements.Append(i + t);
            }

            tags.EndList();
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }
}
