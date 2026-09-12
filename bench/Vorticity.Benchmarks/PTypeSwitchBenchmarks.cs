// What a physical-type switch evaluated PER ELEMENT actually costs.
//
// The optimization programme's branching audit names this as its main lead, and it names it with a
// table: `CompressedValues.ReadUnsigned` and `CanonicalSupport.ReadInteger` are `switch` statements
// over `PType` called from inside per-row loops in a dozen files, and the two decoders that call
// them most per element are the two slowest in the per-encoding decode ranking. That is a
// correlation, and the table cannot be used to test it - docs/05 §1b says in as many words that
// roughly 35 us of every row there is fixed open-and-walk cost, and that between-process variance
// on that axis exceeds the effects being argued about.
//
// So this measures the switch on its own, three ways in one process:
//
//   * LIBRARY - CanonicalSupport.ReadInteger, the real thing. Tracks reality, and moves the day
//     someone specializes it.
//   * LOCAL SWITCH - the benchmark's own copy of the same shape. This is the CONTROL, and it exists
//     because FsstKernelBenchmarks lost its control exactly once by calling the library and
//     labelling the result "the old shape": the wide store then landed in the library, both arms
//     became the same code, and a 7.9x claim silently became 1.37x with nothing regressed.
//   * SPECIALIZED - the same work with the switch hoisted out of the loop by a generic type
//     parameter, which is the remedy the programme proposes and which Vorticity.RowEncoding
//     already uses for its eleven value types.
//
// The gap between the last two is the whole question: it is what the remedy could win, before
// anyone writes it into a decoder.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>A per-element PType switch against the same loop with the switch hoisted.</summary>
[Config(typeof(BenchmarkConfig))]
public class PTypeSwitchBenchmarks
{
    private byte[] _bytes = [];

    /// <summary>Elements read per operation: a batch's worth of offsets, several times over.</summary>
    [Params(8192)]
    public int Count { get; set; } = 8192;

    /// <summary>
    /// The physical types worth separating.
    /// </summary>
    /// <remarks>
    /// I32 and I64 are what offsets and lengths actually are in the corpus. U8 is here because it
    /// is the switch's FIRST arm and its cheapest body - if the cost were the branch alone rather
    /// than the missed inlining, U8 would show it most clearly.
    /// </remarks>
    [Params(PType.U8, PType.I32, PType.I64)]
    public PType Type { get; set; } = PType.I32;

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[Count * 8];
        Random random = new Random(20260912);
        random.NextBytes(_bytes);

        // Keep the values small and positive so no arm saturates or sign-extends differently; the
        // question is dispatch cost, not arithmetic.
        for (int i = 0; i < Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_bytes.AsSpan(i * 8, 8), random.Next(1 << 20));
        }
    }

    /// <summary>The library's reader, called per element.</summary>
    [Benchmark(Baseline = true, Description = "library switch, per element")]
    public long Library()
    {
        ReadOnlySpan<byte> bytes = _bytes;
        PType type = Type;
        long total = 0;
        for (int i = 0; i < Count; i++)
        {
            total += CanonicalSupport.ReadInteger(bytes, type, i);
        }

        return total;
    }

    /// <summary>The same shape, carried here so the comparison survives a change to the library.</summary>
    [Benchmark(Description = "local switch, per element")]
    public long LocalSwitch()
    {
        ReadOnlySpan<byte> bytes = _bytes;
        PType type = Type;
        long total = 0;
        for (int i = 0; i < Count; i++)
        {
            total += Read(bytes, type, i);
        }

        return total;
    }

    /// <summary>The switch hoisted out of the loop by a generic type parameter.</summary>
    [Benchmark(Description = "specialized, switch hoisted")]
    public long Specialized()
    {
        ReadOnlySpan<byte> bytes = _bytes;
        return Type switch
        {
            PType.U8 => SumU8(bytes, Count),
            PType.I32 => Sum<int>(bytes, Count),
            PType.I64 => Sum<long>(bytes, Count),
            _ => throw new NotSupportedException(),
        };
    }

    private static long Read(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.U8 => bytes[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(index * 8, 8)),
        PType.I8 => (sbyte)bytes[index],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.I64 => BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
        _ => throw new NotSupportedException(),
    };

    /// <summary>
    /// One instantiation per element type, so the JIT sees a typed span and no dispatch at all.
    /// </summary>
    /// <remarks>
    /// A generic method over a value type is compiled once PER TYPE, so `T` is known at the point
    /// the load is emitted. That is the same mechanism the programme's proposed remedy uses - it
    /// simply reaches it by a type parameter rather than by a static abstract interface member,
    /// which keeps this benchmark measuring the DISPATCH rather than the interface's own overhead.
    /// </remarks>
    private static long Sum<T>(ReadOnlySpan<byte> bytes, int count)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            total += long.CreateTruncating(values[i]);
        }

        return total;
    }

    private static long SumU8(ReadOnlySpan<byte> bytes, int count)
    {
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            total += bytes[i];
        }

        return total;
    }
}
