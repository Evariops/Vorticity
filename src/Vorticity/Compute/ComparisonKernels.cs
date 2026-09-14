// Comparing one canonical column against one literal, row by row - docs/08-semantics.md §2 and §3.
//
// THE TWO RULES THAT ARE NOT NEGOTIABLE, both of them easy to get wrong by writing the obvious code:
//
//   * FLOATS FOLLOW IEEE 754. Every comparison involving NaN is false, `NaN == NaN` included, and
//     `NaN != x` is false too -- it is not the negation of equality here. C#'s own operators already
//     behave this way, so the kernels use them directly rather than reaching for CompareTo, which
//     orders NaN and would quietly give the row-encoding's TOTAL order instead. docs/08 names that
//     exact confusion as a correctness bug.
//   * SIGNEDNESS IS NOT A DETAIL. A signed literal against an unsigned column is not a cast: `x > -1`
//     on a u64 column is true for every row, and folding the two into one comparison makes it either
//     always true or always false depending on which way the fold went, with the bug invisible below
//     2^63. Both directions are resolved before the loop, by deciding the answer from the sign alone.
//
// Validity is the caller's: every kernel writes Unknown for a null row and never looks at its value.
using System;
using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Per-row comparison of a canonical column against a literal.</summary>
internal static class ComparisonKernels
{
    private const int ViewSize = 16;
    private const int MaxInlineLength = 12;

    /// <summary>
    /// Evaluates <c>column op literal</c> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="arena">The arena the node lives in.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="op">The operator, with the column on the left.</param>
    /// <param name="literal">The constant.</param>
    /// <param name="destination">One <see cref="Trilean"/> state per row.</param>
    /// <exception cref="NotSupportedException">
    /// The column's canonical form or the literal's type is outside the 1.0 filter scope.
    /// </exception>
    internal static void Compare(
        CanonicalArena arena, int nodeIndex, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        // An extension is its storage plus a label; comparing the label is meaningless and
        // comparing the storage is what a timestamp filter actually wants.
        nodeIndex = Unwrap(arena, nodeIndex);
        CanonicalNode node = arena.GetNode(nodeIndex);

        if (literal.Kind == FilterLiteralKind.Null)
        {
            // "A comparison with a null operand yields unknown" - for every row, whatever the
            // column holds. IS NULL is the predicate that asks the question this one cannot.
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                Trilean.Fill(destination, Trilean.Unknown);
                return;

            case CanonicalKind.Bool:
                CompareBool(arena, node, op, literal, destination);
                return;

            case CanonicalKind.Primitive:
                ComparePrimitive(arena, node, op, literal, destination);
                return;

            case CanonicalKind.VarBinView:
                CompareBytes(arena, node, op, literal, destination);
                return;

            default:
                throw new NotSupportedException(
                    $"A filter cannot compare a {node.Kind} column. The 1.0 filter evaluates " +
                    "booleans, primitives, utf8 and binary, plus extensions over those " +
                    "(docs/01-scope.md F7).");
        }
    }

    /// <summary>Evaluates <c>column IN (literals)</c>, which is an OR of equalities.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="literals">The candidates.</param>
    /// <param name="destination">One state per row.</param>
    /// <param name="scratch">A second buffer of the same length.</param>
    internal static void In(
        CanonicalArena arena, int nodeIndex, ReadOnlySpan<FilterLiteral> literals,
        Span<byte> destination, Span<byte> scratch)
    {
        // `x IN (a, b)` is `x = a OR x = b`, three-valued logic included: a null x is unknown
        // against every candidate, so the OR stays unknown, and a null CANDIDATE makes that one
        // comparison unknown rather than false - which is why this is an OR of the kernel above
        // rather than a membership test.
        Compare(arena, nodeIndex, ComparisonOp.Equal, literals[0], destination);
        for (int i = 1; i < literals.Length; i++)
        {
            Compare(arena, nodeIndex, ComparisonOp.Equal, literals[i], scratch);
            Trilean.Or(destination, scratch);
        }
    }

    /// <summary>Evaluates <c>column IS [NOT] NULL</c>, which is never unknown.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="isNull">Whether the predicate is <c>IS NULL</c>.</param>
    /// <param name="destination">One state per row.</param>
    internal static void NullCheck(
        CanonicalArena arena, int nodeIndex, bool isNull, Span<byte> destination)
    {
        int storage = Unwrap(arena, nodeIndex);
        CanonicalNode node = arena.GetNode(storage);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        if (node.Kind == CanonicalKind.Null)
        {
            Trilean.Fill(destination, isNull ? Trilean.True : Trilean.False);
            return;
        }

        if (mask.AllValid)
        {
            Trilean.Fill(destination, isNull ? Trilean.False : Trilean.True);
            return;
        }

        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, isNull ? Trilean.True : Trilean.False);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            bool valid = mask.IsValid(i);
            destination[i] = valid != isNull ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>An extension node's storage; anything else unchanged.</summary>
    internal static int Unwrap(CanonicalArena arena, int nodeIndex) =>
        arena.GetNode(nodeIndex).Kind == CanonicalKind.Extension
            ? arena.GetNode(nodeIndex).StorageIndex
            : nodeIndex;

    private static void CompareBool(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        if (literal.Kind != FilterLiteralKind.Bool)
        {
            throw Mismatch("bool", literal.Kind);
        }

        // false < true, which is the order every SQL engine uses and the one the row encoding uses.
        bool wanted = literal.BoolValue;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset;

        for (int i = 0; i < destination.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            bool value = CanonicalSupport.BitAt(bits, offset + i);
            destination[i] = Trilean.From(true, Apply(op, value.CompareTo(wanted)));
        }
    }

    private static void ComparePrimitive(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        PType ptype = node.PType;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        VortexBuffer values = node.Values;

        if (ptype.IsFloat())
        {
            CompareFloat(ptype, values, mask, op, ToDouble(literal, "a float"), destination);
            return;
        }

        if (ptype.IsSignedInteger())
        {
            CompareSigned(ptype, values, mask, op, literal, destination);
            return;
        }

        CompareUnsigned(ptype, values, mask, op, literal, destination);
    }

    private static void CompareFloat(
        PType ptype, VortexBuffer values, ValidityMask mask, ComparisonOp op, double wanted,
        Span<byte> destination)
    {
        ReadOnlySpan<byte> bytes = values.Span;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            double value = ptype switch
            {
                PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(bytes.Slice(i * 2, 2)),
                PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4)),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(i * 8, 8)),
            };

            // IEEE 754 through C#'s own operators: every one of these is false when either side is
            // NaN, NotEqual included. Routing through CompareTo would order NaN and silently give
            // the row encoding's total order (docs/08-semantics.md §2).
            bool result = op switch
            {
                ComparisonOp.Equal => value == wanted,
                ComparisonOp.NotEqual => value != wanted,
                ComparisonOp.Less => value < wanted,
                ComparisonOp.LessOrEqual => value <= wanted,
                ComparisonOp.Greater => value > wanted,
                _ => value >= wanted,
            };

            destination[i] = result ? Trilean.True : Trilean.False;
        }
    }

    private static void CompareSigned(
        PType ptype, VortexBuffer values, ValidityMask mask, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        long wanted;
        switch (literal.Kind)
        {
            case FilterLiteralKind.Signed:
                wanted = literal.SignedValue;
                break;

            case FilterLiteralKind.Unsigned when literal.UnsignedValue <= long.MaxValue:
                wanted = (long)literal.UnsignedValue;
                break;

            case FilterLiteralKind.Unsigned:
                // Above i64::MaxValue: every value of a signed column is below it, so the answer
                // is the same for every non-null row and no comparison is needed.
                FillFromOrder(mask, op, -1, destination);
                return;

            case FilterLiteralKind.Float:
                CompareSignedAgainstFloat(ptype, values, mask, op, literal.FloatValue, destination);
                return;

            default:
                throw Mismatch("a signed integer", literal.Kind);
        }

        ReadOnlySpan<byte> bytes = values.Span;
        switch (ptype)
        {
            case PType.I8:
                CompareOp<sbyte, long>(bytes, mask, op, wanted, destination);
                break;
            case PType.I16:
                CompareOp<short, long>(bytes, mask, op, wanted, destination);
                break;
            case PType.I32:
                CompareOp<int, long>(bytes, mask, op, wanted, destination);
                break;
            default:
                CompareOp<long, long>(bytes, mask, op, wanted, destination);
                break;
        }
    }

    private static void CompareUnsigned(
        PType ptype, VortexBuffer values, ValidityMask mask, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        ulong wanted;
        switch (literal.Kind)
        {
            case FilterLiteralKind.Unsigned:
                wanted = literal.UnsignedValue;
                break;

            case FilterLiteralKind.Signed when literal.SignedValue >= 0:
                wanted = (ulong)literal.SignedValue;
                break;

            case FilterLiteralKind.Signed:
                // Negative: every value of an unsigned column is above it. `x > -1` is true for
                // every row, and casting the literal instead would have made it false for almost
                // all of them.
                FillFromOrder(mask, op, 1, destination);
                return;

            case FilterLiteralKind.Float:
                CompareUnsignedAgainstFloat(ptype, values, mask, op, literal.FloatValue, destination);
                return;

            default:
                throw Mismatch("an unsigned integer", literal.Kind);
        }

        ReadOnlySpan<byte> bytes = values.Span;
        switch (ptype)
        {
            case PType.U8:
                CompareOp<byte, ulong>(bytes, mask, op, wanted, destination);
                break;
            case PType.U16:
                CompareOp<ushort, ulong>(bytes, mask, op, wanted, destination);
                break;
            case PType.U32:
                CompareOp<uint, ulong>(bytes, mask, op, wanted, destination);
                break;
            default:
                CompareOp<ulong, ulong>(bytes, mask, op, wanted, destination);
                break;
        }
    }

    /// <summary>
    /// Resolves the OPERATOR out of the loop and calls the typed comparison.
    /// </summary>
    /// <typeparam name="TValue">The column's own element type.</typeparam>
    /// <typeparam name="TWide">
    /// The type the comparison happens in: <see cref="long"/> for a signed column,
    /// <see cref="ulong"/> for an unsigned one. Widening one element is free; what it buys is that
    /// the literal is compared in a type that can hold it, which is the signedness rule this file
    /// opens with.
    /// </typeparam>
    private static void CompareOp<TValue, TWide>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, ComparisonOp op, TWide wanted,
        Span<byte> destination)
        where TValue : unmanaged, INumberBase<TValue>
        where TWide : unmanaged, INumberBase<TWide>, IComparisonOperators<TWide, TWide, bool>
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                CompareCore<TValue, TWide, EqualOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.NotEqual:
                CompareCore<TValue, TWide, NotEqualOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.Less:
                CompareCore<TValue, TWide, LessOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.LessOrEqual:
                CompareCore<TValue, TWide, LessOrEqualOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.Greater:
                CompareCore<TValue, TWide, GreaterOp>(bytes, mask, wanted, destination);
                break;
            default:
                CompareCore<TValue, TWide, GreaterOrEqualOp>(bytes, mask, wanted, destination);
                break;
        }
    }

    /// <summary>The comparison loop, with the type, the operator and the validity all resolved.</summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS REPLACES. The loop asked three questions per row that are properties of the CALL:
    /// which physical type is this column (through <c>ReadInteger</c>'s switch), is this row valid
    /// (through <c>ValidityMask.IsValid</c>'s switch on the validity kind), and which operator is
    /// this (through <c>Apply</c>'s switch on an <c>int</c> ordering it had to compute first).
    /// Hoisting all three was measured twice at 6.0x -- 97.2 us to 16.1 us on 65 536 rows of
    /// <c>i64 &lt; literal</c> -- and never applied.
    /// </para>
    /// <para>
    /// The operator arrives as a struct with a static abstract member, which the JIT devirtualizes
    /// and inlines for a value-type instantiation, so the body is one compare and one store. The
    /// ORDERING is gone too: <c>Apply</c> needed a three-way <c>CompareTo</c> before it could ask a
    /// two-way question.
    /// </para>
    /// <para>
    /// Still a byte per row, deliberately. bench/SIMD.md measured a <c>Vector128</c> version of this
    /// loop 1.8x SLOWER and named the cause: the <see cref="Trilean"/> output is one byte per row,
    /// so a vectorized compare has to narrow its mask back down to bytes and the narrowing costs
    /// more than the compare saves. That is a statement about the OUTPUT representation, not about
    /// vectorizing comparisons, and it stands until Trilean becomes two bitmaps.
    /// </para>
    /// </remarks>
    private static void CompareCore<TValue, TWide, TOp>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, TWide wanted, Span<byte> destination)
        where TValue : unmanaged, INumberBase<TValue>
        where TWide : unmanaged, INumberBase<TWide>, IComparisonOperators<TWide, TWide, bool>
        where TOp : struct, IOrderOp
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..destination.Length];
        if (mask.AllValid)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = TOp.Holds(TWide.CreateTruncating(values[i]), wanted)
                    ? Trilean.True
                    : Trilean.False;
            }

            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = !mask.IsValid(i)
                ? Trilean.Unknown
                : TOp.Holds(TWide.CreateTruncating(values[i]), wanted) ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>One comparison operator, as a type the JIT can inline through.</summary>
    private interface IOrderOp
    {
        /// <summary>Whether the operator holds for this pair.</summary>
        /// <typeparam name="T">The comparison type.</typeparam>
        /// <param name="left">The column's value.</param>
        /// <param name="right">The literal.</param>
        static abstract bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool>;
    }

    private readonly struct EqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left == right;
    }

    private readonly struct NotEqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left != right;
    }

    private readonly struct LessOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left < right;
    }

    private readonly struct LessOrEqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left <= right;
    }

    private readonly struct GreaterOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left > right;
    }

    private readonly struct GreaterOrEqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left >= right;
    }

    /// <summary>
    /// An integer column against a float literal, in the literal's domain.
    /// </summary>
    /// <remarks>
    /// <c>x &lt; 3.5</c> on an integer column is a legal and ordinary predicate, so it is answered
    /// rather than refused. The comparison happens in <see cref="double"/>, which is exact for
    /// magnitudes below 2^53 and can be off by one above it -- the same limit any engine that
    /// compares an i64 against a double lands on, and far better than rejecting the predicate.
    /// </remarks>
    private static void CompareSignedAgainstFloat(
        PType ptype, VortexBuffer values, ValidityMask mask, ComparisonOp op, double wanted,
        Span<byte> destination)
    {
        ReadOnlySpan<byte> bytes = values.Span;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            double value = CanonicalSupport.ReadInteger(bytes, ptype, i);
            destination[i] = FloatCompare(op, value, wanted) ? Trilean.True : Trilean.False;
        }
    }

    private static void CompareUnsignedAgainstFloat(
        PType ptype, VortexBuffer values, ValidityMask mask, ComparisonOp op, double wanted,
        Span<byte> destination)
    {
        ReadOnlySpan<byte> bytes = values.Span;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            double value = CompressedValues.ReadUnsigned(bytes, ptype, i);
            destination[i] = FloatCompare(op, value, wanted) ? Trilean.True : Trilean.False;
        }
    }

    private static void CompareBytes(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        if (literal.Kind != FilterLiteralKind.Bytes)
        {
            throw Mismatch("utf8 or binary", literal.Kind);
        }

        ReadOnlySpan<byte> wanted = literal.BytesValue;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        for (int i = 0; i < destination.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            // Ordinal byte order, which for utf8 is also code-point order: UTF-8 is designed so
            // that memcmp of the encoded bytes equals comparison of the code points. Never a
            // culture-aware string comparison (docs/03-architecture.md §1).
            ReadOnlySpan<byte> value = Value(node, i);
            destination[i] = Apply(op, value.SequenceCompareTo(wanted)) ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>Resolves one 16-byte view, inline or by reference.</summary>
    private static ReadOnlySpan<byte> Value(CanonicalNode node, int index)
    {
        ReadOnlySpan<byte> views = node.Views.Span;
        ReadOnlySpan<byte> view = views.Slice(index * ViewSize, ViewSize);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= MaxInlineLength)
        {
            return view.Slice(4, size);
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer(buffer).Span.Slice(offset, size);
    }

    /// <summary>
    /// Fills every non-null row with the answer implied by a comparison whose ORDER is already
    /// known, for the cases where the literal is out of the column's range entirely.
    /// </summary>
    /// <param name="mask">Row validity.</param>
    /// <param name="op">The operator.</param>
    /// <param name="order">The sign of <c>column - literal</c>, the same for every row.</param>
    /// <param name="destination">One state per row.</param>
    private static void FillFromOrder(
        ValidityMask mask, ComparisonOp op, int order, Span<byte> destination)
    {
        byte answer = Apply(op, order) ? Trilean.True : Trilean.False;
        if (mask.AllValid)
        {
            Trilean.Fill(destination, answer);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = mask.IsValid(i) ? answer : Trilean.Unknown;
        }
    }

    private static bool FloatCompare(ComparisonOp op, double value, double wanted) => op switch
    {
        ComparisonOp.Equal => value == wanted,
        ComparisonOp.NotEqual => value != wanted,
        ComparisonOp.Less => value < wanted,
        ComparisonOp.LessOrEqual => value <= wanted,
        ComparisonOp.Greater => value > wanted,
        _ => value >= wanted,
    };

    /// <summary>Turns the sign of a three-way comparison into the operator's answer.</summary>
    private static bool Apply(ComparisonOp op, int order) => op switch
    {
        ComparisonOp.Equal => order == 0,
        ComparisonOp.NotEqual => order != 0,
        ComparisonOp.Less => order < 0,
        ComparisonOp.LessOrEqual => order <= 0,
        ComparisonOp.Greater => order > 0,
        _ => order >= 0,
    };

    private static double ToDouble(FilterLiteral literal, string columnKind) => literal.Kind switch
    {
        FilterLiteralKind.Float => literal.FloatValue,
        FilterLiteralKind.Signed => literal.SignedValue,
        FilterLiteralKind.Unsigned => literal.UnsignedValue,
        _ => throw Mismatch(columnKind, literal.Kind),
    };

    private static NotSupportedException Mismatch(string columnKind, FilterLiteralKind literal) =>
        new NotSupportedException(
            $"A filter on a {columnKind} column cannot be compared against a " +
            $"{literal} literal.");
}
