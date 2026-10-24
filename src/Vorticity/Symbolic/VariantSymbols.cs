using System;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>A path into a variant column: the column, and the keys and indices from its value.</summary>
internal sealed class VariantPathSym(ColumnSym column, VariantStep[] steps) : SymNode
{
    internal ColumnSym Column { get; } = column;

    internal VariantStep[] Steps { get; } = steps;

    internal InvalidOperationException NotComparable() =>
        new($"'{this}' is a variant: compare its value through AsInt64(), AsString(), AsDouble(), AsBool(), AsTimestamp() or a To… conversion, which say which values it takes.");

    public override string ToString() => Column.Field.Path + string.Concat(Steps);
}

/// <summary>
/// The paths of a variant column, on the typed path: a key or an index walks into the value, and
/// <c>As…</c>, <c>To…</c> and <c>Has</c> make of the path a column a filter compares, a group keys
/// on and a selection reads.
/// </summary>
/// <remarks>
/// A path is a value, as a map's key is: the compiler checks the column, not the keys, which are
/// data. A path that is absent, of another type than the one read, or under a null row gives null,
/// so a filter on it is unknown and keeps no row; <c>Has</c> alone is true or false.
/// </remarks>
public static class VariantSymbols
{
    extension(Sym<Variant> variant)
    {
        /// <summary>The value under one whole key, dots included.</summary>
        /// <param name="key">The key.</param>
        public Sym<Variant?> this[string key] => Step(variant.Node, VariantStep.Of(key));

        /// <summary>Element <paramref name="index"/> of an array.</summary>
        /// <param name="index">The element's position, from 0.</param>
        public Sym<Variant?> this[int index] => Step(variant.Node, VariantStep.Of(index));

        /// <summary>The rows whose value is an object holding <paramref name="key"/>: true or false, never unknown.</summary>
        /// <param name="key">The key, whole.</param>
        /// <returns>The predicate.</returns>
        public Predicate Has(string key) => Present(Step(variant.Node, VariantStep.Of(key)).Node);

        /// <summary>The value as an integer, when it is one of any width, and null otherwise.</summary>
        /// <returns>The column read as integers.</returns>
        public Sym<long?> AsInt64() => Read<long?>(variant.Node, VariantRead.AsInt64);

        /// <summary>The value as a double, when it is a float or a double, and null otherwise.</summary>
        /// <returns>The column read as doubles.</returns>
        public Sym<double?> AsDouble() => Read<double?>(variant.Node, VariantRead.AsDouble);

        /// <summary>The value as text, when it is a string, and null otherwise.</summary>
        /// <returns>The column read as text.</returns>
        public Sym<string?> AsString() => Read<string?>(variant.Node, VariantRead.AsString);

        /// <summary>The value as a boolean, when it is one, and null otherwise.</summary>
        /// <returns>The column read as booleans.</returns>
        public Sym<bool?> AsBool() => Read<bool?>(variant.Node, VariantRead.AsBool);

        /// <summary>The value as an instant, when it is a timestamp in UTC, and null otherwise; read in microseconds.</summary>
        /// <returns>The column read as instants.</returns>
        public Sym<DateTimeOffset?> AsTimestamp() => Read<DateTimeOffset?>(variant.Node, VariantRead.AsTimestamp);

        /// <summary>The value converted to an integer: a number without a fraction, a boolean, or text that parses; null when it does not convert.</summary>
        /// <returns>The column converted to integers.</returns>
        public Sym<long?> ToInt64() => Read<long?>(variant.Node, VariantRead.ToInt64);

        /// <summary>The value converted to a double: any number, a boolean, or text that parses; null when it does not convert.</summary>
        /// <returns>The column converted to doubles.</returns>
        public Sym<double?> ToDouble() => Read<double?>(variant.Node, VariantRead.ToDouble);
    }

    extension(Sym<Variant?> variant)
    {
        /// <summary>The value under one whole key, dots included.</summary>
        /// <param name="key">The key.</param>
        public Sym<Variant?> this[string key] => Step(variant.Node, VariantStep.Of(key));

        /// <summary>Element <paramref name="index"/> of an array.</summary>
        /// <param name="index">The element's position, from 0.</param>
        public Sym<Variant?> this[int index] => Step(variant.Node, VariantStep.Of(index));

        /// <summary>The rows whose value is an object holding <paramref name="key"/>: true or false, never unknown.</summary>
        /// <param name="key">The key, whole.</param>
        /// <returns>The predicate.</returns>
        public Predicate Has(string key) => Present(Step(variant.Node, VariantStep.Of(key)).Node);

        /// <summary>The value as an integer, when it is one of any width, and null otherwise.</summary>
        /// <returns>The path read as integers.</returns>
        public Sym<long?> AsInt64() => Read<long?>(variant.Node, VariantRead.AsInt64);

        /// <summary>The value as a double, when it is a float or a double, and null otherwise.</summary>
        /// <returns>The path read as doubles.</returns>
        public Sym<double?> AsDouble() => Read<double?>(variant.Node, VariantRead.AsDouble);

        /// <summary>The value as text, when it is a string, and null otherwise.</summary>
        /// <returns>The path read as text.</returns>
        public Sym<string?> AsString() => Read<string?>(variant.Node, VariantRead.AsString);

        /// <summary>The value as a boolean, when it is one, and null otherwise.</summary>
        /// <returns>The path read as booleans.</returns>
        public Sym<bool?> AsBool() => Read<bool?>(variant.Node, VariantRead.AsBool);

        /// <summary>The value as an instant, when it is a timestamp in UTC, and null otherwise; read in microseconds.</summary>
        /// <returns>The path read as instants.</returns>
        public Sym<DateTimeOffset?> AsTimestamp() => Read<DateTimeOffset?>(variant.Node, VariantRead.AsTimestamp);

        /// <summary>The value converted to an integer: a number without a fraction, a boolean, or text that parses; null when it does not convert.</summary>
        /// <returns>The path converted to integers.</returns>
        public Sym<long?> ToInt64() => Read<long?>(variant.Node, VariantRead.ToInt64);

        /// <summary>The value converted to a double: any number, a boolean, or text that parses; null when it does not convert.</summary>
        /// <returns>The path converted to doubles.</returns>
        public Sym<double?> ToDouble() => Read<double?>(variant.Node, VariantRead.ToDouble);

        /// <summary>The rows the path reaches: true or false, never unknown.</summary>
        public Predicate Exists => Present(variant.Node);
    }

    /// <summary>The path one step further than <paramref name="node"/>.</summary>
    private static Sym<Variant?> Step(SymNode node, VariantStep step) => node switch
    {
        VariantPathSym path => new Sym<Variant?>(new VariantPathSym(path.Column, [.. path.Steps, step])),
        ColumnSym { Field: not Aggregating.ResultFieldExpr and not FunctionFieldExpr and not VariantFieldExpr } column
            when column.Type.Kind == VortexTypeKind.Variant => new Sym<Variant?>(new VariantPathSym(column, [step])),
        _ => throw new InvalidOperationException($"'{node}' is not a variant column of the scan: a path walks into a variant column, before any aggregate."),
    };

    /// <summary>The path read as <paramref name="read"/>: a column of the scan, extracted from the variant's.</summary>
    private static Sym<T> Read<T>(SymNode node, VariantRead read)
    {
        (ColumnSym column, VariantStep[] steps) = Path(node);
        VariantFieldExpr field = new(column.Field, steps, read);
        return new Sym<T>(new ColumnSym(field, VariantFieldExpr.TypeOf(read), column.Extensions, column.Binding, column.Member, column.FieldPath));
    }

    /// <summary>The rows <paramref name="node"/>'s path reaches.</summary>
    private static Predicate Present(SymNode node)
    {
        (ColumnSym column, VariantStep[] steps) = Path(node);
        return new Predicate(new ComparisonExpr(new VariantFieldExpr(column.Field, steps, VariantRead.Has), ComparisonOp.Equal, FilterLiteral.From(true)));
    }

    private static (ColumnSym Column, VariantStep[] Steps) Path(SymNode node) => node switch
    {
        VariantPathSym path => (path.Column, path.Steps),
        ColumnSym { Field: not Aggregating.ResultFieldExpr and not FunctionFieldExpr and not VariantFieldExpr } column
            when column.Type.Kind == VortexTypeKind.Variant => (column, []),
        _ => throw new InvalidOperationException($"'{node}' is not a variant column of the scan: a path reads a variant column, before any aggregate."),
    };
}
