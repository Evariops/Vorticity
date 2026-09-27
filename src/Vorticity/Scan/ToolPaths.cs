using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using Vorticity.Expressions;
using Vorticity.Layouts;

namespace Vorticity;

/// <summary>The tool scan's names and filters, resolved against the schema of the file.</summary>
internal static class ToolPaths
{
    private const int MaxDecimalDigits = 76;

    /// <summary>The field indices of a column named by its top-level name or a <c>.</c>-separated path.</summary>
    internal static int[] Resolve(VortexSchema schema, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int top = schema.IndexOfName(path);
        return top >= 0 ? [top] : Resolve(schema, path.Split('.'), path);
    }

    internal static int[] Resolve(VortexSchema schema, string[] segments, string display)
    {
        int[] indices = new int[segments.Length];
        ReadOnlySpan<VortexField> fields = schema.FieldArray;
        VortexType? parent = null;
        for (int s = 0; s < segments.Length; s++)
        {
            int found = parent is null ? schema.IndexOfName(segments[s]) : parent.IndexOfField(segments[s]);
            if (found < 0)
            {
                throw new VortexSchemaException($"The file has no column '{display}'; its schema is {schema}.");
            }

            indices[s] = found;
            if (s + 1 < segments.Length)
            {
                VortexType type = Storage(fields[found].Type);
                if (type.Kind != VortexTypeKind.Struct)
                {
                    throw new VortexSchemaException($"'{display}' goes through '{segments[s]}', a column of {fields[found].Type}, which has no fields.");
                }

                parent = type;
                fields = type.Fields;
            }
        }

        return indices;
    }

    /// <summary>The type of the column <paramref name="segments"/> names.</summary>
    internal static VortexType TypeOf(VortexSchema schema, string[] segments, string display)
    {
        int[] path = Resolve(schema, segments, display);
        ReadOnlySpan<VortexField> fields = schema.FieldArray;
        VortexType type = fields[path[0]].Type;
        for (int i = 1; i < path.Length; i++)
        {
            type = Storage(type).Fields[path[i]].Type;
        }

        return type;
    }

    /// <summary>The schema of a batch projected by <paramref name="mask"/>: the kept fields, in file order.</summary>
    internal static VortexSchema Project(VortexSchema schema, FieldMask mask) =>
        mask.IsAll ? schema : VortexSchema.Create(ProjectFields(schema.FieldArray, mask));

    private static VortexField[] ProjectFields(ReadOnlySpan<VortexField> fields, FieldMask mask)
    {
        List<VortexField> kept = [];
        for (int i = 0; i < mask.NamedFieldCount; i++)
        {
            int index = mask.GetNamedField(i);
            VortexField field = fields[index];
            FieldMask below = mask.Descend(index);
            VortexType storage = Storage(field.Type);
            if (!below.IsAll && storage.Kind == VortexTypeKind.Struct)
            {
                VortexType projected = VortexType.Struct(ProjectFields(storage.Fields, below));
                field = field with { Type = field.Type.IsNullable ? projected.Nullable : projected };
            }

            kept.Add(field);
        }

        return [.. kept];
    }

    /// <summary>Checks a filter against the schema and rewrites what the text could not know: a list's membership, a decimal's scale, a date written as text.</summary>
    internal static VortexExpr Check(VortexSchema schema, VortexExpr filter)
    {
        switch (filter)
        {
            case LogicalExpr logical:
                return Expr.Logical(logical.IsAnd, Check(schema, logical.Left), Check(schema, logical.Right));
            case NotExpr negation:
                return Expr.Not(Check(schema, negation.Operand));
            case NullCheckExpr check:
                Column(schema, check.Field);
                return check;
            case ComparisonExpr comparison:
            {
                VortexType type = Column(schema, comparison.Field);
                return Compare(comparison.Field, type, comparison.Op, comparison.Value);
            }

            case ColumnComparisonExpr columns:
            {
                VortexType left = Column(schema, columns.Left);
                VortexType right = Column(schema, columns.Right);
                if (!left.NonNullable.Equals(right.NonNullable))
                {
                    throw new VortexSchemaException($"'{columns.Left.Path}' is {left} and '{columns.Right.Path}' is {right}: two columns compare when their types are the same.");
                }

                return columns;
            }

            case InExpr membership:
            {
                VortexType type = Column(schema, membership.Field);
                List<FilterLiteral> literals = [];
                foreach (FilterLiteral literal in membership.Literals)
                {
                    // A null, or a value the column cannot hold exactly, equals no row, so it leaves the set.
                    if (literal.Kind != FilterLiteralKind.Null
                        && Compare(membership.Field, type, ComparisonOp.Equal, literal) is ComparisonExpr { Op: ComparisonOp.Equal } equal)
                    {
                        literals.Add(equal.Value);
                    }
                }

                return literals.Count == 0
                    ? Expr.And(new NullCheckExpr(membership.Field, isNull: true), new NullCheckExpr(membership.Field, isNull: false))
                    : Expr.In(membership.Field, [.. literals]);
            }

            case StringMatchExpr match:
            {
                VortexType type = Column(schema, match.Field);
                VortexType storage = Storage(type);
                if (match.Op == StringMatchOp.Contains && storage.Kind is VortexTypeKind.List or VortexTypeKind.FixedSizeList)
                {
                    return Expr.ListContains(match.Field, match.Pattern);
                }

                if (storage.Kind is not (VortexTypeKind.Utf8 or VortexTypeKind.Binary))
                {
                    throw new VortexSchemaException($"'{match.Field.Path}' is a column of {type}; {match.Op} reads text and binary columns.");
                }

                return match;
            }

            case ListContainsExpr contains:
            {
                VortexType type = Storage(Column(schema, contains.Field));
                if (type.Kind is not (VortexTypeKind.List or VortexTypeKind.FixedSizeList))
                {
                    throw new VortexSchemaException($"'{contains.Field.Path}' is a column of {type}, not a list.");
                }

                return contains;
            }

            default:
                throw new VortexSchemaException($"'{filter}' is not a predicate.");
        }
    }

    /// <summary>The column a hole of an interpolated filter is compared to: the field of the predicate the text ends in.</summary>
    internal static VortexField ColumnBeforeHole(VortexSchema schema, string text)
    {
        string[] segments = ExprText.LastField(text)
            ?? throw new FormatException($"A hole follows '{text}', which names no column before it.");
        string display = string.Join('.', segments);
        return new VortexField(display, TypeOf(schema, segments, display));
    }

    /// <summary>A decimal hole as a literal at the column's scale, refused when it has more digits than the scale.</summary>
    internal static FilterLiteral ExactDecimal<T>(ColumnSym target, T value)
    {
        Predicate exact = SymLowering.Compare(target, ComparisonOp.Equal, value);
        if (exact.Node is ComparisonExpr comparison)
        {
            return comparison.Value;
        }

        throw new VortexSchemaException($"{value} has more digits after the point than '{target.Field.Path}', of {target.Type}, keeps.");
    }

    private static VortexType Column(VortexSchema schema, FieldExpr field) =>
        TypeOf(schema, field.Segments ?? field.Path.Split('.'), field.Path);

    private static VortexExpr Compare(FieldExpr field, VortexType type, ComparisonOp op, FilterLiteral literal)
    {
        if (literal.Kind == FilterLiteralKind.Null)
        {
            return new ComparisonExpr(field, op, literal);
        }

        if (Storage(type).Kind == VortexTypeKind.Decimal)
        {
            if (!TryNumber(literal, out BigInteger mantissa, out int scale))
            {
                throw new VortexSchemaException(
                    $"'{field.Path}' is a column of {type} and the filter compares it with {Describe(literal)} that is not a decimal number.");
            }

            return Expression(field, SymLowering.CompareDecimal(new ColumnSym(field, Storage(type), null, null, -1, []), op, mantissa, scale));
        }

        if (TryInstant(field, type, literal, out ColumnSym? column, out SymLowering.Placement at))
        {
            return Expression(field, SymLowering.CompareAt(column, op, at));
        }

        if (!Convert(field, type, literal, out FilterLiteral converted))
        {
            throw new VortexSchemaException(
                $"'{field.Path}' is a column of {type} and the filter compares it with {Describe(literal)}; no comparison relates the two.");
        }

        return new ComparisonExpr(field, op, converted);
    }

    /// <summary>A lowered predicate as an expression: one that holds for no row, or for every row with a value, spelled on the field.</summary>
    private static VortexExpr Expression(FieldExpr field, Predicate lowered) =>
        lowered.Node ?? (lowered.IsNone
            ? Expr.And(new NullCheckExpr(field, isNull: true), new NullCheckExpr(field, isNull: false))
            : new NullCheckExpr(field, isNull: false));

    /// <summary>
    /// A text time or instant compared with a time or timestamp column, placed among the stored
    /// values: text finer than the column's unit falls between two of them, and compares against
    /// the neighbour on its side rather than a truncated value.
    /// </summary>
    private static bool TryInstant(FieldExpr field, VortexType type, FilterLiteral literal, [NotNullWhen(true)] out ColumnSym? column, out SymLowering.Placement at)
    {
        column = null;
        at = default;
        if (literal.Kind != FilterLiteralKind.Bytes || type.Kind != VortexTypeKind.Extension)
        {
            return false;
        }

        string text = Encoding.UTF8.GetString(literal.BytesValue);
        ColumnSym target = new ColumnSym(field, type, null, null, -1, []);
        switch (type.ExtensionId)
        {
            case ExtensionIds.Time when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out TimeOnly time):
                at = SymLowering.Place(target, ClrShape.For<TimeOnly>.Value, time);
                break;
            case ExtensionIds.Timestamp when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset instant):
                at = SymLowering.Place(target, ClrShape.For<DateTimeOffset>.Value, instant);
                break;
            default:
                return false;
        }

        column = target;
        return true;
    }

    /// <summary>A literal in the column's domain: a text date made storage units, a text uuid made bytes; false when the two cannot compare.</summary>
    private static bool Convert(FieldExpr field, VortexType type, FilterLiteral literal, out FilterLiteral converted)
    {
        converted = literal;
        VortexType storage = Storage(type);
        if (literal.Kind == FilterLiteralKind.Bytes && type.Kind == VortexTypeKind.Extension)
        {
            string text = Encoding.UTF8.GetString(literal.BytesValue);
            ColumnSym column = new ColumnSym(field, type, null, null, -1, []);
            switch (type.ExtensionId)
            {
                case ExtensionIds.Date when DateOnly.TryParse(text, CultureInfo.InvariantCulture, out DateOnly date):
                    converted = SymLowering.Literal(column, ClrShape.For<DateOnly>.Value, date);
                    return true;
                case ExtensionIds.Uuid when Guid.TryParse(text, out Guid uuid):
                    converted = SymLowering.Literal(column, ClrShape.For<Guid>.Value, uuid);
                    return true;
            }
        }

        return (storage.Kind, literal.Kind) switch
        {
            (VortexTypeKind.Bool, FilterLiteralKind.Bool) => true,
            (VortexTypeKind.Primitive, FilterLiteralKind.Signed or FilterLiteralKind.Unsigned or FilterLiteralKind.Float) => true,
            (VortexTypeKind.Utf8 or VortexTypeKind.Binary, FilterLiteralKind.Bytes) => true,
            (VortexTypeKind.FixedSizeList, FilterLiteralKind.Bytes) => type.ExtensionId == ExtensionIds.Uuid,
            (VortexTypeKind.Null, _) => true,
            _ => false,
        };
    }

    /// <summary>
    /// A number of the filter as the decimal the text wrote, <paramref name="mantissa"/> ×
    /// 10^-<paramref name="scale"/>. A float literal goes through its shortest round-trip text,
    /// because the exact value of the double is not what was typed: <c>99.9996</c> is
    /// <c>99.99960000000000093…</c> as a double. Quoted text is read exactly, whatever its digits.
    /// </summary>
    private static bool TryNumber(FilterLiteral literal, out BigInteger mantissa, out int scale)
    {
        scale = 0;
        switch (literal.Kind)
        {
            case FilterLiteralKind.Signed:
                mantissa = literal.SignedValue;
                return true;
            case FilterLiteralKind.Unsigned:
                mantissa = literal.UnsignedValue;
                return true;
            case FilterLiteralKind.Float:
                return TryParseNumber(literal.FloatValue.ToString("R", CultureInfo.InvariantCulture), out mantissa, out scale);
            case FilterLiteralKind.Bytes:
                return TryParseNumber(Encoding.UTF8.GetString(literal.BytesValue), out mantissa, out scale);
            default:
                mantissa = default;
                return false;
        }
    }

    /// <summary>Reads <c>[-+]digits[.digits][e[-+]digits]</c> exactly.</summary>
    private static bool TryParseNumber(ReadOnlySpan<char> text, out BigInteger mantissa, out int scale)
    {
        mantissa = default;
        scale = 0;
        text = text.Trim();
        int exponentAt = text.IndexOfAny('e', 'E');
        int exponent = 0;
        if (exponentAt >= 0)
        {
            // A decimal holds at most 76 digits, so a larger exponent names no value a column holds.
            if (!int.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is < -MaxDecimalDigits * 2 or > MaxDecimalDigits * 2)
            {
                return false;
            }

            text = text[..exponentAt];
        }

        bool negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        int point = text.IndexOf('.');
        ReadOnlySpan<char> whole = point < 0 ? text : text[..point];
        ReadOnlySpan<char> fraction = point < 0 ? default : text[(point + 1)..];
        if (whole.Length + fraction.Length == 0 || whole.ContainsAnyExceptInRange('0', '9') || fraction.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        int count = whole.Length + fraction.Length;
        Span<char> digits = count <= 128 ? stackalloc char[128] : new char[count];
        digits = digits[..count];
        whole.CopyTo(digits);
        fraction.CopyTo(digits[whole.Length..]);
        mantissa = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        scale = fraction.Length - exponent;
        if (scale < 0)
        {
            mantissa *= BigInteger.Pow(10, -scale);
            scale = 0;
        }

        if (negative)
        {
            mantissa = -mantissa;
        }

        return true;
    }

    private static string Describe(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Bool => "a boolean",
        FilterLiteralKind.Signed or FilterLiteralKind.Unsigned => "an integer",
        FilterLiteralKind.Float => "a float",
        _ => "text",
    };

    private static VortexType Storage(VortexType type)
    {
        while (type.Kind == VortexTypeKind.Extension && type.StorageType is { } storage)
        {
            type = storage;
        }

        return type;
    }
}
