using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Finds the row holding a column's smallest or largest value. The answer is a row rather than a
/// value, so a single pass copies nothing per candidate and the caller reads that one row when it
/// is over. The order is the one a filter compares with: floats follow IEEE 754, so a NaN is
/// neither the minimum nor the maximum and is skipped, and -0.0 and 0.0 tie so the first of them
/// stays.
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
    /// <exception cref="NotSupportedException">The column's type is outside the 1.0 filter scope.</exception>
    internal static bool TryFind(
        CanonicalArena arena, int nodeIndex, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
    {
        int index = ComparisonKernels.Unwrap(arena, nodeIndex);
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

            case CanonicalKind.Constant:
                // Every row holds the same value, so every valid row is both the minimum and the
                // maximum: the answer is the first one, and `wantMin` does not enter into it.
                return FirstValid(mask, rows, listed, count, out bestRow);

            default:
                throw new NotSupportedException(
                    $"A {node.Kind} column has no minimum or maximum a filter can take: only " +
                    "booleans, primitives, utf8 and binary do, plus extensions over those.");
        }
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

    /// <summary>
    /// The loop, monomorphised per value type: C#'s own <c>&lt;</c> and <c>&gt;</c>, so that a
    /// NaN compares false against everything and is skipped by name rather than ordered.
    /// </summary>
    private static bool Best<T>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, ReadOnlySpan<int> rows, bool listed, bool wantMin, out int bestRow)
        where T : unmanaged, INumber<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        int count = listed ? rows.Length : values.Length;
        bool allValid = mask.AllValid;
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

            if (bestRow < 0 || (wantMin ? value < best : value > best))
            {
                best = value;
                bestRow = row;
            }
        }

        return bestRow >= 0;
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
