// The filter's comparison kernel: what the library still pays over the best scalar loop.
//
// TWO ARMS, AND ONLY TWO (BENCH-AUDIT.md §3.1). This file was written to decompose three:
//
//   * SWITCHED     - a per-element `PType` switch, operator switch and validity check, the shape the
//                    library had. It priced the BRANCHING audit's remedy at 6x, that 6x was banked,
//                    and the library no longer has the shape - so the arm was history, not a control.
//   * VECTORIZED   - the hoisted shape a `Vector128` at a time. A SETTLED NEGATIVE RESULT
//                    (PERF-AUDIT-v2.md §9): slower than the scalar loop WITH NEON available, because
//                    `Trilean` is one byte per row and each 64-bit lane's result has to be extracted
//                    and stored on its own - the per-lane loop costs more than the comparison saves.
//                    The blocker is the OUTPUT REPRESENTATION, not the arithmetic. Re-measuring a
//                    closed question every run is what §3.1 calls a museum piece.
//
// Both figures stay in bench/BASELINE.md and in the commits that took them. What remains is the pair
// that can still move:
//
//   * HOISTED      - all three branches lifted out of the loop, still scalar, still one value at a
//                    time. The FLOOR, and the baseline.
//   * LIBRARY      - `ComparisonKernels.Compare` as it stands, through THE entry point the scan
//                    calls, over an arena node built here from the same values the other arm sees.
//                    It went through a second entry point, `CompareForBenchmark`, until 2026-09-14:
//                    a shape that could drift from `Compare` without anything noticing, which is
//                    the failure this arm exists to prevent (BENCH-AUDIT.md E3). Its
//                    distance from HOISTED is what the library still pays, including the validity
//                    resolution and the bounds checks the bare arm does not have. A benchmark whose
//                    control is a hand-written copy of "the library shape" cannot say whether the
//                    library still has that shape; this arm is why the 6x above could be confirmed
//                    collected rather than assumed.
//
// THE AUTO-VECTORIZATION CHECK the programme requires is meant to be `[DisassemblyDiagnoser]`, and
// this project cannot run it: BenchmarkDotNet's disassembler needs the out-of-process toolchain that
// net11.0 support is missing (see BenchmarkConfig). Running each arm a second time under
// DOTNET_EnableHWIntrinsic=0 answers the same question without a listing - code the JIT vectorized
// slows down, code it did not is unchanged. That answered the open question here: the hoisted scalar
// arm is IDENTICAL with intrinsics disabled (16.11 -> 16.17 us), so RyuJIT is not vectorizing it and
// the headroom is real.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>A `<` predicate over an i64 column: the library against the best scalar loop.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class FilterKernelBenchmarks
{
    /// <summary>Trilean's three states, as the kernels write them.</summary>
    private const byte True = 1;
    private const byte False = 0;

    private byte[] _values = [];
    private byte[] _destination = [];
    private long _wanted;
    private CanonicalArena? _arena;
    private int _node;

    /// <summary>Rows compared per operation: one split's worth, several times over.</summary>
    [Params(65536)]
    public int Count { get; set; } = 65536;

    [GlobalSetup]
    public void Setup()
    {
        _values = new byte[Count * 8];
        _destination = new byte[Count];
        Random random = new Random(20260912);
        Span<long> typed = MemoryMarshal.Cast<byte, long>(_values);
        for (int i = 0; i < Count; i++)
        {
            typed[i] = random.Next(1 << 20);
        }

        // Selective, like the predicate the scan benchmarks use: a narrow band, not half the column.
        _wanted = 1024;

        // THE SAME BYTES IN AN ARENA, so the library arm can go through `Compare` -- which needs a
        // node, not a span -- while still comparing the values the bare arm compares. Building the
        // node here rather than opening a corpus file keeps the two arms on one array and keeps the
        // row count the parameter says it is.
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.AllocateUninitialized(_values.Length, 8, out Span<byte> into);
        _values.AsSpan().CopyTo(into);
        _node = _arena.AddPrimitive(
            types.Primitive(PType.I64, Nullability.NonNullable),
            Count,
            Validity.NonNullable,
            PType.I64,
            buffer);
    }

    [GlobalCleanup]
    public void Cleanup() => _arena?.Reset();

    /// <summary>What the library actually runs, through its own entry point.</summary>
    /// <remarks>
    /// THE ARM THIS FILE WAS MISSING, and FsstKernelBenchmarks records exactly why it matters: a
    /// benchmark whose control is a hand-written copy of "the library shape" cannot say whether the
    /// library still has that shape. It said 6.0x for two audits running and nothing ever checked
    /// whether the 6.0x had been collected. This arm closes that: its distance from
    /// <see cref="Hoisted"/> is what the library still pays over the best scalar loop, including
    /// the validity resolution and the bounds checks that the bare arm does not have.
    /// </remarks>
    [Benchmark(Description = "library")]
    public int Library()
    {
        ComparisonKernels.Compare(
            _arena!, _node, ComparisonOp.Less, FilterLiteral.From(_wanted), _destination);
        return _destination.Length;
    }

    /// <summary>The same work, all three branches hoisted out of the loop. Still one at a time.</summary>
    [Benchmark(Baseline = true, Description = "branches hoisted, still scalar")]
    public int Hoisted()
    {
        ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(_values);
        Span<byte> destination = _destination;
        long wanted = _wanted;

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = values[i] < wanted ? True : False;
        }

        return destination.Length;
    }
}
