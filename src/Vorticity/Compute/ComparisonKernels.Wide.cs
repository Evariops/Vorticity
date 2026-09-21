using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

internal static partial class ComparisonKernels
{
    /// <summary>The width of a uuid, the fixed-size list of bytes worth a path of its own.</summary>
    private const int UuidSize = 16;

    /// <summary>
    /// A decimal literal as the unscaled integer it stands for, at the column's scale: a signed
    /// integer, or sixteen or thirty-two bytes of little-endian two's complement.
    /// </summary>
    /// <param name="literal">The literal.</param>
    /// <param name="value">The unscaled value.</param>
    /// <returns>Whether the literal is in one of those shapes.</returns>
    internal static bool TryDecimal(FilterLiteral literal, out Int256 value)
    {
        switch (literal.Kind)
        {
            case FilterLiteralKind.Signed:
                value = new Int256(literal.SignedValue);
                return true;

            case FilterLiteralKind.Bytes when literal.BytesValue.Length == 16:
                value = new Int256(BinaryPrimitives.ReadInt128LittleEndian(literal.BytesValue));
                return true;

            case FilterLiteralKind.Bytes when literal.BytesValue.Length == Int256.ByteCount:
                value = Int256.FromLittleEndianBytes(literal.BytesValue);
                return true;

            default:
                value = default;
                return false;
        }
    }

    /// <summary>
    /// The decimal comparison over the values, without a node, so that a constant's element can
    /// reach it as a one-row buffer.
    /// </summary>
    /// <remarks>
    /// The literal is narrowed to the storage once, before the loop, and the loop is the integer
    /// kernel of that width. A literal the storage cannot hold lies beyond every value it can, so
    /// its sign settles every row, as it does for an integer literal outside a column's range.
    /// </remarks>
    private static void CompareDecimalValues(
        DecimalStorageType storage, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op,
        FilterLiteral literal, Span<byte> destination)
    {
        if (!TryDecimal(literal, out Int256 wanted))
        {
            throw Mismatch("a decimal", literal.Kind);
        }

        int beyond = wanted.IsNegative ? 1 : -1;
        switch (storage)
        {
            case DecimalStorageType.I256:
                CompareInt256(values, mask, op, wanted, destination);
                return;

            case DecimalStorageType.I128:
                if (wanted.TryToInt128(out Int128 middle))
                {
                    CompareOp<Int128, Int128>(values, mask, op, middle, destination);
                }
                else
                {
                    FillFromOrder(mask, op, beyond, destination);
                }

                return;
        }

        if (!wanted.TryToInt64(out long narrow))
        {
            FillFromOrder(mask, op, beyond, destination);
            return;
        }

        switch (storage)
        {
            case DecimalStorageType.I8:
                CompareOp<sbyte, long>(values, mask, op, narrow, destination);
                break;
            case DecimalStorageType.I16:
                CompareOp<short, long>(values, mask, op, narrow, destination);
                break;
            case DecimalStorageType.I32:
                CompareOp<int, long>(values, mask, op, narrow, destination);
                break;
            default:
                CompareOp<long, long>(values, mask, op, narrow, destination);
                break;
        }
    }

    private static void CompareInt256(
        ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, Int256 wanted,
        Span<byte> destination)
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                Int256Core<EqualOp>(values, mask, wanted, destination);
                break;
            case ComparisonOp.NotEqual:
                Int256Core<NotEqualOp>(values, mask, wanted, destination);
                break;
            case ComparisonOp.Less:
                Int256Core<LessOp>(values, mask, wanted, destination);
                break;
            case ComparisonOp.LessOrEqual:
                Int256Core<LessOrEqualOp>(values, mask, wanted, destination);
                break;
            case ComparisonOp.Greater:
                Int256Core<GreaterOp>(values, mask, wanted, destination);
                break;
            default:
                Int256Core<GreaterOrEqualOp>(values, mask, wanted, destination);
                break;
        }
    }

    /// <summary>The 256-bit decimal loop, with the operator and the validity resolved.</summary>
    private static void Int256Core<TOp>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, Int256 wanted, Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ReadOnlySpan<ulong> limbs = MemoryMarshal.Cast<byte, ulong>(bytes);
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            int at = i * 4;
            Int256 value = Int256.FromLimbs(limbs[at], limbs[at + 1], limbs[at + 2], limbs[at + 3]);
            destination[i] = TOp.Holds(value.CompareTo(wanted), 0) ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>
    /// A fixed-size list of bytes against a bytes literal, ordered as unsigned bytes, first byte
    /// first: a uuid, whose storage is sixteen bytes in network order, and any list of that shape.
    /// </summary>
    /// <remarks>
    /// A literal of another length is still ordered, the shorter of two equal prefixes first, and
    /// never equal. Sixteen bytes against sixteen compare as one big-endian 128-bit integer, which
    /// is the same order in one comparison instead of a call per row.
    /// </remarks>
    private static void CompareFixedBytes(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        if (literal.Kind != FilterLiteralKind.Bytes)
        {
            throw Mismatch("a fixed-size list of bytes", literal.Kind);
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        int size = checked((int)node.FixedSize);
        ReadOnlySpan<byte> values = FixedBytes(arena, node, destination.Length);
        ReadOnlySpan<byte> wanted = literal.BytesValue;
        if (size == UuidSize && wanted.Length == UuidSize)
        {
            UInt128 key = BinaryPrimitives.ReadUInt128BigEndian(wanted);
            switch (op)
            {
                case ComparisonOp.Equal:
                    UuidCore<EqualOp>(values, mask, key, destination);
                    break;
                case ComparisonOp.NotEqual:
                    UuidCore<NotEqualOp>(values, mask, key, destination);
                    break;
                case ComparisonOp.Less:
                    UuidCore<LessOp>(values, mask, key, destination);
                    break;
                case ComparisonOp.LessOrEqual:
                    UuidCore<LessOrEqualOp>(values, mask, key, destination);
                    break;
                case ComparisonOp.Greater:
                    UuidCore<GreaterOp>(values, mask, key, destination);
                    break;
                default:
                    UuidCore<GreaterOrEqualOp>(values, mask, key, destination);
                    break;
            }

            return;
        }

        switch (op)
        {
            case ComparisonOp.Equal:
                FixedCore<EqualOp>(values, size, mask, wanted, destination);
                break;
            case ComparisonOp.NotEqual:
                FixedCore<NotEqualOp>(values, size, mask, wanted, destination);
                break;
            case ComparisonOp.Less:
                FixedCore<LessOp>(values, size, mask, wanted, destination);
                break;
            case ComparisonOp.LessOrEqual:
                FixedCore<LessOrEqualOp>(values, size, mask, wanted, destination);
                break;
            case ComparisonOp.Greater:
                FixedCore<GreaterOp>(values, size, mask, wanted, destination);
                break;
            default:
                FixedCore<GreaterOrEqualOp>(values, size, mask, wanted, destination);
                break;
        }
    }

    /// <summary>
    /// The bytes of the first <paramref name="rows"/> rows of a fixed-size list of bytes, row
    /// after row.
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="list">A <see cref="CanonicalKind.FixedSizeList"/> node.</param>
    /// <param name="rows">The rows wanted.</param>
    /// <exception cref="NotSupportedException">The elements are not bytes, or some are null.</exception>
    /// <exception cref="VortexFormatException">The elements are fewer than the rows need.</exception>
    private static ReadOnlySpan<byte> FixedBytes(CanonicalArena arena, CanonicalNode list, int rows)
    {
        int size = checked((int)list.FixedSize);
        CanonicalNode elements = arena.GetNode(Unwrap(arena, list.ElementsIndex));
        bool bytes = elements.Kind switch
        {
            CanonicalKind.Primitive => elements.PType == PType.U8,
            CanonicalKind.Constant => elements.DType.Kind == DTypeKind.Primitive && elements.DType.PType == PType.U8,
            _ => false,
        };

        // A null element leaves its row's bytes undefined, and a byte order over undefined bytes is
        // not one a filter can promise.
        if (!bytes || !ValidityMask.From(arena, elements.Validity).AllValid)
        {
            throw new NotSupportedException(
                "A filter compares a fixed-size list whose elements are non-null u8, as a byte " +
                $"string; this one holds {elements.DType}.");
        }

        int length = checked(rows * size);
        ReadOnlySpan<byte> values = elements.Values.Span;
        if (values.Length < length)
        {
            throw new VortexFormatException(
                $"A fixed-size list of {rows} rows of {size} needs {length} elements; it has {values.Length}.");
        }

        return values[..length];
    }

    /// <summary>Sixteen bytes a row against sixteen, as big-endian 128-bit integers.</summary>
    private static void UuidCore<TOp>(
        ReadOnlySpan<byte> values, ValidityMask mask, UInt128 wanted, Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            UInt128 value = BinaryPrimitives.ReadUInt128BigEndian(values.Slice(i * UuidSize, UuidSize));
            destination[i] = TOp.Holds(value, wanted) ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>A row of <paramref name="size"/> bytes against the literal, in bytewise order.</summary>
    private static void FixedCore<TOp>(
        ReadOnlySpan<byte> values, int size, ValidityMask mask, ReadOnlySpan<byte> wanted,
        Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            destination[i] = TOp.Holds(values.Slice(i * size, size).SequenceCompareTo(wanted), 0)
                ? Trilean.True
                : Trilean.False;
        }
    }
}
