// Chunks of a list-view column concatenated, as a scan does for a chunked list column: the elements
// concatenated, and each row's offset moved past the elements of the chunks before it and widened
// with its size to 64 bits.
//
// Sixteen chunks of 4 096 rows, offsets and sizes 32 bits wide, two elements a row on average. The
// chunk nodes are added to the scan's arena at each invocation, over buffers built once, and the
// arena is cleared after.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>CanonicalConcat.Concat</c> of 16 list-view chunks, 65 536 rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ConcatListViewBenchmarks
{
    private const int Chunks = 16;

    private const int ChunkRows = 4096;

    private readonly CanonicalArena _source = new CanonicalArena();
    private readonly VortexBuffer[] _offsets = new VortexBuffer[Chunks];
    private readonly VortexBuffer[] _sizes = new VortexBuffer[Chunks];
    private readonly VortexBuffer[] _elements = new VortexBuffer[Chunks];
    private readonly int[] _elementCounts = new int[Chunks];
    private readonly int[] _chunks = new int[Chunks];
    private ScanContext? _scan;
    private DType _list;
    private DType _byte;

    /// <summary>What one invocation writes: a 64-bit offset and size per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Chunks * ChunkRows, Chunks * ChunkRows * 16L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _scan = new ScanContext(["vortex.primitive", "vortex.listview"]);
        _byte = _scan.Types.Primitive(PType.U8, Nullability.NonNullable);
        _list = _scan.Types.List(_byte, Nullability.NonNullable);
        for (int c = 0; c < Chunks; c++)
        {
            _offsets[c] = _source.Allocate(ChunkRows * 4, 4, out Span<byte> offsetBytes);
            _sizes[c] = _source.Allocate(ChunkRows * 4, 4, out Span<byte> sizeBytes);
            Span<int> offsets = MemoryMarshal.Cast<byte, int>(offsetBytes);
            Span<int> sizes = MemoryMarshal.Cast<byte, int>(sizeBytes);
            int at = 0;
            for (int r = 0; r < ChunkRows; r++)
            {
                offsets[r] = at;
                sizes[r] = random.Next(4);
                at += sizes[r];
            }

            _elementCounts[c] = at;
            _elements[c] = _source.Allocate(Math.Max(at, 1), 1, out Span<byte> elementBytes);
            random.NextBytes(elementBytes);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _scan?.Dispose();

    [Benchmark(Description = "concat list views")]
    public int Concat()
    {
        ScanContext scan = _scan!;
        CanonicalArena arena = scan.Canonical;
        for (int c = 0; c < Chunks; c++)
        {
            int elements = arena.AddPrimitive(_byte, _elementCounts[c], Validity.NonNullable, PType.U8, _elements[c].Slice(0, _elementCounts[c]));
            _chunks[c] = arena.AddListView(_list, ChunkRows, Validity.NonNullable, elements, _offsets[c], PType.I32, _sizes[c], PType.I32);
        }

        int node = CanonicalConcat.Concat(scan.Decode, _list, Chunks * ChunkRows, _chunks);
        arena.Reset();
        return node;
    }
}
