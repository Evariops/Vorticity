using System;
using System.ComponentModel;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>
/// A symbolic value of type <typeparamref name="T"/>: a column of the scanned file, an aggregate of
/// one, or a group's key. Its operators record a predicate instead of evaluating one.
/// </summary>
/// <typeparam name="T">The column's .NET type; the literal of a comparison has this type.</typeparam>
/// <remarks>
/// A lambda over <see cref="Probe{TRecord}"/> runs once, when the scan is built: a breakpoint in it
/// sees symbols, not values, and hits once. Arithmetic is not pushed down and does not compile.
/// </remarks>
public readonly struct Sym<T> : ISymbol
{
    internal Sym(SymNode node)
    {
        Node = node;
    }

    internal SymNode Node { get; }

    SymNode ISymbol.Node => Node;

    Aggregating.IResultNode ISymbol.Result(SymNode[] components) => Aggregating.AggregationPlan.Result(this, components);

    ColumnSym ISymbol.Compared => Column;

    IProjectionElement ISymbol.Projected(int position) =>
        Node is ColumnSym column
            ? new ProjectionElement<T>(column)
            : throw new InvalidOperationException(
                $"Element {position + 1} of the projection, '{this}', is not a column of the scan: a projection reads columns, and an aggregate belongs to a group.");

    internal ColumnSym Column =>
        Node as ColumnSym
        ?? (Node as Aggregating.ResultNode<T>)?.Comparable
        ?? throw new InvalidOperationException("Only a column or an aggregate can be compared: a filter on groups compares the group's key and its aggregates.");

    /// <summary>The rows whose value equals <paramref name="value"/>; <c>== null</c> is <see cref="IsNull"/>.</summary>
    public static Predicate operator ==(Sym<T> column, T value) => SymLowering.Compare(column.Column, ComparisonOp.Equal, value);

    /// <summary>The rows whose values in the two columns are equal.</summary>
    public static Predicate operator ==(Sym<T> left, Sym<T> right) => SymLowering.Compare(left.Column, ComparisonOp.Equal, right.Column);

    /// <summary>The rows whose value differs from <paramref name="value"/>; <c>!= null</c> is <see cref="IsNotNull"/>.</summary>
    public static Predicate operator !=(Sym<T> column, T value) => SymLowering.Compare(column.Column, ComparisonOp.NotEqual, value);

    /// <summary>The rows whose values in the two columns differ.</summary>
    public static Predicate operator !=(Sym<T> left, Sym<T> right) => SymLowering.Compare(left.Column, ComparisonOp.NotEqual, right.Column);

    /// <summary>The rows whose value is below <paramref name="value"/>.</summary>
    public static Predicate operator <(Sym<T> column, T value) => SymLowering.Compare(column.Column, ComparisonOp.Less, value);

    /// <summary>The rows whose left value is below their right value.</summary>
    public static Predicate operator <(Sym<T> left, Sym<T> right) => SymLowering.Compare(left.Column, ComparisonOp.Less, right.Column);

    /// <summary>The rows whose value is at most <paramref name="value"/>.</summary>
    public static Predicate operator <=(Sym<T> column, T value) => SymLowering.Compare(column.Column, ComparisonOp.LessOrEqual, value);

    /// <summary>The rows whose left value is at most their right value.</summary>
    public static Predicate operator <=(Sym<T> left, Sym<T> right) => SymLowering.Compare(left.Column, ComparisonOp.LessOrEqual, right.Column);

    /// <summary>The rows whose value is above <paramref name="value"/>.</summary>
    public static Predicate operator >(Sym<T> column, T value) => SymLowering.Compare(column.Column, ComparisonOp.Greater, value);

    /// <summary>The rows whose left value is above their right value.</summary>
    public static Predicate operator >(Sym<T> left, Sym<T> right) => SymLowering.Compare(left.Column, ComparisonOp.Greater, right.Column);

    /// <summary>The rows whose value is at least <paramref name="value"/>.</summary>
    public static Predicate operator >=(Sym<T> column, T value) => SymLowering.Compare(column.Column, ComparisonOp.GreaterOrEqual, value);

    /// <summary>The rows whose left value is at least their right value.</summary>
    public static Predicate operator >=(Sym<T> left, Sym<T> right) => SymLowering.Compare(left.Column, ComparisonOp.GreaterOrEqual, right.Column);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator +(Sym<T> column, T value) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator -(Sym<T> column, T value) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator *(Sym<T> column, T value) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator /(Sym<T> column, T value) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator %(Sym<T> column, T value) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator +(Sym<T> left, Sym<T> right) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator -(Sym<T> left, Sym<T> right) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator *(Sym<T> left, Sym<T> right) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator /(Sym<T> left, Sym<T> right) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>Arithmetic is not pushed down.</summary>
    [Obsolete(SymLowering.NoArithmetic, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Sym<T> operator %(Sym<T> left, Sym<T> right) => throw new NotSupportedException(SymLowering.NoArithmetic);

    /// <summary>The rows whose value is one of <paramref name="values"/>; a null among them matches nothing.</summary>
    /// <param name="values">The values, at least one.</param>
    /// <returns>The predicate.</returns>
    public Predicate In(params ReadOnlySpan<T> values) => SymLowering.In(Column, values);

    /// <summary>The rows whose value lies in [<paramref name="low"/>, <paramref name="high"/>], both included.</summary>
    /// <param name="low">The lower bound.</param>
    /// <param name="high">The upper bound.</param>
    /// <returns>The predicate.</returns>
    public Predicate Between(T low, T high) => this >= low & this <= high;

    /// <summary>The rows whose value is null.</summary>
    public Predicate IsNull => new Predicate(new NullCheckExpr(Column.Field, isNull: true));

    /// <summary>The rows whose value is not null.</summary>
    public Predicate IsNotNull => new Predicate(new NullCheckExpr(Column.Field, isNull: false));

    /// <summary>Not meaningful: a symbol is compared with <c>==</c> to build a predicate.</summary>
    /// <param name="obj">Ignored.</param>
    /// <returns>Whether <paramref name="obj"/> is the same symbol.</returns>
    public override bool Equals(object? obj) => obj is Sym<T> other && ReferenceEquals(other.Node, Node);

    /// <summary>Not meaningful.</summary>
    /// <returns>A hash of the symbol's identity.</returns>
    public override int GetHashCode() => Node?.GetHashCode() ?? 0;

    /// <summary>The column path, the aggregate or the key the symbol stands for.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Node?.ToString() ?? "<none>";
}

/// <summary>What a <see cref="Sym{T}"/> stands for.</summary>
internal abstract class SymNode
{
}

/// <summary>A symbol whatever its type: what a tuple of symbols, read through <see cref="System.Runtime.CompilerServices.ITuple"/>, holds.</summary>
internal interface ISymbol
{
    SymNode Node { get; }

    /// <summary>The result this symbol stands for in a selection: an aggregate, or a component of the key among <paramref name="components"/>.</summary>
    /// <exception cref="InvalidOperationException">The symbol is neither.</exception>
    Aggregating.IResultNode Result(SymNode[] components);

    /// <summary>The column a comparison or an order reads: the column itself, or the column of a group's results an aggregate is.</summary>
    /// <exception cref="InvalidOperationException">The symbol is neither.</exception>
    ColumnSym Compared { get; }

    /// <summary>The column this symbol stands for as element <paramref name="position"/> of a projection.</summary>
    /// <exception cref="InvalidOperationException">The symbol is not a column.</exception>
    IProjectionElement Projected(int position);
}

/// <summary>A column of the scanned file, with the type the file gives it.</summary>
internal sealed class ColumnSym : SymNode
{
    internal ColumnSym(FieldExpr field, VortexType type, VortexExtensionRegistry? extensions, RecordBinding? binding, int member, int[] fieldPath)
    {
        Field = field;
        Type = type;
        Extensions = extensions;
        Binding = binding;
        Member = member;
        FieldPath = fieldPath;
    }

    internal FieldExpr Field { get; }

    internal VortexType Type { get; }

    internal VortexExtensionRegistry? Extensions { get; }

    /// <summary>The binding the column was reached through, or null on the tool path.</summary>
    internal RecordBinding? Binding { get; }

    internal int Member { get; }

    /// <summary>The column's field indices from the file's root.</summary>
    internal int[] FieldPath { get; }

    public override string ToString() => Field.Path;
}
