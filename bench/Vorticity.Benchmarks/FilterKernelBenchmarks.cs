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

    /// <summary>The same i64 column under a scattered validity bitmap. BENCH-AUDIT.md B27.</summary>
    /// <remarks>
    /// EVERY OTHER NODE IN THIS CLASS IS NonNullable, so the four arms all take the `AllValid` fast
    /// path -- and v2 F11 disassembled that path: seven instructions, one `cset`, and the only jump
    /// is the loop's own back-edge. There is nothing there to make branchless. The loop that DOES
    /// branch is the nullable fallback, one `cbz` on the validity bit per row plus an unconditional
    /// `b`, because the JIT will not speculate the load of `values[i]` for an invalid row. It had no
    /// number because nothing reached it.
    /// </remarks>
    private int _nullableNode;

    // PERF-AUDIT-v2.md F-10. The four columns below exist so that the four kernels F-4 names are
    // REACHED by something. Counted on the two `--ratio-check` filter axes, which are F-4's own
    // closing criterion: 58 904 calls and 60,3 M rows, ALL of them through `CompareSigned`. `In`,
    // `CompareBool`, `CompareFloat` and `CompareBytes` each saw zero. Their correctness is covered
    // -- `ScanFilterTests` exercises `In`, `f64` with NaN and `utf8` -- but a correctness test says
    // nothing about cost, and eight points of this audit were written by reading a file rather than
    // measuring one.
    private int _floatNode;
    private int _boolNode;
    private int _utf8Node;
    private byte[] _scratch = [];
    private FilterLiteral[] _inSet = [];
    private InSet? _inPrepared;

    // Built once: `FilterLiteral.From(string)` encodes, and an allocation inside the timed body
    // would be charged to the kernel it is meant to measure.
    private FilterLiteral _utf8Wanted;

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

        // B27: the same values under a SCATTERED bitmap -- pseudo-random, not a run and not
        // all-valid. The distribution is the measurement: a branch the predictor gets right is free,
        // so a tidy pattern would report that the nullable path costs nothing, which is the lie in
        // the other direction. One row in four is null, drawn from the same seeded generator.
        VortexBuffer validityBits =
            _arena.AllocateUninitialized((Count + 7) / 8, 1, out Span<byte> bitsInto);
        bitsInto.Clear();
        Random validity = new Random(20260915);
        for (int i = 0; i < Count; i++)
        {
            if (validity.Next(4) != 0)
            {
                bitsInto[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        int bitmap = _arena.AddBool(
            types.Bool(Nullability.NonNullable), Count, Validity.NonNullable, validityBits, 0);
        _nullableNode = _arena.AddPrimitive(
            types.Primitive(PType.I64, Nullability.Nullable),
            Count,
            Validity.Bitmap(bitmap),
            PType.I64,
            buffer);

        // f64, the same values, so the only difference between this arm and the i64 one is which
        // kernel runs.
        VortexBuffer floats = _arena.AllocateUninitialized(Count * 8, 8, out Span<byte> floatBytes);
        Span<double> asDouble = MemoryMarshal.Cast<byte, double>(floatBytes);
        for (int i = 0; i < Count; i++)
        {
            asDouble[i] = typed[i];
        }

        _floatNode = _arena.AddPrimitive(
            types.Primitive(PType.F64, Nullability.NonNullable),
            Count, Validity.NonNullable, PType.F64, floats);

        // Bool, one bit per row, the same band so the selectivity matches.
        VortexBuffer bits = _arena.AllocateUninitialized((Count + 7) / 8, 8, out Span<byte> bitBytes);
        bitBytes.Clear();
        for (int i = 0; i < Count; i++)
        {
            if (typed[i] < _wanted)
            {
                bitBytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        _boolNode = _arena.AddBool(types.Bool(Nullability.NonNullable), Count, Validity.NonNullable, bits, 0);

        // Utf8, as views. Every value is short enough to live inline, which is the shape 65 % of a
        // real VarBinView carries (PERF-AUDIT-v2.md R28) and the one `CompareBytes` sees most.
        VortexBuffer views = _arena.AllocateUninitialized(Count * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        for (int i = 0; i < Count; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, 6);
            System.Text.Encoding.ASCII.GetBytes($"v{typed[i] % 100000:D5}", view[4..]);
        }

        _utf8Node = _arena.AddVarBinView(
            types.Utf8(Nullability.NonNullable), Count, Validity.NonNullable, views, default);

        // `In` is an OR of equalities, so its cost is the set size times one Compare plus the ORs.
        // Eight candidates: enough that the fold is visible, few enough to stay a realistic `IN`.
        _utf8Wanted = FilterLiteral.From("v01024");
        _scratch = new byte[Count];
        _inSet = new FilterLiteral[8];
        for (int i = 0; i < _inSet.Length; i++)
        {
            _inSet[i] = FilterLiteral.From((long)(i * 131));
        }

        // Hashed here rather than in the timed body, which is where a scan hashes it too: once for
        // the whole file, not once per batch.
        _inPrepared = InSet.TryBuild(_inSet, signed: true);
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

    /// <summary>The same comparison on a nullable column: the branchy half. BENCH-AUDIT.md B27.</summary>
    /// <remarks>
    /// Its only difference from <see cref="Library"/> is the validity, so the distance between the
    /// two arms IS what the per-row branch and the bit test cost -- the values, the operator and the
    /// literal are the same array and the same constants.
    /// </remarks>
    [Benchmark(Description = "i64 < literal, nullable, library")]
    public int LibraryNullable()
    {
        ComparisonKernels.Compare(
            _arena!, _nullableNode, ComparisonOp.Less, FilterLiteral.From(_wanted), _destination);
        return _destination.Length;
    }

    /// <summary>`i64 &lt; 3.5`: an integer column against a float literal, widened per row.</summary>
    /// <remarks>
    /// The same column and operator as <see cref="Library"/>, the literal a double instead of a
    /// long. Its distance from that arm is what widening costs and nothing else, which is the
    /// question CO-1 asks: the answer has to be in the same neighbourhood, because the kernel is
    /// the same kernel with a wider accumulator type.
    /// </remarks>
    [Benchmark(Description = "library, i64 < float")]
    public int LibraryAgainstFloat()
    {
        ComparisonKernels.Compare(
            _arena!, _node, ComparisonOp.Less, FilterLiteral.From(3.5), _destination);
        return _destination.Length;
    }

    /// <summary>`f64 &lt; literal`, through <c>CompareFloat</c>.</summary>
    /// <remarks>
    /// PERF-AUDIT-v2.md F-10. Same values as the i64 arm, widened: the distance between the two is
    /// what the float kernel costs over the signed one, and nothing else.
    /// </remarks>
    [Benchmark(Description = "library, f64 <")]
    public int LibraryFloat()
    {
        ComparisonKernels.Compare(
            _arena!, _floatNode, ComparisonOp.Less, FilterLiteral.From((double)_wanted), _destination);
        return _destination.Length;
    }

    /// <summary>`bool = literal`, through <c>CompareBool</c>.</summary>
    [Benchmark(Description = "library, bool =")]
    public int LibraryBool()
    {
        ComparisonKernels.Compare(
            _arena!, _boolNode, ComparisonOp.Equal, FilterLiteral.From(true), _destination);
        return _destination.Length;
    }

    /// <summary>`utf8 = literal`, through <c>CompareBytes</c>.</summary>
    [Benchmark(Description = "library, utf8 =")]
    public int LibraryUtf8()
    {
        ComparisonKernels.Compare(
            _arena!, _utf8Node, ComparisonOp.Equal, _utf8Wanted, _destination);
        return _destination.Length;
    }

    /// <summary>`i64 IN (eight candidates)`, through <c>In</c>.</summary>
    /// <remarks>
    /// One pass over the column against a set hashed beforehand, which is what a scan does: the
    /// candidates are prepared once for the whole file, so what this times is the row loop and not
    /// the table. Folding N equalities with <c>Trilean.Or</c> was the shape before, and it made the
    /// reading N passes plus N-1 ORs; passing no set still reaches it.
    /// </remarks>
    [Benchmark(Description = "library, i64 IN (8)")]
    public int LibraryIn()
    {
        ComparisonKernels.In(_arena!, _node, _inSet, _destination, _scratch, _inPrepared);
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
