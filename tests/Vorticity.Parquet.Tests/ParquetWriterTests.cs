using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Zstd;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The writer, read back through the package's own metadata readers: the footer, the schema, the
/// offset indexes, the page headers and every page's values; row groups of whole blocks, a flush that
/// leaves its partial block to the next row group, a page stored as it is when compression saves too
/// little, an empty file, and a file given up.
/// </summary>
public sealed partial class ParquetWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Reading(int Sensor, long Time, double? Value);

    [Fact]
    public async Task WritesEveryValueOnTheBlockGrid()
    {
        const int Rows = 20_000;
        using TempPath path = new();
        VortexSchema schema =
        [
            ("id", VortexType.Int64),
            ("value", VortexType.Float64.Nullable),
            ("name", VortexType.Utf8.Nullable),
            ("flag", VortexType.Bool),
        ];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path.Value, schema))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int i = 0; i < Rows; i++)
            {
                builder.Column<long>(0).Append(i * 3L);
                if (i % 7 == 0)
                {
                    builder.Column<double?>(1).AppendNull();
                }
                else
                {
                    builder.Column<double?>(1).Append(i * 0.5);
                }

                if (i % 5 == 0)
                {
                    builder.Column<string?>(2).AppendNull();
                }
                else
                {
                    builder.Column<string?>(2).Append($"name-{i % 100}");
                }

                builder.Column<bool>(3).Append(i % 3 == 0);
            }

            await writer.WriteAsync(builder, Ct);
            ParquetWriteReport report = await writer.CompleteAsync(Ct);
            Assert.Equal(Rows, report.RowCount);
            Assert.Equal(1, report.RowGroups);
            Assert.Equal(new FileInfo(path.Value).Length, report.Bytes);
        }

        WrittenFile file = new(await System.IO.File.ReadAllBytesAsync(path.Value, Ct));
        Assert.Equal(Rows, file.Footer.RowCount);
        Assert.Single(file.Footer.RowGroups);
        Assert.StartsWith("Vorticity.Parquet version ", file.CreatedBy, StringComparison.Ordinal);
        Assert.Equal(["id", "value", "name", "flag"], Array.ConvertAll(file.Schema.Columns, column => column.Path[0]));
        Assert.Equal(
            [PhysicalType.Int64, PhysicalType.Double, PhysicalType.ByteArray, PhysicalType.Boolean],
            Array.ConvertAll(file.Schema.Columns, column => column.Physical));
        Assert.Equal([0, 1, 1, 0], Array.ConvertAll(file.Schema.Columns, column => column.MaxDefinitionLevel));

        for (int column = 0; column < 4; column++)
        {
            PageLocation[] pages = file.Pages(0, column);
            Assert.Equal([0L, 8_192L, 16_384L], Array.ConvertAll(pages, page => page.FirstRow));
            int row = 0;
            foreach (PageLocation page in pages)
            {
                PageHeader header = file.Header(page);
                int rows = Math.Min(8_192, Rows - row);
                Assert.Equal(PageType.DataPageV2, header.Type);
                Assert.Equal(rows, header.RowCount);
                Assert.Equal(rows, header.ValueCount);
                Assert.Equal(page.Size, header.HeaderLength + header.CompressedPageSize);
                bool[] valid = file.Validity(page, header, rows);
                ReadOnlySpan<byte> values = file.Values(page, header);
                CheckPage(column, row, rows, valid, values);
                Assert.Equal(rows - Array.FindAll(valid, v => v).Length, header.NullCount);
                row += rows;
            }
        }

        // The integer column's bounds, and every column's nulls.
        ColumnChunkMetadata ids = file.Footer.Chunk(0, 0);
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(file.Range(ids.Statistics.MinValue)));
        Assert.Equal((Rows - 1) * 3L, BinaryPrimitives.ReadInt64LittleEndian(file.Range(ids.Statistics.MaxValue)));
        Assert.True(ids.Statistics.IsMinValueExact && ids.Statistics.IsMaxValueExact);
        Assert.Equal(0, ids.Statistics.NullCount);
        Assert.Equal((Rows + 6) / 7, file.Footer.Chunk(0, 1).Statistics.NullCount);
        Assert.Equal((Rows + 4) / 5, file.Footer.Chunk(0, 2).Statistics.NullCount);
        Assert.Equal(CompressionCodec.Zstd, ids.Codec);
        Assert.Equal(Rows, ids.ValueCount);
        Assert.Equal(4L, ids.DataPageOffset);
    }

    private static void CheckPage(int column, int first, int rows, bool[] valid, ReadOnlySpan<byte> values)
    {
        int at = 0;
        for (int r = 0; r < rows; r++)
        {
            int i = first + r;
            switch (column)
            {
                case 0:
                    Assert.True(valid[r]);
                    Assert.Equal(i * 3L, BinaryPrimitives.ReadInt64LittleEndian(values[(r * 8)..]));
                    break;
                case 1:
                    Assert.Equal(i % 7 != 0, valid[r]);
                    if (valid[r])
                    {
                        Assert.Equal(i * 0.5, BinaryPrimitives.ReadDoubleLittleEndian(values[at..]));
                        at += 8;
                    }

                    break;
                case 2:
                    Assert.Equal(i % 5 != 0, valid[r]);
                    if (valid[r])
                    {
                        int length = BinaryPrimitives.ReadInt32LittleEndian(values[at..]);
                        Assert.Equal($"name-{i % 100}", Encoding.UTF8.GetString(values.Slice(at + 4, length)));
                        at += 4 + length;
                    }

                    break;
                default:
                    Assert.True(valid[r]);
                    Assert.Equal(i % 3 == 0, (values[r >> 3] & (1 << (r & 7))) != 0);
                    break;
            }
        }

        if (column is 1 or 2)
        {
            Assert.Equal(values.Length, at);
        }
    }

    [Fact]
    public async Task WritesLogicalTypesAsTheirAnnotationsReadThemBack()
    {
        using TempPath path = new();
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
        ];
        Guid guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        DateTimeOffset at = new(2026, 10, 9, 12, 30, 15, TimeSpan.Zero);
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path.Value, schema))
        {
            ColumnsBuilder builder = writer.Builder();
            builder.Column<sbyte>(0).Append((sbyte)-5);
            builder.Column<ushort?>(1).Append(65_000);
            builder.Column<decimal>(2).Append(1_234_567.89m);
            builder.Column<decimal?>(3).Append(12_345_678_901_234.5678m);
            builder.Column<decimal>(4).Append(-12_345_678_901_234_567_890.12345m);
            builder.Column<DateOnly>(5).Append(new DateOnly(2026, 10, 9));
            builder.Column<DateTimeOffset>(6).Append(at);
            builder.Column<TimeOnly>(7).Append(new TimeOnly(23, 59, 58, 250));
            builder.Column<Guid?>(8).Append(guid);
            builder.Column<sbyte>(0).Append((sbyte)7);
            builder.Column<ushort?>(1).AppendNull();
            builder.Column<decimal>(2).Append(-0.01m);
            builder.Column<decimal?>(3).AppendNull();
            builder.Column<decimal>(4).Append(0m);
            builder.Column<DateOnly>(5).Append(new DateOnly(1969, 12, 31));
            builder.Column<DateTimeOffset>(6).Append(DateTimeOffset.UnixEpoch);
            builder.Column<TimeOnly>(7).Append(TimeOnly.MinValue);
            builder.Column<Guid?>(8).AppendNull();
            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        WrittenFile file = new(await System.IO.File.ReadAllBytesAsync(path.Value, Ct));
        for (int i = 0; i < schema.Count; i++)
        {
            Assert.Equal(schema[i].Type, file.Schema.Columns[i].Type);
        }

        Assert.Equal(12, file.Schema.Columns[4].TypeLength);
        Assert.Equal([-5, 7], Ints(0));
        Assert.Equal([65_000], Ints(1));
        Assert.Equal([123_456_789, -1], Ints(2));
        Assert.Equal(123_456_789_012_345_678L, BinaryPrimitives.ReadInt64LittleEndian(Page(3)));
        byte[] big = Page(4);
        Assert.Equal(24, big.Length);
        Assert.Equal(System.Numerics.BigInteger.Parse("-1234567890123456789012345"), new System.Numerics.BigInteger(big.AsSpan(0, 12), isUnsigned: false, isBigEndian: true));
        Assert.True(big.AsSpan(12).SequenceEqual(new byte[12]));
        Assert.Equal([new DateOnly(2026, 10, 9).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber, -1], Ints(5));
        Assert.Equal((at - DateTimeOffset.UnixEpoch).Ticks / 10, BinaryPrimitives.ReadInt64LittleEndian(Page(6)));
        Assert.Equal([86_398_250, 0], Ints(7));
        Assert.True(Page(8).AsSpan().SequenceEqual(guid.ToByteArray(bigEndian: true)));

        byte[] Page(int column)
        {
            PageLocation page = Assert.Single(file.Pages(0, column));
            return file.Values(page, file.Header(page));
        }

        int[] Ints(int column)
        {
            byte[] values = Page(column);
            int[] ints = new int[values.Length / 4];
            for (int i = 0; i < ints.Length; i++)
            {
                ints[i] = BinaryPrimitives.ReadInt32LittleEndian(values.AsSpan(i * 4));
            }

            return ints;
        }
    }

    [Fact]
    public async Task ClosesRowGroupsOnWholeBlocksAndAFlushKeepsThePartialOne()
    {
        using TempPath path = new();
        ParquetWriteOptions options = new() { BlockRows = 1_024, RowGroupRows = 4_096, Compression = ParquetCompression.Snappy };
        VortexSchema schema = [("n", VortexType.Int32)];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path.Value, schema, options))
        {
            ColumnsBuilder builder = writer.Builder();
            int next = 0;
            Append(3_000);
            await writer.WriteAsync(builder, Ct);

            // 3 000 rows hold two whole blocks: the flush closes them, and 952 rows wait.
            await writer.FlushAsync(Ct);
            Assert.Equal(1, writer.RowGroupCount);

            // 952 + 5 000 rows: a row group of 4 096 closes on its own, and 1 856 rows wait.
            Append(5_000);
            await writer.WriteAsync(builder, Ct);
            Assert.Equal(2, writer.RowGroupCount);
            ParquetWriteReport report = await writer.CompleteAsync(Ct);
            Assert.Equal(3, report.RowGroups);
            Assert.Equal(8_000, report.RowCount);

            void Append(int rows)
            {
                ColumnBuilder<int> n = builder.Column<int>(0);
                for (int i = 0; i < rows; i++)
                {
                    n.Append(next++);
                }
            }
        }

        WrittenFile file = new(await System.IO.File.ReadAllBytesAsync(path.Value, Ct));
        Assert.Equal([2_048L, 4_096L, 1_856L], Array.ConvertAll(file.Footer.RowGroups, group => group.RowCount));
        int expected = 0;
        for (int group = 0; group < 3; group++)
        {
            PageLocation[] pages = file.Pages(group, 0);
            foreach (PageLocation page in pages)
            {
                Assert.Equal(0, page.FirstRow % 1_024);
                PageHeader header = file.Header(page);
                ReadOnlySpan<byte> values = file.Values(page, header);
                for (int r = 0; r < header.RowCount; r++)
                {
                    Assert.Equal(expected++, BinaryPrimitives.ReadInt32LittleEndian(values[(r * 4)..]));
                }
            }

            Assert.Equal(CompressionCodec.Snappy, file.Footer.Chunk(group, 0).Codec);
        }

        Assert.Equal(8_000, expected);
    }

    [Fact]
    public async Task StoresAPageAsItIsWhenCompressionSavesTooLittle()
    {
        using TempPath path = new();
        Random random = new(17);
        VortexSchema schema = [("noise", VortexType.Int64), ("same", VortexType.Int64)];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path.Value, schema))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int i = 0; i < 8_192; i++)
            {
                builder.Column<long>(0).Append(random.NextInt64());
                builder.Column<long>(1).Append(42);
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        WrittenFile file = new(await System.IO.File.ReadAllBytesAsync(path.Value, Ct));
        PageHeader noise = file.Header(file.Pages(0, 0)[0]);
        PageHeader same = file.Header(file.Pages(0, 1)[0]);
        Assert.False(noise.IsCompressed);
        Assert.Equal(noise.UncompressedPageSize, noise.CompressedPageSize);
        Assert.True(same.IsCompressed);
        Assert.True(same.CompressedPageSize < same.UncompressedPageSize / 8);
    }

    [Fact]
    public async Task WritesRecordsAndAnEmptyFile()
    {
        using TempPath records = new();
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Reading>(records.Value))
        {
            Reading[] rows = [new(1, 100, 1.5), new(2, 200, null), new(3, 300, -2.25)];
            await writer.WriteAsync<Reading>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        WrittenFile file = new(await System.IO.File.ReadAllBytesAsync(records.Value, Ct));
        Assert.Equal(3, file.Footer.RowCount);
        Assert.Equal(["Sensor", "Time", "Value"], Array.ConvertAll(file.Schema.Columns, column => column.Path[0]));
        Assert.Equal(1, file.Footer.Chunk(0, 2).Statistics.NullCount);

        using TempPath empty = new();
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Reading>(empty.Value))
        {
            ParquetWriteReport report = await writer.CompleteAsync(Ct);
            Assert.Equal(0, report.RowGroups);
        }

        WrittenFile none = new(await System.IO.File.ReadAllBytesAsync(empty.Value, Ct));
        Assert.Equal(0, none.Footer.RowCount);
        Assert.Empty(none.Footer.RowGroups);
        Assert.Equal(3, none.Schema.Columns.Length);
    }

    [Fact]
    public async Task AFileGivenUpLeavesThePathAsItWas()
    {
        using TempPath path = new();
        await System.IO.File.WriteAllTextAsync(path.Value, "before", Ct);
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Reading>(path.Value))
        {
            await writer.WriteAsync<Reading>([new(1, 2, 3)], Ct);
        }

        Assert.Equal("before", await System.IO.File.ReadAllTextAsync(path.Value, Ct));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path.Value)!, $".{Path.GetFileName(path.Value)}.*.tmp"));
    }

    [Fact]
    public async Task WritesToACallersPipeAndCompletesIt()
    {
        Pipe pipe = new();
        Task<byte[]> drained = DrainAsync(pipe.Reader);
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(pipe.Writer, Reading.Schema))
        {
            await writer.WriteAsync<Reading>([new(1, 2, 3), new(4, 5, null)], Ct);
            await writer.CompleteAsync(Ct);
        }

        WrittenFile file = new(await drained);
        Assert.Equal(2, file.Footer.RowCount);

        static async Task<byte[]> DrainAsync(PipeReader reader)
        {
            using MemoryStream bytes = new();
            await reader.CopyToAsync(bytes);
            await reader.CompleteAsync();
            return bytes.ToArray();
        }
    }

    [Fact]
    public async Task RefusesABuilderItDidNotHandOutAndAWriteAfterCompletion()
    {
        using TempPath first = new();
        using TempPath second = new();
        await using ParquetFileWriter one = VortexSession.Default.CreateParquetWriter<Reading>(first.Value);
        await using ParquetFileWriter two = VortexSession.Default.CreateParquetWriter<Reading>(second.Value);
        await Assert.ThrowsAsync<ArgumentException>(async () => await one.WriteAsync(two.Builder(), Ct));
        await one.CompleteAsync(Ct);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await one.WriteAsync<Reading>([new(1, 2, 3)], Ct));
    }

    [Fact]
    public void RefusesOptionsOutOfRange()
    {
        using TempPath path = new();
        Assert.Throws<ArgumentException>(() => VortexSession.Default.CreateParquetWriter<Reading>(path.Value, new ParquetWriteOptions { BlockRows = 1_000, RowGroupRows = 2_500 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexSession.Default.CreateParquetWriter<Reading>(path.Value, new ParquetWriteOptions { Compression = ParquetCompression.Snappy, CompressionLevel = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexSession.Default.CreateParquetWriter<Reading>(path.Value, new ParquetWriteOptions { Compression = ParquetCompression.Brotli, CompressionLevel = 12 }));
        Assert.False(System.IO.File.Exists(path.Value));
    }

    /// <summary>A file this package wrote, read back through its own metadata readers.</summary>
    private sealed class WrittenFile
    {
        private readonly ZstdDecompressor _zstd = new();

        internal WrittenFile(byte[] bytes)
        {
            Bytes = bytes;
            Assert.True(bytes.AsSpan(0, 4).SequenceEqual("PAR1"u8));
            Assert.True(bytes.AsSpan(bytes.Length - 4).SequenceEqual("PAR1"u8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
            Footer = ParquetFooter.Read(bytes.AsMemory(bytes.Length - 8 - length, length));
            Schema = ParquetSchema.Compile(Footer.Schema, Footer.ColumnOrders);
        }

        internal byte[] Bytes { get; }

        internal ParquetFooter Footer { get; }

        internal ParquetSchema Schema { get; }

        internal string CreatedBy => Encoding.UTF8.GetString(Range(Footer.CreatedBy, Footer.Bytes));

        internal ReadOnlySpan<byte> Range(ByteRange range) => Range(range, Footer.Bytes);

        internal PageLocation[] Pages(int rowGroup, int column)
        {
            ColumnChunkMetadata chunk = Footer.Chunk(rowGroup, column);
            Assert.True(chunk.OffsetIndexOffset > 0);
            return OffsetIndex.Read(Bytes.AsSpan(checked((int)chunk.OffsetIndexOffset), chunk.OffsetIndexLength), Footer.RowGroups[rowGroup].RowCount);
        }

        internal PageHeader Header(PageLocation page) => PageHeader.Read(Bytes.AsSpan(checked((int)page.Offset), page.Size));

        /// <summary>Per row of a v2 page, whether it holds a value: its definition levels, or every row when it has none.</summary>
        internal bool[] Validity(PageLocation page, PageHeader header, int rows)
        {
            bool[] valid = new bool[rows];
            if (header.DefinitionLevelsLength == 0)
            {
                Array.Fill(valid, true);
                return valid;
            }

            ReadOnlySpan<byte> levels = Bytes.AsSpan(checked((int)page.Offset) + header.HeaderLength + header.RepetitionLevelsLength, header.DefinitionLevelsLength);
            byte[] decoded = new byte[rows];
            new RleHybridDecoder(1).Read(levels, decoded);
            for (int r = 0; r < rows; r++)
            {
                valid[r] = decoded[r] == 1;
            }

            return valid;
        }

        /// <summary>A v2 page's values, decompressed when the page says they are compressed.</summary>
        internal byte[] Values(PageLocation page, PageHeader header)
        {
            int levels = header.RepetitionLevelsLength + header.DefinitionLevelsLength;
            ReadOnlySpan<byte> stored = Bytes.AsSpan(checked((int)page.Offset) + header.HeaderLength + levels, header.CompressedPageSize - levels);
            byte[] values = new byte[header.UncompressedPageSize - levels];
            CompressionCodec codec = header.IsCompressed ? CodecOf(page) : CompressionCodec.Uncompressed;
            PageCodecs.Decompress(codec, stored, values, _zstd);
            return values;
        }

        private CompressionCodec CodecOf(PageLocation page)
        {
            for (int group = 0; group < Footer.RowGroups.Length; group++)
            {
                for (int column = 0; column < Schema.Columns.Length; column++)
                {
                    ColumnChunkMetadata chunk = Footer.Chunk(group, column);
                    if (page.Offset >= chunk.DataPageOffset && page.Offset < chunk.DataPageOffset + chunk.TotalCompressedSize)
                    {
                        return chunk.Codec;
                    }
                }
            }

            throw new InvalidOperationException("The page is in no column chunk.");
        }

        private static ReadOnlySpan<byte> Range(ByteRange range, ReadOnlySpan<byte> bytes) => bytes.Slice(range.Start, range.Length);
    }

    /// <summary>A path in the temporary directory, deleted with what was written there.</summary>
    private sealed class TempPath : IDisposable
    {
        public string Value { get; } = Path.Combine(Path.GetTempPath(), $"vorticity-parquet-{Guid.NewGuid():N}.parquet");

        public void Dispose() => System.IO.File.Delete(Value);
    }
}
