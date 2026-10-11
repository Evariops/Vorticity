using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Nested columns assembled from their levels: the two records of the Dremel paper's Document,
/// whose levels the paper lists, laid out by hand in every way a writer may lay them out, and
/// broken in the ways a file must be refused for.
/// </summary>
public sealed class NestedReaderTests
{
    private static readonly string[] Expected =
    [
        "10 | {Backward: [], Forward: [20, 40, 60]} | "
            + "[{Language: [{Code: \"en-us\", Country: \"us\"}, {Code: \"en\", Country: null}], Url: \"http://A\"}, "
            + "{Language: [], Url: \"http://B\"}, {Language: [{Code: \"en-gb\", Country: \"gb\"}], Url: null}]",
        "20 | {Backward: [10, 30], Forward: [80]} | [{Language: [], Url: \"http://C\"}]",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Layout.V1)]
    [InlineData(Layout.V2)]
    [InlineData(Layout.BitPacked)]
    [InlineData(Layout.RowsAcrossPages)]
    public async Task AssemblesTheDocumentsFromTheirLevels(Layout layout)
    {
        byte[] bytes = Document(layout).ToBytes();
        Assert.Equal(Expected, await RowsAsync(bytes, batchRows: 0));
        Assert.Equal(Expected, await RowsAsync(bytes, batchRows: 1));
    }

    [Fact]
    public async Task ReadsANestedFieldAlone()
    {
        byte[] bytes = Document(Layout.RowsAcrossPages).ToBytes();
        using TempFile file = new(bytes);
        await using ParquetFile parquet = await ParquetFile.OpenAsync(file.Path, Ct);
        List<string> names = [];
        await foreach (RecordBatch batch in parquet.Scan("Name").ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    names.Add(Render.Row(batch, 0, r));
                }
            }
        }

        Assert.Equal([Expected[0].Split(" | ")[2], Expected[1].Split(" | ")[2]], names);
    }

    [Fact]
    public async Task RefusesALevelPastItsMaximum()
    {
        HandBuiltFile file = Document(Layout.V1, code: ([0, 2, 1, 1, 0], [2, 3, 1, 2, 1]));
        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(file.ToBytes(), 0));
        Assert.Contains("passes the column's maximum", refused.Message);
    }

    [Fact]
    public async Task RefusesAChunkThatStartsInsideARow()
    {
        HandBuiltFile file = Document(Layout.V1, forward: ([1, 1, 1, 0], [2, 2, 2, 2]));
        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(file.ToBytes(), 0));
        Assert.Contains("starts inside a row", refused.Message);
    }

    [Fact]
    public async Task RefusesColumnsThatDisagreeOnTheirParentsShape()
    {
        // The URLs give the first document two names where the codes give it three.
        HandBuiltFile file = Document(Layout.V1, url: ([0, 1, 0], [2, 2, 2], ["http://A", "http://B", "http://C"]));
        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(file.ToBytes(), 0));
        Assert.Contains("where its holder gives", refused.Message);
    }

    [Fact]
    public async Task RefusesAnElementOfAnEmptyList()
    {
        HandBuiltFile file = Document(Layout.V1, backward: ([0, 1, 0, 1], [1, 1, 2, 2], [10, 30]));
        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(file.ToBytes(), 0));
        Assert.Contains("repeats a list that holds no element", refused.Message);
    }

    /// <summary>The Document file, its pages v1 and a row cut across two of them.</summary>
    internal static byte[] DocumentBytes() => Document(Layout.RowsAcrossPages).ToBytes();

    public enum Layout
    {
        V1,
        V2,
        BitPacked,
        RowsAcrossPages,
    }

    /// <summary>
    /// The Document schema and its two records, each column's levels as the paper lists them unless
    /// a test replaces them.
    /// </summary>
    private static HandBuiltFile Document(
        Layout layout,
        (byte[] Rep, byte[] Def)? code = null,
        (byte[] Rep, byte[] Def)? forward = null,
        (byte[] Rep, byte[] Def, string[] Values)? url = null,
        (byte[] Rep, byte[] Def, long[] Values)? backward = null)
    {
        HandBuiltFile file = new HandBuiltFile(3) { Rows = 2 }
            .Leaf("DocId", FieldRepetition.Required, PhysicalType.Int64)
            .Group("Links", FieldRepetition.Optional, 2)
            .Leaf("Backward", FieldRepetition.Repeated, PhysicalType.Int64)
            .Leaf("Forward", FieldRepetition.Repeated, PhysicalType.Int64)
            .Group("Name", FieldRepetition.Repeated, 2)
            .Group("Language", FieldRepetition.Repeated, 2)
            .Leaf("Code", FieldRepetition.Required, PhysicalType.ByteArray, ConvertedType.Utf8)
            .Leaf("Country", FieldRepetition.Optional, PhysicalType.ByteArray, ConvertedType.Utf8)
            .Leaf("Url", FieldRepetition.Optional, PhysicalType.ByteArray, ConvertedType.Utf8);

        Page(file.Chunk(PhysicalType.Int64, "DocId"), layout, null, 0, [0, 0], 0, HandBuiltFile.Longs(10, 20), rows: 2);
        (byte[] backRep, byte[] backDef, long[] backValues) = backward ?? ([0, 0, 1], [1, 2, 2], [10, 30]);
        Page(file.Chunk(PhysicalType.Int64, "Links", "Backward"), layout, backRep, 1, backDef, 2, HandBuiltFile.Longs(backValues), rows: 2);

        HandBuiltFile.Column forwardColumn = file.Chunk(PhysicalType.Int64, "Links", "Forward");
        (byte[] forwardRep, byte[] forwardDef) = forward ?? ([0, 1, 1, 0], [2, 2, 2, 2]);
        if (layout == Layout.RowsAcrossPages)
        {
            // The first document's links cut after its second: the second page goes on with its row.
            forwardColumn.V1(forwardRep[..2], 1, forwardDef[..2], 2, HandBuiltFile.Longs(20, 40));
            forwardColumn.V1(forwardRep[2..], 1, forwardDef[2..], 2, HandBuiltFile.Longs(60, 80));
        }
        else
        {
            Page(forwardColumn, layout, forwardRep, 1, forwardDef, 2, HandBuiltFile.Longs(20, 40, 60, 80), rows: 2);
        }

        HandBuiltFile.Column codeColumn = file.Chunk(PhysicalType.ByteArray, "Name", "Language", "Code");
        (byte[] codeRep, byte[] codeDef) = code ?? ([0, 2, 1, 1, 0], [2, 2, 1, 2, 1]);
        if (layout == Layout.RowsAcrossPages)
        {
            codeColumn.V1(codeRep[..2], 2, codeDef[..2], 2, HandBuiltFile.Strings("en-us", "en"));
            codeColumn.V1(codeRep[2..], 2, codeDef[2..], 2, HandBuiltFile.Strings("en-gb"));
        }
        else
        {
            Page(codeColumn, layout, codeRep, 2, codeDef, 2, HandBuiltFile.Strings("en-us", "en", "en-gb"), rows: 2);
        }

        Page(file.Chunk(PhysicalType.ByteArray, "Name", "Language", "Country"), layout, [0, 2, 1, 1, 0], 2, [3, 2, 1, 3, 1], 3, HandBuiltFile.Strings("us", "gb"), rows: 2);
        (byte[] urlRep, byte[] urlDef, string[] urlValues) = url ?? ([0, 1, 1, 0], [2, 2, 1, 2], ["http://A", "http://B", "http://C"]);
        Page(file.Chunk(PhysicalType.ByteArray, "Name", "Url"), layout, urlRep, 1, urlDef, 2, HandBuiltFile.Strings(urlValues), rows: 2);
        return file;
    }

    private static void Page(HandBuiltFile.Column column, Layout layout, byte[]? rep, int maxRep, byte[] def, int maxDef, byte[] values, int rows)
    {
        if (layout == Layout.V2)
        {
            column.V2(rep, maxRep, def, maxDef, rows, values);
        }
        else
        {
            column.V1(rep, maxRep, def, maxDef, values, bitPacked: layout == Layout.BitPacked);
        }
    }

    /// <summary>Every row of a file's bytes, its columns' values joined.</summary>
    private static async Task<List<string>> RowsAsync(byte[] bytes, int batchRows)
    {
        using TempFile file = new(bytes);
        await using ParquetFile parquet = await ParquetFile.OpenAsync(file.Path, Ct);
        List<string> rows = [];
        Scan scan = parquet.Scan();
        if (batchRows > 0)
        {
            scan = scan.With(new ScanOptions { BatchRows = batchRows });
        }

        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
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

    /// <summary>Bytes written to a file of the temporary directory, deleted with it.</summary>
    private sealed class TempFile : IDisposable
    {
        internal TempFile(byte[] bytes)
        {
            System.IO.File.WriteAllBytes(Path, bytes);
        }

        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-nested-{Guid.NewGuid():N}.parquet");

        public void Dispose() => System.IO.File.Delete(Path);
    }
}
