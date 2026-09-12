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
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
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

        PType ptype = node.PType;
        ReadOnlySpan<byte> values = node.Values.Span;
        bool signed = ptype.IsSignedInteger();

        Int128 first = Read(values, ptype, signed, 0);
        Int128 second = Read(values, ptype, signed, 1);
        Int128 step = second - first;

        Int128 previous = second;
        for (int row = 2; row < node.Length; row++)
        {
            Int128 value = Read(values, ptype, signed, row);
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

        ulong baseBits = signed
            ? unchecked((ulong)(long)first)
            : (ulong)first;

        return new SequencePlan(baseBits, step);
    }

    /// <summary>Whether the step is written as <c>uint64_value</c> rather than <c>int64_value</c>.</summary>
    /// <remarks>
    /// Only for the one case the signed field cannot hold: an unsigned column climbing by more than
    /// <see cref="long.MaxValue"/> a row. Everything else takes the signed field, which is what the
    /// reference emits for both the i32 and i64 sequences in the corpus.
    /// </remarks>
    internal bool StepIsUnsigned => Step > long.MaxValue;

    private static Int128 Read(ReadOnlySpan<byte> values, PType ptype, bool signed, int row) =>
        signed
            ? CanonicalSupport.ReadInteger(values, ptype, row)
            : (Int128)CompressedValues.ReadUnsigned(values, ptype, row);
}
