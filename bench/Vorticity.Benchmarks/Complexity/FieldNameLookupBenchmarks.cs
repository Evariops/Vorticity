// What finding a column by its name costs, against the columns of the schema.
//
// A schema of `Fields` Int64 columns named column_00000 onwards. Every column is found once by its
// name: through the schema, through the view of a batch read from a file of that schema, and
// through the columns builder of a writer, as code that reaches each column by name does. Run in a
// checkout of the original and in the tree.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Columns found by name, against the schema's columns.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FieldNameLookupBenchmarks
{
    /// <summary>Columns of the schema.</summary>
    [Params(8, 16, 256, 4_096)]
    public int Fields { get; set; }

    private string[] _names = null!;
    private VortexSchema _schema = null!;
    private VortexFile _file = null!;
    private RecordBatch _batch = null!;
    private VortexFileWriter _writer = null!;
    private ColumnsBuilder _builder = null!;

    /// <summary>Builds the schema, the batch and the writer, and checks every name is found where it is.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _names = new string[Fields];
        DTypeArena types = new DTypeArena();
        DType[] fields = new DType[Fields];
        for (int i = 0; i < Fields; i++)
        {
            _names[i] = $"column_{i:D5}";
            fields[i] = types.Primitive(PType.I64, Nullability.NonNullable);
        }

        DType schema = types.Struct(_names, fields, Nullability.NonNullable);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(WriteAsync(schema).GetAwaiter().GetResult()), new VortexOpenOptions(), default)
            .AsTask().GetAwaiter().GetResult();
        _schema = _file.Schema;
        _batch = FirstAsync().GetAwaiter().GetResult();
        _writer = VortexFileWriter.Create(new StreamSegmentSink(new MemoryStream()), schema, new VortexWriteOptions());
        _builder = _writer.Builder();
        long expected = (long)Fields * (Fields - 1) / 2;
        if (Schema() != expected || Batch() != Fields || Builder() != Fields)
        {
            throw new InvalidOperationException("A name was not found where its column is.");
        }
    }

    /// <summary>Releases the batch, the file and the writer.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _batch.Dispose();
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every column found by name in the schema.</summary>
    [Benchmark]
    public long Schema()
    {
        long sum = 0;
        foreach (string name in _names)
        {
            sum += _schema.IndexOf(name);
        }

        return sum;
    }

    /// <summary>Every column of a batch found by name.</summary>
    [Benchmark]
    public long Batch()
    {
        long rows = 0;
        BatchView view = _batch.View;
        foreach (string name in _names)
        {
            rows += view.Column<long>(name).Length;
        }

        return rows;
    }

    /// <summary>Every column of a writer's builder found by name.</summary>
    [Benchmark]
    public long Builder()
    {
        long found = 0;
        foreach (string name in _names)
        {
            _ = _builder.Column<long>(name);
            found++;
        }

        return found;
    }

    private async Task<RecordBatch> FirstAsync()
    {
        await foreach (RecordBatch batch in _file.ScanBuilder().ExecuteAsync().ConfigureAwait(false))
        {
            return batch.View.ToOwned();
        }

        throw new InvalidOperationException("The file holds no batch.");
    }

    private async Task<byte[]> WriteAsync(DType schema)
    {
        CanonicalArena arena = new CanonicalArena();
        int[] columns = new int[Fields];
        for (int i = 0; i < Fields; i++)
        {
            VortexBuffer buffer = arena.Allocate(sizeof(long), sizeof(long), out Span<byte> bytes);
            MemoryMarshal.Cast<byte, long>(bytes)[0] = i;
            columns[i] = arena.AddPrimitive(schema.GetField(i), 1, Validity.NonNullable, PType.I64, buffer);
        }

        MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, new VortexWriteOptions()))
        {
            using (RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, 1, Validity.NonNullable, columns), 0))
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return stream.ToArray();
    }
}
