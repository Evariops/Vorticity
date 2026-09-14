// vortex.sequence on the write side - vortex-sequence-0.86.1/src/compress.rs.
//
// A[i] = base + i * multiplier, in the METADATA, with no children and no buffers. It is the only
// encoding here that costs nothing per row, so where it applies it is not merely the best choice,
// it is the smallest representation the format can express: a `vortex.primitive` node plus its
// buffer becomes one node and about thirty bytes.
//
// IT BECAME REACHABLE WHEN THE DEFAULT TARGET WAS CORRECTED. docs/90-registry.md listed
// `vortex.sequence` under "not in the default target" for as long as that target was believed to be
// `core2025.05.0`; it arrived in `core2025.06.0`, and the default is now `core2026.08.3`. What had
// been recorded as an edition difference was an implementation gap the whole time - three of the
// corpus files we were worst on (`types/fsl_i32_3_*` at 23x, `types/date_ms_nonnull_r8193` at 18x,
// `types/struct_field_names` at 5.2x) are sequences and nothing else.
//
// The wire shape is read off the reference's own files rather than inferred. `fsl_i32_3`'s metadata
// is `0a02 1800 1202 1802`: base and multiplier are both `int64_value`, sint64-encoded, for an i32
// column. Signedness is preserved on the wire, width is not, which is exactly what
// `multiplier_ptype_from_proto` says.
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>An arithmetic progression: the value at row 0 and the step between rows.</summary>
internal sealed class SequencePlan
{
    private SequencePlan(ulong baseBits, Int128 step)
    {
        BaseBits = baseBits;
        Step = step;
    }

    /// <summary>The value at row 0, as the column's own raw bits.</summary>
    internal ulong BaseBits { get; }

    /// <summary>
    /// The step, exact. Held as <see cref="Int128"/> because the difference of two values of a
    /// 64-bit column does not fit in 64 bits in general, and a checked width test is what decides
    /// whether the sequence is expressible at all.
    /// </summary>
    internal Int128 Step { get; }

    /// <summary>
    /// Whether the column is an arithmetic progression with no nulls.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">The column chunk.</param>
    /// <returns>The plan, or <see langword="null"/>.</returns>
    /// <remarks>
    /// NULLS DISQUALIFY IT OUTRIGHT, and not as a simplification: the encoding has no children, so
    /// there is nowhere for a validity bitmap to live. A nullable dtype whose rows all happen to be
    /// valid is fine - the decoder derives AllValid from the nullability - but one null row is not.
    ///
    /// Two rows are enough to define a step and the check is O(n) with no allocation, so this runs
    /// before the run and distinct passes rather than after: when it applies it ends the search.
    /// </remarks>
    internal static SequencePlan? TryBuild(CanonicalArena arena, CanonicalNode node)
    {
        if (node.Kind != CanonicalKind.Primitive || !node.PType.IsInteger() || node.Length < 2)
        {
            return null;
        }

        if (node.Validity.Kind is not (ValidityKind.NonNullable or ValidityKind.AllValid))
        {
            return null;
        }

        ReadOnlySpan<byte> values = node.Values.Span;
        int length = node.Length;

        // PERF-AUDIT-v2.md W-9. THE WALK IS NOT CHEAP, which the point recorded as a suspicion and
        // the probe settled: on `--throughput --write` at a million rows it reads **78 % of the rows
        // it is offered** -- 1 352 columns of 1 728 really are sequences, so the loop runs to the
        // end rather than bailing at row 3 -- and doubling it costs **26 %** of a `sequence` write
        // and **25 %** of a `primitive` one. Two `switch`es per row, on a property of the CALL.
        //
        // Resolved once instead, the way `RowKernels.Gather` resolves its codes. `Int128` stays in
        // the arithmetic: a step is the difference of two 64-bit values and does not fit in 64 bits
        // in general, which is the whole reason this class holds one.
        return node.PType switch
        {
            PType.U8 => Build<byte>(values, length),
            PType.U16 => Build<ushort>(values, length),
            PType.U32 => Build<uint>(values, length),
            PType.U64 => Build<ulong>(values, length),
            PType.I8 => Build<sbyte>(values, length),
            PType.I16 => Build<short>(values, length),
            PType.I32 => Build<int>(values, length),
            _ => Build<long>(values, length),
        };
    }

    /// <summary>Whether the step is written as <c>uint64_value</c> rather than <c>int64_value</c>.</summary>
    /// <remarks>
    /// Only for the one case the signed field cannot hold: an unsigned column climbing by more than
    /// <see cref="long.MaxValue"/> a row. Everything else takes the signed field, which is what the
    /// reference emits for both the i32 and i64 sequences in the corpus.
    /// </remarks>
    internal bool StepIsUnsigned => Step > long.MaxValue;

    /// <summary>The walk, with the physical type resolved and the span cast once.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Int128.CreateTruncating{TOther}(TOther)"/> reproduces what the two helpers it
    /// replaces did, and does it from the TYPE rather than from a flag: a signed
    /// <typeparamref name="T"/> sign-extends as <c>CanonicalSupport.ReadInteger</c> did, an
    /// unsigned one zero-extends as <c>CompressedValues.ReadUnsigned</c> did. `ReadInteger`'s
    /// saturation of a <c>u64</c> never applied here -- it is only reached through the signed
    /// branch, which an unsigned column never took.
    /// </para>
    /// <para>
    /// The base is the column's OWN raw bits, so it is re-read from the typed value rather than
    /// narrowed from the <see cref="Int128"/>: for a signed column that is the two's-complement
    /// pattern, for an unsigned one the value itself.
    /// </para>
    /// </remarks>
    private static SequencePlan? Build<T>(ReadOnlySpan<byte> raw, int length)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(raw)[..length];

        Int128 first = Int128.CreateTruncating(values[0]);
        Int128 step = Int128.CreateTruncating(values[1]) - first;
        Int128 previous = first + step;

        for (int row = 2; row < values.Length; row++)
        {
            Int128 value = Int128.CreateTruncating(values[row]);
            if (value - previous != step)
            {
                return null;
            }

            previous = value;
        }

        // The wire carries the multiplier as either int64_value or uint64_value, so a step outside
        // both is not expressible. Only reachable on a two-row column of 64-bit extremes - a third
        // row would have overflowed the column itself - but the write must fail the check rather
        // than truncate.
        if (step < long.MinValue || step > (Int128)ulong.MaxValue)
        {
            return null;
        }

        return new SequencePlan(BaseBitsOf(values[0]), step);
    }

    /// <summary>Row 0 as the column's own raw bits, which is what the metadata carries.</summary>
    private static ulong BaseBitsOf<T>(T value)
        where T : unmanaged, IBinaryInteger<T> =>
        T.IsNegative(value)
            ? unchecked((ulong)long.CreateTruncating(value))
            : ulong.CreateTruncating(value);
}
