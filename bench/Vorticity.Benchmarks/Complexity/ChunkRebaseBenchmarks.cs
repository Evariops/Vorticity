// What a chunked layout pays to hand each chunk the selected rows that fall in it.
//
// One execution of a chunked node over a range of K chunks, with S selected rows spread over them.
// `Original` is the re-basing as it was, below: the whole selection moved into each chunk's row
// space and filtered by its bounds, once per chunk. `Library` takes each chunk's rows as the next
// run of the ascending selection and moves only those.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using BenchmarkDotNet.Attributes;

using Vorticity.Layouts;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>The re-basing of one selection over the chunks of one range, against their count.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ChunkRebaseBenchmarks
{
    /// <summary>The chunks the range covers.</summary>
    [Params(1, 4, 16, 64)]
    public int Chunks { get; set; }

    /// <summary>The selected rows, spread evenly over the range.</summary>
    [Params(1_024, 16_384)]
    public int Selected { get; set; }

    /// <summary>The rows of the range.</summary>
    private const int Rows = 65_536;

    private long[] _offsets = [];
    private int[] _selection = [];
    private int[] _into = [];

    /// <summary>Builds the chunk offsets and the selection.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _offsets = new long[Chunks + 1];
        for (int i = 0; i <= Chunks; i++)
        {
            _offsets[i] = (long)i * Rows / Chunks;
        }

        _selection = new int[Selected];
        for (int i = 0; i < Selected; i++)
        {
            _selection[i] = (int)((long)i * Rows / Selected);
        }

        _into = new int[Selected];
    }

    /// <summary>The whole selection re-based and filtered for each chunk.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        int total = 0;
        for (int i = 0; i < Chunks; i++)
        {
            total += OriginalRebase(_selection, _offsets[i], _offsets[i + 1] - _offsets[i], _into);
        }

        return total;
    }

    /// <summary>Each chunk's run of the selection, found and moved alone.</summary>
    [Benchmark]
    public int Library()
    {
        int total = 0;
        int taken = 0;
        for (int i = 0; i < Chunks; i++)
        {
            ReadOnlySpan<int> run = ChunkedLayoutReader.NextRun(_selection, ref taken, _offsets[i], _offsets[i + 1]);
            ChunkedLayoutReader.Shift(run, _offsets[i], _into);
            total += run.Length;
        }

        return total;
    }

    /// <summary>The re-basing as it was, copied from the reader.</summary>
    private static int OriginalRebase(ReadOnlySpan<int> selection, long start, long rows, Span<int> into)
    {
        if (start > int.MaxValue)
        {
            return 0;
        }

        int origin = (int)start;
        uint limit = (uint)Math.Min(rows, uint.MaxValue);
        Span<int> slots = into[..selection.Length];
        int i = 0;
        if (Vector128.IsHardwareAccelerated && selection.Length >= Vector128<int>.Count)
        {
            Vector128<int> shift = Vector128.Create(origin);
            Vector128<uint> bound = Vector128.Create(limit);
            Vector128<uint> outside = Vector128<uint>.Zero;
            ref int from = ref MemoryMarshal.GetReference(selection);
            ref int to = ref MemoryMarshal.GetReference(slots);
            for (; i <= selection.Length - Vector128<int>.Count; i += Vector128<int>.Count)
            {
                Vector128<int> local = Vector128.LoadUnsafe(ref from, (nuint)i) - shift;
                local.StoreUnsafe(ref to, (nuint)i);
                outside |= Vector128.GreaterThanOrEqual(local.AsUInt32(), bound);
            }

            if (outside != Vector128<uint>.Zero)
            {
                i = 0;
            }
        }

        int count = i;
        for (; i < selection.Length; i++)
        {
            int local = selection[i] - origin;
            slots[count] = local;
            count += (uint)local < limit ? 1 : 0;
        }

        return count;
    }
}
