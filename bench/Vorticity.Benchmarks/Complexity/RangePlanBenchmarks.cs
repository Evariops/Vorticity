// What planning a scan of a narrow row range costs, against the chunks of the file.
//
// A file of `Columns` Int64 columns cut into `Chunks` chunks of one 64-row block each is planned
// over each of its chunks in turn, every column projected, as an indexer that scans a file chunk by
// chunk plans it. Run in a checkout of the original and in the tree.
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
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A scan's split plan over one chunk's rows, against the file's chunks.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class RangePlanBenchmarks
{
    private const int BlockRows = 64;

    private const int Columns = 32;

    /// <summary>Chunks of every column, one block each.</summary>
    [Params(256, 4_096)]
    public int Chunks { get; set; }

    private VortexFile _file = null!;

    /// <summary>Writes and opens the file, and checks a plan over a chunk splits nothing more.</summary>
    [GlobalSetup]
    public void Setup()
    {
        byte[] data = WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(new MemorySegmentSource(data), new VortexOpenOptions(), default).AsTask().GetAwaiter().GetResult();
        if (Plans() != 2L * Chunks)
        {
            throw new InvalidOperationException("A plan over one chunk's rows is not that chunk alone.");
        }
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>A plan over each chunk's rows in turn.</summary>
    [Benchmark]
    public long Plans()
    {
        long boundaries = 0;
        for (int c = 0; c < Chunks; c++)
        {
            RowRange rows = new RowRange((long)c * BlockRows, (long)(c + 1) * BlockRows);
            boundaries += SplitPlan.Compute(_file.LayoutTree, rows, FieldMask.All, 1 << 16).BoundaryCount;
        }

        return boundaries;
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
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = BlockRows, ChunkTargetBytes = 1 };
        MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, options))
        {
            // A batch a block: each write seals a chunk.
            CanonicalArena arena = new CanonicalArena();
            int[] columns = new int[Columns];
            for (int chunk = 0; chunk < Chunks; chunk++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    VortexBuffer buffer = arena.Allocate(BlockRows * sizeof(long), sizeof(long), out Span<byte> bytes);
                    MemoryMarshal.Cast<byte, long>(bytes).Fill(((long)chunk * Columns) + c);
                    columns[c] = arena.AddPrimitive(i64, BlockRows, Validity.NonNullable, PType.I64, buffer);
                }

                using (RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, BlockRows, Validity.NonNullable, columns), 0))
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
