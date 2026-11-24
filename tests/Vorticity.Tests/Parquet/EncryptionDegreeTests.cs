using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Parquet;
using Xunit;

namespace Vorticity.Tests.Parquet;

/// <summary>
/// A writer of many lanes closes its columns' pages side by side, each encrypting its own: columns
/// under the footer's key encrypt at once, in either mode, and every page reads back as written.
/// </summary>
public sealed class EncryptionDegreeTests : IDisposable
{
    private const int Columns = 16;

    private const int Rows = 120_000;

    private static readonly byte[] FooterKey = Encoding.ASCII.GetBytes("footer-key-16byt");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vx-encrypt-degree-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [InlineData(ParquetEncryptionAlgorithm.AesGcm)]
    [InlineData(ParquetEncryptionAlgorithm.AesGcmCtr)]
    public async Task ColumnsUnderTheFootersKeyEncryptSideBySide(ParquetEncryptionAlgorithm algorithm)
    {
        VortexField[] fields = new VortexField[Columns];
        for (int c = 0; c < Columns; c++)
        {
            fields[c] = new VortexField($"c{c}", VortexType.Int64);
        }

        VortexSchema schema = VortexSchema.Create(fields);

        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 8);
        ParquetWriteOptions options = new()
        {
            Compression = ParquetCompression.Uncompressed,
            DegreeOfParallelism = 8,
            BlockRows = 2_048,
            Encryption = new ParquetEncryption { FooterKey = FooterKey, Algorithm = algorithm },
        };
        await using (ParquetFileWriter writer = session.CreateParquetWriter(_path, schema, options))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int row = 0; row < Rows; row++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    builder.Column<long>(c).Append(Value(row, c));
                }
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        ParquetFile file = await session.OpenParquetAsync(_path, new ParquetOpenOptions { Decryption = new ParquetDecryption { FooterKey = FooterKey } }, Ct);
        byte[] held = file.Footer.Decryptor!.FooterKey!;
        Assert.Equal(FooterKey, held);
        Assert.NotSame(FooterKey, held);
        long seen = 0;
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int c = 0; c < Columns; c++)
                {
                    PrimitiveColumn<long> values = batch.Column(c).AsPrimitive<long>();
                    for (int r = 0; r < batch.RowCount; r++)
                    {
                        Assert.Equal(Value(seen + r, c), values[r]);
                    }
                }

                seen += batch.RowCount;
            }
        }

        Assert.Equal(Rows, seen);

        // The file's copy of the key is wiped as it closes; the caller's is left alone.
        await file.DisposeAsync();
        Assert.All(held, b => Assert.Equal(0, b));
        Assert.Equal("footer-key-16byt"u8.ToArray(), FooterKey);
    }

    private static long Value(long row, int column) => (row * 2_654_435_761L) ^ ((long)column << 40);
}
