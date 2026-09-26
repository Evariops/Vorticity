// A nullable zstd column's values spread over its rows: the values decompress dense, one per valid
// row, and each null row takes a zero. The spread runs once per decoded chunk of every nullable
// primitive zstd column, after libzstd has done its part.
//
// The ported arm is the spread as it was: a whole word of valid rows copied, a word of nulls
// skipped, a mixed word written row by row or its bits walked. The shipped arm is
// `ZstdDecoder.Expand`, which expands a mixed word a vector of rows at a time where AVX-512 is.
// Both run in one process, so tiered PGO is best turned off (`DOTNET_TieredPGO=0`).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Dense values over 65 536 rows: ported loop against the shipped kernel.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ZstdExpandBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Bytes per value.</summary>
    [Params(1, 4, 8)]
    public int Width { get; set; }

    /// <summary>The share of valid rows, in percent.</summary>
    [Params(50, 90)]
    public int Valid { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private Validity _validity;
    private byte[] _values = [];
    private byte[] _rows = [];

    /// <summary>What one invocation writes: a value's bytes per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (long)(parameters.TryGetValue(nameof(Width), out object? w) && w is int width ? width : 4));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        VortexBuffer bits = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
        valid.Clear();
        int count = 0;
        for (int i = 0; i < Rows; i++)
        {
            if (random.Next(100) < Valid)
            {
                valid[i >> 3] |= (byte)(1 << (i & 7));
                count++;
            }
        }

        _validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        _values = new byte[count * Width];
        random.NextBytes(_values);
        _rows = new byte[Rows * Width];
    }

    [Benchmark(Baseline = true, Description = "spread, ported")]
    public byte Ported()
    {
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        switch (Width)
        {
            case 1:
                OldExpand<byte>(_values, _rows, in mask, Rows);
                break;
            case 4:
                OldExpand<uint>(_values, _rows, in mask, Rows);
                break;
            default:
                OldExpand<ulong>(_values, _rows, in mask, Rows);
                break;
        }

        return _rows[^1];
    }

    [Benchmark(Description = "spread, shipped")]
    public byte Shipped()
    {
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        switch (Width)
        {
            case 1:
                ZstdDecoder.Expand<byte>(_values, _rows, in mask, Rows);
                break;
            case 4:
                ZstdDecoder.Expand<uint>(_values, _rows, in mask, Rows);
                break;
            default:
                ZstdDecoder.Expand<ulong>(_values, _rows, in mask, Rows);
                break;
        }

        return _rows[^1];
    }

    private const int SparseScatter = 36;

    private static void OldExpand<T>(ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source);
        Span<T> rows = MemoryMarshal.Cast<byte, T>(destination)[..length];
        ReadOnlySpan<byte> bits = mask.Bits;
        int bitOffset = mask.BitOffset;
        int next = 0;
        for (int row = 0; row < length; row += 64)
        {
            int span = Math.Min(64, length - row);
            ulong full = BitWords.Mask(span);
            ulong word = BitWords.Load(bits, bitOffset + row) & full;
            if (word == full)
            {
                values.Slice(next, span).CopyTo(rows.Slice(row, span));
                next += span;
                continue;
            }

            if (word == 0)
            {
                continue;
            }

            int count = BitOperations.PopCount(word);
            if (next + count > values.Length)
            {
                throw new InvalidOperationException();
            }

            next = count > SparseScatter
                ? OldScatter(values, ref MemoryMarshal.GetReference(rows[row..]), span, word, next)
                : OldWalk(values, ref MemoryMarshal.GetReference(rows[row..]), word, next);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OldScatter<T>(ReadOnlySpan<T> values, ref T rows, int span, ulong word, int next)
        where T : unmanaged, IBinaryInteger<T>
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        int last = values.Length - 1;
        for (int k = 0; k < span; k++)
        {
            int bit = (int)(word >> k) & 1;
            int at = next - (int)((uint)(last - next) >> 31);
            Unsafe.Add(ref rows, k) = Unsafe.Add(ref value, at) & (T.Zero - T.CreateTruncating(bit));
            next += bit;
        }

        return next;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OldWalk<T>(ReadOnlySpan<T> values, ref T rows, ulong word, int next)
        where T : unmanaged
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        while (word != 0)
        {
            Unsafe.Add(ref rows, BitOperations.TrailingZeroCount(word)) = Unsafe.Add(ref value, next++);
            word &= word - 1;
        }

        return next;
    }
}
