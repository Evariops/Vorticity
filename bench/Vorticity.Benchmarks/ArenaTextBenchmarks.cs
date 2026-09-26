// A text column's views walked by the arena: the heap bytes they name, which the writer asks of
// every text column of every batch to size its partitions, and the compact copy of the column into
// another arena, which the writer's transit of rows pays for the values held out of line.
//
// 65 536 values of 0 to 40 bytes, a little over half of them too long to inline.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>CanonicalArena.NamedBytes</c> and <c>CopyFrom</c> of a varbinview column.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ArenaTextBenchmarks
{
    private const int Rows = 1 << 16;

    private readonly CanonicalArena _source = new CanonicalArena();
    private readonly CanonicalArena _target = new CanonicalArena();
    private int _node;

    /// <summary>What one invocation walks: a view per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 16L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        int[] sizes = new int[Rows];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            sizes[i] = random.Next(41);
            heapBytes += sizes[i] > 12 ? sizes[i] : 0;
        }

        VortexBuffer views = _source.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = _source.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
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

        _node = _source.AddVarBinView(new DTypeArena().Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
    }

    [Benchmark(Description = "named bytes")]
    public long Named() => _source.NamedBytes(_node);

    [Benchmark(Description = "compact copy")]
    public int Copy()
    {
        int copied = _target.CopyFrom(_source, _node);
        _target.Reset();
        return copied;
    }
}
