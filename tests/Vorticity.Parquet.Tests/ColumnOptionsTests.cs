using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// What a column may ask of the writer for itself, by name: the encoding of its pages, pinned whatever
/// the writer would price, and a codec of its own.
/// </summary>
public sealed class ColumnOptionsTests : IDisposable
{
    private const int Rows = 20_000;

    private static readonly VortexSchema Schema =
    [
        ("id", VortexType.Int64),
        ("small", VortexType.Int32.Nullable),
        ("measure", VortexType.Float64),
        ("url", VortexType.Utf8),
        ("label", VortexType.Utf8.Nullable),
        ("flag", VortexType.Bool),
        ("unique", VortexType.Int64),
        ("money", VortexType.Decimal(28, 2)),
        ("serial", VortexType.Uuid),
    ];

    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Every column pinned against what the writer would choose for it: a pin is written on every
    /// page under every profile, and the rows read back as the PLAIN file's.
    /// </summary>
    [Theory]
    [InlineData(CompressionProfile.Auto)]
    [InlineData(CompressionProfile.None)]
    [InlineData(CompressionProfile.Fastest)]
    public async Task WritesEachColumnInTheEncodingItIsPinnedTo(CompressionProfile profile)
    {
        Dictionary<string, ParquetEncodingHint> hints = new()
        {
            ["id"] = ParquetEncodingHint.Plain,
            ["small"] = ParquetEncodingHint.ByteStreamSplit,
            ["measure"] = ParquetEncodingHint.Plain,
            ["url"] = ParquetEncodingHint.DeltaLengthByteArray,
            ["label"] = ParquetEncodingHint.DeltaByteArray,
            ["flag"] = ParquetEncodingHint.Plain,
            ["unique"] = ParquetEncodingHint.Dictionary,
            ["money"] = ParquetEncodingHint.ByteStreamSplit,
            ["serial"] = ParquetEncodingHint.DeltaByteArray,
        };
        string pinned = await WriteAsync(new ParquetWriteOptions { RowGroupRows = 16_384, Profile = profile, Hints = hints });
        string plain = await WriteAsync(new ParquetWriteOptions { RowGroupRows = 16_384, Profile = CompressionProfile.None });

        await using (ParquetFile file = await ParquetFile.OpenAsync(pinned, Ct))
        {
            foreach (ParquetRowGroupInfo group in file.Metadata.RowGroups)
            {
                Assert.Equal(["PLAIN"], DataEncodings(group, "id"));
                Assert.Equal(["BYTE_STREAM_SPLIT"], DataEncodings(group, "small"));
                Assert.Equal(["PLAIN"], DataEncodings(group, "measure"));
                Assert.Equal(["DELTA_LENGTH_BYTE_ARRAY"], DataEncodings(group, "url"));
                Assert.Equal(["DELTA_BYTE_ARRAY"], DataEncodings(group, "label"));
                Assert.Equal(["PLAIN"], DataEncodings(group, "flag"));
                Assert.Equal(["BYTE_STREAM_SPLIT"], DataEncodings(group, "money"));
                Assert.Equal(["DELTA_BYTE_ARRAY"], DataEncodings(group, "serial"));

                // Unique values, which the writer would never code, coded on every page.
                ParquetChunkInfo unique = group.Chunks.Single(chunk => chunk.Column == "unique");
                Assert.Contains("RLE_DICTIONARY", unique.Encodings);
                Assert.True(unique.AllDataPagesDictionary);
            }

            Assert.Empty(await file.VerifyAsync(Ct));
        }

        Assert.Equal(await RowsAsync(plain), await RowsAsync(pinned));
    }

    [Fact]
    public async Task RefusesAPinItsColumnDoesNotTake()
    {
        await Refused<ArgumentException>(new() { ["url"] = ParquetEncodingHint.DeltaBinaryPacked });
        await Refused<ArgumentException>(new() { ["id"] = ParquetEncodingHint.Rle });
        await Refused<ArgumentException>(new() { ["id"] = ParquetEncodingHint.DeltaLengthByteArray });
        await Refused<ArgumentException>(new() { ["flag"] = ParquetEncodingHint.Dictionary });
        await Refused<ArgumentException>(new() { ["flag"] = ParquetEncodingHint.ByteStreamSplit });
        await Refused<ArgumentException>(new() { ["serial"] = ParquetEncodingHint.Dictionary });
        await Refused<ArgumentException>(new() { ["nope"] = ParquetEncodingHint.Plain });
        await Refused<ArgumentOutOfRangeException>(new() { ["id"] = (ParquetEncodingHint)99 });

        async Task Refused<TException>(Dictionary<string, ParquetEncodingHint> hints)
            where TException : Exception
        {
            string path = NewPath();
            await Assert.ThrowsAnyAsync<TException>(async () =>
            {
                await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, Schema, new ParquetWriteOptions { Hints = hints });
            });
            Assert.False(System.IO.File.Exists(path));
        }
    }

    /// <summary>
    /// Columns that name their own codec and level take them, the others the file's; the bytes do
    /// not depend on the degree, and every column reads back.
    /// </summary>
    [Fact]
    public async Task CompressesEachColumnWithTheCodecItNames()
    {
        Dictionary<string, ParquetCodec> codecs = new()
        {
            ["url"] = new ParquetCodec(ParquetCompression.Brotli, 11),
            ["measure"] = new ParquetCodec(ParquetCompression.Uncompressed),
            ["id"] = new ParquetCodec(ParquetCompression.Snappy),
            ["label"] = new ParquetCodec(ParquetCompression.Zstd, 19),
            ["small"] = new ParquetCodec(ParquetCompression.Gzip),
        };
        ParquetWriteOptions options = new() { RowGroupRows = 16_384, Compression = ParquetCompression.Zstd, ColumnCompression = codecs };
        string one = await WriteAsync(options with { DegreeOfParallelism = 1 });
        string four = await WriteAsync(options with { DegreeOfParallelism = 4 });
        string fileLevel = await WriteAsync(new ParquetWriteOptions { RowGroupRows = 16_384, Compression = ParquetCompression.Zstd });
        Assert.Equal(await System.IO.File.ReadAllBytesAsync(one, Ct), await System.IO.File.ReadAllBytesAsync(four, Ct));

        await using ParquetFile file = await ParquetFile.OpenAsync(one, Ct);
        await using ParquetFile reference = await ParquetFile.OpenAsync(fileLevel, Ct);
        for (int g = 0; g < file.Metadata.RowGroups.Count; g++)
        {
            ParquetRowGroupInfo group = file.Metadata.RowGroups[g];
            Assert.Equal("BROTLI", Chunk(group, "url").Codec);
            Assert.Equal("UNCOMPRESSED", Chunk(group, "measure").Codec);
            Assert.Equal("SNAPPY", Chunk(group, "id").Codec);
            Assert.Equal("ZSTD", Chunk(group, "label").Codec);
            Assert.Equal("GZIP", Chunk(group, "small").Codec);
            Assert.Equal("ZSTD", Chunk(group, "flag").Codec);

            // A stronger level of the file's codec is a smaller chunk of the same pages.
            Assert.True(Chunk(group, "label").CompressedBytes < Chunk(reference.Metadata.RowGroups[g], "label").CompressedBytes);
        }

        Assert.Empty(await file.VerifyAsync(Ct));
        Assert.Equal(await RowsAsync(fileLevel), await RowsAsync(one));
    }

    [Fact]
    public async Task RefusesACodecItCannotWrite()
    {
        await Refused<ArgumentOutOfRangeException>(new() { ["id"] = new ParquetCodec(ParquetCompression.Snappy, 3) });
        await Refused<ArgumentOutOfRangeException>(new() { ["id"] = new ParquetCodec(ParquetCompression.Gzip, 10) });
        await Refused<ArgumentOutOfRangeException>(new() { ["id"] = new ParquetCodec((ParquetCompression)3) });
        await Refused<ArgumentException>(new() { ["nope"] = new ParquetCodec(ParquetCompression.Zstd) });

        async Task Refused<TException>(Dictionary<string, ParquetCodec> codecs)
            where TException : Exception
        {
            string path = NewPath();
            await Assert.ThrowsAnyAsync<TException>(async () =>
            {
                await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, Schema, new ParquetWriteOptions { ColumnCompression = codecs });
            });
            Assert.False(System.IO.File.Exists(path));
        }
    }

    /// <summary>A chunk's data page encodings: its listed ones but its levels' RLE and its dictionary page's PLAIN.</summary>
    private static string[] DataEncodings(ParquetRowGroupInfo group, string column)
    {
        ParquetChunkInfo chunk = Chunk(group, column);
        return [.. chunk.Encodings.Where(name => name != "RLE" || column == "flag")];
    }

    private static ParquetChunkInfo Chunk(ParquetRowGroupInfo group, string column) => group.Chunks.Single(chunk => chunk.Column == column);

    private async Task<string> WriteAsync(ParquetWriteOptions options)
    {
        string path = NewPath();
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, Schema, options);
        ColumnsBuilder builder = writer.Builder();
        Random random = new(17);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<long>(0).Append(1_000_000L + i);
            if (i % 11 == 0)
            {
                builder.Column<int?>(1).AppendNull();
            }
            else
            {
                builder.Column<int?>(1).Append(i % 300);
            }

            builder.Column<double>(2).Append(20.0 + (5.0 * Math.Sin(i / 400.0)));
            builder.Column<string>(3).Append($"https://example.org/catalogue/section-{i / 1_000:D3}/item-{i:D6}");
            if (i % 7 == 0)
            {
                builder.Column<string?>(4).AppendNull();
            }
            else
            {
                builder.Column<string?>(4).Append($"label-{i % 10}");
            }

            builder.Column<bool>(5).Append(i % 1_000 < 900);
            builder.Column<long>(6).Append(random.NextInt64());
            builder.Column<decimal>(7).Append((i * 3.25m) - 1_000m);
            builder.Column<Guid>(8).Append(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)(i >> 8), (byte)i));
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static async Task<string[][]> RowsAsync(string path)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        List<string[]> rows = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add([.. Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))]);
                }
            }
        }

        return [.. rows];
    }

    /// <summary>A path of its own for each file, since a file the session has mapped stays mapped while it is cached.</summary>
    private string NewPath()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-columns-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        return path;
    }
}
