// Audit SC-02: what a wide scan over a source whose reads copy pays for the segments its lanes share.
//
// A file of 64 integer columns and 1,048,576 rows, in blocks of 8,192, is put in an in-memory object store and opened
// through it, so that every segment a batch asks for is a read the scan's shared set holds for the batches after it.
// Every column is read and its values consumed, at one lane and at sixteen: each batch claims, publishes and releases
// its 64 segments among those the batches in flight hold.
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A scan of every column of a wide file read through an object store, against its lanes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditScanSegmentsBenchmarks
{
    private const int Columns = 64;
    private const int Rows = 1 << 20;
    private const string Key = "data/audit-wide.vortex";

    /// <summary>The scan's lanes.</summary>
    [Params(1, 16)]
    public int Degree { get; set; }

    private MemoryObjectStore _store = null!;
    private ObjectStoreSource _source = null!;
    private VortexFile _file = null!;

    /// <summary>Writes the file into the store and opens it there.</summary>
    [GlobalSetup]
    public void Setup()
    {
        byte[] bytes = WriteAsync().GetAwaiter().GetResult();
        _store = new MemoryObjectStore();
        _store.PutAsync(Key, PipeReader.Create(new ReadOnlySequence<byte>(bytes)), bytes.Length, PutCondition.None, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        _source = new ObjectStoreSource(_store, Key, bytes.Length);
        _file = VortexFile.OpenAsync(_source, new VortexOpenOptions { LeaveSourceOpen = true }).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {bytes.Length} bytes, {Scan()} rows read");
    }

    /// <summary>Closes the file and the store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every column of every row, read.</summary>
    [Benchmark]
    public long Scan() => ScanAsync().GetAwaiter().GetResult();

    private async Task<long> ScanAsync()
    {
        long rows = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(Degree).ExecuteAsync().ConfigureAwait(false))
        {
            using (batch)
            {
                rows += Vorticity.Bench.Scenarios.Consume.Batch(batch);
            }
        }

        return rows;
    }

    private static async Task<byte[]> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        int[] nodes = new int[Columns];
        for (int c = 0; c < Columns; c++)
        {
            names[c] = $"c{c:D2}";
            fields[c] = i64;
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = (row * (c + 1L)) + ((row * 2_654_435_761L) & 1023);
            }

            nodes[c] = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, nodes);
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream, ownsStream: false), schema, new VortexWriteOptions { RowBlockSize = 8_192 }))
        {
            using RecordBatch batch = new RecordBatch(arena, root, 0);
            await writer.WriteAsync(batch).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return stream.ToArray();
    }
}
