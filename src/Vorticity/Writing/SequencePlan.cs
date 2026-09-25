using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// An arithmetic progression: the value at row 0 and the step between rows. The encoding carries
/// both in its metadata and has no children, so there is nowhere for a validity bitmap to live and a
/// column with a null row cannot be one.
/// </summary>
/// <remarks>A value, not an object: one is priced per integer column per chunk, and it holds nothing to share.</remarks>
internal readonly struct SequencePlan
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
    /// 64-bit column does not fit in 64 bits in general.
    /// </summary>
    internal Int128 Step { get; }

    /// <summary>
    /// Whether the column is an arithmetic progression with no nulls. A caller that has already
    /// established a constant step passes it, which skips the walk.
    /// </summary>
    internal static SequencePlan? TryBuild(CanonicalNode node, bool stepsAreConstant = false)
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

        // The physical type is resolved once here, not per row: most columns offered really are
        // sequences, so the walk runs to the end rather than bailing on the third row.
        return node.PType switch
        {
            PType.U8 => Build<byte>(values, length, stepsAreConstant),
            PType.U16 => Build<ushort>(values, length, stepsAreConstant),
            PType.U32 => Build<uint>(values, length, stepsAreConstant),
            PType.U64 => Build<ulong>(values, length, stepsAreConstant),
            PType.I8 => Build<sbyte>(values, length, stepsAreConstant),
            PType.I16 => Build<short>(values, length, stepsAreConstant),
            PType.I32 => Build<int>(values, length, stepsAreConstant),
            _ => Build<long>(values, length, stepsAreConstant),
        };
    }

    /// <summary>
    /// Whether the step is written as <c>uint64_value</c> rather than <c>int64_value</c>: only for
    /// the one case the signed field cannot hold.
    /// </summary>
    internal bool StepIsUnsigned => Step > long.MaxValue;

    /// <summary>The walk, with the physical type resolved and the span cast once.</summary>
    private static SequencePlan? Build<T>(ReadOnlySpan<byte> raw, int length, bool stepsAreConstant)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(raw)[..length];

        Int128 first = Int128.CreateTruncating(values[0]);
        Int128 step = Int128.CreateTruncating(values[1]) - first;
        Int128 previous = first + step;

        for (int row = 2; !stepsAreConstant && row < values.Length; row++)
        {
            Int128 value = Int128.CreateTruncating(values[row]);
            if (value - previous != step)
            {
                return null;
            }

            previous = value;
        }

        // The wire carries the multiplier as either int64_value or uint64_value, so a step outside
        // both is not expressible and the write must refuse rather than truncate.
        if (step < long.MinValue || step > (Int128)ulong.MaxValue)
        {
            return null;
        }

        return new SequencePlan(BaseBitsOf(values[0]), step);
    }

    /// <summary>
    /// The progression a constant column is, read off its element rather than its rows. The guards
    /// read the dtype, because a constant node has no physical type until it is expanded.
    /// </summary>
    internal static SequencePlan? OfConstant(CanonicalNode node)
    {
        DType dtype = node.DType;
        if (node.Kind != CanonicalKind.Constant || dtype.Kind != DTypeKind.Primitive
            || !dtype.PType.IsInteger() || node.Length < 2)
        {
            return null;
        }

        if (node.Validity.Kind is not (ValidityKind.NonNullable or ValidityKind.AllValid))
        {
            return null;
        }

        ReadOnlySpan<byte> element = node.ConstantElement;
        return dtype.PType switch
        {
            PType.U8 => Flat<byte>(element),
            PType.U16 => Flat<ushort>(element),
            PType.U32 => Flat<uint>(element),
            PType.U64 => Flat<ulong>(element),
            PType.I8 => Flat<sbyte>(element),
            PType.I16 => Flat<short>(element),
            PType.I32 => Flat<int>(element),
            _ => Flat<long>(element),
        };
    }

    /// <summary>The element's bits as a base, with a step of zero.</summary>
    private static SequencePlan Flat<T>(ReadOnlySpan<byte> element)
        where T : unmanaged, IBinaryInteger<T> =>
        new SequencePlan(BaseBitsOf(MemoryMarshal.Cast<byte, T>(element)[0]), Int128.Zero);

    /// <summary>Row 0 as the column's own raw bits, which is what the metadata carries.</summary>
    private static ulong BaseBitsOf<T>(T value)
        where T : unmanaged, IBinaryInteger<T> =>
        T.IsNegative(value)
            ? unchecked((ulong)long.CreateTruncating(value))
            : ulong.CreateTruncating(value);
}
