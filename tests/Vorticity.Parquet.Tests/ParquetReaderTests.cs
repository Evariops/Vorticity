using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The reader, over files the writer made: every row read back under every codec, through the typed
/// and the tool scans; batches cut across pages of other sizes; the scan's filter, rows, chosen rows,
/// counts and extremes; every logical type read back and written again to the same bytes; and files
/// that are not Parquet refused.
/// </summary>
public sealed partial class ParquetReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Reading(int Sensor, long Time, double? Value, string? Name, bool Flag);

    private static Reading[] Readings(int count)
    {
        Reading[] rows = new Reading[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = new Reading(
                i % 37,
                1_700_000_000_000L + i * 1_000L,
                i % 7 == 0 ? null : i * 0.25,
                i % 5 == 0 ? null : i % 3 == 0 ? $"a name long enough to leave the view {i}" : $"n{i % 100}",
                i % 3 == 0);
        }

        return rows;
    }

    private static async Task WriteAsync(string path, Reading[] rows, ParquetWriteOptions? options = null)
    {
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Reading>(path, options);
        await writer.WriteAsync<Reading>(rows, Ct);
        await writer.CompleteAsync(Ct);
    }

    private static async Task<List<Reading>> ReadAsync(Scan<Reading> scan)
    {
        List<Reading> rows = [];
        await foreach (Reading row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    [Theory]
    [InlineData(ParquetCompression.Uncompressed)]
    [InlineData(ParquetCompression.Snappy)]
    [InlineData(ParquetCompression.Gzip)]
    [InlineData(ParquetCompression.Brotli)]
    [InlineData(ParquetCompression.Zstd)]
    [InlineData(ParquetCompression.Lz4Raw)]
    public async Task ReadsBackEveryRowItWrote(ParquetCompression compression)
    {
        using TempPath path = new();
        Reading[] rows = Readings(20_000);
        await WriteAsync(path.Value, rows, new ParquetWriteOptions { Compression = compression });

        await using ParquetFile file = await ParquetFile.OpenAsync(path.Value, Ct);
        Assert.Equal(rows.Length, file.RowCount);
        Assert.Equal(Reading.Schema.Count, file.Schema.Count);
        Assert.Equal(rows, await ReadAsync(file.Scan<Reading>()));
    }

    [Fact]
    public async Task CutsBatchesAcrossPagesOfAnotherSize()
    {
        using TempPath path = new();
        Reading[] rows = Readings(10_000);

        // Pages of 1 000 rows read as batches of 8 192, and pages of 8 192 as batches of 3 000: every
        // batch spans pages or a part of one, so every column is gathered.
        await WriteAsync(path.Value, rows, new ParquetWriteOptions { BlockRows = 1_000, RowGroupRows = 4_000 });
        await using (ParquetFile file = await ParquetFile.OpenAsync(path.Value, Ct))
        {
            Assert.Equal(3, file.RowGroupCount);
            Assert.Equal(rows, await ReadAsync(file.Scan<Reading>()));
        }

        await WriteAsync(path.Value, rows);
        await using (ParquetFile file = await ParquetFile.OpenAsync(path.Value, Ct))
        {
            Assert.Equal(rows, await ReadAsync(file.Scan<Reading>().With(new ScanOptions { BatchRows = 3_000 })));
        }
    }

    [Fact]
    public async Task AppliesTheScansFilterRowsAndProjection()
    {
        using TempPath path = new();
        Reading[] rows = Readings(30_000);
        await WriteAsync(path.Value, rows, new ParquetWriteOptions { RowGroupRows = 16_384 });
        await using ParquetFile file = await ParquetFile.OpenAsync(path.Value, Ct);

        Assert.Equal(rows.Length, await file.Scan<Reading>().CountAsync(Ct));
        Assert.Equal(
            Array.FindAll(rows, r => r.Sensor == 3),
            (await ReadAsync(file.Scan<Reading>().Where(p => p.Sensor == 3))).ToArray());
        Assert.Equal(Array.FindAll(rows, r => r.Value > 7_000).Length, await file.Scan<Reading>().Where(p => p.Value > 7_000.0).CountAsync(Ct));
        Assert.Equal(rows[100..20_000], (await ReadAsync(file.Scan<Reading>().Rows(new RowRange(100, 20_000)))).ToArray());
        Assert.Equal([rows[5], rows[16_390], rows[29_999]], (await ReadAsync(file.Scan<Reading>().Rows(5, 16_390, 29_999))).ToArray());
        Assert.Equal(rows[^1].Time, await file.Scan<Reading>().MaxAsync(p => p.Time, Ct));
        Assert.Equal(0, await file.Scan<Reading>().MinAsync(p => p.Sensor, Ct));

        // The tool scan reads the columns it names, and the filter's.
        long flags = 0;
        await foreach (RecordBatch batch in file.Scan("Flag").Where($"Sensor = {3}").ToBatchesAsync(Ct))
        {
            using (batch)
            {
                Assert.Single(batch.Schema);
                flags += batch.SelectedRows;
            }
        }

        Assert.Equal(Array.FindAll(rows, r => r.Sensor == 3).Length, flags);
    }

    [Fact]
    public async Task ReadsLogicalTypesBackToTheSameBytes()
    {
        using TempPath first = new();
        using TempPath second = new();
        VortexSchema schema =
        [
            ("tiny", VortexType.Int8),
            ("small", VortexType.UInt16.Nullable),
            ("d9", VortexType.Decimal(9, 2)),
            ("d18", VortexType.Decimal(18, 4).Nullable),
            ("d28", VortexType.Decimal(28, 5)),
            ("day", VortexType.Date),
            ("at", VortexType.Timestamp(TimeUnit.Microseconds, "UTC")),
            ("clock", VortexType.Time(TimeUnit.Milliseconds)),
            ("id", VortexType.Uuid.Nullable),
            ("half", VortexType.Float16.Nullable),
            ("raw", VortexType.Binary.Nullable),
        ];
        Random random = new(7);
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(first.Value, schema, new ParquetWriteOptions { BlockRows = 512, RowGroupRows = 2_048 }))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int i = 0; i < 5_000; i++)
            {
                bool none = i % 9 == 0;
                builder.Column<sbyte>(0).Append((sbyte)(i % 200 - 100));
                builder.Column<decimal>(2).Append((i - 2_500) * 1.25m);
                builder.Column<decimal>(4).Append(i * -123_456_789.12345m);
                builder.Column<DateOnly>(5).Append(DateOnly.FromDayNumber(700_000 + i));
                builder.Column<DateTimeOffset>(6).Append(DateTimeOffset.UnixEpoch.AddTicks(random.NextInt64(0, 10_000_000_000_000_000)));
                builder.Column<TimeOnly>(7).Append(new TimeOnly(i % 24, i % 60, i % 60, i % 1_000));
                if (none)
                {
                    builder.Column<ushort?>(1).AppendNull();
                    builder.Column<decimal?>(3).AppendNull();
                    builder.Column<Guid?>(8).AppendNull();
                    builder.Column<Half?>(9).AppendNull();
                    builder.Column<ReadOnlyMemory<byte>?>(10).AppendNull();
                }
                else
                {
                    builder.Column<ushort?>(1).Append((ushort)(i * 7));
                    builder.Column<decimal?>(3).Append(i * 1_000_000.0001m);
                    builder.Column<Guid?>(8).Append(new Guid(i, (short)i, (short)-i, 1, 2, 3, 4, 5, 6, 7, 8));
                    builder.Column<Half?>(9).Append((Half)(i * 0.5f));
                    byte[] bytes = new byte[i % 40];
                    random.NextBytes(bytes);
                    builder.Column<ReadOnlyMemory<byte>?>(10).Append(bytes);
                }
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using (ParquetFile file = await ParquetFile.OpenAsync(first.Value, Ct))
        {
            for (int i = 0; i < schema.Count; i++)
            {
                Assert.Equal(schema[i].Type, file.Schema[i].Type);
            }

            await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(second.Value, file.Schema, new ParquetWriteOptions { BlockRows = 512, RowGroupRows = 2_048 });
            await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        Assert.Equal(await System.IO.File.ReadAllBytesAsync(first.Value, Ct), await System.IO.File.ReadAllBytesAsync(second.Value, Ct));

    }

    [Fact]
    public async Task RefusesWhatIsNotParquet()
    {
        using TempPath path = new();
        await System.IO.File.WriteAllBytesAsync(path.Value, new byte[100], Ct);
        await Assert.ThrowsAsync<ParquetFormatException>(async () => await ParquetFile.OpenAsync(path.Value, Ct));

        await System.IO.File.WriteAllBytesAsync(path.Value, "PAR1"u8.ToArray(), Ct);
        await Assert.ThrowsAsync<ParquetFormatException>(async () => await ParquetFile.OpenAsync(path.Value, Ct));

        // A footer past what the open allows.
        await WriteAsync(path.Value, Readings(100));
        await Assert.ThrowsAsync<ParquetFormatException>(async () => await VortexSession.Default.OpenParquetAsync(path.Value, new ParquetOpenOptions { MaxFooterBytes = 16 }, Ct));

        // A file cut short loses its footer's magic.
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path.Value, Ct);
        await System.IO.File.WriteAllBytesAsync(path.Value, bytes[..^1], Ct);
        await Assert.ThrowsAsync<ParquetFormatException>(async () => await ParquetFile.OpenAsync(path.Value, Ct));

        // A footer's length past the file.
        bytes[^8] = 0xFF;
        bytes[^7] = 0xFF;
        await System.IO.File.WriteAllBytesAsync(path.Value, bytes, Ct);
        await Assert.ThrowsAsync<ParquetFormatException>(async () => await ParquetFile.OpenAsync(path.Value, Ct));
    }

    /// <summary>A path in the temporary directory, deleted with what was written there.</summary>
    private sealed class TempPath : IDisposable
    {
        public string Value { get; } = Path.Combine(Path.GetTempPath(), $"vorticity-parquet-{Guid.NewGuid():N}.parquet");

        public void Dispose() => System.IO.File.Delete(Value);
    }
}
