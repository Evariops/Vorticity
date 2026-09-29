// What describing a file's layout costs, against the bytes of its data.
//
// A file of 8 Int64 columns of random 40-bit values, bit-packed, in 16 chunks of `ChunkRows` rows,
// read from memory through a source that copies what it serves, as a store does, and counts it.
// Its layout is described with every flat node's array encodings; the setup prints the bytes the
// description reads. Run in a checkout of the original and in the tree.
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
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A file's layout described, against the bytes of its data.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class LayoutInspectionBenchmarks
{
    private const int Columns = 8;

    private const int Chunks = 16;

    /// <summary>Rows of every chunk.</summary>
    [Params(4_096, 65_536)]
    public int ChunkRows { get; set; }

    private CountingSegmentSource _source = null!;
    private VortexFile _file = null!;

    /// <summary>Writes and opens the file, and prints what one description reads.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _source = new CountingSegmentSource(new MemorySegmentSource(WriteAsync().GetAwaiter().GetResult()));
        _file = VortexFile.OpenAsync(_source, new VortexOpenOptions(), default).AsTask().GetAwaiter().GetResult();
        _source.Reset();
        int nodes = Describe();
        Console.WriteLine($"// {ChunkRows} rows a chunk: the description reads {_source.Bytes} bytes");
        if (nodes < Columns * Chunks)
        {
            throw new InvalidOperationException($"The description has {nodes} nodes.");
        }
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The layout described, every flat node's encodings named.</summary>
    [Benchmark]
    public int Describe() => Count(_file.GetLayoutAsync().AsTask().GetAwaiter().GetResult());

    private static int Count(VortexLayout node)
    {
        int nodes = node.ArrayEncoding is null ? 0 : 1;
        foreach (VortexLayout child in node.Children)
        {
            nodes += Count(child);
        }

        return nodes;
    }

    private async Task<byte[]> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        for (int c = 0; c < Columns; c++)
        {
            names[c] = $"c{c}";
            fields[c] = i64;
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = 4_096, ChunkTargetBytes = 1 };
        MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, options))
        {
            // A batch a chunk: each write seals one.
            CanonicalArena arena = new CanonicalArena();
            int[] columns = new int[Columns];
            ulong state = 0x9E37_79B9_7F4A_7C15;
            for (int chunk = 0; chunk < Chunks; chunk++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    VortexBuffer buffer = arena.Allocate(ChunkRows * sizeof(long), sizeof(long), out Span<byte> bytes);
                    Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                    for (int i = 0; i < ChunkRows; i++)
                    {
                        state ^= state << 13;
                        state ^= state >> 7;
                        state ^= state << 17;
                        values[i] = (long)(state >> 24);
                    }

                    columns[c] = arena.AddPrimitive(i64, ChunkRows, Validity.NonNullable, PType.I64, buffer);
                }

                using (RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, ChunkRows, Validity.NonNullable, columns), 0))
                {
                    await writer.WriteAsync(batch).ConfigureAwait(false);
                }

                arena.Reset();
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return stream.ToArray();
    }
}
