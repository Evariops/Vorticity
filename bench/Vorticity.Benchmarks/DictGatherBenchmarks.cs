using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The gather behind a dictionary of strings alone: sixteen-byte views taken by code, the library's
/// <c>RowKernels.Gather</c> and <c>RowKernels.GatherMasked</c> against a frozen copy of the
/// kernels as they were, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// The shapes are the per-encoding corpus files': <c>u16-nullcodes</c>, five values one of them
/// null under <c>u16</c> codes one in nine null; <c>u16-nullvalues</c>, the same values under codes
/// that are all valid; <c>u64</c> and <c>u8</c>, codes of those widths over five and two hundred
/// values without nulls.
/// </para>
/// <para>
/// The arms are checked against each other, the views and the validity bits, before anything is
/// timed.
/// </para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class DictGatherBenchmarks
{
    private const int Width = 16;

    private byte[] _codes = [];
    private byte[] _codeBits = [];
    private byte[] _values = [];
    private byte[] _valueBits = [];
    private byte[] _output = [];
    private byte[] _outputBits = [];
    private int _entries;

    /// <summary>The dictionary's shape.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "u16-nullcodes";

    /// <summary>Rows gathered in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["u16-nullcodes", "u16-nullvalues", "u64", "u8"];

    /// <summary>Rows whose output stays in the first-level cache, and a scan window.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation writes: a sixteen-byte view a row.</summary>
    /// <param name="method">Unused; every arm writes the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * Width);
    }

    [GlobalSetup]
    public void Setup()
    {
        _entries = Shape == "u8" ? 200 : 5;
        int codeWidth = Shape switch { "u64" => 8, "u8" => 1, _ => 2 };
        _codes = new byte[Rows * codeWidth];
        for (int row = 0; row < Rows; row++)
        {
            ulong code = (ulong)(row % _entries);
            switch (codeWidth)
            {
                case 1: _codes[row] = (byte)code; break;
                case 2: BitConverter.TryWriteBytes(_codes.AsSpan(row * 2), (ushort)code); break;
                default: BitConverter.TryWriteBytes(_codes.AsSpan(row * 8), code); break;
            }
        }

        // Nullable codes: one row in nine null, and its code left as the column holds it.
        _codeBits = new byte[((Rows + 63) / 64 * 8) + 8];
        for (int row = 0; row < Rows; row++)
        {
            if (row % 9 != 4)
            {
                _codeBits[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        _values = new byte[_entries * Width];
        for (int entry = 0; entry < _entries; entry++)
        {
            _values[entry * Width] = (byte)(entry % 13);
            _values.AsSpan((entry * Width) + 4, Math.Min(entry % 13, 12)).Fill((byte)('a' + (entry % 26)));
        }

        _valueBits = new byte[(_entries + 7) / 8];
        for (int entry = 0; entry < _entries; entry++)
        {
            if (Shape == "u8" || Shape == "u64" || entry != 2)
            {
                _valueBits[entry >> 3] |= (byte)(1 << (entry & 7));
            }
        }

        _output = new byte[Rows * Width];
        _outputBits = new byte[(Rows + 7) / 8];
        Check();
    }

    private void Check()
    {
        Original();
        byte[] views = (byte[])_output.Clone();
        byte[] bits = (byte[])_outputBits.Clone();
        _output.AsSpan().Fill(0xA5);
        _outputBits.AsSpan().Fill(0x5A);
        Current();
        if (!_output.AsSpan().SequenceEqual(views) || !_outputBits.AsSpan().SequenceEqual(bits))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{Shape}: the library's gather differs from the original, views at byte {_output.AsSpan().CommonPrefixLength(views)}, bits at byte {_outputBits.AsSpan().CommonPrefixLength(bits)}."));
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        ReadOnlySpan<Vector128<byte>> source = MemoryMarshal.Cast<byte, Vector128<byte>>(_values);
        Span<Vector128<byte>> target = MemoryMarshal.Cast<byte, Vector128<byte>>(_output);
        _outputBits.AsSpan().Clear();
        return Shape switch
        {
            "u16-nullcodes" => DictGatherOriginal.GatherNullableCodes(
                MemoryMarshal.Cast<byte, ushort>(_codes), source, target, _codeBits, _valueBits, _outputBits),
            "u16-nullvalues" => DictGatherOriginal.GatherNullableValues(
                MemoryMarshal.Cast<byte, ushort>(_codes), source, target, _valueBits, _outputBits),
            "u64" => DictGatherOriginal.Gather(MemoryMarshal.Cast<byte, ulong>(_codes), source, target),
            _ => DictGatherOriginal.Gather<byte>(_codes, source, target),
        };
    }

    [Benchmark]
    public int Current()
    {
        _outputBits.AsSpan().Clear();
        return Shape switch
        {
            "u16-nullcodes" => RowKernels.GatherMasked(
                _codes, PType.U16, _values, Width, _entries, _output, Rows, _codeBits, 0, _valueBits, 0, false, _outputBits),
            "u16-nullvalues" => RowKernels.GatherMasked(
                _codes, PType.U16, _values, Width, _entries, _output, Rows, default, 0, _valueBits, 0, false, _outputBits),
            "u64" => RowKernels.Gather(_codes, PType.U64, _values, Width, _entries, _output, Rows),
            _ => RowKernels.Gather(_codes, PType.U8, _values, Width, _entries, _output, Rows),
        };
    }
}
