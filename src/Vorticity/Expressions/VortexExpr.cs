using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Compute;

namespace Vorticity.Expressions;

/// <summary>What an <see cref="VortexExpr"/> node is.</summary>
internal enum ExprKind : byte
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

    /// <summary>Whether a column is null, or is not null.</summary>
    NullCheck = 5,

    /// <summary><c>IN</c> over a literal set.</summary>
    In = 6,

    /// <summary><c>StartsWith</c>, <c>Contains</c> or <c>Like</c> over bytes.</summary>
    StringMatch = 7,

    /// <summary>Whether a list holds an element equal to a literal.</summary>
    ListContains = 8,

    /// <summary>A binary comparison of two columns of the same type.</summary>
    ColumnComparison = 9,
}

/// <summary>The three byte-pattern predicates a filter can apply to a column.</summary>
/// <remarks>
/// The comparison is bytewise, which for UTF-8 is code-point order, and <c>_</c> in a
/// <see cref="Like"/> pattern matches one character of a utf8 column and one byte of a binary one.
/// Case folding would need a definition of "case" for UTF-8 that the library does not carry, so
/// every operator here is case-sensitive.
/// </remarks>
internal enum StringMatchOp : byte
{
    /// <summary>The value's bytes begin with the pattern; every value begins with an empty one.</summary>
    StartsWith = 0,

    /// <summary>The pattern occurs somewhere in the value; an empty pattern always does.</summary>
    Contains = 1,

    /// <summary>The SQL wildcard match: <c>%</c> any run, <c>_</c> any one character (byte, in binary), the escape quotes either.</summary>
    Like = 2,
}

/// <summary>The comparison operators a filter admits.</summary>
internal enum ComparisonOp : byte
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

/// <summary>A column reference, by dotted path.</summary>
internal sealed class FieldExpr : VortexExpr
{
    // An empty segment is the schema's business, not this constructor's: a field name may be
    // empty, so nothing is refused here and a path that names nothing fails at the scan, which
    // holds the schema and can say which path was unknown. A field whose name contains a dot is
    // unreachable through this grammar, as it is through a projection path, and by design: both
    // invent no escaping syntax and leave field indices as the way in.
    internal FieldExpr(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;

        // Split and encode once, here, because resolving the path happens per batch and
        // DType.IndexOfField takes UTF-8: doing it there would put a string split and an encode on
        // a code path that must not allocate at all. Each segment is encoded straight out of the
        // path, so the reference costs its encoded names and their array and no string besides.
        ReadOnlySpan<char> rest = path;
        SegmentsUtf8 = new byte[rest.Count('.') + 1][];
        for (int i = 0; i < SegmentsUtf8.Length; i++)
        {
            int dot = rest.IndexOf('.');
            SegmentsUtf8[i] = Utf8(dot < 0 ? rest : rest[..dot]);
            rest = dot < 0 ? default : rest[(dot + 1)..];
        }
    }

    /// <summary>A column reference by its field names, one per struct level: a name may hold a dot.</summary>
    internal FieldExpr(string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Path = string.Join('.', segments);
        Segments = segments;
        SegmentsUtf8 = new byte[segments.Length][];
        for (int i = 0; i < segments.Length; i++)
        {
            SegmentsUtf8[i] = Utf8(segments[i]);
        }
    }

    /// <summary>A field name as UTF-8; the empty name shares the empty array.</summary>
    private static byte[] Utf8(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return [];
        }

        byte[] bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(name)];
        System.Text.Encoding.UTF8.GetBytes(name, bytes);
        return bytes;
    }

    /// <summary>The names, one per struct level, when the reference was built from them; null for a parsed path.</summary>
    internal string[]? Segments { get; }

    /// <summary>The dotted path, e.g. <c>payload.size</c>.</summary>
    public string Path { get; }

    /// <summary>The path's segments as UTF-8, encoded once at construction.</summary>
    internal byte[][] SegmentsUtf8 { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.Field;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.Add(Path);
    }
}

/// <summary>A constant operand.</summary>
internal sealed class LiteralExpr : VortexExpr
{
    internal LiteralExpr(FilterLiteral value) => Value = value;

    /// <summary>The constant.</summary>
    public FilterLiteral Value { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.Literal;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) =>
        ArgumentNullException.ThrowIfNull(paths);
}

/// <summary>A field compared against a literal.</summary>
/// <remarks>
/// <see cref="Field"/> is always the left operand of <see cref="Op"/>, whichever way the caller
/// wrote it: <c>Expr.Lt(Expr.Literal(3), Expr.Field("x"))</c> becomes <c>x &gt; 3</c>. Normalizing
/// at construction is what keeps the evaluator and the pruner from each having to handle both.
/// </remarks>
internal sealed class ComparisonExpr : VortexExpr
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
    internal override ExprKind Kind => ExprKind.Comparison;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary><c>AND</c> or <c>OR</c> over two operands.</summary>
internal sealed class LogicalExpr : VortexExpr
{
    internal LogicalExpr(bool isAnd, VortexExpr left, VortexExpr right)
    {
        IsAnd = isAnd;
        Left = left;
        Right = right;
        Height = ExprDepth.Over(left, right);
    }

    internal override int Height { get; }

    /// <summary><see langword="true"/> for <c>AND</c>, <see langword="false"/> for <c>OR</c>.</summary>
    public bool IsAnd { get; }

    /// <summary>The left operand.</summary>
    public VortexExpr Left { get; }

    /// <summary>The right operand.</summary>
    public VortexExpr Right { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.Logical;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths)
    {
        Left.CollectFields(paths);
        Right.CollectFields(paths);
    }
}

/// <summary><c>NOT</c> over one operand.</summary>
internal sealed class NotExpr : VortexExpr
{
    internal NotExpr(VortexExpr operand)
    {
        _ = ExprDepth.Over(operand, operand);
        Operand = operand;
    }

    /// <summary>The negated operand.</summary>
    public VortexExpr Operand { get; }

    /// <remarks>
    /// Counted down the operand rather than kept in a field every negation would carry: a run of
    /// negations is no longer than the evaluator's depth.
    /// </remarks>
    internal override int Height => Operand.Height + 1;

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.Not;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Operand.CollectFields(paths);
}

/// <summary>The bound on how deep a filter nests, checked as each level is built.</summary>
/// <remarks>
/// Every walk over a filter recurses, so one nested deeper than the evaluator takes is refused where
/// it is built rather than where a walk runs out of stack, which ends the process.
/// </remarks>
internal static class ExprDepth
{
    /// <summary>The height of a node over <paramref name="left"/> and <paramref name="right"/>.</summary>
    /// <exception cref="ArgumentException">The node would nest deeper than the evaluator takes.</exception>
    internal static int Over(VortexExpr left, VortexExpr right)
    {
        int height = Math.Max(left.Height, right.Height) + 1;
        if (height > FilterEvaluator.MaxDepth)
        {
            ThrowTooDeep();
        }

        return height;
    }

    /// <summary>Whether a node over <paramref name="left"/> and <paramref name="right"/> would nest too deep.</summary>
    internal static bool TooDeep(VortexExpr left, VortexExpr right) =>
        Math.Max(left.Height, right.Height) >= FilterEvaluator.MaxDepth;

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooDeep() =>
        throw new ArgumentException($"A filter expression nests deeper than {FilterEvaluator.MaxDepth} levels.");
}

/// <summary><c>is null</c> or <c>is not null</c>.</summary>
/// <remarks>
/// Never yields <c>unknown</c>, which is what makes it the one predicate a zone of nothing but
/// nulls can still satisfy.
/// </remarks>
internal sealed class NullCheckExpr : VortexExpr
{
    internal NullCheckExpr(FieldExpr field, bool isNull)
    {
        Field = field;
        IsNull = isNull;
    }

    /// <summary>The column.</summary>
    public FieldExpr Field { get; }

    /// <summary><see langword="true"/> for <c>is null</c>.</summary>
    public bool IsNull { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.NullCheck;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary><c>IN</c> over a set of literals.</summary>
internal sealed class InExpr : VortexExpr
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
    internal override ExprKind Kind => ExprKind.In;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary>A field matched against a byte pattern.</summary>
/// <remarks>
/// A null value yields <c>unknown</c>, as a comparison does, so a null row never satisfies one of
/// these and never satisfies its negation either.
/// </remarks>
internal sealed class StringMatchExpr : VortexExpr
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
    internal override ExprKind Kind => ExprKind.StringMatch;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary>A list column tested for an element equal to a literal.</summary>
/// <remarks>
/// The reference's <c>vortex.list.contains</c> with a constant needle, and the question a Bloom
/// filter over a list's elements answers. Three-valued like a comparison, with one rule of its
/// own: a null list is <c>unknown</c>, and so is every row under a null literal; otherwise the row
/// is <c>true</c> when an element equals the literal under the comparison kernels' equality (IEEE
/// for floats, so a NaN matches nothing and the two zeros match each other) and <c>false</c> when
/// none does. A null element matches nothing and leaves the row <c>false</c> rather than unknown,
/// because the result takes the list's validity alone. An empty list is <c>false</c>.
/// </remarks>
internal sealed class ListContainsExpr : VortexExpr
{
    internal ListContainsExpr(FieldExpr field, FilterLiteral value)
    {
        Field = field;
        Value = value;
    }

    /// <summary>The list column: a list or a fixed-size list of a comparable element, or an extension over one.</summary>
    public FieldExpr Field { get; }

    /// <summary>The element sought.</summary>
    public FilterLiteral Value { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.ListContains;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Field.CollectFields(paths);
}

/// <summary>Two columns of the same type compared row by row; a null on either side is unknown.</summary>
internal sealed class ColumnComparisonExpr : VortexExpr
{
    internal ColumnComparisonExpr(FieldExpr left, ComparisonOp op, FieldExpr right)
    {
        Left = left;
        Op = op;
        Right = right;
    }

    /// <summary>The column on the left of the operator.</summary>
    public FieldExpr Left { get; }

    /// <summary>The operator.</summary>
    public ComparisonOp Op { get; }

    /// <summary>The column on the right of the operator.</summary>
    public FieldExpr Right { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.ColumnComparison;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths)
    {
        Left.CollectFields(paths);
        Right.CollectFields(paths);
    }
}

/// <summary>Builds filter expressions.</summary>
internal static class Expr
{
    /// <summary>A column, by dotted path.</summary>
    /// <param name="path">e.g. <c>payload.size</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <remarks>
    /// The empty path is a path. A Vortex field name may be empty, so <c>""</c> names the field
    /// called <c>""</c> wherever a schema has one, exactly as <c>Project("")</c> reaches the same
    /// column; refusing it here would let one grammar accept what the other turns down. A path that
    /// names nothing is refused by the scan, which holds the schema and can say which path it was.
    /// </remarks>
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

    /// <summary><c>field is null</c>.</summary>
    /// <param name="field">The column.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    public static NullCheckExpr IsNull(FieldExpr field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new NullCheckExpr(field, true);
    }

    /// <summary><c>field is not null</c>.</summary>
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

    /// <summary>The SQL <c>like</c> match, <c>field like pattern</c>.</summary>
    /// <param name="field">The column; utf8 or binary, or an extension over one.</param>
    /// <param name="pattern">The pattern: <c>%</c> any run, <c>_</c> any one character of utf8, one byte of binary.</param>
    /// <param name="escape">The byte that quotes a wildcard or itself. Default <c>\</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is not a bytes literal.</exception>
    public static StringMatchExpr Like(
        FieldExpr field, FilterLiteral pattern, byte escape = (byte)'\\') =>
        Match(field, StringMatchOp.Like, pattern, escape);

    /// <summary><c>list</c> holds an element equal to <paramref name="value"/>.</summary>
    /// <param name="list">A list or fixed-size list column of booleans, numbers or bytes, or an extension over one.</param>
    /// <param name="value">The element sought; a null one makes every row unknown.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    public static ListContainsExpr ListContains(FieldExpr list, FilterLiteral value)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new ListContainsExpr(list, value);
    }

    /// <summary>
    /// Rejects the one shape these predicates cannot evaluate, when the node is built rather than
    /// during a scan - the same rule <see cref="Compare"/> applies to a column-to-column
    /// comparison.
    /// </summary>
    private static StringMatchExpr Match(
        FieldExpr field, StringMatchOp op, FilterLiteral pattern, byte escape)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (pattern.Kind != FilterLiteralKind.Bytes)
        {
            throw new ArgumentException(
                $"{op} matches BYTES, so its pattern must be a bytes literal; this one is " +
                $"{pattern.Kind}. A column of any other type has no byte pattern to match.",
                nameof(pattern));
        }

        return new StringMatchExpr(field, op, pattern, escape);
    }

    /// <summary><c>left AND right</c>, or <c>OR</c>.</summary>
    /// <remarks>
    /// Both are associative, so a run of one operator is joined by the heights of its sides, as an
    /// AVL tree joins: a chain of n operands built one at a time is O(log n) high whichever way it
    /// leans, where it was n high and refused past the evaluator's depth. The operands keep their
    /// order, which evaluation follows.
    /// </remarks>
    internal static LogicalExpr Logical(bool isAnd, VortexExpr left, VortexExpr right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Join(isAnd, left, right);
    }

    /// <summary>
    /// <paramref name="operands"/>, at least one, in order, joined by one operator as a balanced
    /// tree of one node between two operands: a run known whole, as a parser reads one.
    /// </summary>
    internal static VortexExpr Chain(bool isAnd, ReadOnlySpan<VortexExpr> operands)
    {
        if (operands.Length == 1)
        {
            return operands[0];
        }

        int middle = operands.Length / 2;
        return new LogicalExpr(isAnd, Chain(isAnd, operands[..middle]), Chain(isAnd, operands[middle..]));
    }

    /// <summary>The height <see cref="Chain(bool, ReadOnlySpan{VortexExpr})"/> gives <paramref name="operands"/>, without building it.</summary>
    internal static int ChainHeight(ReadOnlySpan<VortexExpr> operands)
    {
        if (operands.Length == 1)
        {
            return operands[0].Height;
        }

        int middle = operands.Length / 2;
        return Math.Max(ChainHeight(operands[..middle]), ChainHeight(operands[middle..])) + 1;
    }

    /// <summary>
    /// A node over two balanced sides: beside the side it is not more than one level from, down the
    /// other's spine of the same operator, and rotated back on the way up.
    /// </summary>
    private static LogicalExpr Join(bool isAnd, VortexExpr left, VortexExpr right)
    {
        if (left.Height > right.Height + 1 && left is LogicalExpr high && high.IsAnd == isAnd)
        {
            return Rotated(isAnd, high.Left, Join(isAnd, high.Right, right));
        }

        if (right.Height > left.Height + 1 && right is LogicalExpr tall && tall.IsAnd == isAnd)
        {
            return Rotated(isAnd, Join(isAnd, left, tall.Left), tall.Right);
        }

        return new LogicalExpr(isAnd, left, right);
    }

    /// <summary>
    /// A node over two sides whose heights differ by two at most, rotated once or twice to within
    /// one where the taller side is of the same operator. An operand of another kind is whole, and
    /// a node over it keeps its height.
    /// </summary>
    private static LogicalExpr Rotated(bool isAnd, VortexExpr left, VortexExpr right)
    {
        if (right.Height > left.Height + 1 && right is LogicalExpr r && r.IsAnd == isAnd)
        {
            return r.Left.Height > r.Right.Height && r.Left is LogicalExpr inner && inner.IsAnd == isAnd
                ? new LogicalExpr(isAnd, new LogicalExpr(isAnd, left, inner.Left), new LogicalExpr(isAnd, inner.Right, r.Right))
                : new LogicalExpr(isAnd, new LogicalExpr(isAnd, left, r.Left), r.Right);
        }

        if (left.Height > right.Height + 1 && left is LogicalExpr l && l.IsAnd == isAnd)
        {
            return l.Right.Height > l.Left.Height && l.Right is LogicalExpr inner && inner.IsAnd == isAnd
                ? new LogicalExpr(isAnd, new LogicalExpr(isAnd, l.Left, inner.Left), new LogicalExpr(isAnd, inner.Right, right))
                : new LogicalExpr(isAnd, l.Left, new LogicalExpr(isAnd, l.Right, right));
        }

        return new LogicalExpr(isAnd, left, right);
    }

    /// <summary>
    /// Normalizes a comparison so the field is on the left, rejecting the two shapes the 1.0 filter
    /// does not evaluate.
    /// </summary>
    /// <remarks>
    /// Refused here, when the node is built, rather than discovered during a scan. Comparing two
    /// columns would need a second dispatch dimension in every kernel and prunes nothing from a
    /// zone map, so one exception at build time replaces a matrix of kernels.
    /// </remarks>
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
                ? "A filter comparison is between a field and a literal. It cannot compare " +
                  "two columns to each other."
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
