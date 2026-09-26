// A chunk of views moved into a concatenated column: each view copied, and each that references a
// data buffer given the index that buffer takes among the concatenated ones. The loop under every
// scan of a chunked text column whose chunks are all valid.
//
// Half the views are inline and half reference one of four buffers, in no order, so the rebase
// cannot be predicted view by view.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks;

/// <summary><c>CanonicalConcat.RebaseInto</c> over 65 536 views.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class RebaseViewsBenchmarks
{
    private const int Rows = 1 << 16;

    private const int ViewSize = 16;

    private byte[] _source = [];
    private byte[] _block = [];

    /// <summary>What one invocation reads and writes: a view per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 32L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _source = new byte[Rows * ViewSize];
        _block = new byte[Rows * ViewSize];
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> view = _source.AsSpan(i * ViewSize, ViewSize);
            random.NextBytes(view);
            bool inline = random.Next(2) == 0;
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)(inline ? random.Next(13) : random.Next(13, 200)));
            if (!inline)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..], (uint)random.Next(4));
            }
        }
    }

    [Benchmark(Description = "rebase views")]
    public byte Rebase()
    {
        CanonicalConcat.RebaseInto(_source, _block, Rows, bufferBase: 7, dataBufferCount: 4, chunkIndex: 1);
        return _block[^1];
    }
}
