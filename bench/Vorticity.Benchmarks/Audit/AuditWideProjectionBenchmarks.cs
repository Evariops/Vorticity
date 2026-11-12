// What a projection of many columns costs a batch, its struct's type rebuilt from its fields' names and
// types at every batch.
//
// A file of 262,144 rows of 128 long columns, from memory, at one lane; 64 of them projected, in batches of 1,024 or
// 16,384 rows.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A wide projection read.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditWideProjectionBenchmarks
{
    private const int Rows = 1 << 18;
    private const int Columns = 128;

    private VortexFile _file = null!;
    private string[] _projected = [];

    /// <summary>The rows a batch holds.</summary>
    [Params(1_024, 16_384)]
    public int BatchRows { get; set; }

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        for (int c = 0; c < Columns; c++)
        {
            names[c] = $"column_{c:D3}";
            fields[c] = types.Primitive(PType.I64, Nullability.NonNullable);
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        byte[] bytes = WriteAsync(schema).GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        List<string> projected = [];
        for (int c = 0; c < Columns; c += 2)
        {
            projected.Add($"column_{c:D3}");
        }

        _projected = [.. projected];
        Console.WriteLine($"// {Read()} rows");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The projection read.</summary>
    [Benchmark]
    public long Read() => ReadAsync().GetAwaiter().GetResult();

    private async Task<long> ReadAsync()
    {
        long rows = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder().Project(_projected).WithDegreeOfParallelism(1).WithMaxBatchRows(BatchRows).ExecuteAsync().ConfigureAwait(false))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<byte[]> WriteAsync(DType schema)
    {
        Random random = new Random(1024);
        const int Chunk = 16_384;
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, new VortexWriteOptions()))
        {
            for (int start = 0; start < Rows; start += Chunk)
            {
                CanonicalArena arena = new CanonicalArena();
                int[] columns = new int[Columns];
                for (int c = 0; c < Columns; c++)
                {
                    VortexBuffer buffer = arena.Allocate(Chunk * sizeof(long), sizeof(long), out Span<byte> bytes);
                    Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                    for (int i = 0; i < Chunk; i++)
                    {
                        values[i] = random.NextInt64(1_000_000);
                    }

                    columns[c] = arena.AddPrimitive(schema.GetField(c), Chunk, Validity.NonNullable, PType.I64, buffer);
                }

                using RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, Chunk, Validity.NonNullable, columns), start);
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return stream.ToArray();
    }
}
