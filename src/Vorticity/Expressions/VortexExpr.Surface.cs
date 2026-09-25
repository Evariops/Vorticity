using System;
using System.Collections.Generic;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>
/// A filter for a file whose schema is not known when the program is compiled: parsed from text, or
/// combined with <c>&amp;</c>, <c>|</c> and <c>!</c>. The typed scan builds its filters from
/// <see cref="Predicate"/> instead.
/// </summary>
/// <remarks>
/// An immutable tree, built once per scan. Three-valued logic lives in the evaluator: a comparison
/// with a null is unknown and a row is kept only when the filter is true. A literal is checked
/// against its column when the scan is built, and a literal of a type the column cannot compare to
/// throws <see cref="VortexSchemaException"/> there.
/// </remarks>
public abstract class VortexExpr
{
    private protected VortexExpr()
    {
    }

    /// <summary>
    /// Parses the filter grammar: comparisons (<c>=</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>&gt;</c>, <c>&gt;=</c>) of a column with a literal or another column, <c>and</c>, <c>or</c>,
    /// <c>not</c>, parentheses, <c>in (…)</c>, <c>is [not] null</c>, <c>like</c>, <c>starts with</c>
    /// and <c>contains</c>.
    /// </summary>
    /// <param name="text">The filter, e.g. <c>day &gt;= 900 and city = 'Paris'</c>.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="FormatException">The text is not in the grammar.</exception>
    public static VortexExpr Parse(ReadOnlySpan<char> text) => ExprText.Parse(text);

    /// <summary>The rows both filters keep.</summary>
    public static VortexExpr operator &(VortexExpr left, VortexExpr right) => Expr.And(left, right);

    /// <summary>The rows either filter keeps.</summary>
    public static VortexExpr operator |(VortexExpr left, VortexExpr right) => Expr.Or(left, right);

    /// <summary>The rows the filter does not keep, under three-valued logic.</summary>
    public static VortexExpr operator !(VortexExpr operand) => Expr.Not(operand);

    /// <summary>The filter in the grammar <see cref="Parse"/> reads.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => ExprText.Format(this);

    /// <summary>What this node is, so a consumer can switch without a type test.</summary>
    internal abstract ExprKind Kind { get; }

    /// <summary>The levels of <c>and</c>, <c>or</c> and <c>not</c> from this node down, no more than the evaluator takes.</summary>
    internal virtual int Height => 0;

    /// <summary>Adds every field path this expression reads to <paramref name="paths"/>.</summary>
    /// <param name="paths">The set to add to.</param>
    /// <remarks>
    /// The scan unions these into the projection before planning: a filter column must be read even
    /// when the caller did not project it, then dropped again before the batch is handed out.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    internal abstract void CollectFields(ICollection<string> paths);
}
