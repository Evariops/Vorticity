// What learning how a written chunk is encoded costs, against the chunk's bytes.
//
// A file of 8 Int64 columns of random 40-bit values, bit-packed, in 16 chunks of `ChunkRows` rows,
// so that a chunk's segment is 5 bytes a row, read from memory through a source that copies what
// it serves, as a store does, and counts it. `Seeds` asks, for the last chunk of each column, what
// an append would continue it with; `Fragment` builds a Bloom fragment over the first column, whose
// dictionary probe asks the same of every chunk of every column before the scan reads them. The
// setup prints the bytes each reads. Run in a checkout of the original and in the tree.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A written chunk's encoding learnt, against the chunk's bytes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ChunkTreeReadBenchmarks
{
    private const int Columns = 8;

    private const int Chunks = 16;

    /// <summary>Rows of every chunk.</summary>
    [Params(4_096, 65_536)]
    public int ChunkRows { get; set; }

    private readonly WritePolicy _policy = WritePolicy.None.For("c0", IndexSpec.Bloom(falsePositivePpm: 10_000));
    private readonly VortexWriteOptions _options = new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 };
    private CountingSegmentSource _source = null!;
    private VortexFile _file = null!;
    private readonly ArrayNodeArena _tree = new ArrayNodeArena();
    private LayoutNode[] _last = null!;
    private DType[] _types = null!;

    /// <summary>Writes the file, opens it, checks every column's last chunk has a seed, and prints the bytes read.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _source = new CountingSegmentSource(new MemorySegmentSource(WriteAsync().GetAwaiter().GetResult()));
        _file = VortexFile.OpenAsync(_source, new VortexOpenOptions(), default).AsTask().GetAwaiter().GetResult();
        _last = new LayoutNode[Columns];
        _types = new DType[Columns];
        for (int c = 0; c < Columns; c++)
        {
            System.Collections.Generic.List<(LayoutNode Flat, long Start)> chunks =
                VortexFileWriter.AppendPlan.ColumnChunks(_file.LayoutTree, c).Chunks;
            if (chunks.Count != Chunks)
            {
                throw new InvalidOperationException($"Column {c} has {chunks.Count} chunks.");
            }

            _last[c] = chunks[^1].Flat;
            _types[c] = _file.DType.GetField(c);
        }

        _source.Reset();
        int seeded = Seeds();
        long seedBytes = _source.Bytes;
        _source.Reset();
        int fragment = Fragment();
        Console.WriteLine($"// {ChunkRows} rows a chunk: seeds read {seedBytes} bytes, the fragment {_source.Bytes}");
        if (seeded != Columns || fragment == 0)
        {
            throw new InvalidOperationException("A column's last chunk has no seed, or no fragment was built.");
        }
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>What each column's last chunk was written as.</summary>
    [Benchmark]
    public int Seeds() => SeedsAsync().GetAwaiter().GetResult();

    /// <summary>A Bloom fragment over the first column, the whole file read.</summary>
    [Benchmark]
    public int Fragment() =>
        VortexFileIndexer.BuildFragmentAsync(_file, _policy, new RowRange(0, _file.RowCount), options: _options)
            .AsTask().GetAwaiter().GetResult().Bytes.Length;

    private async Task<int> SeedsAsync()
    {
        int seeded = 0;
        for (int c = 0; c < Columns; c++)
        {
            seeded += await VortexFileWriter.AppendPlan.SeedAsync(_file, _last[c], _types[c], _tree, CancellationToken.None).ConfigureAwait(false)
                is null ? 0 : 1;
        }

        return seeded;
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
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = 4_096,
            ChunkTargetBytes = 1,
            Identity = Guid.NewGuid(),
        };
        MemoryStream stream = new MemoryStream();
        await using VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, options);
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
        return stream.ToArray();
    }
}
