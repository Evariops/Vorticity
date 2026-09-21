using System;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Scanning;

/// <summary>
/// Refuses a filter whose constants name a comparison the schema cannot make, before anything is
/// read.
/// </summary>
/// <remarks>
/// The comparison kernels already refuse a constant of the wrong kind, but only over the rows they
/// are handed, and an ordering predicate never gets that far: a zone whose bounds cannot be ordered
/// against the constant is ruled out, so every zone is, and the caller receives an empty result
/// instead of an error -- a file that looks empty rather than a filter that is wrong. Settling it
/// against the schema decides the whole family at the call that took the filter, once, and costs
/// nothing per row.
/// </remarks>
internal static class FilterTypeCheck
{
    /// <summary>What a column and a constant have to share for a comparison to mean anything.</summary>
    private enum Domain : byte
    {
        /// <summary>Nothing is refused here: the kernels are the ones that rule on this shape.</summary>
        Open,

        /// <summary>Booleans.</summary>
        Bool,

        /// <summary>Integers and floats, which compare against each other.</summary>
        Number,

        /// <summary>Text and binary, which compare as bytes.</summary>
        Bytes,

        /// <summary>
        /// Decimals, compared against their unscaled value at the column's scale: a signed integer,
        /// or sixteen or thirty-two bytes of little-endian two's complement.
        /// </summary>
        Decimal,

        /// <summary>A fixed-size list of non-null bytes, a uuid among them, which compares as bytes.</summary>
        FixedBytes,

        /// <summary>A list, which no comparison reads and <c>ListContains</c> looks inside.</summary>
        List,
    }

    /// <summary>
    /// Checks every constant <paramref name="filter"/> carries against the column it names.
    /// </summary>
    /// <param name="schema">The file's schema.</param>
    /// <param name="filter">The predicate.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
    /// <exception cref="ArgumentException">
    /// A constant and its column are of kinds no comparison relates.
    /// </exception>
    internal static void Check(DType schema, VortexExpr filter, string parameterName)
    {
        if (schema.IsDefault)
        {
            return;
        }

        Walk(schema, filter, parameterName, 0);
    }

    private static void Walk(DType schema, VortexExpr filter, string parameterName, int depth)
    {
        if (depth > FilterEvaluator.MaxDepth)
        {
            throw new ArgumentException(
                $"A filter expression nests deeper than {FilterEvaluator.MaxDepth} levels.",
                parameterName);
        }

        switch (filter)
        {
            case ComparisonExpr comparison:
                CheckConstant(schema, comparison.Field, comparison.Value, parameterName);
                return;

            case InExpr membership:
            {
                FilterLiteral[] literals = membership.Literals;
                for (int i = 0; i < literals.Length; i++)
                {
                    CheckConstant(schema, membership.Field, literals[i], parameterName);
                }

                return;
            }

            case StringMatchExpr match:
                CheckText(schema, match.Field, match.Op, parameterName);
                return;

            case ColumnComparisonExpr columns:
                CheckColumns(schema, columns, parameterName);
                return;

            case NotExpr negation:
                Walk(schema, negation.Operand, parameterName, depth + 1);
                return;

            case LogicalExpr logical:
                Walk(schema, logical.Left, parameterName, depth + 1);
                Walk(schema, logical.Right, parameterName, depth + 1);
                return;

            default:
                // A null check reads no constant, and ListContains is refused by the list kernel
                // itself, loudly, whatever the element type is.
                return;
        }
    }

    private static void CheckConstant(
        DType schema, FieldExpr field, FilterLiteral value, string parameterName)
    {
        if (!TryResolve(schema, field, out DType column))
        {
            return;
        }

        // A list column refuses every comparison, constant or not, and saying so is the kernel's;
        // a null constant is unknown for every row by design, and a null column answers unknown to
        // every constant.
        Domain wanted = DomainOf(column);
        if (wanted is Domain.Open or Domain.List || value.Kind == FilterLiteralKind.Null ||
            Accepts(wanted, value))
        {
            return;
        }

        throw new ArgumentException(
            $"'{field.Path}' is a column of {column} and the filter compares it against a " +
            $"{Name(value)} constant. No comparison relates the two: build the constant " +
            "from the column's own type.",
            parameterName);
    }

    /// <summary>Whether a column of <paramref name="wanted"/> compares against <paramref name="value"/>.</summary>
    private static bool Accepts(Domain wanted, FilterLiteral value) => wanted switch
    {
        Domain.Bool => value.Kind == FilterLiteralKind.Bool,
        Domain.Number => value.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned or FilterLiteralKind.Float,
        Domain.Bytes or Domain.FixedBytes => value.Kind == FilterLiteralKind.Bytes,
        Domain.Decimal => ComparisonKernels.TryDecimal(value, out _),
        _ => true,
    };

    private static void CheckText(
        DType schema, FieldExpr field, StringMatchOp op, string parameterName)
    {
        if (!TryResolve(schema, field, out DType column))
        {
            return;
        }

        if (DomainOf(column) is Domain.Open or Domain.Bytes)
        {
            return;
        }

        throw new ArgumentException(
            $"'{field.Path}' is a column of {column} and the filter asks {op} of it. {op} reads " +
            "text and binary columns only.",
            parameterName);
    }

    /// <summary>
    /// Refuses two columns the kernel cannot compare row by row: storages of different domains,
    /// numbers of different widths, decimals of different scales, byte lists of different sizes,
    /// or a form no comparison reads.
    /// </summary>
    private static void CheckColumns(DType schema, ColumnComparisonExpr columns, string parameterName)
    {
        if (!TryResolve(schema, columns.Left, out DType left) ||
            !TryResolve(schema, columns.Right, out DType right))
        {
            return;
        }

        DType a = Storage(left);
        DType b = Storage(right);
        if (a.IsDefault || b.IsDefault || a.Kind == DTypeKind.Null || b.Kind == DTypeKind.Null)
        {
            // A null column is unknown against anything, which the kernel answers without a type.
            return;
        }

        Domain domain = DomainOf(a);
        bool comparable = domain == DomainOf(b) && domain switch
        {
            Domain.Bool => true,
            Domain.Number => a.PType == b.PType,
            Domain.Bytes => a.Kind == b.Kind,
            Domain.Decimal => a.Scale == b.Scale,
            Domain.FixedBytes => a.FixedSize == b.FixedSize,
            _ => false,
        };

        if (comparable)
        {
            return;
        }

        throw new ArgumentException(
            $"'{columns.Left.Path}' is a column of {left} and '{columns.Right.Path}' one of " +
            $"{right}. Two columns compare when their types are the same: booleans, numbers, " +
            "decimals, text, binary or fixed-size lists of bytes.",
            parameterName);
    }

    /// <summary>A dtype past its extension labels, which is what a comparison reads.</summary>
    private static DType Storage(DType column)
    {
        for (int i = 0; i < VortexLimits.MaxDTypeDepth && column.Kind == DTypeKind.Extension; i++)
        {
            column = column.StorageType;
        }

        return column;
    }

    /// <summary>Walks a field path down the schema.</summary>
    /// <returns>
    /// <see langword="false"/> when the path names nothing, which is another check's error to
    /// report or, for a caller that answers rather than throws, a question it declines.
    /// </returns>
    private static bool TryResolve(DType schema, FieldExpr field, out DType column)
    {
        column = schema;
        byte[][] segments = field.SegmentsUtf8;
        for (int i = 0; i < segments.Length; i++)
        {
            if (column.IsDefault || column.Kind != DTypeKind.Struct)
            {
                return false;
            }

            int index = column.IndexOfField(segments[i]);
            if (index < 0)
            {
                return false;
            }

            column = column.GetField(index);
        }

        return !column.IsDefault;
    }

    private static Domain DomainOf(DType column)
    {
        // An extension is a label over a storage dtype, and a filter compares the storage: a date
        // column takes the integer constant its storage takes.
        column = Storage(column);
        return column.Kind switch
        {
            DTypeKind.Bool => Domain.Bool,
            DTypeKind.Primitive => Domain.Number,
            DTypeKind.Utf8 or DTypeKind.Binary => Domain.Bytes,
            DTypeKind.Decimal => Domain.Decimal,
            DTypeKind.FixedSizeList when IsByte(column.ElementType) => Domain.FixedBytes,
            DTypeKind.List or DTypeKind.FixedSizeList => Domain.List,
            _ => Domain.Open,
        };
    }

    private static bool IsByte(DType element) =>
        !element.IsDefault && element.Kind == DTypeKind.Primitive && element.PType == PType.U8 &&
        !element.IsNullable;

    private static string Name(FilterLiteral value) => value.Kind switch
    {
        FilterLiteralKind.Bool => "boolean",
        FilterLiteralKind.Signed => "signed integer",
        FilterLiteralKind.Unsigned => "unsigned integer",
        FilterLiteralKind.Float => "float",
        _ => $"{value.BytesValue.Length}-byte text or binary",
    };
}
