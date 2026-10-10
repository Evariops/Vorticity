using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Files this writer encrypts, read back: every column under the footer's key, columns under keys
/// of their own in counter mode, a plaintext footer signed, an AAD prefix the file does not store,
/// pages v1 compressed with their dictionary; each the same rows as the file in plaintext, and none
/// read without its keys, under another prefix, or with a byte altered.
/// </summary>
public sealed class EncryptionWriteTests : IDisposable
{
    private const int Rows = 30_000;

    private static readonly byte[] FooterKey = Encoding.ASCII.GetBytes("footer-key-16byt");

    private static readonly byte[] ScoreKey = Encoding.ASCII.GetBytes("score-key-24-bytes-long!");

    private static readonly byte[] LabelKey = Encoding.ASCII.GetBytes("label-key-of-thirty-two-bytes!!!");

    private static readonly VortexSchema Schema =
    [
        ("id", VortexType.Int64),
        ("count", VortexType.Int32.Nullable),
        ("score", VortexType.Float64.Nullable),
        ("label", VortexType.Utf8.Nullable),
        ("flag", VortexType.Bool.Nullable),
        ("blob", VortexType.Binary),
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

    public static TheoryData<string> Modes => ["uniform", "columns-ctr", "plaintext-footer", "hidden-prefix", "v1-zstd"];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ReadsBackWhatItEncrypts(string mode)
    {
        (ParquetWriteOptions options, ParquetDecryption decryption) = Configuration(mode);
        string plain = await WriteAsync(new ParquetWriteOptions { DataPageVersion = options.DataPageVersion, Compression = options.Compression });
        string encrypted = await WriteAsync(options);

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(encrypted, Ct);
        string magic = options.Encryption!.PlaintextFooter ? "PAR1" : "PARE";
        Assert.Equal(magic, Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(magic, Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("label-7"u8));

        string[][] expected = await RowsAsync(plain, null);
        Assert.Equal(expected, await RowsAsync(encrypted, decryption));
    }

    [Fact]
    public async Task RefusesWhatItEncryptsWithoutItsKeysOrWithAByteAltered()
    {
        (ParquetWriteOptions options, ParquetDecryption decryption) = Configuration("columns-ctr");
        string encrypted = await WriteAsync(options);
        await Assert.ThrowsAsync<ParquetUnsupportedException>(() => RowsAsync(encrypted, null));
        ParquetUnsupportedException missing = await Assert.ThrowsAsync<ParquetUnsupportedException>(
            () => RowsAsync(encrypted, decryption with { ColumnKeys = new Dictionary<string, ReadOnlyMemory<byte>> { ["score"] = ScoreKey } }));
        Assert.Contains("label", missing.Message, StringComparison.Ordinal);

        // A byte of a page altered: the page's module fails its authentication.
        (ParquetWriteOptions uniform, ParquetDecryption keys) = Configuration("uniform");
        string target = await WriteAsync(uniform);
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(target, Ct);
        bytes[100] ^= 0x01;
        await System.IO.File.WriteAllBytesAsync(target, bytes, Ct);
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(target, keys));

        // Another prefix: the footer fails its authentication.
        (ParquetWriteOptions hidden, ParquetDecryption prefixed) = Configuration("hidden-prefix");
        string other = await WriteAsync(hidden);
        await Assert.ThrowsAsync<ParquetUnsupportedException>(() => RowsAsync(other, prefixed with { AadPrefix = default }));
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(other, prefixed with { AadPrefix = Encoding.UTF8.GetBytes("table-2") }));
    }

    /// <summary>A plaintext footer opens without a key: its plaintext columns read, and its encrypted ones are refused.</summary>
    [Fact]
    public async Task APlaintextFootersPlaintextColumnsReadWithoutAKey()
    {
        (ParquetWriteOptions options, _) = Configuration("plaintext-footer");
        string encrypted = await WriteAsync(options);
        await using ParquetFile file = await ParquetFile.OpenAsync(encrypted, Ct);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan("count", "flag").ToBatchesAsync(Ct))
        {
            using (batch)
            {
                rows += batch.RowCount;
            }
        }

        Assert.Equal(Rows, rows);
        await Assert.ThrowsAsync<ParquetUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan("score").ToBatchesAsync(Ct))
            {
                batch.Dispose();
            }
        });
    }

    /// <summary>
    /// An encrypted file prunes as its plaintext twin does: its row groups by the statistics of its
    /// decrypted metadata, its batches by its decrypted page index, its row groups by its decrypted
    /// Bloom filters; every chunk it reads, read whole.
    /// </summary>
    [Theory]
    [InlineData("uniform")]
    [InlineData("columns-ctr")]
    [InlineData("plaintext-footer")]
    public async Task PrunesAsItsPlaintextTwinDoes(string mode)
    {
        (ParquetWriteOptions options, ParquetDecryption decryption) = Configuration(mode);
        Dictionary<string, double> blooms = new() { ["label"] = 0.01, ["count"] = 0.01 };
        string plain = await WriteAsync(new ParquetWriteOptions { BloomFilters = blooms });
        string encrypted = await WriteAsync(options with { BloomFilters = blooms });
        VortexExpr[] filters =
        [
            Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(1_025_000L))),
            Expr.Eq(Expr.Field("label"), Expr.Literal(FilterLiteral.From(Encoding.UTF8.GetBytes("nope")))),
            Expr.Eq(Expr.Field("count"), Expr.Literal(FilterLiteral.From(1_000L))),
        ];
        foreach (VortexExpr filter in filters)
        {
            (long plainRows, long plainPruned) = await CountAsync(plain, null, filter);
            (long encryptedRows, long encryptedPruned) = await CountAsync(encrypted, decryption, filter);
            Assert.Equal(plainRows, encryptedRows);
            Assert.Equal(plainPruned, encryptedPruned);
            Assert.True(plainPruned > 0, $"{filter} pruned nothing");
        }
    }

    private static async Task<(long Rows, long Pruned)> CountAsync(string path, ParquetDecryption? decryption, VortexExpr filter)
    {
        await using ParquetFile file = await VortexSession.Default.OpenParquetAsync(path, new ParquetOpenOptions { Decryption = decryption }, Ct);
        Scan scan = file.Scan().Where(filter);
        long rows = await scan.CountAsync(Ct);
        return (rows, scan.Metrics.BlocksPruned);
    }

    private static (ParquetWriteOptions Options, ParquetDecryption Decryption) Configuration(string mode)
    {
        Dictionary<string, ReadOnlyMemory<byte>> keys = new() { ["score"] = ScoreKey, ["label"] = LabelKey };
        ParquetDecryption decryption = new() { FooterKey = FooterKey, ColumnKeys = keys };
        Dictionary<string, ParquetColumnKey> columns = new()
        {
            ["score"] = new ParquetColumnKey(ScoreKey, Encoding.UTF8.GetBytes("score")),
            ["label"] = new ParquetColumnKey(LabelKey, Encoding.UTF8.GetBytes("label")),
            ["id"] = new ParquetColumnKey(default, default),
        };
        return mode switch
        {
            "uniform" => (new ParquetWriteOptions { Encryption = new ParquetEncryption { FooterKey = FooterKey } }, decryption),
            "columns-ctr" => (new ParquetWriteOptions { Encryption = new ParquetEncryption { FooterKey = FooterKey, ColumnKeys = columns, Algorithm = ParquetEncryptionAlgorithm.AesGcmCtr } }, decryption),
            "plaintext-footer" => (new ParquetWriteOptions { Encryption = new ParquetEncryption { FooterKey = FooterKey, FooterKeyMetadata = Encoding.UTF8.GetBytes("footer"), ColumnKeys = columns, PlaintextFooter = true } }, decryption),
            "hidden-prefix" => (new ParquetWriteOptions { Encryption = new ParquetEncryption { FooterKey = FooterKey, AadPrefix = Encoding.UTF8.GetBytes("table-1"), StoreAadPrefix = false } }, decryption with { AadPrefix = Encoding.UTF8.GetBytes("table-1") }),
            _ => (new ParquetWriteOptions { DataPageVersion = DataPageVersion.V1, Compression = ParquetCompression.Zstd, Encryption = new ParquetEncryption { FooterKey = FooterKey, ColumnKeys = columns } }, decryption),
        };
    }

    private async Task<string> WriteAsync(ParquetWriteOptions options)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vx-encrypt-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, Schema, options with { RowGroupRows = 16_384 });
        ColumnsBuilder builder = writer.Builder();
        Random random = new(31);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<long>(0).Append(1_000_000L + i);
            if (i % 13 == 0)
            {
                builder.Column<int?>(1).AppendNull();
                builder.Column<double?>(2).AppendNull();
                builder.Column<string?>(3).AppendNull();
                builder.Column<bool?>(4).AppendNull();
            }
            else
            {
                builder.Column<int?>(1).Append(i % 300);
                builder.Column<double?>(2).Append(20.0 + (5.0 * Math.Sin(i / 400.0)));
                builder.Column<string?>(3).Append($"label-{i % 10}");
                builder.Column<bool?>(4).Append(i % 1_000 < 900);
            }

            byte[] blob = new byte[random.Next(0, 24)];
            random.NextBytes(blob);
            builder.Column<ReadOnlyMemory<byte>>(5).Append(blob);
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static async Task<string[][]> RowsAsync(string path, ParquetDecryption? decryption)
    {
        List<string[]> rows = [];
        await using ParquetFile file = await VortexSession.Default.OpenParquetAsync(path, new ParquetOpenOptions { Decryption = decryption }, Ct);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add([.. Enumerable.Range(0, file.Schema.Count).Select(c => Render.Row(batch, c, r))]);
                }
            }
        }

        return [.. rows];
    }
}
