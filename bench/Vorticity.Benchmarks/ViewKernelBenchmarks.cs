// The three view kernels against the per-row loops they replaced, in ONE process.
//
// BENCH-AUDIT.md §3.2: `ViewKernels` was written against the 1M throughput gate alone, and its
// header carries the sharpest claim in the repository -- that calling `Utf8.IsValid` on a
// five-byte span a million times is dominated by the call and by a vector loop's set-up, so
// ONE pass over the whole heap plus one byte test per row is equivalent and much cheaper. That is
// an argument, and it deserved a number.
//
// The scalar arms are the shapes the header names: a length read through `ReadInteger`'s switch and
// added to a scalar; a `Utf8.IsValid` call per row before a `WriteView` through two freshly sliced
// spans; a compare of consecutive offsets read the same way. `CanonicalSupport.WriteView` is used
// by BOTH arms -- the view's byte layout is not what is being compared, and rewriting it here would
// only add a way to be wrong.
//
// ALIGNMENT MEASURED, NOT ASSUMED: these buffers are plain GC arrays, and §4.4 asked whether that
// changes the number against the library's aligned arena allocations. `AlignmentBenchmarks`
// (`--explore Alignment`) answers on M4 Pro: eight bytes past a 64-byte boundary costs 0.3% to
// 1.8% over two runs, the same sign every time but under the fast profile's own +-3% fidelity.
// Not re-measured on x64.
//
// 65 536 rows of 8 bytes: a 512 KiB heap, which is the size at which the per-row call is the cost
// and not the memory. Eight bytes is also under the twelve that a view inlines, so the rows take
// the inline branch -- the one `vortex.fsst` takes for short strings, and the one where the
// per-row overhead has the least other work to hide behind.
using System;
using System.Runtime.InteropServices;
using System.Text.Unicode;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Length sums, view building and offset checking, against the per-row loops.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ViewKernelBenchmarks
{
    /// <summary>Rows per operation.</summary>
    private const int Rows = 1 << 16;

    /// <summary>Bytes per row: under the inline limit, as a short string is.</summary>
    private const int Width = 8;

    /// <summary>Bytes in one Arrow view.</summary>
    private const int ViewSize = 16;

    private const PType LengthsType = PType.U32;

    private byte[] _lengths = [];
    private byte[] _offsets = [];
    private byte[] _heap = [];
    private byte[] _views = [];

    [GlobalSetup]
    public void Setup()
    {
        _lengths = new byte[Rows * sizeof(uint)];
        Span<uint> lengths = MemoryMarshal.Cast<byte, uint>(_lengths);
        lengths.Fill(Width);

        _offsets = new byte[(Rows + 1) * sizeof(uint)];
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = (uint)(i * Width);
        }

        // ASCII, so both arms take the same branches: the heap is valid UTF-8 and every row starts
        // on a code-point boundary. A heap with multi-byte sequences would measure the same paths.
        _heap = new byte[Rows * Width];
        for (int i = 0; i < _heap.Length; i++)
        {
            _heap[i] = (byte)('a' + (i % 26));
        }

        _views = new byte[Rows * ViewSize];
    }

    [BenchmarkCategory("sum")]
    [Benchmark(Baseline = true, Description = "sum lengths, switch per row")]
    public long SumScalar() => SumLengthsPerRow(_lengths, LengthsType, Rows);

    [BenchmarkCategory("sum")]
    [Benchmark(Description = "sum lengths, library")]
    public long SumLibrary() => ViewKernels.SumLengths(_lengths, LengthsType, [], Rows).Total;

    [BenchmarkCategory("build")]
    [Benchmark(Baseline = true, Description = "build views, IsValid per row")]
    public bool BuildScalar() =>
        BuildPerRow(_lengths, LengthsType, _heap, _views, Rows, requireUtf8: true);

    [BenchmarkCategory("build")]
    [Benchmark(Description = "build views, library")]
    public bool BuildLibrary() =>
        ViewKernels.BuildFromLengths(
            _lengths, LengthsType, [], _heap, _views, Rows, requireUtf8: true);

    [BenchmarkCategory("ascending")]
    [Benchmark(Baseline = true, Description = "require ascending, switch per row")]
    public int AscendingScalar() => AscendingPerRow(_offsets, LengthsType, Rows);

    [BenchmarkCategory("ascending")]
    [Benchmark(Description = "require ascending, library")]
    public int AscendingLibrary()
    {
        ViewKernels.RequireAscending(_offsets, LengthsType, Rows, "bench");
        return Rows;
    }

    /// <summary>`SumLengths` as it was: `ReadInteger`'s switch per row, added to a scalar.</summary>
    private static long SumLengthsPerRow(ReadOnlySpan<byte> lengths, PType ptype, int count)
    {
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            total += CanonicalSupport.ReadInteger(lengths, ptype, i);
        }

        return total;
    }

    /// <summary>
    /// `BuildFromLengths` as it was: `Utf8.IsValid` on every row's bytes, through two spans sliced
    /// per row.
    /// </summary>
    private static bool BuildPerRow(
        ReadOnlySpan<byte> lengths, PType ptype, ReadOnlySpan<byte> heap, Span<byte> views,
        int count, bool requireUtf8)
    {
        bool referenced = false;
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            int size = (int)CanonicalSupport.ReadInteger(lengths, ptype, i);
            ReadOnlySpan<byte> value = heap.Slice(offset, size);
            if (requireUtf8 && !Utf8.IsValid(value))
            {
                throw new InvalidOperationException($"row {i} is not valid UTF-8");
            }

            referenced |= CanonicalSupport.WriteView(
                views.Slice(i * ViewSize, ViewSize), value, size, 0, offset);
            offset += size;
        }

        return referenced;
    }

    /// <summary>`RequireAscending` as it was: a compare of two switch-read offsets per row.</summary>
    private static int AscendingPerRow(ReadOnlySpan<byte> offsets, PType ptype, int count)
    {
        long previous = CanonicalSupport.ReadInteger(offsets, ptype, 0);
        for (int i = 1; i <= count; i++)
        {
            long current = CanonicalSupport.ReadInteger(offsets, ptype, i);
            if (current < previous)
            {
                return i;
            }

            previous = current;
        }

        return count;
    }
}
