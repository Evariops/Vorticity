// A thousand columns whose chunks outlive the batch that reads them.
//
// A chunk larger than a batch is decoded once and borrowed by every batch that overlaps it, and
// the scan context hands the decode back by key. With one such chunk per column the context holds
// a thousand of them at once, which is the size at which a lookup that walks them stops being
// free -- and the size at which a lookup keyed wrongly would hand a column its neighbour's rows
// without anything else noticing.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public class WideRetentionTests
{
    private const int Columns = 1_000;
    private const int Rows = 128;
    private const int BatchRows = 8;

    [Fact]
    public async Task EveryColumnKeepsItsOwnRowsWhenAThousandChunksAreRetainedAtOnce()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-wide-{Guid.NewGuid():N}.vortex");
        try
        {
            await WriteAsync(path);

            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            long seen = 0;
            await foreach (RecordBatch batch in file.Scan()
                .WithMaxBatchRows(BatchRows).ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                using (batch)
                {
                    List<string> wrong = [];
                    for (int column = 0; column < Columns; column++)
                    {
                        ReadOnlySpan<long> values = batch.Column(column).AsPrimitive<long>().Values;
                        for (int row = 0; row < batch.RowCount; row++)
                        {
                            if (values[row] != Value(column, seen + row))
                            {
                                wrong.Add($"{Name(column)}[{seen + row}]");
                            }
                        }
                    }

                    Assert.Empty(wrong);
                    seen += batch.RowCount;
                }
            }

            Assert.Equal(Rows, seen);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A value that names its own column, so a lookup handing back another one shows.</summary>
    private static long Value(int column, long row) => ((long)column * Rows) + row;

    private static string Name(int column) => $"c{column:D4}";

    private static async Task WriteAsync(string path)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        for (int i = 0; i < Columns; i++)
        {
            names[i] = Name(i);
            fields[i] = i64;
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);

        // Uncompressed, so that what the batch borrows is the decoded chunk and not a decoder's
        // own buffer, and one chunk, so that every column outlives every batch.
        VortexWriteOptions options = new VortexWriteOptions { Compress = false };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);

        CanonicalArena arena = new CanonicalArena();
        int[] columns = new int[Columns];
        for (int i = 0; i < Columns; i++)
        {
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = Value(i, row);
            }

            columns[i] = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        }

        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, columns);
        using RecordBatch record = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(record, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
    }
}
