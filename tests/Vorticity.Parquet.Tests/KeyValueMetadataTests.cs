using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The footer's key-value metadata: the Vortex schema a file is written from, which restores on read
/// what Parquet cannot say where the two agree, and the caller's own pairs.
/// </summary>
public sealed class KeyValueMetadataTests : IDisposable
{
    private static readonly VortexType Money = VortexType.Extension("test.money", VortexType.Int64, new byte[] { 2, 0 });

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-keyvalue-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task RestoresTheZoneAndTheExtensionParquetCannotSay()
    {
        VortexSchema schema =
        [
            ("at", VortexType.Timestamp(TimeUnit.Microseconds, "Europe/Paris")),
            ("money", Money.Nullable),
            ("utc", VortexType.Timestamp(TimeUnit.Milliseconds, "UTC")),
            ("n", VortexType.Int32),
        ];
        DateTimeOffset at = new(2026, 10, 9, 12, 30, 15, TimeSpan.FromHours(2));
        await WriteAsync(schema, new ParquetWriteOptions { KeyValueMetadata = new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" } }, builder =>
        {
            builder.Column<DateTimeOffset>(0).Append(at);
            builder.Column<long?>(1).Append(1_234);
            builder.Column<DateTimeOffset>(2).Append(at);
            builder.Column<int>(3).Append(7);
            builder.Column<DateTimeOffset>(0).Append(DateTimeOffset.UnixEpoch);
            builder.Column<long?>(1).AppendNull();
            builder.Column<DateTimeOffset>(2).Append(DateTimeOffset.UnixEpoch);
            builder.Column<int>(3).Append(8);
        });

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Assert.Equal(schema, file.Schema);

        // The package's own pair first, then the caller's in the order of their keys.
        Assert.Equal([ParquetSchema.VortexSchemaKey, "a", "z"], file.KeyValueMetadata.Select(pair => pair.Key));
        Assert.Equal(["first", "last"], file.KeyValueMetadata.Skip(1).Select(pair => pair.Value));

        // The values read as written, through the restored types.
        List<string> rows = await RowsAsync(file.Scan().ToBatchesAsync(Ct));
        Assert.Equal(2, rows.Count);
        Assert.Contains("1234", rows[0], StringComparison.Ordinal);
        Assert.Contains("null", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesTimesInUnitsParquetLacksAsTheirValuesAndRestoresThem()
    {
        // Parquet has no date that counts milliseconds and no time or timestamp that counts seconds:
        // each is written as the integers it holds, unannotated, rather than converted to a unit
        // that would change them, and reads back as it was.
        VortexSchema schema =
        [
            ("day", VortexType.Extension("vortex.date", VortexType.Int64, new[] { (byte)TimeUnit.Milliseconds }).Nullable),
            ("clock", VortexType.Time(TimeUnit.Seconds)),
            ("at", VortexType.Timestamp(TimeUnit.Seconds)),
            ("zoned", VortexType.Timestamp(TimeUnit.Seconds, "Europe/Paris").Nullable),
        ];
        DateTimeOffset at = new(2026, 10, 9, 12, 30, 15, TimeSpan.FromHours(2));
        Action<ColumnsBuilder> fill = builder =>
        {
            builder.Column<DateOnly?>(0).Append(new DateOnly(2026, 10, 9));
            builder.Column<TimeOnly>(1).Append(new TimeOnly(23, 59, 58));
            builder.Column<DateTime>(2).Append(new DateTime(2026, 10, 9, 12, 30, 15, DateTimeKind.Unspecified));
            builder.Column<DateTimeOffset?>(3).Append(at);
            builder.Column<DateOnly?>(0).AppendNull();
            builder.Column<TimeOnly>(1).Append(TimeOnly.MinValue);
            builder.Column<DateTime>(2).Append(new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Unspecified));
            builder.Column<DateTimeOffset?>(3).AppendNull();
        };
        await WriteAsync(schema, ParquetWriteOptions.Default, fill);

        string vortex = _path + ".vortex";
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(vortex, schema))
            {
                ColumnsBuilder builder = writer.Builder();
                fill(builder);
                await writer.WriteAsync(builder, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
            Assert.Equal(schema, file.Schema);
            Assert.Equal(
                [(PhysicalType.Int64, LogicalTypeKind.None), (PhysicalType.Int32, LogicalTypeKind.None), (PhysicalType.Int64, LogicalTypeKind.None), (PhysicalType.Int64, LogicalTypeKind.None)],
                file.Compiled.Columns.Select(column => (column.Physical, column.Logical.Kind)));

            await using VortexFile expected = await VortexFile.OpenAsync(vortex, Ct);
            Assert.Equal(await RowsAsync(expected.Scan().ToBatchesAsync(Ct)), await RowsAsync(file.Scan().ToBatchesAsync(Ct)));
        }
        finally
        {
            System.IO.File.Delete(vortex);
        }
    }

    [Fact]
    public async Task IgnoresASchemaThatDisagrees()
    {
        // The pair names a schema of other columns: the file reads as Parquet says it is.
        VortexSchema schema = [("n", VortexType.Int64), ("s", VortexType.Utf8)];
        await WriteAsync(schema, ParquetWriteOptions.Default, builder =>
        {
            builder.Column<long>(0).Append(1);
            builder.Column<string>(1).Append("one");
        });

        await using (ParquetFile file = await ParquetFile.OpenAsync(_path, Ct))
        {
            ParquetSchema compiled = file.Compiled;
            VortexSchema other = [("n", Money)];
            Assert.Same(compiled, compiled.Restored(Convert.ToBase64String(Types.Serialization.DTypeFlatBuffers.Serialize(VortexTypes.ToDType(other, new Types.DTypeArena())))));
            Assert.Same(compiled, compiled.Restored("not base64 at all"));
            Assert.Same(compiled, compiled.Restored(Convert.ToBase64String([1, 2, 3, 4, 5])));
            Assert.Equal(schema, file.Schema);
        }
    }

    [Fact]
    public async Task RefusesThePackagesOwnKey()
    {
        ParquetWriteOptions options = new() { KeyValueMetadata = new Dictionary<string, string> { [ParquetSchema.VortexSchemaKey] = "mine" } };
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync([("n", VortexType.Int64)], options, builder => builder.Column<long>(0).Append(1)));
    }

    /// <summary>Every row of the batches, each value rendered and the columns joined.</summary>
    private static async Task<List<string>> RowsAsync(IAsyncEnumerable<RecordBatch> batches)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add(string.Join(" | ", Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))));
                }
            }
        }

        return rows;
    }

    private async Task WriteAsync(VortexSchema schema, ParquetWriteOptions options, Action<ColumnsBuilder> fill)
    {
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, options);
        ColumnsBuilder builder = writer.Builder();
        fill(builder);
        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
    }
}
