using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A file written on several threads: its columns close their pages side by side, and the file is
/// the same bytes at every degree, and reads back the rows written.
/// </summary>
public sealed partial class WriterDegreeTests
{
    private const int Rows = 70_000;

    [VortexRecord]
    public partial record struct Event(long Id, string Name, double Score, int? Sparse, string Unique, string[] Tags, decimal Price);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ParquetCompression.Zstd)]
    [InlineData(ParquetCompression.Snappy)]
    public async Task WritesTheSameBytesAtEveryDegree(ParquetCompression compression)
    {
        Event[] rows = Events();
        byte[]? first = null;
        foreach (int degree in (int[])[1, 2, 4, 8])
        {
            string path = Path.Combine(Path.GetTempPath(), $"vorticity-degree-{Guid.NewGuid():N}.parquet");
            try
            {
                ParquetWriteOptions options = new()
                {
                    DegreeOfParallelism = degree,
                    Compression = compression,
                    RowGroupRows = 32_768,
                    BloomFilters = new Dictionary<string, double> { ["Unique"] = 0.01 },
                    WriteChecksums = true,
                };
                await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Event>(path, options))
                {
                    // In batches that end inside blocks, as a caller's do.
                    for (int start = 0; start < rows.Length; start += 9_999)
                    {
                        await writer.WriteAsync<Event>(rows.AsSpan(start, Math.Min(9_999, rows.Length - start)), Ct);
                    }

                    await writer.CompleteAsync(Ct);
                }

                byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, Ct);
                if (first is null)
                {
                    first = bytes;
                    await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
                    List<string> read = [];
                    await foreach (Event row in file.Scan<Event>().ToRecordsAsync(Ct))
                    {
                        read.Add(Render(row));
                    }

                    Assert.Equal(rows.Select(Render), read);
                }
                else
                {
                    Assert.True(first.AsSpan().SequenceEqual(bytes), $"degree {degree} wrote other bytes");
                }
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }

    private static string Render(Event row) =>
        $"{row.Id}|{row.Name}|{row.Score}|{row.Sparse?.ToString() ?? "null"}|{row.Unique}|{string.Join(',', row.Tags)}|{row.Price.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture)}";

    private static Event[] Events()
    {
        Random random = new(29);
        Event[] rows = new Event[Rows];
        for (int i = 0; i < Rows; i++)
        {
            string[] tags = [.. Enumerable.Range(0, random.Next(4)).Select(t => $"tag-{random.Next(50)}")];
            rows[i] = new Event(
                i * 3L + random.Next(3),
                $"name-{random.Next(300)}",
                Math.Round(random.NextDouble() * 1_000, 3),
                random.Next(5) == 0 ? null : random.Next(-1_000, 1_000),
                Guid.NewGuid().ToString("N"),
                tags,
                random.Next(-1_000_000, 1_000_000) / 100m);
        }

        return rows;
    }
}
