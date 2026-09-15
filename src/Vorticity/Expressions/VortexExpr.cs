// Filter expressions - docs/01-scope.md F7 and docs/08-semantics.md §3.
//
// An expression tree is built ONCE PER SCAN, never per batch, which is what lets it be an ordinary
// immutable object graph instead of another arena. docs/03-architecture.md §4 invariant 1 bounds
// managed allocation PER BATCH; the builder is already a per-scan allocation and this joins it.
//
// The scope is deliberately the one F7 names -- comparisons, AND/OR/NOT, IS NULL, IN -- and two
// limits are enforced at CONSTRUCTION rather than discovered during a scan:
//
//   * a comparison is between a field and a literal. Comparing two columns needs a second dispatch
//     dimension in every kernel, prunes nothing from a zone map, and is vanishingly rare in
//     pushdown; refusing it here costs one exception at build time instead of a kernel matrix.
//   * a literal's type is checked against the column only when the filter runs, because the schema
//     is not in scope at construction.
//
// Three-valued logic lives in the evaluator, not here: this file is only the shape.
using System;
using System.Collections.Generic;

namespace Vorticity.Expressions;

/// <summary>What an <see cref="VortexExpr"/> node is.</summary>
public enum ExprKind : byte
{
    /// <summary>A named column, possibly a nested path.</summary>
    Field = 0,

    /// <summary>A constant.</summary>
    Literal = 1,

    /// <summary>A comparison between a field and a literal.</summary>
    Comparison = 2,

    /// <summary><c>AND</c> or <c>OR</c>.</summary>
    Logical = 3,

    /// <summary><c>NOT</c>.</summary>
    Not = 4,

    /// <summary><c>IS NULL</c> or <c>IS NOT NULL</c>.</summary>
    NullCheck = 5,

    /// <summary><c>IN</c> over a literal set.</summary>
    In = 6,

    /// <summary><c>StartsWith</c>, <c>Contains</c> or <c>Like</c> over bytes.</summary>
    StringMatch = 7,
}

/// <summary>The three byte-pattern predicates of docs/12-index-reads.md §7.</summary>
/// <remarks>
/// BYTES, NOT TEXT, and the distinction is the whole contract: the comparison is bytewise, which for
/// UTF-8 is code-point order, and <c>_</c> in a <see cref="Like"/> pattern matches one BYTE rather
/// than one code point. Case folding needs a definition of "case" for UTF-8 that this iteration does
/// not have (§13), so every operator here is case-sensitive.
/// </remarks>
public enum StringMatchOp : byte
{
    /// <summary>The value's bytes begin with the pattern; every value begins with an empty one.</summary>
    StartsWith = 0,

    /// <summary>The pattern occurs somewhere in the value; an empty pattern always does.</summary>
    Contains = 1,

    /// <summary>SQL <c>LIKE</c>: <c>%</c> any run, <c>_</c> any one byte, the escape quotes either.</summary>
    Like = 2,
}

/// <summary>The comparison operators F7 admits.</summary>
public enum ComparisonOp : byte
{
    /// <summary><c>=</c>.</summary>
    Equal = 0,

    /// <summary><c>!=</c>.</summary>
    NotEqual = 1,

    /// <summary><c>&lt;</c>.</summary>
    Less = 2,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual = 3,

    /// <summary><c>&gt;</c>.</summary>
    Greater = 4,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual = 5,
}

/// <summary>A node of a filter expression.</summary>
public abstract class VortexExpr
{
    private protected VortexExpr()
    {
    }

    /// <summary>What this node is, so a consumer can switch without a type test.</summary>
    public abstract ExprKind Kind { get; }

    /// <summary>Adds every field path this expression reads to <paramref name="paths"/>.</summary>
    /// <param name="paths">The set to add to.</param>
    /// <remarks>
    /// The scan unions these into the projection before planning: a filter column must be READ even
    /// when the caller did not project it, and discarded before the batch is produced
    /// (docs/03-architecture.md §3.4).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public abstract void CollectFields(ICollection<string> paths);
}

/// <summary>A column reference, by dotted path.</summary>
public sealed class FieldExpr : VortexExpr
{
    /// <remarks>
    /// EMPTY SEGMENTS ARE THE SCHEMA'S BUSINESS, NOT THIS CONSTRUCTOR'S. PERF-AUDIT-v2.md R18a: a
    /// Vortex field name may be empty -- `corpus/types/struct_field_names` has one, and its first
    /// column is literally named "" -- and `Projection` reaches it, because it splits on dots and
    /// lets `DType.IndexOfField` decide whether the segment names anything. This refused the same
    /// path up front, so `Project("")` worked and `Expr.Field("")` threw: the same file, the same
    /// grammar, two answers.
    ///
    /// A path naming nothing still fails, at the place that can say so usefully -- the scan, which
    /// has the schema and reports which path is unknown. What is gone is the guess made before
    /// anything was known.
    ///
    /// Neither grammar can address a field whose NAME contains a dot, and that is deliberate on
    /// both sides: `Projection`'s header says it invents no escaping syntax and points at
    /// `ProjectFields(ReadOnlySpan&lt;int&gt;)` as the documented way in.
    /// </remarks>
    internal FieldExpr(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;

        // Split and encode ONCE, here, because resolving the path is a per-batch operation and
        // DType.IndexOfField takes UTF-8. Doing it per batch would put a string split and an
        // encode on a path that docs/03-architecture.md §4 invariant 1 requires to allocate
        // nothing.
        string[] parts = path.Split('.');
        SegmentsUtf8 = new byte[parts.Length][];
        for (int i = 0; i < parts.Length; i++)
        {
            SegmentsUtf8[i] = System.Text.Encoding.UTF8.GetBytes(parts[i]);
        }
    }

    /// <summary>The dotted path, e.g. <c>payload.size</c>.</summary>
    public string Path { get; }

    /// <summary>The path's segments as UTF-8, encoded once at construction.</summary>
    internal byte[][] SegmentsUtf8 { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.Field;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.Add(Path);
    }
}

/// <summary>A constant operand.</summary>
public sealed class LiteralExpr : VortexExpr
{
    internal LiteralExpr(FilterLiteral value) => Value = value;

    /// <summary>The constant.</summary>
    public FilterLiteral Value { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.Literal;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths) =>
        ArgumentNullException.ThrowIfNull(paths);
}

/// <summary>A field compared against a literal.</summary>
/// <remarks>
/// <see cref="Field"/> is always the left operand of <see cref="Op"/>, whichever way the caller
/// wrote it: <c>Expr.Lt(Expr.Literal(3), Expr.Field("x"))</c> becomes <c>x &gt; 3</c>. Normalizing
/// at construction is what keeps the evaluator and the pruner from each having to handle both.
/// </remarks>
public sealed class ComparisonExpr : VortexExpr
{
    internal ComparisonExpr(FieldExpr field, ComparisonOp op, FilterLiteral value)
    {
        Field = field;
        Op = op;
        Value = value;
    }

    /// <summary>The column.</summary>
    public FieldExpr Field { get; }

    /// <summary>The operator, with the field on the left.</summary>
    public ComparisonOp Op { get; }

    /// <summary>The constant.</summary>
    public FilterLiteral Value { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.Comparison;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary><c>AND</c> or <c>OR</c> over two operands.</summary>
public sealed class LogicalExpr : VortexExpr
{
    internal LogicalExpr(bool isAnd, VortexExpr left, VortexExpr right)
    {
        IsAnd = isAnd;
        Left = left;
        Right = right;
    }

    /// <summary><see langword="true"/> for <c>AND</c>, <see langword="false"/> for <c>OR</c>.</summary>
    public bool IsAnd { get; }

    /// <summary>The left operand.</summary>
    public VortexExpr Left { get; }

    /// <summary>The right operand.</summary>
    public VortexExpr Right { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.Logical;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths)
    {
        Left.CollectFields(paths);
        Right.CollectFields(paths);
    }
}

/// <summary><c>NOT</c> over one operand.</summary>
public sealed class NotExpr : VortexExpr
{
    internal NotExpr(VortexExpr operand) => Operand = operand;

    /// <summary>The negated operand.</summary>
    public VortexExpr Operand { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.Not;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths) => Operand.CollectFields(paths);
}

/// <summary><c>IS NULL</c> or <c>IS NOT NULL</c>.</summary>
/// <remarks>
/// Never yields <c>unknown</c> (docs/08-semantics.md §3), which is what makes it the one predicate
/// a zone of nothing but nulls can still satisfy.
/// </remarks>
public sealed class NullCheckExpr : VortexExpr
{
    internal NullCheckExpr(FieldExpr field, bool isNull)
    {
        Field = field;
        IsNull = isNull;
    }

    /// <summary>The column.</summary>
    public FieldExpr Field { get; }

    /// <summary><see langword="true"/> for <c>IS NULL</c>.</summary>
    public bool IsNull { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.NullCheck;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary><c>IN</c> over a set of literals.</summary>
public sealed class InExpr : VortexExpr
{
    internal InExpr(FieldExpr field, FilterLiteral[] values)
    {
        Field = field;
        Values = values;
    }

    /// <summary>The column.</summary>
    public FieldExpr Field { get; }

    /// <summary>The candidate values, in the order given.</summary>
    public IReadOnlyList<FilterLiteral> Values { get; }

    /// <summary>The candidates, without the interface indirection.</summary>
    internal FilterLiteral[] Literals => (FilterLiteral[])Values;

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.In;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary>A field matched against a byte pattern.</summary>
/// <remarks>
/// docs/12-index-reads.md §7. Null yields <c>unknown</c>, as a comparison does (08 §3), so a null
/// row never satisfies one of these and never satisfies its negation either.
/// </remarks>
public sealed class StringMatchExpr : VortexExpr
{
    internal StringMatchExpr(FieldExpr field, StringMatchOp op, FilterLiteral pattern, byte escape)
    {
        Field = field;
        Op = op;
        Pattern = pattern;
        Escape = escape;
    }

    /// <summary>The column; it must be utf8 or binary, or an extension over one.</summary>
    public FieldExpr Field { get; }

    /// <summary>Which of the three predicates.</summary>
    public StringMatchOp Op { get; }

    /// <summary>The pattern, always a <see cref="FilterLiteralKind.Bytes"/> literal.</summary>
    public FilterLiteral Pattern { get; }

    /// <summary>The byte that quotes a wildcard, for <see cref="StringMatchOp.Like"/>.</summary>
    public byte Escape { get; }

    /// <inheritdoc/>
    public override ExprKind Kind => ExprKind.StringMatch;

    /// <inheritdoc/>
    public override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary>Builds filter expressions.</summary>
public static class Expr
{
    /// <summary>A column, by dotted path.</summary>
    /// <param name="path">e.g. <c>payload.size</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public static FieldExpr Field(string path) => new FieldExpr(path);

    /// <summary>A constant.</summary>
    /// <param name="value">The constant.</param>
    public static LiteralExpr Literal(FilterLiteral value) => new LiteralExpr(value);

    /// <summary><c>left = right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    public static ComparisonExpr Eq(VortexExpr left, VortexExpr right) =>
        Compare(left, ComparisonOp.Equal, right);

    /// <summary><c>left != right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    public static ComparisonExpr Ne(VortexExpr left, VortexExpr right) =>
        Compare(left, ComparisonOp.NotEqual, right);

    /// <summary><c>left &lt; right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    public static ComparisonExpr Lt(VortexExpr left, VortexExpr right) =>
        Compare(left, ComparisonOp.Less, right);

    /// <summary><c>left &lt;= right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    public static ComparisonExpr Le(VortexExpr left, VortexExpr right) =>
        Compare(left, ComparisonOp.LessOrEqual, right);

    /// <summary><c>left &gt; right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    public static ComparisonExpr Gt(VortexExpr left, VortexExpr right) =>
        Compare(left, ComparisonOp.Greater, right);

    /// <summary><c>left &gt;= right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    public static ComparisonExpr Ge(VortexExpr left, VortexExpr right) =>
        Compare(left, ComparisonOp.GreaterOrEqual, right);

    /// <summary><c>left AND right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    public static LogicalExpr And(VortexExpr left, VortexExpr right) => Logical(true, left, right);

    /// <summary><c>left OR right</c>.</summary>
    /// <param name="left">One operand.</param>
    /// <param name="right">The other.</param>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    public static LogicalExpr Or(VortexExpr left, VortexExpr right) => Logical(false, left, right);

    /// <summary><c>NOT operand</c>.</summary>
    /// <param name="operand">The operand.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is null.</exception>
    public static NotExpr Not(VortexExpr operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        return new NotExpr(operand);
    }

    /// <summary><c>field IS NULL</c>.</summary>
    /// <param name="field">The column.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    public static NullCheckExpr IsNull(FieldExpr field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new NullCheckExpr(field, true);
    }

    /// <summary><c>field IS NOT NULL</c>.</summary>
    /// <param name="field">The column.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    public static NullCheckExpr IsNotNull(FieldExpr field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new NullCheckExpr(field, false);
    }

    /// <summary><c>field IN (values)</c>.</summary>
    /// <param name="field">The column.</param>
    /// <param name="values">The candidates; at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public static InExpr In(FieldExpr field, params FilterLiteral[] values)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException(
                "IN needs at least one candidate; an empty set matches nothing and is more likely " +
                "a bug than an intent.",
                nameof(values));
        }

        return new InExpr(field, (FilterLiteral[])values.Clone());
    }

    /// <summary><c>field</c> begins with <paramref name="pattern"/>.</summary>
    /// <param name="field">The column; utf8 or binary, or an extension over one.</param>
    /// <param name="pattern">The prefix, as bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is not a bytes literal.</exception>
    public static StringMatchExpr StartsWith(FieldExpr field, FilterLiteral pattern) =>
        Match(field, StringMatchOp.StartsWith, pattern, (byte)'\\');

    /// <summary><paramref name="pattern"/> occurs somewhere in <c>field</c>.</summary>
    /// <param name="field">The column; utf8 or binary, or an extension over one.</param>
    /// <param name="pattern">The needle, as bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is not a bytes literal.</exception>
    public static StringMatchExpr Contains(FieldExpr field, FilterLiteral pattern) =>
        Match(field, StringMatchOp.Contains, pattern, (byte)'\\');

    /// <summary>SQL <c>field LIKE pattern</c>.</summary>
    /// <param name="field">The column; utf8 or binary, or an extension over one.</param>
    /// <param name="pattern">The pattern: <c>%</c> any run, <c>_</c> any one byte.</param>
    /// <param name="escape">The byte that quotes a wildcard or itself. Default <c>\</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is not a bytes literal.</exception>
    public static StringMatchExpr Like(
        FieldExpr field, FilterLiteral pattern, byte escape = (byte)'\\') =>
        Match(field, StringMatchOp.Like, pattern, escape);

    /// <summary>
    /// Rejects the one shape these predicates cannot evaluate, at CONSTRUCTION rather than during a
    /// scan — the same rule <see cref="Compare"/> applies to a column-to-column comparison.
    /// </summary>
    private static StringMatchExpr Match(
        FieldExpr field, StringMatchOp op, FilterLiteral pattern, byte escape)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (pattern.Kind != FilterLiteralKind.Bytes)
        {
            throw new ArgumentException(
                $"{op} matches BYTES, so its pattern must be a bytes literal; this one is " +
                $"{pattern.Kind}. A column of any other type has no byte pattern to match " +
                "(docs/12-index-reads.md §7).",
                nameof(pattern));
        }

        return new StringMatchExpr(field, op, pattern, escape);
    }

    private static LogicalExpr Logical(bool isAnd, VortexExpr left, VortexExpr right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new LogicalExpr(isAnd, left, right);
    }

    /// <summary>
    /// Normalizes a comparison so the field is on the left, rejecting the two shapes the 1.0 filter
    /// does not evaluate.
    /// </summary>
    private static ComparisonExpr Compare(VortexExpr left, ComparisonOp op, VortexExpr right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left is FieldExpr field && right is LiteralExpr literal)
        {
            return new ComparisonExpr(field, op, literal.Value);
        }

        if (left is LiteralExpr reversedLiteral && right is FieldExpr reversedField)
        {
            return new ComparisonExpr(reversedField, Reverse(op), reversedLiteral.Value);
        }

        throw new ArgumentException(
            left is FieldExpr && right is FieldExpr
                ? "A filter comparison is between a field and a literal. Comparing two columns is " +
                  "outside the 1.0 filter scope (docs/01-scope.md F7)."
                : "A filter comparison is between a field and a literal.",
            nameof(right));
    }

    /// <summary>The operator with its operands swapped: <c>3 &lt; x</c> is <c>x &gt; 3</c>.</summary>
    private static ComparisonOp Reverse(ComparisonOp op) => op switch
    {
        ComparisonOp.Less => ComparisonOp.Greater,
        ComparisonOp.LessOrEqual => ComparisonOp.GreaterOrEqual,
        ComparisonOp.Greater => ComparisonOp.Less,
        ComparisonOp.GreaterOrEqual => ComparisonOp.LessOrEqual,
        _ => op,
    };
}
