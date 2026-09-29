// What indexing a written file costs, against its chunks.
//
// A file of `Columns` Int64 columns cut into `Chunks` chunks of one 64-row block each, written in
// memory with an identity, is indexed after the fact into a fragment: a Bloom filter on its first
// column. Every chunk of the pass visits every column, whichever the policy indexes. Run in a
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
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A fragment built over a written file, against the file's chunks.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FragmentChunksBenchmarks
{
    private const int BlockRows = 64;

    private const int Columns = 32;

    /// <summary>Chunks of every column, one block each.</summary>
    [Params(256, 1_024)]
    public int Chunks { get; set; }

    private readonly WritePolicy _policy = WritePolicy.None.For("c0", IndexSpec.Bloom(falsePositivePpm: 10_000));
    private readonly VortexWriteOptions _options = new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 };
    private VortexFile _file = null!;

    /// <summary>Writes and opens the file, and checks its chunks are the ones asked for.</summary>
    [GlobalSetup]
    public void Setup()
    {
        byte[] data = WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(new MemorySegmentSource(data), new VortexOpenOptions(), default).AsTask().GetAwaiter().GetResult();
        int chunks = VortexFileWriter.AppendPlan.ColumnChunks(_file.LayoutTree, 0).Chunks.Count;
        if (chunks != Chunks || Build() == 0)
        {
            throw new InvalidOperationException($"The file has {chunks} chunks, or no fragment was built.");
        }
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The fragment built over the whole file.</summary>
    [Benchmark]
    public int Build() =>
        VortexFileIndexer.BuildFragmentAsync(_file, _policy, new RowRange(0, _file.RowCount), options: _options)
            .AsTask().GetAwaiter().GetResult().Bytes.Length;

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
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = BlockRows,
            ChunkTargetBytes = 1,
            Identity = Guid.NewGuid(),
        };
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
                    Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                    for (int i = 0; i < BlockRows; i++)
                    {
                        long row = ((long)chunk * BlockRows) + i;
                        values[i] = (row * 2_654_435_761L * (c + 1)) & 0xFFFF_FFFFL;
                    }

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
