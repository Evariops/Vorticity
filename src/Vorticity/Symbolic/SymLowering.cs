using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Vorticity.Expressions;
using Vorticity.Types.Numerics;

namespace Vorticity;

/// <summary>Turns the operators of <see cref="Sym{T}"/> into the engine's expressions, with literals in the column's own units.</summary>
internal static class SymLowering
{
    internal const string NoArithmetic = "Vortex does not push arithmetic down. Compute it on the columns after the scan.";

    /// <summary>Where a value falls among a column's stored values; see <see cref="Place"/>.</summary>
    /// <param name="Floor">The stored value at or below it, when <paramref name="Beyond"/> is 0.</param>
    /// <param name="Exact">Whether it is that value.</param>
    /// <param name="Beyond">1 above every value the column can store, -1 below every one, 0 among them.</param>
    internal readonly record struct Placement(FilterLiteral Floor, bool Exact, int Beyond);

    internal static Predicate Compare<T>(ColumnSym column, ComparisonOp op, T value)
    {
        if (value is null)
        {
            return op switch
            {
                ComparisonOp.Equal => new Predicate(new NullCheckExpr(column.Field, isNull: true)),
                ComparisonOp.NotEqual => new Predicate(new NullCheckExpr(column.Field, isNull: false)),
                _ => Predicate.None,
            };
        }

        ClrShape shape = ClrShape.For<T>.Value;
        if (shape.Kind is ClrKind.Decimal or ClrKind.VortexDecimal)
        {
            return CompareDecimal(column, op, value);
        }

        return CompareAt(column, op, Place(column, shape, value));
    }

    /// <summary>
    /// A comparison with a value placed among the column's stored values: against the value itself
    /// when the column stores it, and otherwise, since it lies strictly between two stored values
    /// or beyond every one, against the neighbour on the right side of it.
    /// </summary>
    internal static Predicate CompareAt(ColumnSym column, ComparisonOp op, Placement at)
    {
        if (at.Exact && at.Beyond == 0)
        {
            return new Predicate(new ComparisonExpr(column.Field, op, at.Floor));
        }

        Predicate valid = new Predicate(new NullCheckExpr(column.Field, isNull: false));
        if (at.Beyond != 0)
        {
            bool holds = op == ComparisonOp.NotEqual
                || (at.Beyond > 0 ? op is ComparisonOp.Less or ComparisonOp.LessOrEqual : op is ComparisonOp.Greater or ComparisonOp.GreaterOrEqual);
            return holds ? valid : Predicate.None;
        }

        return op switch
        {
            ComparisonOp.Equal => Predicate.None,
            ComparisonOp.NotEqual => valid,
            ComparisonOp.Less or ComparisonOp.LessOrEqual => new Predicate(new ComparisonExpr(column.Field, ComparisonOp.LessOrEqual, at.Floor)),
            _ => new Predicate(new ComparisonExpr(column.Field, ComparisonOp.Greater, at.Floor)),
        };
    }

    /// <summary>
    /// Where a value falls among the values a column stores: the stored value at or below it, and
    /// whether it is that value. A time or an instant finer than the column's unit falls between
    /// two, and one past what the column's integers reach falls beyond them all.
    /// </summary>
    internal static Placement Place<T>(ColumnSym column, ClrShape shape, T value)
    {
        object boxed = value!;
        return shape.Kind is ClrKind.TimeOnly or ClrKind.DateTime or ClrKind.DateTimeOffset
            && column.Type.ExtensionId is ExtensionIds.Time or ExtensionIds.Timestamp
            ? PlaceInstant(column.Type, shape.Kind, boxed)
            : new Placement(Literal(column, shape, value), true, 0);
    }

    private static Placement PlaceInstant(VortexType type, ClrKind kind, object boxed)
    {
        Int128 ticks = kind switch
        {
            ClrKind.TimeOnly => ((TimeOnly)boxed).Ticks,
            ClrKind.DateTime => (Int128)Utc(type, (DateTime)boxed).Ticks - TemporalUnits.UnixEpochTicks,
            _ => (Int128)((DateTimeOffset)boxed).UtcTicks - TemporalUnits.UnixEpochTicks,
        };
        (Int128 floor, bool exact) = TemporalUnits.FromTicks(ticks, type.Unit ?? TimeUnit.Microseconds);
        if (floor > long.MaxValue || (floor == long.MaxValue && !exact))
        {
            return new Placement(default, false, 1);
        }

        return floor < long.MinValue ? new Placement(default, false, -1) : new Placement(FilterLiteral.From((long)floor), exact, 0);
    }

    /// <summary>An instant as the column stores it: a local time made universal for a column with a zone, taken as it reads for a naive one.</summary>
    private static DateTime Utc(VortexType type, DateTime instant) =>
        type.TimeZone is not null && instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : instant;

    internal static Predicate Compare(ColumnSym left, ComparisonOp op, ColumnSym right)
    {
        if (!left.Type.NonNullable.Equals(right.Type.NonNullable))
        {
            throw new VortexSchemaException($"'{left.Field.Path}' is {left.Type} and '{right.Field.Path}' is {right.Type}: two columns compare when their types are the same.");
        }

        return new Predicate(new ColumnComparisonExpr(left.Field, op, right.Field));
    }

    internal static Predicate In<T>(ColumnSym column, ReadOnlySpan<T> values)
    {
        ClrShape shape = ClrShape.For<T>.Value;
        List<FilterLiteral> literals = new List<FilterLiteral>(values.Length);
        foreach (T value in values)
        {
            if (value is null)
            {
                continue;
            }

            if (shape.Kind is ClrKind.Decimal or ClrKind.VortexDecimal)
            {
                if (TryExactDecimal(column, value, out FilterLiteral exact))
                {
                    literals.Add(exact);
                }

                continue;
            }

            // A value the column cannot store matches no row, so it leaves the list.
            Placement at = Place(column, shape, value);
            if (at.Exact && at.Beyond == 0)
            {
                literals.Add(at.Floor);
            }
        }

        return literals.Count == 0 ? Predicate.None : new Predicate(Expr.In(column.Field, [.. literals]));
    }

    internal static Predicate StringMatch(ColumnSym column, StringMatchOp op, string text, char escape = '\\')
    {
        ArgumentNullException.ThrowIfNull(text);
        if (escape > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(escape), escape, "The escape is an ASCII character.");
        }

        return new Predicate(op switch
        {
            StringMatchOp.StartsWith => Expr.StartsWith(column.Field, FilterLiteral.From(text)),
            StringMatchOp.Contains => Expr.Contains(column.Field, FilterLiteral.From(text)),
            _ => Expr.Like(column.Field, FilterLiteral.From(text), (byte)escape),
        });
    }

    internal static Predicate ListContains<T>(ColumnSym list, T value)
    {
        if (value is null)
        {
            return Predicate.None;
        }

        VortexType element = list.Type.ElementType ?? throw new VortexSchemaException($"'{list.Field.Path}' is not a list.");
        ColumnSym elementColumn = new ColumnSym(list.Field, element, list.Extensions, null, -1, list.FieldPath);
        Placement at = Place(elementColumn, ClrShape.For<T>.Value, value);
        return at.Exact && at.Beyond == 0 ? new Predicate(Expr.ListContains(list.Field, at.Floor)) : Predicate.None;
    }

    /// <summary>The literal of <paramref name="value"/> in the storage units of <paramref name="column"/>.</summary>
    internal static FilterLiteral Literal<T>(ColumnSym column, ClrShape shape, T value)
    {
        object boxed = value!;
        VortexType type = column.Type;
        if (type.Kind == VortexTypeKind.Extension && column.Extensions is { } extensions
            && extensions.TryGet(type.ExtensionId!, out ExtensionRegistration registration)
            && registration.ClrType == boxed.GetType())
        {
            return registration.ToLiteral(boxed, type.ExtensionMetadata.Span);
        }

        switch (shape.Kind)
        {
            case ClrKind.Bool:
                return FilterLiteral.From((bool)boxed);
            case ClrKind.Signed:
                return FilterLiteral.From(Convert.ToInt64(boxed, System.Globalization.CultureInfo.InvariantCulture));
            case ClrKind.Unsigned:
                return FilterLiteral.From(Convert.ToUInt64(boxed, System.Globalization.CultureInfo.InvariantCulture));
            case ClrKind.Float:
                return FilterLiteral.From(boxed is Half half ? (double)half : Convert.ToDouble(boxed, System.Globalization.CultureInfo.InvariantCulture));
            case ClrKind.String:
                return FilterLiteral.From((string)boxed);
            case ClrKind.Binary:
                return FilterLiteral.From(((ReadOnlyMemory<byte>)boxed).Span);
            case ClrKind.DateOnly:
            {
                long days = ((DateOnly)boxed).DayNumber - TemporalUnits.UnixEpochDayNumber;
                return FilterLiteral.From(type.Unit == TimeUnit.Milliseconds ? days * 86_400_000L : days);
            }

            case ClrKind.TimeOnly or ClrKind.DateTime or ClrKind.DateTimeOffset:
            {
                Placement at = PlaceInstant(type, shape.Kind, boxed);
                return at.Exact && at.Beyond == 0
                    ? at.Floor
                    : throw new VortexSchemaException($"{boxed} is not a value of '{column.Field.Path}', of {type}: its unit holds no such instant.");
            }
            case ClrKind.Guid:
            {
                Span<byte> bytes = stackalloc byte[16];
                ((Guid)boxed).TryWriteBytes(bytes, bigEndian: true, out _);
                return FilterLiteral.From(bytes);
            }

            default:
                throw new VortexUnsupportedException(
                    ClrFit.Name(typeof(T)), ComponentKind.Feature, $"'{column.Field.Path}' cannot be compared with a {ClrFit.Name(typeof(T))}; a list is filtered by Contains.");
        }
    }

    private static Predicate CompareDecimal<T>(ColumnSym column, ComparisonOp op, T value)
    {
        (BigInteger mantissa, int scale) = MantissaOf(value!);
        return CompareDecimal(column, op, mantissa, scale);
    }

    /// <summary>A decimal column compared with <paramref name="mantissa"/> × 10^-<paramref name="scale"/>, lowered as a typed value is.</summary>
    internal static Predicate CompareDecimal(ColumnSym column, ComparisonOp op, BigInteger mantissa, int scale)
    {
        (BigInteger scaled, bool exact, int sign) = Rescale(column, mantissa, scale);
        if (exact)
        {
            return new Predicate(new ComparisonExpr(column.Field, op, WideLiteral(scaled)));
        }

        // The value lies strictly between two representable ones, so equality never holds and an
        // ordering compares against the neighbour on the right side of it.
        BigInteger floor = sign < 0 ? scaled - 1 : scaled;
        BigInteger ceiling = floor + 1;
        return op switch
        {
            ComparisonOp.Equal => Predicate.None,
            ComparisonOp.NotEqual => new Predicate(new NullCheckExpr(column.Field, isNull: false)),
            ComparisonOp.Less or ComparisonOp.LessOrEqual => new Predicate(new ComparisonExpr(column.Field, ComparisonOp.LessOrEqual, WideLiteral(floor))),
            _ => new Predicate(new ComparisonExpr(column.Field, ComparisonOp.GreaterOrEqual, WideLiteral(ceiling))),
        };
    }

    private static bool TryExactDecimal<T>(ColumnSym column, T value, out FilterLiteral literal)
    {
        (BigInteger mantissa, int scale) = MantissaOf(value!);
        (BigInteger scaled, bool exact, _) = Rescale(column, mantissa, scale);
        literal = exact ? WideLiteral(scaled) : default;
        return exact;
    }

    private static (BigInteger Mantissa, int Scale) MantissaOf(object value) => value switch
    {
        decimal d => Mantissa(d),
        VortexDecimal v => (Int256Big(v), v.Scale),
        _ => throw new VortexSchemaException($"{value.GetType()} is not a decimal."),
    };

    /// <summary>The value as an unscaled integer at the column's scale, truncated toward zero, and whether that is exact.</summary>
    private static (BigInteger Scaled, bool Exact, int Sign) Rescale(ColumnSym column, BigInteger mantissa, int scale)
    {
        if (column.Type.Kind != VortexTypeKind.Decimal)
        {
            throw new VortexSchemaException($"'{column.Field.Path}' is {column.Type}, not a decimal.");
        }

        int target = column.Type.Scale;
        if (scale <= target)
        {
            return (mantissa * BigInteger.Pow(10, target - scale), true, mantissa.Sign);
        }

        BigInteger quotient = BigInteger.DivRem(mantissa, BigInteger.Pow(10, scale - target), out BigInteger remainder);
        return (quotient, remainder.IsZero, mantissa.Sign);
    }

    private static (BigInteger Mantissa, int Scale) Mantissa(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger magnitude = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        int scale = (bits[3] >> 16) & 0xFF;
        return (bits[3] < 0 ? -magnitude : magnitude, scale);
    }

    private static BigInteger Int256Big(VortexDecimal value)
    {
        Span<byte> bytes = stackalloc byte[32];
        value.Unscaled.WriteLittleEndianBytes(bytes);
        return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
    }

    /// <summary>An unscaled decimal as a literal: a signed integer when it fits, sixteen or thirty-two little-endian bytes otherwise.</summary>
    internal static FilterLiteral WideLiteral(BigInteger value)
    {
        if (value >= long.MinValue && value <= long.MaxValue)
        {
            return FilterLiteral.From((long)value);
        }

        int width = value >= (BigInteger)Int128.MinValue && value <= (BigInteger)Int128.MaxValue ? 16 : 32;
        byte[] bytes = new byte[width];
        if (!value.TryWriteBytes(bytes, out int written, isUnsigned: false, isBigEndian: false))
        {
            throw new VortexSchemaException($"The decimal {value} does not fit 256 bits.");
        }

        if (value.Sign < 0)
        {
            bytes.AsSpan(written).Fill(0xFF);
        }

        return FilterLiteral.From(bytes);
    }
}
