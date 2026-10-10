using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Parquet.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The standard's VARIANT: the suite's <c>shredded_variant</c> cases, each row's variant the bytes
/// its case's <c>.variant.bin</c> holds and each error refused, and files put together by hand for
/// what the suite does not reach: objects past 255 fields, metadata that changes from row to row,
/// variants in a list, batches after the first, and a variant written back.
/// </summary>
/// <remarks>
/// The suite's cases come from another implementation's tests, whose writer chose the encoding a
/// rebuilt value takes here, the fewest bytes for every id, offset and count and the fields in name
/// order: the comparison is of bytes. Seven files it marks invalid, a variant or a field without its
/// value column and fields both shredded and in the value, may be refused or read; they are read,
/// as the suite's values say.
/// </remarks>
public sealed class VariantTests : IDisposable
{
    private readonly List<string> _paths = [];

    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsEveryCaseOfTheSuiteAsItsVariantOrRefusesIt()
    {
        string? cases = Root is null || !Directory.Exists(Root) ? null
            : Directory.EnumerateFiles(Root, "cases.json", SearchOption.AllDirectories).FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)) == "shredded_variant");
        Assert.SkipWhen(cases is null, "VORTICITY_PARQUET_DATA holds no shredded_variant/cases.json.");
        string directory = Path.GetDirectoryName(cases)!;
        using JsonDocument json = JsonDocument.Parse(await System.IO.File.ReadAllBytesAsync(cases, Ct));
        List<string> failures = [];
        int read = 0;
        int refused = 0;
        foreach (JsonElement test in json.RootElement.EnumerateArray())
        {
            if (!test.TryGetProperty("parquet_file", out JsonElement file))
            {
                continue;
            }

            int number = test.GetProperty("case_number").GetInt32();
            string? error = test.TryGetProperty("error_message", out JsonElement message) ? message.GetString() : null;
            string?[] expected = test.TryGetProperty("variant_file", out JsonElement single) ? [single.GetString()]
                : test.TryGetProperty("variant_files", out JsonElement many) ? many.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()).ToArray()
                : [];
            try
            {
                List<byte[]?> rows = await ReadAsync(Path.Combine(directory, file.GetString()!));
                if (error is not null)
                {
                    failures.Add($"case {number}: read {string.Join(", ", rows.Select(Hex))} where the suite says '{error}'");
                    continue;
                }

                if (rows.Count != expected.Length)
                {
                    failures.Add($"case {number}: {rows.Count} rows where the suite has {expected.Length} variants");
                    continue;
                }

                for (int row = 0; row < rows.Count; row++)
                {
                    byte[]? oracle = expected[row] is { } name ? await System.IO.File.ReadAllBytesAsync(Path.Combine(directory, name), Ct) : null;
                    if (oracle is null != rows[row] is null || (oracle is not null && !oracle.AsSpan().SequenceEqual(rows[row])))
                    {
                        failures.Add($"case {number} row {row}: {Hex(rows[row])} where the suite has {Hex(oracle)}");
                    }
                }

                read++;
            }
            catch (ParquetUnsupportedException e) when (error is not null && error.StartsWith("Unsupported", StringComparison.Ordinal))
            {
                refused++;
                Assert.Contains("typed_value", e.Message, StringComparison.Ordinal);
            }
            catch (ParquetFormatException e) when (error is not null && error.StartsWith("Invalid", StringComparison.Ordinal))
            {
                refused++;
                Assert.Contains("variant", e.Message, StringComparison.Ordinal);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add($"case {number}: {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal((131, 6), (read, refused));
    }

    /// <summary>
    /// 299 fields of a row's value and one shredded field make an object of 300: its count takes
    /// four bytes, its ids and offsets two, and the shredded field lands in its name's place.
    /// </summary>
    [Fact]
    public async Task RebuildsAnObjectOfMoreThan255FieldsWithWideIdsAndOffsets()
    {
        string[] names = Enumerable.Range(0, 300).Select(i => $"f{i:D3}").ToArray();
        byte[] metadata = VariantBytes.Metadata(sorted: true, names);
        byte[] partial = VariantBytes.Object(Enumerable.Range(0, 300).Where(i => i != 150).Select(i => (i, VariantBytes.Int(i))).ToArray());
        HandBuiltFile file = ShreddedObject("f150");
        file.Rows = 3;
        file.Chunk(PhysicalType.ByteArray, "var", "metadata").V2(null, 0, new byte[3], 0, 3, HandBuiltFile.Binaries(metadata, metadata, metadata));
        file.Chunk(PhysicalType.ByteArray, "var", "value").V2(null, 0, [1, 0, 1], 1, 3, HandBuiltFile.Binaries(partial, VariantBytes.Int(7)));
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "f150", "value").V2(null, 0, [1, 1, 0], 2, 3, []);
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "f150", "typed_value").V2(null, 0, [2, 1, 0], 2, 3, HandBuiltFile.Ints(150));

        List<byte[]?> rows = await ReadAsync(await SaveAsync(file));

        byte[] whole = VariantBytes.Object(Enumerable.Range(0, 300).Select(i => (i, VariantBytes.Int(i))).ToArray());
        Assert.Equal(0x02 | (0b1_01_01 << 2), whole[0]);
        Holds(metadata, whole, rows[0]);
        Holds(metadata, VariantBytes.Object(), rows[1]);
        Holds(metadata, VariantBytes.Int(7), rows[2]);
    }

    /// <summary>
    /// A row whose metadata is not the last row's has its fields' ids looked up again: in a sorted
    /// dictionary by halves, in an unsorted one from its start, where the ids of the same names differ.
    /// </summary>
    [Fact]
    public async Task LooksFieldIdsUpAgainWhereTheMetadataChanges()
    {
        byte[] sorted = VariantBytes.Metadata(sorted: true, "a", "b");
        byte[] unsorted = VariantBytes.Metadata(sorted: false, "b", "a");
        HandBuiltFile file = ShreddedObject("a", "b");
        file.Rows = 3;
        file.Chunk(PhysicalType.ByteArray, "var", "metadata").V2(null, 0, new byte[3], 0, 3, HandBuiltFile.Binaries(sorted, unsorted, sorted));
        file.Chunk(PhysicalType.ByteArray, "var", "value").V2(null, 0, [0, 0, 0], 1, 3, []);
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "a", "value").V2(null, 0, [1, 1, 1], 2, 3, []);
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "a", "typed_value").V2(null, 0, [2, 2, 2], 2, 3, HandBuiltFile.Ints(1, 3, 5));
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "b", "value").V2(null, 0, [1, 1, 1], 2, 3, []);
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "b", "typed_value").V2(null, 0, [2, 2, 2], 2, 3, HandBuiltFile.Ints(2, 4, 6));

        List<byte[]?> rows = await ReadAsync(await SaveAsync(file));

        Holds(sorted, VariantBytes.Object((0, VariantBytes.Int(1)), (1, VariantBytes.Int(2))), rows[0]);
        Holds(unsorted, VariantBytes.Object((1, VariantBytes.Int(3)), (0, VariantBytes.Int(4))), rows[1]);
        Holds(sorted, VariantBytes.Object((0, VariantBytes.Int(5)), (1, VariantBytes.Int(6))), rows[2]);
    }

    [Fact]
    public async Task RefusesAShreddedFieldItsRowsMetadataDoesNotName()
    {
        byte[] metadata = VariantBytes.Metadata(sorted: true, "z");
        HandBuiltFile file = ShreddedObject("a");
        file.Rows = 1;
        file.Chunk(PhysicalType.ByteArray, "var", "metadata").V2(null, 0, new byte[1], 0, 1, HandBuiltFile.Binaries(metadata));
        file.Chunk(PhysicalType.ByteArray, "var", "value").V2(null, 0, [0], 1, 1, []);
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "a", "value").V2(null, 0, [1], 2, 1, []);
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "a", "typed_value").V2(null, 0, [2], 2, 1, HandBuiltFile.Ints(1));
        string path = await SaveAsync(file);

        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(() => ReadAsync(path));
        Assert.Contains("'a' its row's metadata does not name", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Twenty thousand rows, three batches: partially shredded objects, fully shredded ones, and
    /// values a typed column leaves as they are, a string of 70 bytes among them, each batch's
    /// rebuilt in the scratch the last one left.
    /// </summary>
    [Fact]
    public async Task RebuildsEveryBatchOfAFileOfSeveral()
    {
        const int Rows = 20_000;
        byte[] metadata = VariantBytes.Metadata(sorted: true, "a", "z");
        string tail = new('x', 70);
        List<byte[]> values = [];
        List<int> typed = [];
        byte[] valueLevels = new byte[Rows];
        byte[] groupLevels = new byte[Rows];
        byte[] typedLevels = new byte[Rows];
        List<byte[]> expected = [];
        for (int i = 0; i < Rows; i++)
        {
            switch (i % 3)
            {
                case 0:
                    values.Add(VariantBytes.Object((1, VariantBytes.Text($"s{i}"))));
                    valueLevels[i] = 1;
                    typed.Add(i);
                    (groupLevels[i], typedLevels[i]) = (1, 2);
                    expected.Add(VariantBytes.Object((0, VariantBytes.Int(i)), (1, VariantBytes.Text($"s{i}"))));
                    break;
                case 1:
                    typed.Add(i);
                    (groupLevels[i], typedLevels[i]) = (1, 2);
                    expected.Add(VariantBytes.Object((0, VariantBytes.Int(i))));
                    break;
                default:
                    values.Add(VariantBytes.Text(tail + i));
                    valueLevels[i] = 1;
                    expected.Add(VariantBytes.Text(tail + i));
                    break;
            }
        }

        HandBuiltFile file = ShreddedObject("a");
        file.Rows = Rows;
        file.Chunk(PhysicalType.ByteArray, "var", "metadata").V2(null, 0, new byte[Rows], 0, Rows, HandBuiltFile.Binaries(Enumerable.Repeat(metadata, Rows).ToArray()));
        file.Chunk(PhysicalType.ByteArray, "var", "value").V2(null, 0, valueLevels, 1, Rows, HandBuiltFile.Binaries([.. values]));
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "a", "value").V2(null, 0, groupLevels, 2, Rows, []);
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "a", "typed_value").V2(null, 0, typedLevels, 2, Rows, HandBuiltFile.Ints([.. typed]));

        List<byte[]?> rows = await ReadAsync(await SaveAsync(file));

        Assert.Equal(Rows, rows.Count);
        for (int i = 0; i < Rows; i++)
        {
            Assert.True(rows[i].AsSpan().SequenceEqual([.. metadata, .. expected[i]]), $"row {i}: {Hex(rows[i])}");
        }
    }

    /// <summary>A variant as a list's element: valid, null and absent elements in their lists, a null list and an empty one.</summary>
    [Fact]
    public async Task ReadsVariantsInAList()
    {
        byte[] metadata = VariantBytes.Metadata(sorted: true);
        HandBuiltFile file = new(1);
        file.Group("vs", FieldRepetition.Optional, 1, ConvertedType.List)
            .Group("list", FieldRepetition.Repeated, 1)
            .Group("element", FieldRepetition.Optional, 2, logical: LogicalTypeKind.Variant)
            .Leaf("metadata", FieldRepetition.Required, PhysicalType.ByteArray)
            .Leaf("value", FieldRepetition.Required, PhysicalType.ByteArray);
        file.Rows = 3;
        byte[] repetition = [0, 1, 1, 0, 0];
        byte[] definition = [3, 2, 3, 0, 1];
        file.Chunk(PhysicalType.ByteArray, "vs", "list", "element", "metadata").V2(repetition, 1, definition, 3, 3, HandBuiltFile.Binaries(metadata, metadata));
        file.Chunk(PhysicalType.ByteArray, "vs", "list", "element", "value").V2(repetition, 1, definition, 3, 3, HandBuiltFile.Binaries(VariantBytes.Text("one"), VariantBytes.Int(2)));

        await using ParquetFile parquet = await ParquetFile.OpenAsync(await SaveAsync(file), Ct);
        Assert.Equal(VortexTypeKind.Variant, parquet.Schema[0].Type.ElementType!.Kind);
        await foreach (RecordBatch batch in parquet.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                ListColumn lists = batch.Column(0).AsList();
                VortexColumn elements = lists.Elements;
                StructColumn parts = elements.AsStruct();
                Assert.Equal(3, lists.GetLength(0));
                int first = checked((int)lists.GetOffset(0));
                Assert.Equal([.. metadata, .. VariantBytes.Text("one")], [.. parts.GetField(0).AsBinary().GetSpan(first), .. parts.GetField(1).AsBinary().GetSpan(first)]);
                Assert.False(elements.IsValid(first + 1));
                Assert.Equal(VariantBytes.Int(2), parts.GetField(1).AsBinary().GetSpan(first + 2).ToArray());
                Assert.False(batch.Column(0).IsValid(1));
                Assert.Equal(0, lists.GetLength(2));
            }
        }
    }

    /// <summary>
    /// A variant read is written back as the standard's unshredded form, a group annotated VARIANT
    /// of its metadata and its value, and read again as the same bytes: shredded objects included,
    /// whose rebuilt values are what the new file holds.
    /// </summary>
    [Fact]
    public async Task WritesAVariantBackAsItsMetadataAndItsValue()
    {
        byte[] sorted = VariantBytes.Metadata(sorted: true, "a", "b");
        HandBuiltFile file = ShreddedObject("a", "b");
        file.Rows = 2;
        file.Chunk(PhysicalType.ByteArray, "var", "metadata").V2(null, 0, new byte[2], 0, 2, HandBuiltFile.Binaries(sorted, sorted));
        file.Chunk(PhysicalType.ByteArray, "var", "value").V2(null, 0, [0, 1], 1, 2, HandBuiltFile.Binaries(VariantBytes.Text("loose")));
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "a", "value").V2(null, 0, [1, 0], 2, 2, []);
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "a", "typed_value").V2(null, 0, [2, 0], 2, 2, HandBuiltFile.Ints(1));
        file.Chunk(PhysicalType.ByteArray, "var", "typed_value", "b", "value").V2(null, 0, [2, 0], 2, 2, HandBuiltFile.Binaries(VariantBytes.Null));
        file.Chunk(PhysicalType.Int32, "var", "typed_value", "b", "typed_value").V2(null, 0, [1, 0], 2, 2, []);
        string source = await SaveAsync(file);
        List<byte[]?> read = await ReadAsync(source);
        Holds(sorted, VariantBytes.Object((0, VariantBytes.Int(1)), (1, VariantBytes.Null)), read[0]);
        Holds(sorted, VariantBytes.Text("loose"), read[1]);

        string target = Path.Combine(Path.GetTempPath(), $"vx-variant-{Guid.NewGuid():N}.parquet");
        _paths.Add(target);
        await using (ParquetFile parquet = await ParquetFile.OpenAsync(source, Ct))
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(target, parquet.Schema, ParquetWriteOptions.Default))
        {
            await foreach (RecordBatch batch in parquet.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        Assert.Equal(read, await ReadAsync(target));
        await using ParquetFile written = await ParquetFile.OpenAsync(target, Ct);
        Assert.Equal(["var.metadata", "var.value"], written.Metadata.Columns.Select(c => c.Path).ToArray());

        // And as Vortex, whose writer takes the variant node as it is and spells it vortex.parquet.variant.
        string vortex = Path.Combine(Path.GetTempPath(), $"vx-variant-{Guid.NewGuid():N}.vortex");
        _paths.Add(vortex);
        await using (ParquetFile parquet = await ParquetFile.OpenAsync(source, Ct))
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(vortex, parquet.Schema))
        {
            await foreach (RecordBatch batch in parquet.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        List<byte[]?> again = [];
        await using VortexFile reread = await VortexFile.OpenAsync(vortex, Ct);
        await foreach (RecordBatch batch in reread.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                StructColumn parts = batch.Column(0).AsStruct();
                for (int row = 0; row < batch.RowCount; row++)
                {
                    again.Add([.. parts.GetField(0).AsBinary().GetSpan(row), .. parts.GetField(1).AsBinary().GetSpan(row)]);
                }
            }
        }

        Assert.Equal(read, again);
    }

    /// <summary>
    /// A required variant of a required metadata, an optional value and an optional typed_value
    /// object of <paramref name="fields"/>, each a required group of an optional value and an
    /// optional INT32 typed_value: the file's chunks follow in that order.
    /// </summary>
    private static HandBuiltFile ShreddedObject(params string[] fields)
    {
        HandBuiltFile file = new(1);
        file.Group("var", FieldRepetition.Required, 3, logical: LogicalTypeKind.Variant)
            .Leaf("metadata", FieldRepetition.Required, PhysicalType.ByteArray)
            .Leaf("value", FieldRepetition.Optional, PhysicalType.ByteArray)
            .Group("typed_value", FieldRepetition.Optional, fields.Length);
        foreach (string field in fields)
        {
            file.Group(field, FieldRepetition.Required, 2)
                .Leaf("value", FieldRepetition.Optional, PhysicalType.ByteArray)
                .Leaf("typed_value", FieldRepetition.Optional, PhysicalType.Int32);
        }

        return file;
    }

    private async Task<string> SaveAsync(HandBuiltFile file)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vx-variant-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await System.IO.File.WriteAllBytesAsync(path, file.ToBytes(), Ct);
        return path;
    }

    /// <summary>Each row's variant of the file's <c>var</c> column, its metadata then its value, or null for a null row.</summary>
    private static async Task<List<byte[]?>> ReadAsync(string path)
    {
        List<byte[]?> rows = [];
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        int column = Enumerable.Range(0, file.Schema.Count).Single(i => file.Schema[i].Name == "var");
        Assert.Equal(VortexTypeKind.Variant, file.Schema[column].Type.Kind);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                VortexColumn variant = batch.Column(column);
                Assert.Equal(DTypeKind.Variant, variant.DType.Kind);
                StructColumn parts = variant.AsStruct();
                BinaryColumn metadata = parts.GetField(0).AsBinary();
                BinaryColumn value = parts.GetField(1).AsBinary();
                for (int row = 0; row < batch.RowCount; row++)
                {
                    rows.Add(variant.IsValid(row) ? [.. metadata.GetSpan(row), .. value.GetSpan(row)] : null);
                }
            }
        }

        return rows;
    }

    /// <summary>Asserts <paramref name="row"/> is the variant of <paramref name="metadata"/> and <paramref name="value"/>.</summary>
    private static void Holds(byte[] metadata, byte[] value, byte[]? row) => Assert.Equal(Hex([.. metadata, .. value]), Hex(row));

    private static string Hex(byte[]? bytes) => bytes is null ? "null" : Convert.ToHexString(bytes);
}
