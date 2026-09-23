using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

internal static partial class ComparisonKernels
{
    /// <summary>
    /// Evaluates <c>left op right</c>, row by row, over two columns of the same type.
    /// </summary>
    /// <param name="arena">The arena both columns live in.</param>
    /// <param name="leftIndex">The column on the left of the operator.</param>
    /// <param name="op">The operator.</param>
    /// <param name="rightIndex">The column on the right of the operator.</param>
    /// <param name="destination">One <see cref="Trilean"/> state per row.</param>
    /// <remarks>
    /// A null on either side is unknown, and floats keep the literal kernels' IEEE rules: C#'s own
    /// operators, so a NaN on either side makes every operator but <c>!=</c> false. Booleans order
    /// false first, text and binary bytewise, decimals by their unscaled value at their shared
    /// scale, and a fixed-size list of bytes (a uuid) as unsigned bytes, first byte first.
    /// <para>
    /// A constant side is expanded into its twin first: the kernel then has two contiguous columns
    /// and one loop per type, and the twin lives in the batch's arena and dies with it.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The two columns are of different forms, or of a form no comparison reads.
    /// </exception>
    internal static void CompareColumns(
        CanonicalArena arena, int leftIndex, ComparisonOp op, int rightIndex, Span<byte> destination)
    {
        int left = Expanded(arena, Unwrap(arena, leftIndex));
        int right = Expanded(arena, Unwrap(arena, rightIndex));
        CanonicalNode l = arena.GetNode(left);
        CanonicalNode r = arena.GetNode(right);
        if (l.Kind == CanonicalKind.Null || r.Kind == CanonicalKind.Null)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        BothValid valid = new BothValid(
            ValidityMask.From(arena, l.Validity), ValidityMask.From(arena, r.Validity));
        if (valid.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (l.Kind != r.Kind)
        {
            throw ColumnsMismatch($"a {l.Kind} column with a {r.Kind} column");
        }

        switch (l.Kind)
        {
            case CanonicalKind.Bool:
                CompareBoolColumns(l, r, in valid, op, destination);
                return;

            case CanonicalKind.Primitive:
                if (l.PType != r.PType)
                {
                    throw ColumnsMismatch($"a {l.PType} column with a {r.PType} column");
                }

                ComparePrimitiveColumns(l.PType, l.Values.Span, r.Values.Span, in valid, op, destination);
                return;

            case CanonicalKind.Decimal:
                CompareDecimalColumns(l, r, in valid, op, destination);
                return;

            case CanonicalKind.VarBinView:
                CompareViewColumns(l, r, in valid, op, destination);
                return;

            case CanonicalKind.FixedSizeList:
                CompareFixedColumns(arena, l, r, in valid, op, destination);
                return;

            default:
                throw ColumnsMismatch($"two {l.Kind} columns");
        }
    }

    /// <summary>The node itself, or the materialized twin of a constant, a dictionary or a run-end node.</summary>
    private static int Expanded(CanonicalArena arena, int index) => arena.GetNode(index).Kind switch
    {
        CanonicalKind.Constant => arena.MaterializeConstant(index),
        CanonicalKind.Dictionary or CanonicalKind.RunEnd => arena.MaterializeEncoded(index),
        _ => index,
    };

    private static NotSupportedException ColumnsMismatch(string what) =>
        new NotSupportedException(
            $"A filter cannot compare {what}. Two columns compare when they have the same type: " +
            "booleans, primitives, decimals, utf8, binary or fixed-size lists of bytes.");

    /// <summary>The validity of a row of a pair: both sides valid.</summary>
    private readonly ref struct BothValid
    {
        private readonly ValidityMask _left;
        private readonly ValidityMask _right;

        internal BothValid(ValidityMask left, ValidityMask right)
        {
            _left = left;
            _right = right;
            AllValid = left.AllValid && right.AllValid;
            AllInvalid = left.AllInvalid || right.AllInvalid;
        }

        /// <summary>Whether no row of either side is null.</summary>
        internal bool AllValid { get; }

        /// <summary>Whether every row of one side is null.</summary>
        internal bool AllInvalid { get; }

        /// <summary>Whether row <paramref name="index"/> holds a value on both sides.</summary>
        internal bool IsValid(int index) => _left.IsValid(index) && _right.IsValid(index);
    }

    /// <summary>Two boolean columns: four answers, one per pair of bits, chosen per row.</summary>
    private static void CompareBoolColumns(
        CanonicalNode left, CanonicalNode right, in BothValid valid, ComparisonOp op,
        Span<byte> destination)
    {
        Span<byte> answers = stackalloc byte[4];
        for (int pair = 0; pair < 4; pair++)
        {
            bool a = (pair & 2) != 0;
            bool b = (pair & 1) != 0;
            answers[pair] = Apply(op, a.CompareTo(b)) ? Trilean.True : Trilean.False;
        }

        ReadOnlySpan<byte> leftBits = left.Bits.Span;
        ReadOnlySpan<byte> rightBits = right.Bits.Span;
        int leftOffset = left.BitOffset;
        int rightOffset = right.BitOffset;
        bool allValid = valid.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !valid.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            int pair = (CanonicalSupport.BitAt(leftBits, leftOffset + i) ? 2 : 0)
                | (CanonicalSupport.BitAt(rightBits, rightOffset + i) ? 1 : 0);
            destination[i] = answers[pair];
        }
    }

    private static void ComparePrimitiveColumns(
        PType ptype, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, in BothValid valid,
        ComparisonOp op, Span<byte> destination)
    {
        switch (ptype)
        {
            case PType.I8:
                ColumnsOp<sbyte>(left, right, in valid, op, destination);
                break;
            case PType.I16:
                ColumnsOp<short>(left, right, in valid, op, destination);
                break;
            case PType.I32:
                ColumnsOp<int>(left, right, in valid, op, destination);
                break;
            case PType.I64:
                ColumnsOp<long>(left, right, in valid, op, destination);
                break;
            case PType.U8:
                ColumnsOp<byte>(left, right, in valid, op, destination);
                break;
            case PType.U16:
                ColumnsOp<ushort>(left, right, in valid, op, destination);
                break;
            case PType.U32:
                ColumnsOp<uint>(left, right, in valid, op, destination);
                break;
            case PType.U64:
                ColumnsOp<ulong>(left, right, in valid, op, destination);
                break;
            case PType.F16:
                ColumnsOp<Half>(left, right, in valid, op, destination);
                break;
            case PType.F32:
                ColumnsOp<float>(left, right, in valid, op, destination);
                break;
            default:
                ColumnsOp<double>(left, right, in valid, op, destination);
                break;
        }
    }

    /// <summary>Resolves the operator out of the loop for two columns of one element type.</summary>
    private static void ColumnsOp<T>(
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, in BothValid valid, ComparisonOp op,
        Span<byte> destination)
        where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                ColumnsCore<T, EqualOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.NotEqual:
                ColumnsCore<T, NotEqualOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.Less:
                ColumnsCore<T, LessOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.LessOrEqual:
                ColumnsCore<T, LessOrEqualOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.Greater:
                ColumnsCore<T, GreaterOp>(left, right, in valid, destination);
                break;
            default:
                ColumnsCore<T, GreaterOrEqualOp>(left, right, in valid, destination);
                break;
        }
    }

    /// <summary>The pairwise loop, with the type, the operator and the validity resolved.</summary>
    private static void ColumnsCore<T, TOp>(
        ReadOnlySpan<byte> leftBytes, ReadOnlySpan<byte> rightBytes, in BothValid valid,
        Span<byte> destination)
        where T : unmanaged, IComparisonOperators<T, T, bool>
        where TOp : struct, IOrderOp
    {
        int rows = destination.Length;
        ReadOnlySpan<T> left = MemoryMarshal.Cast<byte, T>(leftBytes)[..rows];
        ReadOnlySpan<T> right = MemoryMarshal.Cast<byte, T>(rightBytes)[..rows];
        if (valid.AllValid)
        {
            for (int i = 0; i < rows; i++)
            {
                destination[i] = TOp.Holds(left[i], right[i]) ? Trilean.True : Trilean.False;
            }

            return;
        }

        for (int i = 0; i < rows; i++)
        {
            destination[i] = valid.IsValid(i)
                ? (TOp.Holds(left[i], right[i]) ? Trilean.True : Trilean.False)
                : Trilean.Unknown;
        }
    }

    /// <summary>
    /// Two decimal columns: the integer loop of their storage when they share one, and a widening
    /// to 256 bits per row when they do not, which a chunk stored wider than its precision needs
    /// is the one way to meet.
    /// </summary>
    private static void CompareDecimalColumns(
        CanonicalNode left, CanonicalNode right, in BothValid valid, ComparisonOp op,
        Span<byte> destination)
    {
        if (left.Scale != right.Scale)
        {
            throw ColumnsMismatch($"a decimal of scale {left.Scale} with one of scale {right.Scale}");
        }

        DecimalStorageType storage = left.Storage;
        ReadOnlySpan<byte> leftValues = left.Values.Span;
        ReadOnlySpan<byte> rightValues = right.Values.Span;
        if (storage != right.Storage)
        {
            MixedDecimalColumns(
                leftValues, DecimalStorage.ByteWidth(storage), rightValues,
                DecimalStorage.ByteWidth(right.Storage), in valid, op, destination);
            return;
        }

        switch (storage)
        {
            case DecimalStorageType.I8:
                ColumnsOp<sbyte>(leftValues, rightValues, in valid, op, destination);
                break;
            case DecimalStorageType.I16:
                ColumnsOp<short>(leftValues, rightValues, in valid, op, destination);
                break;
            case DecimalStorageType.I32:
                ColumnsOp<int>(leftValues, rightValues, in valid, op, destination);
                break;
            case DecimalStorageType.I64:
                ColumnsOp<long>(leftValues, rightValues, in valid, op, destination);
                break;
            case DecimalStorageType.I128:
                ColumnsOp<Int128>(leftValues, rightValues, in valid, op, destination);
                break;
            default:
                MixedDecimalColumns(
                    leftValues, Int256.ByteCount, rightValues, Int256.ByteCount, in valid, op,
                    destination);
                break;
        }
    }

    private static void MixedDecimalColumns(
        ReadOnlySpan<byte> left, int leftWidth, ReadOnlySpan<byte> right, int rightWidth,
        in BothValid valid, ComparisonOp op, Span<byte> destination)
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                WideColumnsCore<EqualOp>(left, leftWidth, right, rightWidth, in valid, destination);
                break;
            case ComparisonOp.NotEqual:
                WideColumnsCore<NotEqualOp>(left, leftWidth, right, rightWidth, in valid, destination);
                break;
            case ComparisonOp.Less:
                WideColumnsCore<LessOp>(left, leftWidth, right, rightWidth, in valid, destination);
                break;
            case ComparisonOp.LessOrEqual:
                WideColumnsCore<LessOrEqualOp>(left, leftWidth, right, rightWidth, in valid, destination);
                break;
            case ComparisonOp.Greater:
                WideColumnsCore<GreaterOp>(left, leftWidth, right, rightWidth, in valid, destination);
                break;
            default:
                WideColumnsCore<GreaterOrEqualOp>(left, leftWidth, right, rightWidth, in valid, destination);
                break;
        }
    }

    /// <summary>Both sides widened to 256 bits, row by row.</summary>
    private static void WideColumnsCore<TOp>(
        ReadOnlySpan<byte> left, int leftWidth, ReadOnlySpan<byte> right, int rightWidth,
        in BothValid valid, Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        bool allValid = valid.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !valid.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            int order = Widen(left, leftWidth, i).CompareTo(Widen(right, rightWidth, i));
            destination[i] = TOp.Holds(order, 0) ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>Row <paramref name="row"/> of a decimal values buffer, sign-extended to 256 bits.</summary>
    /// <param name="values">Little-endian two's complement values, <paramref name="width"/> bytes each.</param>
    /// <param name="width">One of 1, 2, 4, 8, 16 and 32.</param>
    /// <param name="row">The row.</param>
    internal static Int256 Widen(ReadOnlySpan<byte> values, int width, int row)
    {
        ReadOnlySpan<byte> slot = values.Slice(row * width, width);
        return width switch
        {
            1 => new Int256((sbyte)slot[0]),
            2 => new Int256(BinaryPrimitives.ReadInt16LittleEndian(slot)),
            4 => new Int256(BinaryPrimitives.ReadInt32LittleEndian(slot)),
            8 => new Int256(BinaryPrimitives.ReadInt64LittleEndian(slot)),
            16 => new Int256(BinaryPrimitives.ReadInt128LittleEndian(slot)),
            _ => Int256.FromLittleEndianBytes(slot),
        };
    }

    private static void CompareViewColumns(
        CanonicalNode left, CanonicalNode right, in BothValid valid, ComparisonOp op,
        Span<byte> destination)
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                ViewColumnsCore<EqualOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.NotEqual:
                ViewColumnsCore<NotEqualOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.Less:
                ViewColumnsCore<LessOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.LessOrEqual:
                ViewColumnsCore<LessOrEqualOp>(left, right, in valid, destination);
                break;
            case ComparisonOp.Greater:
                ViewColumnsCore<GreaterOp>(left, right, in valid, destination);
                break;
            default:
                ViewColumnsCore<GreaterOrEqualOp>(left, right, in valid, destination);
                break;
        }
    }

    /// <summary>Two byte columns in ordinal byte order, which for utf8 is code-point order.</summary>
    private static void ViewColumnsCore<TOp>(
        CanonicalNode left, CanonicalNode right, in BothValid valid, Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        ViewValues leftValues = new ViewValues(left);
        ViewValues rightValues = new ViewValues(right);
        bool allValid = valid.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !valid.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            int order = leftValues.At(i).SequenceCompareTo(rightValues.At(i));
            destination[i] = TOp.Holds(order, 0) ? Trilean.True : Trilean.False;
        }
    }

    private static void CompareFixedColumns(
        CanonicalArena arena, CanonicalNode left, CanonicalNode right, in BothValid valid,
        ComparisonOp op, Span<byte> destination)
    {
        int size = checked((int)left.FixedSize);
        if (size != (int)right.FixedSize)
        {
            throw ColumnsMismatch($"a fixed-size list of {size} with one of {right.FixedSize}");
        }

        int rows = destination.Length;
        ReadOnlySpan<byte> leftBytes = FixedBytes(arena, left, rows);
        ReadOnlySpan<byte> rightBytes = FixedBytes(arena, right, rows);
        switch (op)
        {
            case ComparisonOp.Equal:
                FixedColumnsCore<EqualOp>(leftBytes, rightBytes, size, in valid, destination);
                break;
            case ComparisonOp.NotEqual:
                FixedColumnsCore<NotEqualOp>(leftBytes, rightBytes, size, in valid, destination);
                break;
            case ComparisonOp.Less:
                FixedColumnsCore<LessOp>(leftBytes, rightBytes, size, in valid, destination);
                break;
            case ComparisonOp.LessOrEqual:
                FixedColumnsCore<LessOrEqualOp>(leftBytes, rightBytes, size, in valid, destination);
                break;
            case ComparisonOp.Greater:
                FixedColumnsCore<GreaterOp>(leftBytes, rightBytes, size, in valid, destination);
                break;
            default:
                FixedColumnsCore<GreaterOrEqualOp>(leftBytes, rightBytes, size, in valid, destination);
                break;
        }
    }

    /// <summary>Two rows of <paramref name="size"/> bytes, as unsigned bytes, first byte first.</summary>
    private static void FixedColumnsCore<TOp>(
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int size, in BothValid valid,
        Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        bool allValid = valid.AllValid;
        bool uuid = size == UuidSize;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !valid.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            ReadOnlySpan<byte> a = left.Slice(i * size, size);
            ReadOnlySpan<byte> b = right.Slice(i * size, size);
            bool holds = uuid
                ? TOp.Holds(BinaryPrimitives.ReadUInt128BigEndian(a), BinaryPrimitives.ReadUInt128BigEndian(b))
                : TOp.Holds(a.SequenceCompareTo(b), 0);
            destination[i] = holds ? Trilean.True : Trilean.False;
        }
    }
}
