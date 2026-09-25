using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The range check of a <c>vortex.listview</c> node alone, the only per-row work its decode does:
/// the library's <c>ListViewDecoder.ValidateRanges</c> against a frozen copy of the loop as it was,
/// in one process and on one clock.
/// </summary>
/// <remarks>
/// <c>u64</c> is the per-encoding corpus files' offsets and sizes, lists of 0 to 4 elements laid
/// end to end; <c>u32</c> and <c>i32</c> the same in narrower types. Before anything is timed, both
/// arms accept the rows, and the library still refuses a negative size and a list past the
/// elements.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ListViewValidationBenchmarks
{
    private byte[] _offsets = [];
    private byte[] _sizes = [];
    private int _elements;
    private PType _type;

    /// <summary>The offsets' and sizes' physical type.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "u64";

    /// <summary>Rows checked in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["u64", "u32", "i32"];

    /// <summary>Rows whose offsets stay in the first-level cache, and a scan window.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation reads: an offset and a size a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's shape and rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        int width = (string)parameters[nameof(Shape)]! == "u64" ? 8 : 4;
        return (rows, (long)rows * 2 * width);
    }

    [GlobalSetup]
    public void Setup()
    {
        _type = Shape switch { "u64" => PType.U64, "u32" => PType.U32, _ => PType.I32 };
        int width = Shape == "u64" ? 8 : 4;
        _offsets = new byte[Rows * width];
        _sizes = new byte[Rows * width];
        long at = 0;
        for (int row = 0; row < Rows; row++)
        {
            long size = row % 5;
            Write(_offsets, row, width, at);
            Write(_sizes, row, width, size);
            at += size;
        }

        _elements = (int)at;
        Original();
        Current();
        Refuses("a negative size", sizes => Write(sizes, Rows / 2, width, width == 4 ? -1 : long.MinValue));
        Refuses("a list past the elements", sizes => Write(sizes, Rows - 1, width, _elements + 1));
    }

    private void Refuses(string what, Action<byte[]> corrupt)
    {
        byte[] sizes = (byte[])_sizes.Clone();
        corrupt(sizes);
        try
        {
            ListViewDecoder.ValidateRanges(_offsets, _type, sizes, _type, Rows, _elements);
        }
        catch (VortexFormatException)
        {
            return;
        }

        throw new InvalidOperationException($"{Shape}: the library accepted {what}.");
    }

    private static void Write(byte[] into, int row, int width, long value)
    {
        if (width == 8)
        {
            BitConverter.TryWriteBytes(into.AsSpan(row * 8), value);
        }
        else
        {
            BitConverter.TryWriteBytes(into.AsSpan(row * 4), (int)value);
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        switch (_type)
        {
            case PType.U64: ValidateOriginal<ulong>(_offsets, _sizes, Rows, _elements); break;
            case PType.U32: ValidateOriginal<uint>(_offsets, _sizes, Rows, _elements); break;
            default: ValidateOriginal<int>(_offsets, _sizes, Rows, _elements); break;
        }

        return Rows;
    }

    [Benchmark]
    public int Current()
    {
        ListViewDecoder.ValidateRanges(_offsets, _type, _sizes, _type, Rows, _elements);
        return Rows;
    }

    /// <summary>The loop as it was, kept here unchanged so that editing the library's can never move this arm.</summary>
    private static void ValidateOriginal<T>(ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> sizes, int length, int elementsLength)
        where T : unmanaged
    {
        ReadOnlySpan<T> typedOffsets = MemoryMarshal.Cast<byte, T>(offsets)[..length];
        ReadOnlySpan<T> typedSizes = MemoryMarshal.Cast<byte, T>(sizes)[..length];
        ulong limit = (ulong)elementsLength;
        for (int i = 0; i < typedOffsets.Length; i++)
        {
            long offset = Widen(typedOffsets[i]);
            long size = Widen(typedSizes[i]);
            if (offset < 0 || size < 0)
            {
                throw new InvalidOperationException($"row {i} is negative");
            }

            ulong end = (ulong)offset + (ulong)size;
            if (end > limit)
            {
                throw new InvalidOperationException($"row {i} runs past the elements");
            }
        }
    }

    private static long Widen<T>(T value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref value);
        }

        if (typeof(T) == typeof(ulong))
        {
            ulong wide = Unsafe.As<T, ulong>(ref value);
            return wide > long.MaxValue ? long.MaxValue : (long)wide;
        }

        return Unsafe.As<T, int>(ref value);
    }
}
