// Does the alignment of a kernel's buffers change its number? A curve, so `explore`.
//
// BENCH-AUDIT.md §4.4 asks the kernel classes for buffers "aligned like the library's", because
// the library decodes into `CanonicalArena` allocations that carry an explicit alignment while
// these classes hand their kernels a plain `new byte[]` off the GC heap. If the two differ enough
// to move a number, every kernel figure in this repository is measured on a buffer the library
// never uses.
//
// It is a curve and not a pair of arms, so it lives in `explore` and stays out of the run with no
// argument: the answer is wanted ONCE, written down, and then the classes can say why they do what
// they do. §4.4 already carries one measurement of it -- 64-byte aligned against offset by 8, on
// `i64 vector`, 25.2 against 25.6 us -- on one kernel, before the class measured the shipped path.
//
// HOW THE SKEW IS MADE. A pinned array's data address does not move, so the offset from it to the
// next 64-byte boundary is computed once and the span starts there (skew 0) or eight bytes past it
// (skew 8). Eight rather than one because a byte-misaligned span is not a case the library can
// produce: `Alignment` aligns to a power of two at least the element width, so the realistic
// question is "aligned to 64, or aligned to 8".
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>The same kernels on 64-byte-aligned buffers and on buffers eight bytes past.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AlignmentBenchmarks
{
    private const int Bits = 1 << 20;
    private const int Bytes = Bits / 8;
    private const int Blocks = 64;
    private const int BitWidth = 17;

    /// <summary>Bytes between the 64-byte boundary and the start of every buffer.</summary>
    [Params(0, 8)]
    public int Skew { get; set; }

    private byte[] _uniformArray = [];
    private byte[] _valuesArray = [];
    private byte[] _packedArray = [];
    private ulong[] _fastlanesPacked = [];
    private ulong[] _fastlanesOutput = [];
    private int _uniformStart;
    private int _valuesStart;
    private int _packedStart;
    private int _fastlanesPackedStart;
    private int _fastlanesOutputStart;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260914);

        _uniformArray = Pinned<byte>(Bytes, out _uniformStart);
        _uniformArray.AsSpan(_uniformStart, Bytes).Fill(0xFF);

        _valuesArray = Pinned<byte>(Bits, out _valuesStart);
        Span<byte> values = _valuesArray.AsSpan(_valuesStart, Bits);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (byte)(random.Next(0, 4) == 0 ? 0 : 1);
        }

        _packedArray = Pinned<byte>(Bytes, out _packedStart);

        int words = 16 * BitWidth * Blocks;
        _fastlanesPacked = Pinned<ulong>(words, out _fastlanesPackedStart);
        random.NextBytes(
            MemoryMarshal.AsBytes(_fastlanesPacked.AsSpan(_fastlanesPackedStart, words)));
        _fastlanesOutput = Pinned<ulong>(Blocks * 1024, out _fastlanesOutputStart);
    }

    [Benchmark(Description = "bitmap classify")]
    public bool Classify()
    {
        BitmapKernels.Classify(
            _uniformArray.AsSpan(_uniformStart, Bytes), 3, Bits - 5,
            out bool anySet, out bool anyClear);
        return anySet | anyClear;
    }

    [Benchmark(Description = "bitmap pack bytes")]
    public int PackBytes()
    {
        BitmapKernels.PackBytes(
            _valuesArray.AsSpan(_valuesStart, Bits), _packedArray.AsSpan(_packedStart, Bytes));
        return _packedArray[_packedStart];
    }

    [Benchmark(Description = "fastlanes unpack")]
    public ulong Unpack()
    {
        int words = 16 * BitWidth * Blocks;
        FastLanes.UnpackBlocks<ulong>(
            _fastlanesPacked.AsSpan(_fastlanesPackedStart, words),
            BitWidth,
            _fastlanesOutput.AsSpan(_fastlanesOutputStart, Blocks * 1024),
            Blocks);
        return _fastlanesOutput[_fastlanesOutputStart];
    }

    /// <summary>
    /// A pinned array with room to start <see cref="Skew"/> bytes past a 64-byte boundary, and the
    /// element index of that start.
    /// </summary>
    /// <typeparam name="T">The element type; the skew is applied in BYTES and must divide it.</typeparam>
    /// <param name="length">Usable elements.</param>
    /// <param name="start">The element index to start at.</param>
    private T[] Pinned<T>(int length, out int start)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        T[] array = GC.AllocateUninitializedArray<T>(length + (128 / size), pinned: true);
        nint address = Unsafe.ByteOffset(
            ref Unsafe.NullRef<byte>(),
            ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)));
        int toBoundary = (int)((64 - (address & 63)) & 63);
        start = (toBoundary + Skew) / size;
        return array;
    }
}
