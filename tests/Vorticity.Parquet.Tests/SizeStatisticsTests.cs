using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The size statistics this writer gives a chunk and its pages: a byte array column's value bytes,
/// lengths left out, and a nested column's level histograms, in its metadata and its indexes, each
/// what the rows written hold.
/// </summary>
public sealed partial class SizeStatisticsTests : IDisposable
{
    private const int Rows = 20_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-sizes-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Doc(long Id, string Name, string? Note, string[] Tags);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task CountsTheBytesAndTheLevelsWritten()
    {
        Random random = new(31);
        Doc[] rows = new Doc[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Doc(i, $"name-{random.Next(1_000_000)}", i % 4 == 0 ? null : $"n{i}", [.. Enumerable.Range(0, random.Next(5)).Select(t => $"t{t}")]);
        }

        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Doc>(_path, new ParquetWriteOptions { BlockRows = 4_096, RowGroupRows = 4 * 4_096 }))
        {
            await writer.WriteAsync<Doc>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        int groups = file.Footer.RowGroups.Length;
        long name = 0;
        long note = 0;
        long[] repetition = new long[2];
        for (int group = 0; group < groups; group++)
        {
            // An integer column has none.
            Assert.False(file.Footer.Chunk(group, 0).SizeStatistics.IsPresent);

            name += Bytes(file, bytes, group, 1);
            note += Bytes(file, bytes, group, 2);
            (long? _, long[]? tagsRepetition, long[]? _) = Sizes(file, group, 3);
            Assert.NotNull(tagsRepetition);
            for (int level = 0; level < 2; level++)
            {
                repetition[level] += tagsRepetition[level];
            }

            // The column index's pages add up to the chunk.
            long[] pages = Histograms(file, bytes, group, 3, field: 6);
            Assert.Equal(tagsRepetition[0], pages.Where((_, i) => i % 2 == 0).Sum());
            Assert.Equal(tagsRepetition[1], pages.Where((_, i) => i % 2 == 1).Sum());
        }

        Assert.Equal(rows.Sum(r => (long)Encoding.UTF8.GetByteCount(r.Name)), name);
        Assert.Equal(rows.Sum(r => r.Note is null ? 0L : Encoding.UTF8.GetByteCount(r.Note)), note);

        // A row is a level 0, and every element past a list's first a level 1.
        Assert.Equal(Rows, repetition[0]);
        Assert.Equal(rows.Sum(r => (long)Math.Max(0, r.Tags.Length - 1)), repetition[1]);
    }

    /// <summary>A byte array column's value bytes in a chunk: its metadata's, checked against its offset index's pages.</summary>
    private static long Bytes(ParquetFile file, byte[] bytes, int group, int column)
    {
        (long? total, long[]? _, long[]? _) = Sizes(file, group, column);
        Assert.NotNull(total);
        ColumnChunkMetadata chunk = file.Footer.Chunk(group, column);
        ThriftCompactReader reader = new(bytes.AsSpan(checked((int)chunk.OffsetIndexOffset), chunk.OffsetIndexLength));
        short saved = reader.EnterStruct();
        long pages = -1;
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id == 2)
            {
                pages = 0;
                int count = reader.ReadListHeader(out _);
                for (int i = 0; i < count; i++)
                {
                    pages += reader.ReadI64();
                }
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct(saved);
        Assert.Equal(total, pages);
        return total.Value;
    }

    /// <summary>A chunk's <c>SizeStatistics</c>: its byte array bytes and its histograms, each null when absent.</summary>
    private static (long? Bytes, long[]? Repetition, long[]? Definition) Sizes(ParquetFile file, int group, int column)
    {
        ColumnChunkMetadata chunk = file.Footer.Chunk(group, column);
        Assert.True(chunk.SizeStatistics.IsPresent);
        ThriftCompactReader reader = new(chunk.SizeStatistics.Of(file.Footer.Bytes));
        short saved = reader.EnterStruct();
        long? total = null;
        long[]? repetition = null;
        long[]? definition = null;
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    total = reader.ReadI64();
                    break;
                case 2:
                case 3:
                    long[] histogram = new long[reader.ReadListHeader(out _)];
                    for (int i = 0; i < histogram.Length; i++)
                    {
                        histogram[i] = reader.ReadI64();
                    }

                    (id == 2 ? ref repetition : ref definition) = histogram;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        return (total, repetition, definition);
    }

    /// <summary>A list of numbers field <paramref name="field"/> of a chunk's column index holds.</summary>
    private static long[] Histograms(ParquetFile file, byte[] bytes, int group, int column, short field)
    {
        ColumnChunkMetadata chunk = file.Footer.Chunk(group, column);
        ThriftCompactReader reader = new(bytes.AsSpan(checked((int)chunk.ColumnIndexOffset), chunk.ColumnIndexLength));
        short saved = reader.EnterStruct();
        List<long> values = [];
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id == field)
            {
                int count = reader.ReadListHeader(out _);
                for (int i = 0; i < count; i++)
                {
                    values.Add(reader.ReadI64());
                }
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct(saved);
        return [.. values];
    }
}
