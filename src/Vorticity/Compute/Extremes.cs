using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Aggregating;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

/// <summary>
/// Finds the row holding a column's smallest or largest value. The answer is a row rather than a
/// value, so a single pass copies nothing per candidate and the caller reads that one row when it
/// is over. The order is the one a filter compares with: floats follow IEEE 754, so a NaN is
/// neither the minimum nor the maximum and is skipped. Zeros of both signs, which compare equal,
/// are ordered as IEEE 754's minimum and maximum order them, -0.0 below +0.0, so that the zero
/// found is the same whatever the order of the rows.
/// </summary>
internal static class Extremes
{
    /// <summary>
    /// Finds the row of the smallest (or largest) non-null, non-NaN value among the rows of the
    /// column at <paramref name="nodeIndex"/>.
    /// </summary>
    /// <param name="arena">The arena holding the decoded split.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="rows">The rows to consider, when <paramref name="listed"/>.</param>
    /// <param name="listed">Whether <paramref name="rows"/> lists the rows, or every row is considered.</param>
    /// <param name="wantMin">Whether the smallest value is wanted, else the largest.</param>
    /// <param name="bestRow">The row holding it, when one does.</param>
    /// <returns>Whether any row held a value.</returns>
    /// <exception cref="NotSupportedException">The column's type is not one the filter supports.</exception>
    internal static bool TryFind(
        CanonicalArena arena, int nodeIndex, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        // An encoded column is decoded: the row that holds the extreme is what is asked for, and a
        // dictionary may hold entries no row names.
        int index = arena.Decoded(ComparisonKernels.Unwrap(arena, nodeIndex));
        CanonicalNode node = arena.GetNode(index);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int count = listed ? rows.Length : node.Length;
        bestRow = -1;
        if (count == 0 || mask.AllInvalid)
        {
            return false;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                return false;

            case CanonicalKind.Bool:
                return Bool(node, mask, rows, listed, wantMin, out bestRow);

            case CanonicalKind.Primitive:
                return Primitive(node, mask, rows, listed, wantMin, out bestRow);

            case CanonicalKind.VarBinView:
                return Bytes(node, mask, rows, listed, wantMin, out bestRow);

            case CanonicalKind.Decimal:
                return Decimal(node, mask, rows, listed, wantMin, out bestRow);

            case CanonicalKind.Constant:
                // Every row holds the same value, so every valid row is both the minimum and the
                // maximum: the answer is the first one, and `wantMin` does not enter into it --
                // unless the value is a NaN, which an extreme skips, and then there is none.
                return !IsNaN(node) && FirstValid(mask, rows, listed, count, out bestRow);

            default:
                throw new NotSupportedException(
                    $"A {node.Kind} column has no minimum or maximum a filter can take: only " +
                    "booleans, primitives, decimals, utf8 and binary do, plus extensions over those.");
        }
    }

    /// <summary>Whether a constant's one value is a floating-point NaN.</summary>
    private static bool IsNaN(CanonicalNode node)
    {
        if (node.DType.Kind != DTypeKind.Primitive)
        {
            return false;
        }

        ReadOnlySpan<byte> element = node.ConstantElement;
        return node.DType.PType switch
        {
            PType.F16 => Half.IsNaN(BinaryPrimitives.ReadHalfLittleEndian(element)),
            PType.F32 => float.IsNaN(BinaryPrimitives.ReadSingleLittleEndian(element)),
            PType.F64 => double.IsNaN(BinaryPrimitives.ReadDoubleLittleEndian(element)),
            _ => false,
        };
    }

    /// <summary>The first row of the selection that is not null, for a column with one value.</summary>
    private static bool FirstValid(
        ValidityMask mask, ReadOnlySpan<int> rows, bool listed, int count, out int bestRow)
    {
        if (mask.AllValid)
        {
            bestRow = listed ? rows[0] : 0;
            return true;
        }

        for (int i = 0; i < count; i++)
        {
            int row = listed ? rows[i] : i;
            if (mask.IsValid(row))
            {
                bestRow = row;
                return true;
            }
        }

        bestRow = -1;
        return false;
    }

    private static bool Bool(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        // false < true: the smallest is the first false, or the first value when there is none.
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset;
        int count = listed ? rows.Length : node.Length;
        bestRow = -1;
        for (int i = 0; i < count; i++)
        {
            int row = listed ? rows[i] : i;
            if (!mask.IsValid(row))
            {
                continue;
            }

            bool value = CanonicalSupport.BitAt(bits, offset + row);
            if (bestRow < 0)
            {
                bestRow = row;
            }

            if (value != wantMin)
            {
                // The extreme itself: nothing can beat it.
                bestRow = row;
                return true;
            }
        }

        return bestRow >= 0;
    }

    private static bool Primitive(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        ReadOnlySpan<byte> bytes = node.Values.Span;
        return node.PType switch
        {
            PType.I8 => Best<sbyte>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.I16 => Best<short>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.I32 => Best<int>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.I64 => Best<long>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.U8 => Best<byte>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.U16 => Best<ushort>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.U32 => Best<uint>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.U64 => Best<ulong>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.F16 => Best<Half>(bytes, mask, rows, listed, wantMin, out bestRow),
            PType.F32 => Best<float>(bytes, mask, rows, listed, wantMin, out bestRow),
            _ => Best<double>(bytes, mask, rows, listed, wantMin, out bestRow),
        };
    }

    /// <summary>A decimal's unscaled values, which order as the numbers they scale to: every row of a column shares its scale.</summary>
    private static bool Decimal(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        ReadOnlySpan<byte> bytes = node.Values.Span;
        return node.Storage switch
        {
            DecimalStorageType.I8 => Best<sbyte>(bytes, mask, rows, listed, wantMin, out bestRow),
            DecimalStorageType.I16 => Best<short>(bytes, mask, rows, listed, wantMin, out bestRow),
            DecimalStorageType.I32 => Best<int>(bytes, mask, rows, listed, wantMin, out bestRow),
            DecimalStorageType.I64 => Best<long>(bytes, mask, rows, listed, wantMin, out bestRow),
            DecimalStorageType.I128 => Best<Int128>(bytes, mask, rows, listed, wantMin, out bestRow),
            _ => Wide(bytes, mask, rows, listed, wantMin, out bestRow),
        };
    }

    /// <summary><see cref="Best{T}"/> for 256-bit values, which have an order and no NaN.</summary>
    private static bool Wide(
        ReadOnlySpan<byte> bytes, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        ReadOnlySpan<Int256> values = MemoryMarshal.Cast<byte, Int256>(bytes);
        int count = listed ? rows.Length : values.Length;
        bool allValid = mask.AllValid;
        bestRow = -1;
        Int256 best = default;
        for (int i = 0; i < count; i++)
        {
            int row = listed ? rows[i] : i;
            if (!allValid && !mask.IsValid(row))
            {
                continue;
            }

            int order = values[row].CompareTo(best);
            if (bestRow < 0 || (wantMin ? order < 0 : order > 0))
            {
                best = values[row];
                bestRow = row;
            }
        }

        return bestRow >= 0;
    }

    /// <summary>
    /// The loop, monomorphised per value type: C#'s own <c>&lt;</c> and <c>&gt;</c>, so that a
    /// NaN compares false against everything and is skipped by name rather than ordered. A zero that
    /// ties the best so far takes its place when its sign makes it the lesser or the greater, a test
    /// the compiler drops for integers.
    /// </summary>
    private static bool Best<T>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
        where T : unmanaged, INumber<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        int count = listed ? rows.Length : values.Length;
        bool allValid = mask.AllValid;
        if (!listed && allValid && IsInteger<T>() && Vector.IsHardwareAccelerated && Vector<T>.IsSupported && count >= Vector<T>.Count)
        {
            // Integers of every row, which hold no NaN and no signed zero: the extreme by vectors, then its first row,
            // where a row at a time compared and branched on each.
            T extreme = Extreme(values[..count], wantMin);
            bestRow = values[..count].IndexOf(extreme);
            return true;
        }

        bestRow = -1;
        T best = default;
        for (int i = 0; i < count; i++)
        {
            int row = listed ? rows[i] : i;
            if (!allValid && !mask.IsValid(row))
            {
                continue;
            }

            T value = values[row];
            if (T.IsNaN(value))
            {
                continue;
            }

            if (bestRow < 0
                || (wantMin
                    ? value < best || ExtremeOps.ZeroBelow(value, best)
                    : value > best || ExtremeOps.ZeroAbove(value, best)))
            {
                best = value;
                bestRow = row;
            }
        }

        return bestRow >= 0;
    }

    /// <summary>Whether <typeparamref name="T"/> is a primitive integer, whose order has no NaN and no signed zero.</summary>
    private static bool IsInteger<T>() =>
        typeof(T) == typeof(sbyte) || typeof(T) == typeof(byte) || typeof(T) == typeof(short) || typeof(T) == typeof(ushort)
        || typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(long) || typeof(T) == typeof(ulong);

    /// <summary>The least or the greatest of <paramref name="values"/>, at least a vector of them, a vector at a time.</summary>
    private static T Extreme<T>(ReadOnlySpan<T> values, bool wantMin)
        where T : unmanaged, INumber<T>
    {
        ref T at = ref MemoryMarshal.GetReference(values);
        int lanes = Vector<T>.Count;
        Vector<T> held = Vector.LoadUnsafe(ref at);
        int i = lanes;
        for (; i <= values.Length - lanes; i += lanes)
        {
            Vector<T> next = Vector.LoadUnsafe(ref at, (nuint)i);
            held = wantMin ? Vector.Min(held, next) : Vector.Max(held, next);
        }

        T best = held[0];
        for (int lane = 1; lane < lanes; lane++)
        {
            best = wantMin ? T.Min(best, held[lane]) : T.Max(best, held[lane]);
        }

        for (; i < values.Length; i++)
        {
            best = wantMin ? T.Min(best, values[i]) : T.Max(best, values[i]);
        }

        return best;
    }

    private static bool Bytes(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        int count = listed ? rows.Length : node.Length;
        bool allValid = mask.AllValid;
        bestRow = -1;
        ReadOnlySpan<byte> best = default;
        for (int i = 0; i < count; i++)
        {
            int row = listed ? rows[i] : i;
            if (!allValid && !mask.IsValid(row))
            {
                continue;
            }

            ReadOnlySpan<byte> value = LiteralReader.ViewAt(node, row);
            if (bestRow < 0)
            {
                best = value;
                bestRow = row;
                continue;
            }

            int order = value.SequenceCompareTo(best);
            if (wantMin ? order < 0 : order > 0)
            {
                best = value;
                bestRow = row;
            }
        }

        return bestRow >= 0;
    }
}
