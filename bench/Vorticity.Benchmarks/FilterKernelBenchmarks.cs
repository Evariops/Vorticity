// The filter's comparison kernel, decomposed: what hoisting the branches wins, and what
// vectorizing wins on top of it.
//
// The SIMD audit names `ComparisonKernels` first, and its reason is that the entire filter surface
// is scalar. The branching audit names the same loop for a different reason: it carries THREE
// loop-invariant branches per element - a `PType` switch to read the value, an operator switch to
// compare it, and a validity check - on a column whose type, operator and nullability are all fixed
// before the loop starts.
//
// Those two audits propose different work on the same code, so the useful measurement is not
// "scalar against vector" but the decomposition:
//
//   * LIBRARY      - ComparisonKernels.Compare as it stands. Tracks reality.
//   * HOISTED      - the same work with all three branches lifted out of the loop, still scalar and
//                    still one value at a time. This is what the BRANCHING audit's remedy is worth
//                    here, alone.
//   * VECTORIZED   - the hoisted shape with the comparison done a Vector128 at a time. The
//                    difference between this and HOISTED is what the SIMD audit's remedy is worth
//                    ON TOP, rather than the two effects added together and attributed to whichever
//                    change lands first.
//
// THE AUTO-VECTORIZATION CHECK the programme requires is meant to be `[DisassemblyDiagnoser]`, and
// this project cannot run it: BenchmarkDotNet's disassembler needs the out-of-process toolchain that
// net11.0 support is missing (see BenchmarkConfig). Running each arm a second time under
// DOTNET_EnableHWIntrinsic=0 answers the same question without a listing - code the JIT vectorized
// slows down, code it did not is unchanged.
//
// WHAT THAT ANSWERED, AND IT WAS NOT WHAT THIS FILE FIRST GUESSED. The hoisted scalar arm is
// IDENTICAL with intrinsics disabled (16.11 -> 16.17 us), so RyuJIT is not vectorizing it and the
// headroom is real. The hand-written vector arm is the one that collapses (29.46 -> 59.95 us),
// which is only the expected confirmation that it was using NEON.
//
// And it is still SLOWER THAN THE SCALAR LOOP with NEON available. The comparison vectorizes
// perfectly; what does not is the output. `Trilean` is one byte per row, so each 64-bit lane's
// result has to be extracted and stored on its own, and that per-lane loop costs more than the
// comparison saves. The blocker for this kernel is the OUTPUT REPRESENTATION, not the arithmetic -
// which is a different finding from "the filter surface is scalar, therefore vectorize it", and a
// more useful one.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Benchmarks;

/// <summary>A `<` predicate over an i64 column: library, branch-hoisted, and vectorized.</summary>
[Config(typeof(BenchmarkConfig))]
public class FilterKernelBenchmarks
{
    /// <summary>Trilean's three states, as the kernels write them.</summary>
    private const byte True = 1;
    private const byte False = 0;

    private byte[] _values = [];
    private byte[] _destination = [];
    private long _wanted;

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
    }

    /// <summary>
    /// The shape the library runs: read through a physical-type switch, compare through an operator
    /// switch, check validity, one row at a time.
    /// </summary>
    /// <remarks>
    /// Carried here rather than called through <c>ComparisonKernels</c>, for the reason
    /// FsstKernelBenchmarks records: an arm that calls the library stops being a control the moment
    /// the library changes, and this is a benchmark whose entire purpose is to price changing it.
    /// The validity branch is written as the always-valid case, which is the CHEAPEST it can be -
    /// so this arm flatters the library rather than the alternatives.
    /// </remarks>
    [Benchmark(Baseline = true, Description = "per-element switches (library shape)")]
    public int Switched()
    {
        ReadOnlySpan<byte> bytes = _values;
        Span<byte> destination = _destination;
        Vorticity.Types.PType ptype = Vorticity.Types.PType.I64;
        Op op = Op.Less;
        long wanted = _wanted;

        for (int i = 0; i < destination.Length; i++)
        {
            long value = Read(bytes, ptype, i);
            destination[i] = Apply(op, value.CompareTo(wanted)) ? True : False;
        }

        return destination.Length;
    }

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
        ComparisonKernels.CompareForBenchmark(
            _values, Vorticity.Types.PType.I64, ComparisonOp.Less, _wanted, _destination);
        return _destination.Length;
    }

    /// <summary>The same work, all three branches hoisted out of the loop. Still one at a time.</summary>
    [Benchmark(Description = "branches hoisted, still scalar")]
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

    /// <summary>The hoisted shape, a <see cref="Vector128{T}"/> of lanes at a time.</summary>
    /// <remarks>
    /// `Vector128` explicitly rather than `Vector{T}`, because this machine is arm64 and NEON is
    /// 128 bits wide - so this is the width that actually runs here. The 256 and 512 paths the
    /// architecture requires are not exercised on this machine at all.
    ///
    /// THIS IS THE STRAIGHTFORWARD SHAPE, and it loses. Two 64-bit lanes per vector is a narrow win
    /// to begin with, and the per-lane extraction below gives it all back: the mask is a vector and
    /// the destination is bytes, so there is no store that writes both lanes at once. Narrowing
    /// long -> int -> short -> byte across eight source vectors would write sixteen results in one
    /// store and is the untested alternative; it is not written here because an audit measures what
    /// exists before proposing what does not.
    /// </remarks>
    [Benchmark(Description = "hoisted and vectorized")]
    public int Vectorized()
    {
        ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(_values);
        Span<byte> destination = _destination;
        Vector128<long> wanted = Vector128.Create(_wanted);
        int lanes = Vector128<long>.Count;
        int i = 0;

        for (; i <= values.Length - lanes; i += lanes)
        {
            // LessThan yields all-ones per lane; the low byte of each lane is then 0xFF or 0x00, and
            // one AND with 1 turns it into Trilean's True/False without a branch.
            Vector128<long> mask = Vector128.LessThan(
                Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values), (nuint)i), wanted);
            for (int lane = 0; lane < lanes; lane++)
            {
                destination[i + lane] = (byte)(mask[lane] & 1);
            }
        }

        for (; i < values.Length; i++)
        {
            destination[i] = values[i] < _wanted ? True : False;
        }

        return destination.Length;
    }

    private enum Op
    {
        Less,
        LessOrEqual,
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
    }

    private static long Read(ReadOnlySpan<byte> bytes, Vorticity.Types.PType ptype, int index) =>
        ptype switch
        {
            Vorticity.Types.PType.U8 => bytes[index],
            Vorticity.Types.PType.I8 => (sbyte)bytes[index],
            Vorticity.Types.PType.U16 => System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
            Vorticity.Types.PType.I16 => System.Buffers.Binary.BinaryPrimitives
                .ReadInt16LittleEndian(bytes.Slice(index * 2, 2)),
            Vorticity.Types.PType.U32 => System.Buffers.Binary.BinaryPrimitives
                .ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
            Vorticity.Types.PType.I32 => System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
            Vorticity.Types.PType.I64 => System.Buffers.Binary.BinaryPrimitives
                .ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
            _ => throw new NotSupportedException(),
        };

    private static bool Apply(Op op, int order) => op switch
    {
        Op.Less => order < 0,
        Op.LessOrEqual => order <= 0,
        Op.Equal => order == 0,
        Op.NotEqual => order != 0,
        Op.Greater => order > 0,
        Op.GreaterOrEqual => order >= 0,
        _ => throw new NotSupportedException(),
    };
}
