// The run expansion `vortex.runend` is, as x64 ran it and as it runs now, in ONE process.
//
// The loop the library shipped was measured on an Apple M4 Pro, where 256-bit vectors are not
// accelerated: there a run took four 128-bit stores a step in a loop that called nothing. On x64
// that loop was never reached -- it was taken only without 256-bit vectors -- and a run went through
// the general one: a store a step, a bound tested a run, and for sixteen-byte values, which no
// 256-bit vector of that element type exists for, a span fill a run. The `Before` arms are that
// loop, copied; the `After` arms call what ships.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>The run expansion before and after, on runs as short as a taxi trip's and as long as ClickBench's.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public unsafe class RunEndKernelBenchmarks
{
    private ushort[] _ends = [];
    private ulong[] _values8 = [];
    private Vector128<byte>[] _values16 = [];
    // Native and aligned, as the blocks a decode writes are: a managed array lies where the GC puts it.
    private ulong* _rows8;
    private Vector128<byte>* _rows16;

    /// <summary>Rows a window expands: a taxi chunk.</summary>
    [Params(65_536)]
    public int Rows { get; set; }

    /// <summary>The run lengths, drawn uniformly between the two: short is the writer's floor, four rows a run on average and more.</summary>
    [ParamsSource(nameof(Lengths))]
    public string Length { get; set; } = "4-12";

    /// <summary>The run lengths the current profile measures.</summary>
    public static IEnumerable<string> Lengths => BenchmarkConfig.Full ? ["1-8", "4-12", "16-64", "200-1000"] : ["4-12", "200-1000"];

    /// <summary>What one invocation moves: the window's rows, at the arm's width.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        long rows = parameters.TryGetValue(nameof(Rows), out object? value) && value is int count ? count : 65_536;
        return (rows, rows * (method.EndsWith("16", StringComparison.Ordinal) ? 16 : 8));
    }

    [GlobalSetup]
    public void Setup()
    {
        string[] bounds = Length.Split('-');
        int shortest = int.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
        int longest = int.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture);
        Random random = new Random(20261011);
        List<ushort> ends = [];
        int at = 0;
        while (at < Rows)
        {
            at = Math.Min(Rows, at + random.Next(shortest, longest + 1));
            ends.Add((ushort)at);
        }

        _ends = [.. ends];
        _values8 = new ulong[_ends.Length];
        random.NextBytes(MemoryMarshal.AsBytes(_values8.AsSpan()));
        _values16 = new Vector128<byte>[_ends.Length];
        random.NextBytes(MemoryMarshal.AsBytes(_values16.AsSpan()));
        _rows8 = (ulong*)NativeMemory.AlignedAlloc((nuint)Rows * sizeof(ulong), 64);
        _rows16 = (Vector128<byte>*)NativeMemory.AlignedAlloc((nuint)Rows * 16, 64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_rows8);
        NativeMemory.AlignedFree(_rows16);
    }

    [Benchmark(Baseline = true)]
    public int Before8() => Before<ulong>(_ends, _values8, new Span<ulong>(_rows8, Rows));

    [Benchmark]
    public int After8() => RunKernels.Expand<ushort, ulong>(_ends, _values8, 0, 0, new Span<ulong>(_rows8, Rows));

    [Benchmark]
    public int Before16() => Filled<Vector128<byte>>(_ends, _values16, new Span<Vector128<byte>>(_rows16, Rows));

    [Benchmark]
    public int After16() => RunKernels.Expand<ushort, Vector128<byte>>(_ends, _values16, 0, 0, new Span<Vector128<byte>>(_rows16, Rows));

    /// <summary>The general loop as x64 ran it for a value of 1, 2, 4 or 8 bytes, untracked.</summary>
    private static int Before<TValue>(ushort[] ends, TValue[] values, Span<TValue> rows)
        where TValue : unmanaged
    {
        Span<TValue> destination = rows;
        ref TValue into = ref MemoryMarshal.GetReference(destination);
        int length = rows.Length;
        ulong unsignedLength = (ulong)(uint)length;
        int lanes = Vector256<TValue>.Count;
        int position = 0;
        for (int run = 0; run < ends.Length && position < length; run++)
        {
            ulong end = ends[run];
            if (end > unsignedLength)
            {
                end = unsignedLength;
            }

            int endRow = (int)end;
            if (endRow <= position)
            {
                continue;
            }

            int count = endRow - position;
            if (position + count + lanes - 1 <= length)
            {
                Vector256<TValue> repeated = Vector256.Create(values[run]);
                for (int k = 0; k < count; k += lanes)
                {
                    repeated.StoreUnsafe(ref into, (nuint)(position + k));
                }
            }
            else
            {
                destination[position..endRow].Fill(values[run]);
            }

            position = endRow;
        }

        return position;
    }

    /// <summary>The fill a run x64 took for a sixteen-byte value.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Filled<TValue>(ushort[] ends, TValue[] values, Span<TValue> rows)
        where TValue : unmanaged
    {
        Span<TValue> destination = rows;
        ulong unsignedLength = (ulong)(uint)rows.Length;
        int position = 0;
        for (int run = 0; run < ends.Length && position < rows.Length; run++)
        {
            ulong end = ends[run];
            if (end > unsignedLength)
            {
                end = unsignedLength;
            }

            int endRow = (int)end;
            if (endRow <= position)
            {
                continue;
            }

            destination[position..endRow].Fill(values[run]);
            position = endRow;
        }

        return position;
    }
}
