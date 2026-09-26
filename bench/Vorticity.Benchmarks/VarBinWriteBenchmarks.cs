// A text column written as `vortex.varbin`, the serialization the writer tries for every text
// column it does not compress: the values' sizes summed, the values gathered into one heap, and
// an offset a row written at the width the heap needs.
//
// 65 536 values: short ones, all held in their views, or of 0 to 40 bytes, a little over half of
// them out of line. The node is added to a work arena at each invocation, over views and a heap
// built once, and the arena is cleared after, since the write allocates the offsets in it.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Editions;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>ArrayBlobWriter.Write</c> of an uncompressed text column of 65 536 rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class VarBinWriteBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("short", "mixed")]
    public string Shape { get; set; } = "short";

    private readonly CanonicalArena _source = new CanonicalArena();
    private readonly CanonicalArena _work = new CanonicalArena();
    private readonly ArrayBlobWriter.Workspace _workspace = new ArrayBlobWriter.Workspace();
    private readonly EncodingDictionary _encodings = new EncodingDictionary(ComponentKind.Array, EditionRegistry.Newest);
    private VortexBuffer _views;
    private VortexBuffer _heap;
    private DType _utf8;

    /// <summary>What one invocation reads: a view per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 16L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        int longest = Shape == "short" ? 12 : 40;
        int[] sizes = new int[Rows];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            sizes[i] = random.Next(longest + 1);
            heapBytes += sizes[i] > 12 ? sizes[i] : 0;
        }

        _views = _source.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        _heap = _source.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
        random.NextBytes(heapSpan);
        int at = 0;
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)sizes[i]);
            if (sizes[i] <= 12)
            {
                random.NextBytes(view.Slice(4, sizes[i]));
                continue;
            }

            heapSpan.Slice(at, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)at);
            at += sizes[i];
        }

        _utf8 = new DTypeArena().Binary(Nullability.NonNullable);
    }

    [GlobalCleanup]
    public void Cleanup() => _workspace.Dispose();

    [Benchmark(Description = "varbin write")]
    public int Write()
    {
        int node = _work.AddVarBinView(_utf8, Rows, Validity.NonNullable, _views, [_heap]);
        using ArrayBlobWriter.BlobLease lease = ArrayBlobWriter.Write(_workspace, _work, node, _encodings);
        int length = lease.Length;
        _work.Reset();
        return length;
    }
}
