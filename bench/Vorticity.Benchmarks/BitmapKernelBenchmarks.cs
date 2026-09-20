// The four bitmap kernels against the loops they replaced, in ONE process.
//
// BENCH-AUDIT.md §3.2: `BitmapKernels` was vectorized while being measured only at the 1M
// throughput gate, where the run-to-run spread on the short files is +-20% and a 3% kernel is
// invisible. The claims in its own header -- "36% of the scan on an all-invalid million-row
// column", "two output bytes per iteration instead of a read-modify-write per value" -- had no
// bench behind them. This is that bench: the ported scalar arm is the shape each kernel replaced,
// spelled out here rather than in the library, so the two run against one clock.
//
// SIZED FOR CACHE, NOT FOR THE 100-500 us BAND §4.4 asks of a kernel class, and deliberately: the
// two arms of each pair differ by one to two orders of magnitude, so no single input puts both in
// that band. One mebibit is 128 KiB, which stays in L2 -- these kernels are meant to run on a
// validity bitmap that the decode just produced and that is therefore hot -- and it leaves the
// library arm at 5 to 40 us, far above BenchmarkDotNet's floor (§4.4: "un cas de 12 us n'est pas un
// probleme"; the driver picks the invocation count).
//
// ALIGNMENT MEASURED, NOT ASSUMED: these buffers are plain GC arrays, and §4.4 asked whether that
// changes the number against the library's aligned arena allocations. Answered on M4 Pro: eight
// bytes past a 64-byte boundary costs 0.3% to 1.8% over two runs, the same sign every time but
// under the fast profile's own +-3% fidelity. Not re-measured on x64. The curve that answered it
// is gone, the answer being wanted once.
//
// THE SECOND ARM IS NOT A STRAW MAN. Each scalar arm is the loop the library's own remarks describe
// as the previous state: a per-byte edge mask in `Classify`, a bit at a time in `CountSet` and
// `CopyRange`, a read-modify-write per value in `PackBytes`. Where that loop was already the right
// shape, the ratio will say so, and that is a result too.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks;

/// <summary>Each bitmap kernel against the bit-at-a-time loop it replaced.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class BitmapKernelBenchmarks
{
    /// <summary>Bits per operation: 128 KiB of bitmap, which stays in L2.</summary>
    private const int Bits = 1 << 20;

    /// <summary>Bytes of the bitmap.</summary>
    private const int Bytes = Bits / 8;

    private byte[] _bits = [];
    private byte[] _uniform = [];
    private byte[] _destination = [];
    private byte[] _values = [];
    private byte[] _packed = [];

    /// <summary>What one invocation moves, which is not the same for every arm.</summary>
    /// <param name="method">The arm.</param>
    /// <param name="parameters">Unused; this class has no <c>[Params]</c>.</param>
    /// <remarks>
    /// `PackBytes` reads one BYTE per row and writes one bit, so it touches nine times what
    /// `Classify` does over the same rows. Declaring one number for the class would have made that
    /// arm read nine times too slow.
    /// </remarks>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => method switch
    {
        nameof(PackScalar) or nameof(PackLibrary) => (Bits, Bits + Bytes),
        nameof(CopyScalar) or nameof(CopyLibrary) => (Bits, 2L * Bytes),
        _ => (Bits, Bytes),
    };

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260914);

        _bits = new byte[Bytes];
        random.NextBytes(_bits);

        // CLASSIFY GETS A UNIFORM BITMAP, and that is the whole measurement. On a mixed one both
        // arms settle in the first byte or two and BenchmarkDotNet reports ZeroMeasurement -- the
        // early exit is measured, not the loop. The case the kernel's own header claims ("on an
        // all-invalid million-row column it was 36% of the scan") is the uniform one, where no
        // exit fires and the whole range is walked. A validity bitmap that is all-valid or
        // all-invalid is also the common case: it is what decides that the bitmap is dropped.
        _uniform = new byte[Bytes];
        _uniform.AsSpan().Fill(0xFF);

        _destination = new byte[Bytes + 1];

        // One byte per value, 0 or 1, which is exactly `vortex.bytebool`'s buffer.
        _values = new byte[Bits];
        for (int i = 0; i < _values.Length; i++)
        {
            _values[i] = (byte)(random.Next(0, 4) == 0 ? 0 : 1);
        }

        _packed = new byte[Bytes];
    }

    [BenchmarkCategory("classify")]
    [Benchmark(Baseline = true, Description = "classify, per byte")]
    public bool ClassifyScalar()
    {
        ClassifyPerByte(_uniform, 3, Bits - 5, out bool anySet, out bool anyClear);
        return anySet | anyClear;
    }

    [BenchmarkCategory("classify")]
    [Benchmark(Description = "classify, library")]
    public bool ClassifyLibrary()
    {
        BitmapKernels.Classify(_uniform, 3, Bits - 5, out bool anySet, out bool anyClear);
        return anySet | anyClear;
    }

    [BenchmarkCategory("count")]
    [Benchmark(Baseline = true, Description = "count set, per bit")]
    public int CountScalar() => CountSetPerBit(_bits, 3, Bits - 5);

    [BenchmarkCategory("count")]
    [Benchmark(Description = "count set, library")]
    public int CountLibrary() => BitmapKernels.CountSet(_bits, 3, Bits - 5);

    [BenchmarkCategory("copy")]
    [Benchmark(Baseline = true, Description = "copy range, per bit")]
    public int CopyScalar()
    {
        CopyRangePerBit(_bits, 3, _destination, 5, Bits - 5);
        return _destination[0];
    }

    [BenchmarkCategory("copy")]
    [Benchmark(Description = "copy range, library")]
    public int CopyLibrary()
    {
        BitmapKernels.CopyRange(_bits, 3, _destination, 5, Bits - 5);
        return _destination[0];
    }

    [BenchmarkCategory("pack")]
    [Benchmark(Baseline = true, Description = "pack bytes, per value")]
    public int PackScalar()
    {
        PackBytesPerValue(_values, _packed);
        return _packed[0];
    }

    [BenchmarkCategory("pack")]
    [Benchmark(Description = "pack bytes, library")]
    public int PackLibrary()
    {
        BitmapKernels.PackBytes(_values, _packed);
        return _packed[0];
    }

    /// <summary>`Classify` as it was: a byte at a time, with both edge masks recomputed per byte.</summary>
    private static void ClassifyPerByte(
        ReadOnlySpan<byte> bits, int bitOffset, int length, out bool anySet, out bool anyClear)
    {
        anySet = false;
        anyClear = false;
        int firstByte = bitOffset >> 3;
        long endExclusive = (long)bitOffset + length;
        int lastByte = (int)((endExclusive - 1) >> 3);
        int lo = bitOffset & 7;
        int hi = (int)((endExclusive - 1) & 7) + 1;

        for (int index = firstByte; index <= lastByte; index++)
        {
            int low = index == firstByte ? lo : 0;
            int high = index == lastByte ? hi : 8;
            byte mask = (byte)(((1 << high) - 1) & ~((1 << low) - 1));
            byte value = (byte)(bits[index] & mask);
            anySet |= value != 0;
            anyClear |= value != mask;
            if (anySet && anyClear)
            {
                return;
            }
        }
    }

    /// <summary>`CountSet` as it was: one bit at a time.</summary>
    private static int CountSetPerBit(ReadOnlySpan<byte> bits, int start, int count)
    {
        int set = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            set += (bits[bit >> 3] >> (bit & 7)) & 1;
        }

        return set;
    }

    /// <summary>`CopyRange` as it was: one bit at a time, read-modify-write on the destination.</summary>
    private static void CopyRangePerBit(
        ReadOnlySpan<byte> source, int sourceStart, Span<byte> destination, int destinationStart,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            int from = sourceStart + i;
            int into = destinationStart + i;
            int bit = (source[from >> 3] >> (from & 7)) & 1;
            int index = into >> 3;
            int shift = into & 7;
            destination[index] = (byte)((destination[index] & ~(1 << shift)) | (bit << shift));
        }
    }

    /// <summary>`PackBytes` as it was: a read-modify-write of the destination byte per value.</summary>
    private static void PackBytesPerValue(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        destination[..((source.Length + 7) / 8)].Clear();
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != 0)
            {
                destination[i >> 3] |= (byte)(1 << (i & 7));
            }
        }
    }
}
