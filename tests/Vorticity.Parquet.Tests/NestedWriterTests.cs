using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Nested columns written: a file of lists, maps and structs read, written again by this package's
/// writer, and read back to the same rows, its every page on a grid small enough that rows and row
/// groups cut through the lists.
/// </summary>
public sealed class NestedWriterTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> DocumentedFiles =>
    [
        "data/nested_lists.snappy.parquet",
        "data/nested_maps.snappy.parquet",
        "data/repeated_no_annotation.parquet",
        "data/repeated_primitive_no_list.parquet",
        "data/nullable.impala.parquet",
        "data/nonnullable.impala.parquet",
        "data/list_columns.parquet",
        "data/map_no_value.parquet",
        "data/old_list_structure.parquet",
        "data/null_list.parquet",
        "data/incorrect_map_schema.parquet",
        "data/nested_structs.rust.parquet",
    ];

    [Theory]
    [InlineData(ParquetCompression.Uncompressed, 8_192, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Zstd, 1, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Uncompressed, 8_192, DataPageVersion.V1)]
    [InlineData(ParquetCompression.Zstd, 1, DataPageVersion.V1)]
    public async Task RewritesTheDocumentsToTheSameRows(ParquetCompression compression, int blockRows, DataPageVersion pages)
    {
        using Temp source = new();
        await System.IO.File.WriteAllBytesAsync(source.Path, NestedReaderTests.DocumentBytes(), Ct);
        List<string> before = await RowsAsync(source.Path);
        using Temp target = new();
        await RewriteAsync(source.Path, target.Path, new ParquetWriteOptions { Compression = compression, BlockRows = blockRows, RowGroupRows = blockRows, DataPageVersion = pages });
        Assert.Equal(before, await RowsAsync(target.Path));
    }

    [Theory]
    [MemberData(nameof(DocumentedFiles))]
    public async Task RewritesAFileOfOtherWritersToTheSameRows(string relative)
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        string source = Path.Combine(Root!, relative);
        List<string> before = await RowsAsync(source);
        foreach (int blockRows in (int[])[8_192, 2])
        {
            foreach (DataPageVersion pages in (DataPageVersion[])[DataPageVersion.V2, DataPageVersion.V1])
            {
                using Temp target = new();
                await RewriteAsync(source, target.Path, new ParquetWriteOptions { BlockRows = blockRows, RowGroupRows = 2 * blockRows, DataPageVersion = pages });
                Assert.Equal(before, await RowsAsync(target.Path));
            }
        }
    }

    /// <summary>
    /// A list view whose offsets and sizes are of two types, neither the other's: shredded through
    /// both widened once, each by its own type, to the same lists.
    /// </summary>
    [Fact]
    public async Task WritesAListViewOfOffsetsAndSizesOfTwoTypes()
    {
        VortexSchema schema = [("tags", VortexType.List(VortexType.Int32))];
        Arrays.CanonicalArena arena = new();
        Types.DTypeArena types = new();
        Types.DType row = VortexTypes.ToDType(schema, types);
        Types.DType list = row.GetField(0);
        int[] values = [1, 2, 3, 4, 5, 6];
        byte[] valueBytes = new byte[values.Length * sizeof(int)];
        Buffer.BlockCopy(values, 0, valueBytes, 0, valueBytes.Length);
        int elements = arena.AddPrimitive(list.ElementType, values.Length, Arrays.Validity.NonNullable, Types.PType.I32, Copy(valueBytes));

        // Rows [], [1], [2, 3], [4, 5, 6]; then [5, 6] again, out of order, as a view may be.
        byte[] offsets = [0, 0, 0, 0, 1, 0, 3, 0, 4, 0];
        byte[] sizes = [0, 1, 2, 3, 2];
        int view = arena.AddListView(list, 5, Arrays.Validity.NonNullable, elements, Copy(offsets), Types.PType.U16, Copy(sizes), Types.PType.U8);
        int root = arena.AddStruct(row, 5, Arrays.Validity.NonNullable, [view]);

        using Temp target = new();
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(target.Path, schema))
        {
            using RecordBatch batch = new(arena, root, 0);
            await writer.WriteAsync(batch, Ct);
            await writer.CompleteAsync(Ct);
        }

        Assert.Equal(["[]", "[1]", "[2, 3]", "[4, 5, 6]", "[5, 6]"], await RowsAsync(target.Path));

        Buffers.VortexBuffer Copy(byte[] bytes)
        {
            Buffers.VortexBuffer buffer = arena.Allocate(bytes.Length, 16, out Span<byte> destination);
            bytes.CopyTo(destination);
            return buffer;
        }
    }

    private static async Task RewriteAsync(string source, string target, ParquetWriteOptions options)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(source, Ct);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(target, file.Schema, options);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                await writer.WriteAsync(batch, Ct);
            }
        }

        await writer.CompleteAsync(Ct);
    }

    /// <summary>Every row of a file, its columns' values joined.</summary>
    private static async Task<List<string>> RowsAsync(string path)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    string[] row = new string[batch.Schema.Count];
                    for (int c = 0; c < row.Length; c++)
                    {
                        row[c] = Render.Row(batch, c, r);
                    }

                    rows.Add(string.Join(" | ", row));
                }
            }
        }

        return rows;
    }

    /// <summary>A path of the temporary directory, deleted with what was written there.</summary>
    private sealed class Temp : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-nested-{Guid.NewGuid():N}.parquet");

        public void Dispose() => System.IO.File.Delete(Path);
    }
}
