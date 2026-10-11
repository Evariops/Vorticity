using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Files of the standard's test suite whose contents their documentation states, read and compared
/// with it: nested lists and maps in their every structure, as other writers shred them. The files
/// are not in the repository; <c>VORTICITY_PARQUET_DATA</c> names the suite's directory.
/// </summary>
public sealed class DocumentedFileTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsMapsWithNullValuesOrNoneAndAListOfTheirKeys()
    {
        List<string[]> rows = await RowsAsync("data/map_no_value.parquet");
        Assert.Equal(3, rows.Count);
        for (int i = 0; i < 3; i++)
        {
            int first = 3 * i + 1;
            Assert.Equal(
                [
                    $"{{{first} -> null, {first + 1} -> null, {first + 2} -> null}}",
                    $"{{{first} -> null, {first + 1} -> null, {first + 2} -> null}}",
                    $"[{first}, {first + 1}, {first + 2}]",
                ],
                rows[i]);
        }
    }

    [Fact]
    public async Task ReadsTheLegacyTwoLevelListOfLists()
    {
        List<string[]> rows = await RowsAsync("data/old_list_structure.parquet");
        Assert.Equal([["[[1, 2], [3, 4]]"]], rows);
    }

    [Fact]
    public async Task ReadsAMapWhoseKeysAWriterMadeOptional()
    {
        // The writer's map kept no order: its file holds the second key first.
        List<string[]> rows = await RowsAsync("data/incorrect_map_schema.parquet");
        Assert.Equal([["{\"parent\" -> \"another\", \"name\" -> \"report\"}"]], rows);
    }

    [Fact]
    public async Task ReadsAnEmptyList()
    {
        List<string[]> rows = await RowsAsync("data/null_list.parquet");
        Assert.Equal([["[]"]], rows);
    }

    [Fact]
    public async Task ReadsListsOfListsOfListsWithNullsInside()
    {
        List<string[]> rows = await RowsAsync("data/nested_lists.snappy.parquet");
        Assert.Equal(
            [
                ["[[[\"a\", \"b\"], [\"c\"]], [null, [\"d\"]]]", "1"],
                ["[[[\"a\", \"b\"], [\"c\", \"d\"]], [null, [\"e\"]]]", "1"],
                ["[[[\"a\", \"b\"], [\"c\", \"d\"], [\"e\"]], [null, [\"f\"]]]", "1"],
            ],
            rows);
    }

    [Fact]
    public async Task ReadsMapsOfMapsNullAndEmpty()
    {
        List<string[]> rows = await RowsAsync("data/nested_maps.snappy.parquet");
        Assert.Equal(
            [
                "{\"a\" -> {1 -> true, 2 -> false}}",
                "{\"b\" -> {1 -> true}}",
                "{\"c\" -> null}",
                "{\"d\" -> {}}",
                "{\"e\" -> {1 -> true}}",
                "{\"f\" -> {3 -> true, 4 -> false, 5 -> true}}",
            ],
            rows.ConvertAll(row => row[0]));
    }

    [Fact]
    public async Task ReadsARepeatedGroupWithoutAnnotationAsAList()
    {
        List<string[]> rows = await RowsAsync("data/repeated_no_annotation.parquet");
        Assert.Equal(
            [
                ["1", "null"],
                ["2", "null"],
                ["3", "{phone: []}"],
                ["4", "{phone: [{number: 5555555555, kind: null}]}"],
                ["5", "{phone: [{number: 1111111111, kind: \"home\"}]}"],
                ["6", "{phone: [{number: 1111111111, kind: \"home\"}, {number: 2222222222, kind: null}, {number: 3333333333, kind: \"mobile\"}]}"],
            ],
            rows);
    }

    [Fact]
    public async Task ReadsRepeatedLeavesWithoutAnnotationAsLists()
    {
        List<string[]> rows = await RowsAsync("data/repeated_primitive_no_list.parquet");
        Assert.Equal(
            [
                ["[0, 1, 2, 3]", "[\"foo\", \"zero\", \"one\", \"two\"]", "{Int32_list_in_group: [0, 1, 2, 3], String_list_in_group: [\"foo\", \"zero\", \"one\", \"two\"]}"],
                ["[]", "[\"three\"]", "{Int32_list_in_group: [], String_list_in_group: [\"three\"]}"],
                ["[4]", "[\"four\"]", "{Int32_list_in_group: [4], String_list_in_group: [\"four\"]}"],
                ["[5, 6, 7, 8]", "[\"five\", \"six\", \"seven\", \"eight\"]", "{Int32_list_in_group: [5, 6, 7, 8], String_list_in_group: [\"five\", \"six\", \"seven\", \"eight\"]}"],
            ],
            rows);
    }

    [Fact]
    public async Task ReadsListsMapsAndStructsNestedInEveryOrderWithNullsAtEveryLevel()
    {
        List<string[]> rows = await RowsAsync("data/nullable.impala.parquet");
        Assert.Equal(7, rows.Count);
        Assert.Equal(
            [
                "2", "[null, 1, 2, null, 3, null]", "[[null, 1, 2, null], [3, null, 4], [], null]", "{\"k1\" -> 2, \"k2\" -> null}",
                "[{\"k3\" -> null, \"k1\" -> 1}, null, {}]",
                "{A: null, b: [null], C: {d: [[{E: null, F: null}, {E: 10, F: \"aaa\"}, {E: null, F: null}, {E: -10, F: \"bbb\"}, {E: null, F: null}], [{E: 11, F: \"c\"}, null], [], null]}, "
                    + "g: {\"g1\" -> {H: {i: [2.2, null]}}, \"g2\" -> {H: {i: []}}, \"g3\" -> null, \"g4\" -> {H: {i: null}}, \"g5\" -> {H: null}}}",
            ],
            rows[1]);
        Assert.Equal(["3", "[]", "[null]", "{}", "[null, null]", "{A: null, b: null, C: {d: []}, g: {}}"], rows[2]);
        Assert.Equal(["4", "null", "[]", "{}", "[]", "{A: null, b: null, C: {d: null}, g: null}"], rows[3]);
        Assert.Equal(["6", "null", "null", "null", "null", "null"], rows[5]);
        Assert.Equal(["7", "null", "[null, [5, 6]]", "{\"k1\" -> null, \"k3\" -> null}", "null", "{A: 7, b: [2, 3, null], C: {d: [[], [null], null]}, g: null}"], rows[6]);

        List<string[]> required = await RowsAsync("data/nonnullable.impala.parquet");
        Assert.Equal(
            [[
                "8", "[-1]", "[[-1, -2], []]", "{\"k1\" -> -1}", "[{}, {\"k1\" -> 1}, {}, {}]",
                "{a: -1, B: [-1], c: {D: [[{e: -1, f: \"nonnullable\"}]]}, G: {}}",
            ]],
            required);
    }

    [Fact]
    public async Task DecodesEveryByteStreamSplitColumnAsItsPlainTwin()
    {
        // The columns go in pairs, each value PLAIN and then BYTE_STREAM_SPLIT.
        List<string[]> rows = await RowsAsync("data/byte_stream_split_extended.gzip.parquet");
        Assert.NotEmpty(rows);
        foreach (string[] row in rows)
        {
            Assert.Equal(14, row.Length);
            for (int c = 0; c < row.Length; c += 2)
            {
                Assert.Equal(row[c], row[c + 1]);
            }
        }
    }

    [Fact]
    public async Task ReadsListsOfNullableValues()
    {
        List<string[]> rows = await RowsAsync("data/list_columns.parquet");
        Assert.Equal(
            [
                ["[1, 2, 3]", "[\"abc\", \"efg\", \"hij\"]"],
                ["[null, 1]", "null"],
                ["[4]", "[\"efg\", null, \"hij\", \"xyz\"]"],
            ],
            rows);
    }

    /// <summary>
    /// A tool rather than a test: the rows of the file <c>VORTICITY_PARQUET_DUMP</c> names written out
    /// to the one <c>VORTICITY_PARQUET_REPORT</c> names, for a file whose contents are looked at.
    /// </summary>
    [Fact]
    public async Task WritesOutAFilesRows()
    {
        string? path = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DUMP");
        string? reportPath = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_REPORT");
        Assert.SkipWhen(path is null || reportPath is null, "VORTICITY_PARQUET_DUMP names no file to write out.");
        await using StreamWriter report = new(reportPath!, append: false);
        await using ParquetFile file = await ParquetFile.OpenAsync(path!, Ct);
        for (int c = 0; c < file.Schema.Count; c++)
        {
            await report.WriteLineAsync($"{file.Schema[c].Name}: {file.Schema[c].Type}");
        }

        foreach (string[] row in await RowsAsync(path!))
        {
            await report.WriteLineAsync(string.Join(" | ", row));
        }
    }

    /// <summary>Every row of a file, each column's value written out.</summary>
    private static async Task<List<string[]>> RowsAsync(string relative)
    {
        if (!Path.IsPathRooted(relative))
        {
            Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
            relative = Path.Combine(Root!, relative);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(relative, Ct);
        List<string[]> rows = [];
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

                    rows.Add(row);
                }
            }
        }

        return rows;
    }
}
