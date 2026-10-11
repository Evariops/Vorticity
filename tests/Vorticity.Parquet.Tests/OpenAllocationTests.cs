using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The open of a Parquet file is its schema and its row groups, not their product: a row group's
/// column chunks are indexed when a read first reaches the group, so what an open allocates grows with
/// the columns and with the row groups, and never with the chunks.
/// </summary>
/// <remarks>
/// Four footers, of 50 and 200 columns by 50 and 200 row groups. Under a cost of the columns plus the
/// row groups the widest open costs exactly what the two mixed ones do, less the smallest; a cost per
/// chunk would leave 22 500 chunks over, kilobytes at a few bytes a chunk.
/// </remarks>
[Collection(nameof(ParquetAllocationCollection))]
public sealed class OpenAllocationTests : IDisposable
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
    public async Task AnOpenAllocatesForColumnsAndRowGroupsNotForTheirChunks()
    {
        DebuggableAttribute? debuggable = typeof(ParquetFile).Assembly.GetCustomAttribute<DebuggableAttribute>();
        Assert.SkipWhen(debuggable is { IsJITOptimizerDisabled: true }, "Allocation figures are Release figures: run `dotnet test -c Release`.");

        string small = await WriteAsync(50, 50);
        string wide = await WriteAsync(200, 50);
        string tall = await WriteAsync(50, 200);
        string both = await WriteAsync(200, 200);
        long smallBytes = await FloorAsync(small);
        long wideBytes = await FloorAsync(wide);
        long tallBytes = await FloorAsync(tall);
        long bothBytes = await FloorAsync(both);

        long excess = bothBytes - (wideBytes + tallBytes - smallBytes);
        Assert.True(
            Math.Abs(excess) <= 2_048,
            string.Create(CultureInfo.InvariantCulture, $"Opens of 50 and 200 columns by 50 and 200 row groups allocate {smallBytes}, {wideBytes}, {tallBytes} and {bothBytes} bytes: {excess} past a cost of the columns plus the row groups"));
    }

    /// <summary>The least an open of <paramref name="path"/> allocates, warm, over five.</summary>
    private static async Task<long> FloorAsync(string path)
    {
        await OpenAsync(path);
        long least = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            least = Math.Min(least, await OpenAsync(path));
        }

        return least;
    }

    /// <summary>What opening <paramref name="path"/>, reading its schema and its rows, and closing it allocate.</summary>
    private static async Task<long> OpenAsync(string path)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await using (ParquetFile file = await ParquetFile.OpenAsync(path, Ct))
        {
            GC.KeepAlive(file.Schema);
            GC.KeepAlive(file.RowCount);
        }

        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>A file of <paramref name="columns"/> columns and <paramref name="groups"/> row groups of a row each.</summary>
    private async Task<string> WriteAsync(int columns, int groups)
    {
        VortexField[] fields = new VortexField[columns];
        for (int c = 0; c < columns; c++)
        {
            fields[c] = new VortexField($"c{c:D3}", VortexType.Int32);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-open-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, VortexSchema.Create(fields), new ParquetWriteOptions { BlockRows = 1, RowGroupRows = 1 });
        ColumnsBuilder builder = writer.Builder();
        for (int row = 0; row < groups; row++)
        {
            for (int c = 0; c < columns; c++)
            {
                builder.Column<int>(c).Append(row + c);
            }
        }

        await writer.WriteAsync(builder, Ct);
        ParquetWriteReport report = await writer.CompleteAsync(Ct);
        Assert.Equal(groups, report.RowGroupCount);
        return path;
    }
}
