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
//
// AND SO IS THE EXACT COPY, WHICH IS THE REPAIR THIS FILE NEEDED. Its `exact copy` arm used to call
// FsstSymbolTable.Decode - "what the library does" - and the wide store then LANDED in that method.
// Both arms ran the same shape from that commit on, and the table stopped being able to demonstrate
// the 7.9x it was written to demonstrate: it read 1.37x instead, with nothing regressed. A benchmark
// that measures the library and calls the result "the old shape" has a shelf life of exactly one
// commit. FastLanesKernelBenchmarks never had the problem because it always carried its own scalar
// loop, and that is now the rule here too.
//
// So there are three arms rather than two, and the third is the one that keeps the other two
// honest:
//
//   * EXACT and WIDE are the benchmark's own loops. Their ratio is the SHAPE question, and it is
//     the claim docs/05 makes. Neither validates, because the question is about the store.
//   * LIBRARY is FsstSymbolTable.Create plus Decode: what a decoder actually runs, validation
//     included. Its distance from WIDE is the price of that validation, and measuring it was worth
//     it - the gap had been attributed in writing to Create, which loops over 200 symbol lengths
//     and cannot cost what was attributed to it.
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

    /// <summary>The shape the library REPLACED: copy exactly the symbol's length, every time.</summary>
    /// <remarks>
    /// Carried here rather than called through the library, which no longer contains it. Everything
    /// except the store is identical to <see cref="Wide"/>, so their quotient is about the store and
    /// nothing else.
    /// </remarks>
    [Benchmark(Baseline = true, Description = "exact copy")]
    public int Exact()
    {
        ReadOnlySpan<byte> symbols = _symbols;
        ReadOnlySpan<byte> lengths = _lengths;
        ReadOnlySpan<byte> codes = _codes;
        Span<byte> destination = _output;
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
            symbols.Slice(code * 8, width).CopyTo(destination.Slice(written, width));
            written += width;
        }

        return written;
    }

    /// <summary>The replaced shape WITH the validation a decoder does, to close the 2x2.</summary>
    /// <remarks>
    /// The historical figure this file exists to defend - 306.8 us for "exact copy" - was measured
    /// by calling Decode back when Decode held the exact copy, so it included validation. The bare
    /// arm above does not, and comparing the two across that difference is what makes a 7.9x turn
    /// into a 4.7x for no reason anybody changed. This arm is the like-for-like partner of
    /// <see cref="Library"/>: same validation, different store.
    /// </remarks>
    [Benchmark(Description = "exact copy, validation included")]
    public int ExactValidated()
    {
        ReadOnlySpan<byte> symbols = _symbols;
        ReadOnlySpan<byte> lengths = _lengths;
        ReadOnlySpan<byte> codes = _codes;
        Span<byte> destination = _output;
        int count = lengths.Length;
        int written = 0;

        for (int i = 0; i < codes.Length; i++)
        {
            byte code = codes[i];
            if (code == 255)
            {
                if (i + 1 >= codes.Length || written >= destination.Length)
                {
                    throw new InvalidOperationException("truncated");
                }

                destination[written++] = codes[++i];
                continue;
            }

            if (code >= count)
            {
                throw new InvalidOperationException("unknown code");
            }

            int width = lengths[code];
            if (written + width > destination.Length)
            {
                throw new InvalidOperationException("overrun");
            }

            symbols.Slice(code * 8, width).CopyTo(destination.Slice(written, width));
            written += width;
        }

        return written;
    }

    /// <summary>What a decoder actually runs: the same wide store, plus the validation.</summary>
    /// <remarks>
    /// The third arm exists because the difference between this and <see cref="Wide"/> was once
    /// written down as the cost of <see cref="FsstSymbolTable.Create"/> without being measured.
    /// Create validates the table shape and walks the symbol lengths - 200 iterations here - so it
    /// is not a plausible home for a microsecond figure. What Decode does per CODE and the arms
    /// above do not is check that the code names a symbol the table holds and that the write stays
    /// inside the destination: 65 536 of those, against 200 of the other.
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
