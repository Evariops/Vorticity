// The FSST decode kernel: the shape the library carries, and the price of its validation.
//
// WHY THIS EXISTS RATHER THAN A SECOND WHOLE-FILE RUN. Measuring the kernel end to end put a ~35 us
// open-and-walk cost in front of it and left the answer at the mercy of run-to-run drift: three
// measurements of effectively identical code came back 135, 146 and 202 us, which is more spread
// than any of the differences being argued about. docs/05-benchmarks.md §5 warns about exactly this
// - "thermal drift over a long run systematically favors whoever goes first" - and the fix it
// prescribes is to measure the two candidates against one clock.
//
// TWO ARMS, AND ONLY TWO (BENCH-AUDIT.md §3.1). The pair that carried the SHAPE question - an exact
// copy of each symbol's real length against one unaligned 8-byte store per symbol, the shape fsst-rs
// is built around - documented a 4.7x that was banked in `6ce4d2a` and cannot move again: the exact
// copy is no longer anywhere in the library, so its arm was a museum piece the suite paid for on
// every run. The figure stays in bench/BASELINE.md and in that commit.
//
// What remains is the pair that can still move:
//
//   * WIDE is the benchmark's own loop, carrying the reference shape. It is the floor, and the
//     baseline: a decoder cannot beat a bare loop with no validation in it.
//   * LIBRARY is FsstSymbolTable.Create plus Decode: what a decoder actually runs. Its distance
//     from WIDE is the price of the validation, and measuring it was worth it - the gap had been
//     attributed in writing to Create, which loops over 200 symbol lengths and cannot cost what was
//     attributed to it.
//
// The WIDE copy is duplicated here rather than kept in the library behind a switch: a benchmark may
// carry a variant it is measuring, a decoder may not carry one it does not use. And it carries its
// OWN loop rather than calling in, which is the repair this file needed once already: the `exact
// copy` arm used to call FsstSymbolTable.Decode, the wide store then landed in that method, both
// arms ran the same shape from that commit on, and the table read 1.37x with nothing regressed. A
// benchmark that measures the library and calls the result "the old shape" has a shelf life of
// exactly one commit.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>The FSST inner loop, bare and as a decoder runs it, against one clock.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
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

    /// <summary>What a decoder actually runs: the same wide store, plus the validation.</summary>
    /// <remarks>
    /// This arm exists because the difference between this and <see cref="Wide"/> was once
    /// written down as the cost of <see cref="FsstSymbolTable.Create"/> without being measured.
    /// Create validates the table shape and walks the symbol lengths - 200 iterations here - so it
    /// is not a plausible home for a microsecond figure. What Decode does per CODE and
    /// <see cref="Wide"/> does not is check that the code names a symbol the table holds and that
    /// the write stays inside the destination: 65 536 of those, against 200 of the other.
    /// </remarks>
    [Benchmark(Description = "library, validation included")]
    public int Library()
    {
        Span<byte> symbolScratch = stackalloc byte[FsstSymbolTable.SymbolScratchBytes];
        Span<byte> widthScratch = stackalloc byte[FsstSymbolTable.WidthScratchBytes];
        FsstDecodeTable table = FsstSymbolTable
            .Create(_symbols, _lengths, "vortex.fsst")
            .Prepare(symbolScratch, widthScratch);
        return table.Decode(_codes, _output, "vortex.fsst");
    }

    /// <summary>The reference's shape: one 8-byte store per symbol, advance by the real length.</summary>
    [Benchmark(Baseline = true, Description = "wide store")]
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
