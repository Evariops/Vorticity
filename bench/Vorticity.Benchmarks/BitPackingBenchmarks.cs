using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>
/// The <c>fastlanes.bitpacked</c> unpack kernel alone: the library's <c>FastLanes.UnpackBlocks</c>
/// against a frozen copy of the row-at-a-time kernel, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// The default case is the shape of the per-encoding corpus file <c>fastlanes_bitpacked</c>: u32
/// values at 10 bits, in runs of 8 blocks, which stay in the first-level cache, and of 128, as a
/// scan window of 131 072 rows hands them. <c>--full</c> adds other element types and widths, and
/// the whole million rows in one call, so a change tuned for one shape is seen on the others.
/// </para>
/// <para>
/// Both buffers start <c>Offset</c> bytes past a 128-byte boundary: a row of a block is 128 bytes
/// for every element type, one cache line where lines are 128 bytes, two half lines when the buffer
/// is off by 64. The arms are checked against each other value for value before anything is timed.
/// </para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[BitPackingColumns]
public class BitPackingBenchmarks
{
    private const int Alignment = 128;

    private byte[] _packed = [];
    private int _packedStart;
    private int _packedLength;
    private byte[] _output = [];
    private int _outputStart;
    private int _outputLength;
    private int _elementBits;
    private int _bitWidth;

    /// <summary>The element type and the bit width, as <c>u32-w10</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "u32-w10";

    /// <summary>Whole blocks of 1 024 values per call.</summary>
    [ParamsSource(nameof(BlockCounts))]
    public int Blocks { get; set; } = 128;

    /// <summary>Bytes past a 128-byte boundary at which both buffers start.</summary>
    [ParamsSource(nameof(Offsets))]
    public int Offset { get; set; }

    /// <summary>The offsets the current profile measures: aligned, and half a line off.</summary>
    public static IEnumerable<int> Offsets => BenchmarkConfig.Full ? [0, 64] : [0];

    /// <summary>The shapes the current profile measures.</summary>
    public static IEnumerable<string> Shapes => BenchmarkConfig.Full
        ? ["u32-w10", "u32-w3", "u32-w17", "u32-w31", "u8-w3", "u16-w10", "u64-w10", "u64-w33"]
        : ["u32-w10"];

    /// <summary>
    /// The block counts the current profile measures: 8, whose output stays in the first-level cache
    /// and shows the kernel's own cost; 128, a scan window, whose output goes out to the second level
    /// and shows what storing it costs; and under <c>--full</c> the corpus file's 977.
    /// </summary>
    public static IEnumerable<int> BlockCounts => BenchmarkConfig.Full ? [8, 128, 977] : [8, 128];

    /// <summary>What one invocation writes: every value of every block, at the element width.</summary>
    /// <param name="method">Unused; every arm writes the same output.</param>
    /// <param name="parameters">The case's shape and block count.</param>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        (int elementBits, _) = Parse((string)parameters[nameof(Shape)]!);
        long values = (long)(int)parameters[nameof(Blocks)]! * FastLanes.BlockSize;
        return (values, values * (elementBits / 8));
    }

    /// <summary>The packed bytes one invocation reads.</summary>
    internal static long PackedBytes(IReadOnlyDictionary<string, object?> parameters)
    {
        (_, int bitWidth) = Parse((string)parameters[nameof(Shape)]!);
        return (long)(int)parameters[nameof(Blocks)]! * FastLanes.BlockByteLength(bitWidth);
    }

    [GlobalSetup]
    public void Setup()
    {
        (_elementBits, _bitWidth) = Parse(Shape);
        _packedLength = Blocks * FastLanes.BlockByteLength(_bitWidth);
        _outputLength = Blocks * FastLanes.BlockSize * (_elementBits / 8);
        (_packed, _packedStart) = Aligned(_packedLength, Offset);
        (_output, _outputStart) = Aligned(_outputLength, Offset);
        new Random(20260924).NextBytes(_packed.AsSpan(_packedStart, _packedLength));
        Check();
    }

    private void Check()
    {

        (byte[] expected, int expectedStart) = Aligned(_outputLength, 0);
        Original();
        _output.AsSpan(_outputStart, _outputLength).CopyTo(expected.AsSpan(expectedStart, _outputLength));
        _output.AsSpan(_outputStart, _outputLength).Fill(0xA5);
        Current();
        ReadOnlySpan<byte> want = expected.AsSpan(expectedStart, _outputLength);
        ReadOnlySpan<byte> got = _output.AsSpan(_outputStart, _outputLength);
        if (!got.SequenceEqual(want))
        {
            int at = got.CommonPrefixLength(want) / (_elementBits / 8);
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{Shape}: the library's unpack differs from the original at value {at} (block {at / FastLanes.BlockSize}, position {at % FastLanes.BlockSize})."));
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        switch (_elementBits)
        {
            case 8:
                BitPackingOriginal.UnpackBlocks(Packed<byte>(), _bitWidth, Output<byte>(), Blocks);
                break;
            case 16:
                BitPackingOriginal.UnpackBlocks(Packed<ushort>(), _bitWidth, Output<ushort>(), Blocks);
                break;
            case 32:
                BitPackingOriginal.UnpackBlocks(Packed<uint>(), _bitWidth, Output<uint>(), Blocks);
                break;
            default:
                BitPackingOriginal.UnpackBlocks(Packed<ulong>(), _bitWidth, Output<ulong>(), Blocks);
                break;
        }

        return _output[_outputStart];
    }

    [Benchmark]
    public int Current()
    {
        switch (_elementBits)
        {
            case 8:
                FastLanes.UnpackBlocks(Packed<byte>(), _bitWidth, Output<byte>(), Blocks);
                break;
            case 16:
                FastLanes.UnpackBlocks(Packed<ushort>(), _bitWidth, Output<ushort>(), Blocks);
                break;
            case 32:
                FastLanes.UnpackBlocks(Packed<uint>(), _bitWidth, Output<uint>(), Blocks);
                break;
            default:
                FastLanes.UnpackBlocks(Packed<ulong>(), _bitWidth, Output<ulong>(), Blocks);
                break;
        }

        return _output[_outputStart];
    }

    /// <summary>
    /// The same output written with a constant and nothing read: how fast this working set can be
    /// stored at all, which no unpack can beat.
    /// </summary>
    /// <remarks>Not zero, because a zero fill can clear whole cache lines without storing them.</remarks>
    [Benchmark]
    public int StoreFloor()
    {
        _output.AsSpan(_outputStart, _outputLength).Fill(0x5A);
        return _output[_outputStart];
    }

    private ReadOnlySpan<T> Packed<T>()
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(_packed.AsSpan(_packedStart, _packedLength));

    private Span<T> Output<T>()
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(_output.AsSpan(_outputStart, _outputLength));

    private static unsafe (byte[] Array, int Start) Aligned(int length, int offset)
    {
        byte[] array = GC.AllocateUninitializedArray<byte>(length + (2 * Alignment), pinned: true);
        nint address = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        int start = (int)((Alignment - (address % Alignment)) % Alignment) + offset;
        return (array, start);
    }

    private static (int ElementBits, int BitWidth) Parse(string shape)
    {
        int split = shape.IndexOf("-w", StringComparison.Ordinal);
        int elementBits = int.Parse(shape.AsSpan(1, split - 1), CultureInfo.InvariantCulture);
        int bitWidth = int.Parse(shape.AsSpan(split + 2), CultureInfo.InvariantCulture);
        return (elementBits, bitWidth);
    }
}

/// <summary>The two columns only this class has: packed bytes read per second, and cycles per value.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class BitPackingColumnsAttribute : ColumnConfigBaseAttribute
{
    public BitPackingColumnsAttribute()
        : base(new PackedThroughputColumn(), new CyclesPerValueColumn())
    {
    }
}

/// <summary>Packed gigabytes read per second.</summary>
internal sealed class PackedThroughputColumn : IColumn
{
    public string Id => nameof(PackedThroughputColumn);

    public string ColumnName => "GB/s in";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 3;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => "Packed bytes one invocation reads, over its mean; GB/s is the decoded bytes it writes";

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (mean is null || mean.Value <= 0)
        {
            return "-";
        }

        long bytes = BitPackingBenchmarks.PackedBytes(Parameters(benchmarkCase));
        return (bytes / mean.Value).ToString("F2", CultureInfo.InvariantCulture);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);

    internal static Dictionary<string, object?> Parameters(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Parameters.Items.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
}

/// <summary>Cycles per decoded value, at a clock measured on this machine when the summary is built.</summary>
/// <remarks>
/// The clock is timed on a chain of dependent single-cycle integer operations, which runs at one
/// operation per cycle on any out-of-order core, so the figure is the core's frequency under a
/// single busy thread, which is what the kernel runs at.
/// </remarks>
internal sealed class CyclesPerValueColumn : IColumn
{
    private static readonly Lazy<double> Gigahertz = new(Calibrate);

    public string Id => nameof(CyclesPerValueColumn);

    public string ColumnName => "cycles/value";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 4;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => string.Create(
        CultureInfo.InvariantCulture,
        $"Mean in cycles over the values written, at {Gigahertz.Value:F2} GHz timed on a dependent integer chain");

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        Dictionary<string, object?> parameters = PackedThroughputColumn.Parameters(benchmarkCase);
        (long values, _) = BitPackingBenchmarks.BenchmarkWork(string.Empty, parameters);
        if (mean is null || values <= 0)
        {
            return "-";
        }

        return (mean.Value * Gigahertz.Value / values).ToString("F3", CultureInfo.InvariantCulture);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);

    private static double Calibrate()
    {
        const long Iterations = 25_000_000;
        Chain(Iterations / 10, 1);
        double best = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long started = Stopwatch.GetTimestamp();
            ulong sink = Chain(Iterations, (ulong)attempt + 3);
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            GC.KeepAlive(sink);
            best = Math.Max(best, Iterations * 8 / seconds / 1e9);
        }

        return best;
    }

    // Eight dependent operations an iteration, alternating add and xor so that no two can be folded.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong Chain(long iterations, ulong seed)
    {
        ulong x = seed;
        ulong y = BitOperations.RotateLeft(seed, 17) | 1;
        for (long i = 0; i < iterations; i++)
        {
            ulong k = (ulong)i;
            x += y;
            x ^= k;
            x += y;
            x ^= k;
            x += y;
            x ^= k;
            x += y;
            x ^= k;
        }

        return x;
    }
}
