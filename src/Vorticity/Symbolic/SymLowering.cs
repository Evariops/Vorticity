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

        return new Predicate(new ComparisonExpr(column.Field, op, Literal(column, shape, value)));
    }

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

            literals.Add(Literal(column, shape, value));
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
        return new Predicate(Expr.ListContains(list.Field, Literal(elementColumn, ClrShape.For<T>.Value, value)));
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

            case ClrKind.TimeOnly:
                return FilterLiteral.From(TemporalUnits.FromTicks(((TimeOnly)boxed).Ticks, type.Unit ?? TimeUnit.Microseconds));
            case ClrKind.DateTime:
            {
                DateTime instant = (DateTime)boxed;
                if (type.TimeZone is not null && instant.Kind == DateTimeKind.Local)
                {
                    instant = instant.ToUniversalTime();
                }

                return FilterLiteral.From(TemporalUnits.FromTicks(instant.Ticks - TemporalUnits.UnixEpochTicks, type.Unit ?? TimeUnit.Microseconds));
            }

            case ClrKind.DateTimeOffset:
                return FilterLiteral.From(TemporalUnits.FromTicks(((DateTimeOffset)boxed).UtcTicks - TemporalUnits.UnixEpochTicks, type.Unit ?? TimeUnit.Microseconds));
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
