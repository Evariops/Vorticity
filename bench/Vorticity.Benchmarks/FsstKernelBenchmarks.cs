// The FSST decode kernel, alone, with both candidate shapes in ONE process.
//
// WHY THIS EXISTS RATHER THAN A SECOND WHOLE-FILE RUN. Measuring the kernel through
// DecodeComparison put a ~35 us open-and-walk cost in front of it and left the answer at the mercy
// of run-to-run drift: three measurements of effectively identical code came back 135, 146 and 202
// us, which is more spread than any of the differences being argued about. docs/05-benchmarks.md §5
// warns about exactly this - "thermal drift over a long run systematically favors whoever goes
// first" - and the fix it prescribes is to measure the two candidates against one clock.
//
// So both shapes run here, in the same process, over the same in-memory code stream:
//
//   * EXACT: copy the symbol's real length. What the library does.
//   * WIDE: one unaligned 8-byte store per symbol, advancing by the real length, with a narrow
//     tail where the slack runs out. What fsst-rs does, and what its comment says the whole
//     decompressor is shaped around.
//
// The WIDE copy is duplicated here rather than kept in the library behind a switch: a benchmark may
// carry a variant it is measuring, a decoder may not carry one it does not use.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>Two shapes of the FSST inner loop, measured against one clock.</summary>
[Config(typeof(BenchmarkConfig))]
public class FsstKernelBenchmarks
{
    private byte[] _symbols = [];
    private byte[] _lengths = [];
    private byte[] _codes = [];
    private byte[] _output = [];

    /// <summary>How many code bytes to decode: enough that the loop, not the call, is measured.</summary>
    [Params(1 << 16)]
    public int CodeBytes { get; set; } = 1 << 16;

    [GlobalSetup]
    public void Setup()
    {
        // A table whose widths look like a real one: mostly 2-6 bytes, a few singles.
        const int Count = 200;
        _symbols = new byte[Count * 8];
        _lengths = new byte[Count];
        Random random = new Random(20260912);
        for (int i = 0; i < Count; i++)
        {
            int width = 1 + (i % 8);
            _lengths[i] = (byte)width;
            for (int b = 0; b < width; b++)
            {
                _symbols[(i * 8) + b] = (byte)random.Next('a', 'z');
            }
        }

        _codes = new byte[CodeBytes];
        long decoded = 0;
        for (int i = 0; i < _codes.Length; i++)
        {
            // One escape in every 64 codes, which is about what a trained table produces.
            if (i % 64 == 63 && i + 1 < _codes.Length)
            {
                _codes[i] = 255;
                _codes[++i] = (byte)random.Next(256);
                decoded++;
                continue;
            }

            byte code = (byte)random.Next(Count);
            _codes[i] = code;
            decoded += _lengths[code];
        }

        _output = new byte[decoded + 64];
    }

    /// <summary>The library's kernel: copy the symbol's real length.</summary>
    [Benchmark(Baseline = true, Description = "exact copy")]
    public int Exact()
    {
        FsstSymbolTable table = FsstSymbolTable.Create(_symbols, _lengths, "vortex.fsst");
        return table.Decode(_codes, _output, "vortex.fsst");
    }

    /// <summary>The reference's shape: one 8-byte store per symbol, advance by the real length.</summary>
    [Benchmark(Description = "wide store")]
    public int Wide()
    {
        ReadOnlySpan<byte> symbols = _symbols;
        ReadOnlySpan<byte> lengths = _lengths;
        ReadOnlySpan<byte> codes = _codes;
        Span<byte> destination = _output;

        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte table = ref MemoryMarshal.GetReference(symbols);
        int wideLimit = destination.Length - 8;
        int written = 0;

        for (int i = 0; i < codes.Length; i++)
        {
            byte code = codes[i];
            if (code == 255)
            {
                destination[written++] = codes[++i];
                continue;
            }

            int width = lengths[code];
            if (written <= wideLimit)
            {
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref output, (uint)written),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref table, (uint)(code * 8))));
                written += width;
                continue;
            }

            symbols.Slice(code * 8, width).CopyTo(destination.Slice(written, width));
            written += width;
        }

        return written;
    }
}
