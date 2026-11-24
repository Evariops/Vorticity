using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Tests.Parquet;

/// <summary>
/// A count a page declares sizes what its decode allocates, so a hostile one is refused as a format
/// error before anything is sized from it: never multiplied into a size that wraps past 2^31 and
/// leaves a block too small for the values written into it.
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
