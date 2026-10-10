using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The standard's modular encryption, read from the suite's encrypted files with the keys its README
/// gives: encrypted footers and plaintext ones, a column's own key and the footer's, AES_GCM_V1 and
/// AES_GCM_CTR_V1, keys of 128 and 256 bits, an AAD prefix stored and one the reader supplies; and
/// refused, with the wrong key, without one, or under another prefix.
/// </summary>
/// <remarks>
/// The files hold the rows of the C++ writer's test generator, which each row is held to: row i's
/// boolean is whether i is even, its int32 i, its list of int64 i·2·10¹² and i·2·10¹² + 10¹², its
/// INT96 the three counts i, i + 1 and i + 2, its float i·1.1 and its double i·1.1111111, its bytes
/// "parquet" and i in three digits where i is even and null where odd, its fixed bytes ten of i.
/// </remarks>
public sealed class EncryptionTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The README writes the keys as digits: they are those digits' ASCII, as the suite's writers took them.</summary>
    private static readonly Dictionary<string, byte[]> Keys128 = new()
    {
        ["kf"] = Encoding.ASCII.GetBytes("0123456789012345"),
        ["kc1"] = Encoding.ASCII.GetBytes("1234567890123450"),
        ["kc2"] = Encoding.ASCII.GetBytes("1234567890123451"),
    };

    private static readonly Dictionary<string, byte[]> Keys256 = new()
    {
        ["kf"] = Encoding.ASCII.GetBytes("01234567890123456789012345678901"),
        ["kc1"] = Encoding.ASCII.GetBytes("12345678901234567890123456789012"),
        ["kc2"] = Encoding.ASCII.GetBytes("12345678901234567890123456789013"),
        ["kc3"] = Encoding.ASCII.GetBytes("12345678901234567890123456789014"),
        ["kc4"] = Encoding.ASCII.GetBytes("12345678901234567890123456789015"),
        ["kc5"] = Encoding.ASCII.GetBytes("12345678901234567890123456789016"),
        ["kc6"] = Encoding.ASCII.GetBytes("12345678901234567890123456789017"),
        ["kc7"] = Encoding.ASCII.GetBytes("12345678901234567890123456789018"),
        ["kc8"] = Encoding.ASCII.GetBytes("12345678901234567890123456789019"),
    };

    [Theory]
    [InlineData("uniform_encryption.parquet.encrypted", "", false)]
    [InlineData("encrypt_columns_and_footer.parquet.encrypted", "", false)]
    [InlineData("encrypt_columns_and_footer_ctr.parquet.encrypted", "", false)]
    [InlineData("encrypt_columns_and_footer_aad.parquet.encrypted", "tester", false)]
    [InlineData("encrypt_columns_and_footer_disable_aad_storage.parquet.encrypted", "tester", false)]
    [InlineData("encrypt_columns_plaintext_footer.parquet.encrypted", "", false)]
    [InlineData("uniform_encryption.parquet.encrypted", "", true)]
    [InlineData("encrypt_columns_and_footer.parquet.encrypted", "", true)]
    [InlineData("encrypt_columns_and_footer_ctr.parquet.encrypted", "", true)]
    [InlineData("encrypt_columns_and_footer_disable_aad_storage.parquet.encrypted", "tester", true)]
    [InlineData("encrypt_columns_plaintext_footer.parquet.encrypted", "", true)]
    public async Task ReadsTheRowsTheSuitesWriterEncrypted(string name, string prefix, bool aes256)
    {
        string? path = Find(name, aes256);
        Assert.SkipWhen(path is null, $"VORTICITY_PARQUET_DATA holds no {(aes256 ? "aes256/" : "")}{name}.");
        List<string> rows = await RowsAsync(path, Decryption(aes256 ? Keys256 : Keys128, prefix));
        Assert.Equal(50, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Expected(i), rows[i]);
        }
    }

    [Fact]
    public async Task ReadsTheSuitesEncryptedBloomFilterFile()
    {
        string? path = Find("encrypt_columns_and_footer_bloom_filter.parquet.encrypted", aes256: false);
        Assert.SkipWhen(path is null, "VORTICITY_PARQUET_DATA holds no encrypt_columns_and_footer_bloom_filter.parquet.encrypted.");
        List<string> rows = await RowsAsync(path, Decryption(Keys128, ""));
        Assert.Equal(2000, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"{i + 0.5} | {i + 0.25f} | {i} | \"name_{i}\""), rows[i]);
        }
    }

    /// <summary>A key not the file's fails its modules' authentication; no key, or no prefix where the file stores none, is refused as what the read lacks.</summary>
    [Fact]
    public async Task RefusesAWrongKeyAMissingOneAndAnotherPrefix()
    {
        string? footer = Find("encrypt_columns_and_footer.parquet.encrypted", aes256: false);
        string? hidden = Find("encrypt_columns_and_footer_disable_aad_storage.parquet.encrypted", aes256: false);
        string? stored = Find("encrypt_columns_and_footer_aad.parquet.encrypted", aes256: false);
        Assert.SkipWhen(footer is null || hidden is null || stored is null, "VORTICITY_PARQUET_DATA holds no encrypted files.");

        Dictionary<string, byte[]> wrong = new(Keys128) { ["kf"] = Encoding.ASCII.GetBytes("0123456789012346") };
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(footer, Decryption(wrong, "")));
        await Assert.ThrowsAsync<ParquetUnsupportedException>(() => RowsAsync(footer, null));

        Dictionary<string, byte[]> noColumn = new(Keys128);
        noColumn.Remove("kc1");
        ParquetUnsupportedException missing = await Assert.ThrowsAsync<ParquetUnsupportedException>(() => RowsAsync(footer, Decryption(noColumn, "")));
        Assert.Contains("double_field", missing.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ParquetUnsupportedException>(() => RowsAsync(hidden, Decryption(Keys128, "")));
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(hidden, Decryption(Keys128, "another")));
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(stored, Decryption(Keys128, "another")));
    }

    /// <summary>
    /// A plaintext footer opens without a key, as a reader older than encryption does: its plaintext
    /// columns read, and an encrypted one is refused until its key is given.
    /// </summary>
    [Fact]
    public async Task ReadsAPlaintextFootersPlaintextColumnsWithoutAKey()
    {
        string? path = Find("encrypt_columns_plaintext_footer.parquet.encrypted", aes256: false);
        Assert.SkipWhen(path is null, "VORTICITY_PARQUET_DATA holds no encrypt_columns_plaintext_footer.parquet.encrypted.");
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan("int32_field", "ba_field").ToBatchesAsync(Ct))
        {
            using (batch)
            {
                rows += batch.RowCount;
                Assert.Equal("0", Render.Row(batch, 0, 0));
            }
        }

        Assert.Equal(50, rows);
        await Assert.ThrowsAsync<ParquetUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan("double_field").ToBatchesAsync(Ct))
            {
                batch.Dispose();
            }
        });
    }

    private static string? Find(string name, bool aes256) =>
        Root is null || !Directory.Exists(Root) ? null
            : Directory.EnumerateFiles(Root, name, SearchOption.AllDirectories).FirstOrDefault(p => p.Contains("aes256", StringComparison.Ordinal) == aes256);

    private static ParquetDecryption Decryption(Dictionary<string, byte[]> keys, string prefix) => new()
    {
        KeyResolver = request => keys.TryGetValue(Encoding.UTF8.GetString(request.KeyMetadata.Span), out byte[]? key) ? key : ReadOnlyMemory<byte>.Empty,
        AadPrefix = Encoding.UTF8.GetBytes(prefix),
    };

    /// <summary>Row <paramref name="i"/> of the C++ writer's test generator, as <see cref="Render"/> writes it.</summary>
    private static string Expected(int i)
    {
        long first = i * 2L * 1_000_000_000_000;
        string bytes = i % 2 == 0 ? "0x" + Convert.ToHexString(Encoding.ASCII.GetBytes($"parquet{i:D3}")) : "null";
        string fixedBytes = "[" + string.Join(", ", Enumerable.Repeat(i, 10)) + "]";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(i % 2 == 0 ? "true" : "false")} | {i} | [{first}, {first + 1_000_000_000_000}] | [{i}, 0, 0, 0, {i + 1}, 0, 0, 0, {i + 2}, 0, 0, 0] | {i * 1.1f} | {i * 1.1111111} | {bytes} | {fixedBytes}");
    }

    private static async Task<List<string>> RowsAsync(string path, ParquetDecryption? decryption)
    {
        List<string> rows = [];
        ParquetOpenOptions options = new() { Decryption = decryption };
        await using ParquetFile file = await VortexSession.Default.OpenParquetAsync(path, options, Ct);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int row = 0; row < batch.RowCount; row++)
                {
                    rows.Add(string.Join(" | ", Enumerable.Range(0, file.Schema.Count).Select(c => Render.Row(batch, c, row))));
                }
            }
        }

        return rows;
    }
}
