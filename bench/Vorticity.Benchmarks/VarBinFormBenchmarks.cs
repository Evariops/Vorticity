// A probe of Arrow VIEWS against an OFFSETS canonical form, on the three arms that decide it.
//
// The proposal is to replace `CanonicalKind.VarBinView` with an offsets-plus-heap form for Binary
// and Utf8, under one condition: the consumer arm must not lose more than the scan arm gains, AND
// the take arm must be neutral; without both, the change is not made. That is a comparison of
// three numbers, and none of the three existed.
//
// WHY THE KERNEL AND NOT THE FILE. The proposal asks for an internal switch on `parquet_variant`
// and `fsst`, which means writing the offsets form first -- that IS the change, not its probe.
// What the decision actually turns on is per-row arithmetic over two memory layouts, and the
// project's rule is that such a thing is measured at the kernel and never end to end. The scan
// arm's END of the range is already known from the file: short-circuiting view construction takes
// a 1M-row `parquet_variant` scan from 4 262 us to 568 and an `fsst` one from 8 623 to 5 503, so
// the offsets form can save at most 87% and 36% of those scans respectively. This class supplies
// what that measurement cannot: what the other two arms COST.
//
// THE TWO WIDTHS ARE THE WHOLE POINT, not a sweep. A view carries values of twelve bytes or fewer
// INLINE, so a short-string consumer never touches the heap while an offsets consumer always does;
// past twelve, both go to the heap and the view is pure overhead. Eight bytes and twenty-four
// bracket that boundary, and the answer is expected to change sign across it.
//
// THE TAKE ARM OUTPUTS VIEWS FROM BOTH FORMS, because the proposal fixes that as a constraint:
// "a filter's or a take's output stays VarBinView whatever the input", or the take and filter
// axes (0.23 to 0.32 against the reference) regress. So the offsets arm measures a gather THROUGH
// the heap into freshly built views, which is the real cost the change would introduce.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Views against offsets, on the build, consume and take arms. A probe: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class VarBinFormBenchmarks
{
    /// <summary>Rows per operation, as <see cref="ViewKernelBenchmarks"/> uses.</summary>
    private const int Rows = 1 << 16;

    /// <summary>Bytes in one Arrow view.</summary>
    private const int ViewSize = 16;

    /// <summary>Rows a scattered take asks for, matching `--throughput --take`.</summary>
    private const int TakeRows = 64;

    private const PType OffsetsType = PType.U32;

    /// <summary>
    /// Bytes per value. 8 is inline in a view and 24 is not, which is where the answer changes sign.
    /// </summary>
    [Params(8, 24)]
    public int Width { get; set; } = 8;

    private byte[] _lengths = [];
    private byte[] _offsets = [];
    private byte[] _heap = [];
    private byte[] _views = [];
    private byte[] _taken = [];
    private int[] _wanted = [];

    /// <summary>What one invocation moves, per arm.</summary>
    /// <param name="method">The benchmark method's name.</param>
    /// <param name="parameters">The case's <c>[Params]</c>, which carry <see cref="Width"/>.</param>
    /// <returns>Rows and bytes, for the ns/row and GB/s columns.</returns>
    /// <remarks>
    /// THE ARMS MOVE DIFFERENT AMOUNTS and one number for the class would flatter whichever moves
    /// least: building reads the heap and writes sixteen bytes a row, consuming reads a view or two
    /// offsets plus the value, and a take of 64 rows moves 64 rows.
    /// </remarks>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(parameters);
        long width = parameters.TryGetValue(nameof(Width), out object? value) && value is int w ? w : 0;

        return method switch
        {
            nameof(BuildViews) => (Rows, Rows * (width + ViewSize + sizeof(uint))),
            nameof(BuildOffsets) => (Rows, Rows * (2L * sizeof(uint))),
            nameof(ConsumeViews) => (Rows, Rows * (long)ViewSize),
            nameof(ConsumeOffsets) => (Rows, Rows * (2L * sizeof(uint) + width)),
            nameof(TakeViews) => (TakeRows, TakeRows * 2L * ViewSize),
            _ => (TakeRows, TakeRows * (2L * sizeof(uint) + width + ViewSize)),
        };
    }

    [GlobalSetup]
    public void Setup()
    {
        _heap = new byte[Rows * Width];
        for (int i = 0; i < _heap.Length; i++)
        {
            // Plain ASCII: the comparison is memory layout, not UTF-8 validation.
            _heap[i] = (byte)('a' + (i % 26));
        }

        _lengths = new byte[Rows * sizeof(uint)];
        _offsets = new byte[(Rows + 1) * sizeof(uint)];
        Span<uint> lengths = MemoryMarshal.Cast<byte, uint>(_lengths);
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        for (int i = 0; i < Rows; i++)
        {
            lengths[i] = (uint)Width;
            offsets[i] = (uint)(i * Width);
        }

        offsets[Rows] = (uint)(Rows * Width);

        _views = new byte[Rows * ViewSize];
        ViewKernels.BuildFromOffsets(
            _offsets, OffsetsType, _heap, _views, Rows, requireUtf8: false, default, VarBinDecoder.Id);

        _taken = new byte[TakeRows * ViewSize];
        _wanted = new int[TakeRows];
        for (int i = 0; i < TakeRows; i++)
        {
            _wanted[i] = i * (Rows / TakeRows);
        }
    }

    // ------------------------------------------------------------------------------------ build

    /// <summary>What the decoders pay today: sixteen bytes per row, values inlined under 12.</summary>
    [Benchmark(Description = "build, views")]
    [BenchmarkCategory("build")]
    public int BuildViews()
    {
        ViewKernels.BuildFromOffsets(
            _offsets, OffsetsType, _heap, _views, Rows, requireUtf8: false, default, VarBinDecoder.Id);
        return _views.Length;
    }

    /// <summary>
    /// What the offsets form would pay: a prefix sum over the lengths the file already carries.
    /// </summary>
    /// <remarks>
    /// AND FOR `vortex.varbin` EVEN THIS IS FREE, because the offsets ARE the file's second buffer:
    /// the decoder would reference them. This arm is the FSST, OnPair and zstd case, which carry
    /// lengths rather than offsets and so must accumulate once.
    /// </remarks>
    [Benchmark(Description = "build, offsets")]
    [BenchmarkCategory("build")]
    public uint BuildOffsets()
    {
        ReadOnlySpan<uint> lengths = MemoryMarshal.Cast<byte, uint>(_lengths);
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        uint running = 0;
        for (int i = 0; i < Rows; i++)
        {
            offsets[i] = running;
            running += lengths[i];
        }

        offsets[Rows] = running;
        return running;
    }

    // ---------------------------------------------------------------------------------- consume

    /// <summary>
    /// `BinaryColumn.GetSpan` over every row of a view array: one 16-byte load, then a branch.
    /// </summary>
    /// <remarks>
    /// The first byte of each value is summed rather than discarded, so the load cannot be optimized
    /// away. Under the inline limit this NEVER TOUCHES THE HEAP, which is the advantage the offsets
    /// form gives up.
    /// </remarks>
    [Benchmark(Description = "consume GetSpan, views")]
    [BenchmarkCategory("consume")]
    public long ConsumeViews()
    {
        ReadOnlySpan<byte> views = _views;
        ReadOnlySpan<byte> heap = _heap;
        long sink = 0;
        for (int i = 0; i < Rows; i++)
        {
            ref byte view = ref Unsafe.Add(ref MemoryMarshal.GetReference(views), i * ViewSize);
            int size = (int)Unsafe.ReadUnaligned<uint>(ref view);
            ReadOnlySpan<byte> value = size <= 12
                ? MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref view, sizeof(uint)), size)
                : heap.Slice((int)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12)), size);
            sink += value[0] + size;
        }

        return sink;
    }

    /// <summary>
    /// The same, from offsets: two adjacent loads and a window on the heap, every row.
    /// </summary>
    [Benchmark(Description = "consume GetSpan, offsets")]
    [BenchmarkCategory("consume")]
    public long ConsumeOffsets()
    {
        ReadOnlySpan<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        ReadOnlySpan<byte> heap = _heap;
        long sink = 0;
        for (int i = 0; i < Rows; i++)
        {
            int start = (int)offsets[i];
            int size = (int)offsets[i + 1] - start;
            ReadOnlySpan<byte> value = heap.Slice(start, size);
            sink += value[0] + size;
        }

        return sink;
    }

    // ------------------------------------------------------------------------------------- take

    /// <summary>A scattered take over views: gather sixteen bytes per selected row.</summary>
    [Benchmark(Description = "take 64 scattered, views")]
    [BenchmarkCategory("take")]
    public int TakeViews()
    {
        ReadOnlySpan<byte> views = _views;
        Span<byte> taken = _taken;
        for (int i = 0; i < _wanted.Length; i++)
        {
            views.Slice(_wanted[i] * ViewSize, ViewSize).CopyTo(taken.Slice(i * ViewSize, ViewSize));
        }

        return taken.Length;
    }

    /// <summary>
    /// A scattered take over offsets, producing VIEWS, which is a constraint rather than a
    /// choice: a take's output stays a view array whatever its input was.
    /// </summary>
    [Benchmark(Description = "take 64 scattered, offsets")]
    [BenchmarkCategory("take")]
    public int TakeOffsets()
    {
        ReadOnlySpan<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        ReadOnlySpan<byte> heap = _heap;
        Span<byte> taken = _taken;
        for (int i = 0; i < _wanted.Length; i++)
        {
            int row = _wanted[i];
            int start = (int)offsets[row];
            int size = (int)offsets[row + 1] - start;
            CanonicalSupport.WriteView(
                taken.Slice(i * ViewSize, ViewSize), heap.Slice(start, size), size, 0, start);
        }

        return taken.Length;
    }
}
