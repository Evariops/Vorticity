// The OnPair concatenation against the loop it replaced, in ONE process.
//
// This loop is 48% of an OnPair scan and was A/B-ed BY HAND -- the one place in the repository
// where a change of this size rests on a measurement nothing can re-run. It is also the loop
// where a type switch was measured at +0.9% and called refuted; `ConcatenateCore`'s own remark
// answers that the refutation "held while the rest of the body was this expensive". Two claims
// about the same twenty lines, three months apart, and no bench.
//
// The scalar arm is the body that remark describes: a switch on the code's physical type, a compare
// against the token count, two bounds-checked reads of the offsets table, a compare of
// `written + size` against the destination length, and a `CopyTo`. The library arm is
// `BuildTokenTable` plus `Concatenate` -- the table is built INSIDE the measurement, because it is
// part of what the new shape costs and leaving it out would flatter it.
//
// ALIGNMENT MEASURED, NOT ASSUMED: these buffers are plain GC arrays -- does that
// change the number against the library's aligned arena allocations? Answered on M4 Pro: eight
// bytes past a 64-byte boundary costs 0.3% to 1.8% over two runs, the same sign every time but
// under the fast profile's own +-3% fidelity. Not re-measured on x64. The curve that answered it
// is gone, the answer being wanted once.
//
// 65 536 codes over a 4 096-token dictionary of 1 to 16 bytes: the token count OnPair actually
// reaches (`MaxTokenCount` is 1 << 16) and the size range its wide store is built around.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>The token concatenation, typed against per-code dispatch.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class OnPairKernelBenchmarks
{
    /// <summary>Codes concatenated per operation.</summary>
    private const int Codes = 1 << 16;

    /// <summary>Tokens in the dictionary.</summary>
    private const int Tokens = 4096;

    /// <summary>Longest token; `OnPairDecoder.MaxTokenSize`, which its wide store is built on.</summary>
    private const int MaxTokenSize = 16;

    private const PType CodesType = PType.U16;
    private const PType OffsetsType = PType.U32;

    private byte[] _codes = [];
    private byte[] _offsets = [];
    private byte[] _dictionary = [];
    private byte[] _destination = [];
    private long[] _table = [];

    /// <summary>
    /// What one invocation moves: <c>Codes</c> codes, and the tokens they concatenate.
    /// </summary>
    /// <param name="parameters">Unused; this class has no <c>[Params]</c>.</param>
    /// <remarks>
    /// The byte count is the AVERAGE token size times the codes: the sizes are uniform on
    /// 1..<see cref="MaxTokenSize"/>, so a code writes 8.5 bytes on average.
    /// </remarks>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Codes, (Codes * (MaxTokenSize + 1)) / 2);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260914);

        // Token sizes 1..MaxTokenSize, so both the wide-store path and its exact-copy tail run.
        int[] sizes = new int[Tokens];
        int total = 0;
        for (int t = 0; t < Tokens; t++)
        {
            sizes[t] = random.Next(1, MaxTokenSize + 1);
            total += sizes[t];
        }

        _offsets = new byte[(Tokens + 1) * sizeof(uint)];
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        int running = 0;
        for (int t = 0; t < Tokens; t++)
        {
            offsets[t] = (uint)running;
            running += sizes[t];
        }

        offsets[Tokens] = (uint)running;

        _dictionary = new byte[total];
        random.NextBytes(_dictionary);

        _codes = new byte[Codes * sizeof(ushort)];
        Span<ushort> codes = MemoryMarshal.Cast<byte, ushort>(_codes);
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)random.Next(0, Tokens);
        }

        // Room for the worst case, which is what the decoder allocates from the summed sizes.
        _destination = new byte[(Codes * MaxTokenSize) + MaxTokenSize];
        _table = new long[Tokens];
    }

    [Benchmark(Baseline = true, Description = "concatenate, switch per code")]
    public int ConcatScalar() =>
        ConcatenatePerCode(
            _codes, CodesType, 0, Codes, _offsets, OffsetsType, Tokens, _dictionary, _destination);

    [Benchmark(Description = "concatenate, library")]
    public int ConcatLibrary()
    {
        OnPairDecoder.BuildTokenTable(_offsets, OffsetsType, Tokens, _table);
        return OnPairDecoder.Concatenate(
            _codes, CodesType, 0, Codes, _table, _dictionary, _destination);
    }

    /// <summary>
    /// The concatenation as it was: the physical-type switch per code, two bounds-checked reads of
    /// the offsets table, the destination bound, and a `CopyTo`.
    /// </summary>
    private static int ConcatenatePerCode(
        ReadOnlySpan<byte> codes, PType codesPType, int codeStart, int codeEnd,
        ReadOnlySpan<byte> offsets, PType offsetsPType, int tokenCount,
        ReadOnlySpan<byte> dictionary, Span<byte> destination)
    {
        int written = 0;
        for (int i = codeStart; i < codeEnd; i++)
        {
            long code = CanonicalSupport.ReadInteger(codes, codesPType, i);
            if ((ulong)code >= (ulong)tokenCount)
            {
                return -1;
            }

            int start = (int)CanonicalSupport.ReadInteger(offsets, offsetsPType, (int)code);
            int end = (int)CanonicalSupport.ReadInteger(offsets, offsetsPType, (int)code + 1);
            int size = end - start;
            if (written + size > destination.Length)
            {
                return -1;
            }

            dictionary.Slice(start, size).CopyTo(destination.Slice(written, size));
            written += size;
        }

        return written;
    }
}
