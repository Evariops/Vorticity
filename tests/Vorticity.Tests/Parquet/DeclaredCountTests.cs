using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;
using Xunit;

namespace Vorticity.Tests.Parquet;

/// <summary>
/// A count or a length the file declares sizes what its read allocates, so a hostile one is refused
/// as a format error before anything is sized from it: never multiplied into a size that wraps past
/// 2^31 and leaves a block too small for the values written into it, never laid out on the stack.
/// </summary>
public sealed class DeclaredCountTests : IDisposable
{
    /// <summary>One past 2^28: sixteen bytes a view, it wraps a 32-bit size back to sixteen bytes.</summary>
    private const int Wrapping = (1 << 28) + 1;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-declared-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task RefusesADictionaryThatDeclaresMoreEntriesThanItsBytesHold()
    {
        // 100 000 empty strings, each its four-byte length, under a count of 2^28 + 1.
        HandBuiltFile built = new(1);
        built.Leaf("name", FieldRepetition.Required, PhysicalType.ByteArray, ConvertedType.Utf8);
        built.Rows = 1;
        built.Chunk(PhysicalType.ByteArray, "name")
            .Dictionary(Wrapping, new byte[400_000])
            .Declared(1, [0], ParquetEncoding.RleDictionary);
        await System.IO.File.WriteAllBytesAsync(_path, built.ToBytes(), Ct);

        ParquetFormatException refused = await ScanAsync();
        Assert.Contains($"declares {Wrapping} entries", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAPageWhoseDeclaredRowsDecodePastTheCap()
    {
        HandBuiltFile built = new(1);
        built.Leaf("name", FieldRepetition.Required, PhysicalType.ByteArray, ConvertedType.Utf8);
        built.Rows = Wrapping;
        built.Chunk(PhysicalType.ByteArray, "name").Declared(Wrapping, new byte[16], ParquetEncoding.DeltaLengthByteArray);
        await System.IO.File.WriteAllBytesAsync(_path, built.ToBytes(), Ct);

        ParquetFormatException refused = await ScanAsync();
        Assert.Contains("the reader allows", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnAadPartPastItsBoundBeforeItSizesAnything()
    {
        // An encrypted footer whose algorithm stores 2 MB of unique AAD bytes: more than a thread's
        // stack, where the AAD of the footer's module was once laid out.
        ArrayBufferWriter<byte> tail = new();
        ThriftCompactWriter writer = new(tail);
        short crypto = writer.BeginStruct();
        short algorithm = writer.BeginStructField(1);
        short gcm = writer.BeginStructField(1);
        writer.WriteBinaryField(2, new byte[2 * 1024 * 1024]);
        writer.EndStruct(gcm);
        writer.EndStruct(algorithm);
        writer.EndStruct(crypto);
        writer.Flush();
        byte[] module = new byte[4 + 12 + 16];
        BinaryPrimitives.WriteInt32LittleEndian(module, 12 + 16);
        tail.Write(module);

        ArrayBufferWriter<byte> file = new();
        file.Write("PARE"u8);
        file.Write(tail.WrittenSpan);
        BinaryPrimitives.WriteInt32LittleEndian(file.GetSpan(4), tail.WrittenCount);
        file.Advance(4);
        file.Write("PARE"u8);
        await System.IO.File.WriteAllBytesAsync(_path, file.WrittenSpan.ToArray(), Ct);

        ParquetOpenOptions options = new() { Decryption = new ParquetDecryption { FooterKey = "footer-key-16byt"u8.ToArray() } };
        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(async () =>
        {
            await using ParquetFile opened = await VortexSession.Default.OpenParquetAsync(_path, options, Ct);
        });
        Assert.Contains("aad_file_unique is 2097152 bytes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesARowGroupOfFewerChunksThanTheSchemaHasLeaves()
    {
        HandBuiltFile built = new(2);
        built.Leaf("a", FieldRepetition.Required, PhysicalType.Int64).Leaf("b", FieldRepetition.Required, PhysicalType.Int64);
        built.Rows = 1;
        built.Chunk(PhysicalType.Int64, "a").V2(null, 0, [0], 0, 1, HandBuiltFile.Longs(7));
        await System.IO.File.WriteAllBytesAsync(_path, built.ToBytes(), Ct);

        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(async () =>
        {
            await using ParquetFile opened = await ParquetFile.OpenAsync(_path, Ct);
        });
        Assert.Contains("holds 1 column chunks where the schema has 2 leaves", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesASchemaWithoutAFieldRatherThanReadItsRootAsALeaf()
    {
        HandBuiltFile built = new(0);
        built.Rows = 3;
        await System.IO.File.WriteAllBytesAsync(_path, built.ToBytes(), Ct);

        await Assert.ThrowsAsync<ParquetUnsupportedException>(async () =>
        {
            await using ParquetFile opened = await ParquetFile.OpenAsync(_path, Ct);
        });
    }

    private async Task<ParquetFormatException> ScanAsync()
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        return await Assert.ThrowsAsync<ParquetFormatException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
            {
                batch.Dispose();
            }
        });
    }
}
