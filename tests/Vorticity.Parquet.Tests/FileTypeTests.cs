using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// FILE: a group of the fields the standard names, each optional, read as the struct of them under
/// <c>parquet.file</c> and written back as a group annotated FILE; a group whose fields are not
/// those reads as the struct alone, its annotation dropped.
/// </summary>
public sealed class FileTypeTests : IDisposable
{
    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsAFileGroupAsItsStructAndWritesItBack()
    {
        HandBuiltFile file = new(1);
        file.Group("photo", FieldRepetition.Optional, 6, logical: LogicalTypeKind.File)
            .Leaf("uri", FieldRepetition.Optional, PhysicalType.ByteArray, ConvertedType.Utf8)
            .Leaf("offset", FieldRepetition.Optional, PhysicalType.Int64)
            .Leaf("size", FieldRepetition.Optional, PhysicalType.Int64)
            .Leaf("content_type", FieldRepetition.Optional, PhysicalType.ByteArray, ConvertedType.Utf8)
            .Leaf("checksum", FieldRepetition.Optional, PhysicalType.ByteArray, ConvertedType.Utf8)
            .Leaf("inline", FieldRepetition.Optional, PhysicalType.ByteArray);
        file.Rows = 3;

        // An inline image, a range of an object, and a null reference.
        file.Chunk(PhysicalType.ByteArray, "photo", "uri").V2(null, 0, [1, 2, 0], 2, 3, HandBuiltFile.Strings("s3://bucket/archive.tar"));
        file.Chunk(PhysicalType.Int64, "photo", "offset").V2(null, 0, [1, 2, 0], 2, 3, HandBuiltFile.Longs(512));
        file.Chunk(PhysicalType.Int64, "photo", "size").V2(null, 0, [2, 2, 0], 2, 3, HandBuiltFile.Longs(4, 1024));
        file.Chunk(PhysicalType.ByteArray, "photo", "content_type").V2(null, 0, [2, 2, 0], 2, 3, HandBuiltFile.Strings("image/png", "image/jpeg"));
        file.Chunk(PhysicalType.ByteArray, "photo", "checksum").V2(null, 0, [2, 1, 0], 2, 3, HandBuiltFile.Strings("CRC32:3d8a4c4c"));
        file.Chunk(PhysicalType.ByteArray, "photo", "inline").V2(null, 0, [2, 1, 0], 2, 3, HandBuiltFile.Binaries([0x89, 0x50, 0x4E, 0x47]));
        string source = await SaveAsync(file);

        string[] expected = await RowsAsync(source);
        Assert.Equal(
            [
                "{uri: null, offset: null, size: 4, content_type: \"image/png\", checksum: \"CRC32:3d8a4c4c\", inline: 0x89504E47}",
                "{uri: \"s3://bucket/archive.tar\", offset: 512, size: 1024, content_type: \"image/jpeg\", checksum: null, inline: null}",
                "null",
            ],
            expected);

        string target = Path.Combine(Path.GetTempPath(), $"vx-file-{Guid.NewGuid():N}.parquet");
        _paths.Add(target);
        await using (ParquetFile parquet = await ParquetFile.OpenAsync(source, Ct))
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(target, parquet.Schema, ParquetWriteOptions.Default))
        {
            Assert.Equal("parquet.file", parquet.Schema[0].Type.ExtensionId);
            await foreach (RecordBatch batch in parquet.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        Assert.Equal(expected, await RowsAsync(target));
        await using ParquetFile written = await ParquetFile.OpenAsync(target, Ct);
        Assert.Equal("parquet.file", written.Schema[0].Type.ExtensionId);
    }

    [Theory]
    [InlineData("name", FieldRepetition.Optional)]
    [InlineData("uri", FieldRepetition.Required)]
    internal async Task ReadsAGroupThatIsNoFileAsAStruct(string name, FieldRepetition repetition)
    {
        HandBuiltFile file = new(1);
        file.Group("photo", FieldRepetition.Optional, 1, logical: LogicalTypeKind.File)
            .Leaf(name, repetition, PhysicalType.ByteArray, ConvertedType.Utf8);
        file.Rows = 1;
        file.Chunk(PhysicalType.ByteArray, "photo", name).V2(null, 0, [repetition == FieldRepetition.Optional ? (byte)2 : (byte)1], repetition == FieldRepetition.Optional ? 2 : 1, 1, HandBuiltFile.Strings("x"));
        string source = await SaveAsync(file);

        await using ParquetFile parquet = await ParquetFile.OpenAsync(source, Ct);
        Assert.Equal(VortexTypeKind.Struct, parquet.Schema[0].Type.Kind);
        Assert.Equal([$"{{{name}: \"x\"}}"], await RowsAsync(source));
    }

    private async Task<string> SaveAsync(HandBuiltFile file)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vx-file-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await System.IO.File.WriteAllBytesAsync(path, file.ToBytes(), Ct);
        return path;
    }

    private static async Task<string[]> RowsAsync(string path)
    {
        List<string> rows = [];
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int row = 0; row < batch.RowCount; row++)
                {
                    rows.Add(Render.Row(batch, 0, row));
                }
            }
        }

        return [.. rows];
    }
}
