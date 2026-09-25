using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The <c>vortex.datetimeparts</c> recomposition alone: days, seconds and subseconds back into one
/// timestamp, the library's <c>IntegerKernels.Recompose</c> against a frozen copy of the loop as it
/// was, in one process and on one clock.
/// </summary>
/// <remarks>
/// <c>i64-i32-i32</c> is the per-encoding corpus file's parts, milliseconds a day apart;
/// <c>u16-u32-u16</c> the same values in the narrowest types that hold them. The arms are checked
/// against each other before anything is timed.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class RecomposeBenchmarks
{
    private const long MillisPerDay = 86_400_000;
    private const long MillisPerSecond = 1_000;

    private byte[] _days = [];
    private byte[] _seconds = [];
    private byte[] _subseconds = [];
    private long[] _output = [];
    private PType _daysType;
    private PType _secondsType;
    private PType _subsecondsType;

    /// <summary>The parts' physical types.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "i64-i32-i32";

    /// <summary>Rows recomposed in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["i64-i32-i32", "u16-u32-u16"];

    /// <summary>Rows whose output stays in the first-level cache, and a scan window.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation writes: a timestamp a row.</summary>
    /// <param name="method">Unused; every arm writes the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * sizeof(long));
    }

    [GlobalSetup]
    public void Setup()
    {
        bool narrow = Shape == "u16-u32-u16";
        (_daysType, _secondsType, _subsecondsType) = narrow ? (PType.U16, PType.U32, PType.U16) : (PType.I64, PType.I32, PType.I32);
        _days = Part(narrow ? 2 : 8, row => 19_000 + (row / 86_400));
        _seconds = Part(4, row => row % 86_400);
        _subseconds = Part(narrow ? 2 : 4, row => (row * 7) % 1_000);
        _output = new long[Rows];

        Original();
        long[] expected = (long[])_output.Clone();
        _output.AsSpan().Fill(-1);
        Current();
        if (!_output.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{Shape}: the library's recomposition differs from the original at row {_output.AsSpan().CommonPrefixLength(expected)}.");
        }
    }

    private byte[] Part(int width, Func<int, long> value)
    {
        byte[] part = new byte[Rows * width];
        for (int row = 0; row < Rows; row++)
        {
            long v = value(row);
            switch (width)
            {
                case 2: BitConverter.TryWriteBytes(part.AsSpan(row * 2), (ushort)v); break;
                case 4: BitConverter.TryWriteBytes(part.AsSpan(row * 4), (int)v); break;
                default: BitConverter.TryWriteBytes(part.AsSpan(row * 8), v); break;
            }
        }

        return part;
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        if (_daysType == PType.U16)
        {
            RecomposeOriginal<ushort, uint, ushort>(_days, _seconds, _subseconds, _output, MillisPerDay, MillisPerSecond);
        }
        else
        {
            RecomposeOriginal<long, int, int>(_days, _seconds, _subseconds, _output, MillisPerDay, MillisPerSecond);
        }

        return _output[0];
    }

    [Benchmark]
    public long Current()
    {
        IntegerKernels.Recompose(_days, _daysType, _seconds, _secondsType, _subseconds, _subsecondsType, _output, MillisPerDay, MillisPerSecond);
        return _output[0];
    }

    /// <summary>The loop as it was, kept here unchanged so that editing the library's can never move this arm.</summary>
    private static void RecomposeOriginal<TA, TB, TC>(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, Span<long> destination, long scaleA, long scaleB)
        where TA : unmanaged, IBinaryInteger<TA>
        where TB : unmanaged, IBinaryInteger<TB>
        where TC : unmanaged, IBinaryInteger<TC>
    {
        int count = destination.Length;
        ReadOnlySpan<TA> sa = MemoryMarshal.Cast<byte, TA>(a)[..count];
        ReadOnlySpan<TB> sb = MemoryMarshal.Cast<byte, TB>(b)[..count];
        ReadOnlySpan<TC> sc = MemoryMarshal.Cast<byte, TC>(c)[..count];
        for (int i = 0; i < count; i++)
        {
            destination[i] = unchecked(
                (long.CreateTruncating(sa[i]) * scaleA) +
                (long.CreateTruncating(sb[i]) * scaleB) +
                long.CreateTruncating(sc[i]));
        }
    }
}
