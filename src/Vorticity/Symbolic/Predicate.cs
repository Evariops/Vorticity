using Vorticity.Expressions;

namespace Vorticity;

/// <summary>
/// A filter recorded by the operators of <see cref="Sym{T}"/>: what a scan pushes down to its
/// statistics, zone maps, indexes and kernels.
/// </summary>
/// <remarks>
/// <c>&amp;&amp;</c> and <c>||</c> evaluate both sides, as <c>&amp;</c> and <c>|</c> do: the lambda records a
/// predicate once, it does not evaluate one per row. <c>default(Predicate)</c> is <see cref="All"/>.
/// </remarks>
public readonly struct Predicate
{
    private readonly PredicateShape _shape;

    internal Predicate(VortexExpr node)
    {
        Node = node;
        _shape = PredicateShape.Expression;
    }

    private Predicate(PredicateShape shape)
    {
        Node = null;
        _shape = shape;
    }

    /// <summary>Every row: the identity of <c>&amp;</c>, for a filter built in a loop.</summary>
    public static Predicate All => default;

    /// <summary>No row: the identity of <c>|</c>.</summary>
    public static Predicate None => new Predicate(PredicateShape.None);

    /// <summary>The engine's expression, or null for <see cref="All"/> and <see cref="None"/>.</summary>
    internal VortexExpr? Node { get; }

    internal bool IsAll => _shape == PredicateShape.All;

    internal bool IsNone => _shape == PredicateShape.None;

    /// <summary>The rows both predicates keep.</summary>
    /// <param name="left">A predicate.</param>
    /// <param name="right">A predicate.</param>
    /// <returns>The conjunction.</returns>
    public static Predicate operator &(Predicate left, Predicate right)
    {
        if (left.IsNone || right.IsNone)
        {
            return None;
        }

        if (left.IsAll)
        {
            return right;
        }

        return right.IsAll ? left : new Predicate(Expr.Logical(true, left.Node!, right.Node!));
    }

    /// <summary>The rows either predicate keeps.</summary>
    /// <param name="left">A predicate.</param>
    /// <param name="right">A predicate.</param>
    /// <returns>The disjunction.</returns>
    public static Predicate operator |(Predicate left, Predicate right)
    {
        if (left.IsAll || right.IsAll)
        {
            return All;
        }

        if (left.IsNone)
        {
            return right;
        }

        return right.IsNone ? left : new Predicate(Expr.Logical(false, left.Node!, right.Node!));
    }

    /// <summary>The rows the predicate does not keep, under three-valued logic: a null stays unknown.</summary>
    /// <param name="operand">A predicate.</param>
    /// <returns>The negation.</returns>
    public static Predicate operator !(Predicate operand) =>
        operand.IsAll ? None : operand.IsNone ? All : new Predicate(new NotExpr(operand.Node!));

    /// <summary>Always false, so that <c>||</c> evaluates its right side.</summary>
    /// <param name="p">A predicate.</param>
    /// <returns>False.</returns>
    public static bool operator true(Predicate p) => false;

    /// <summary>Always false, so that <c>&amp;&amp;</c> evaluates its right side.</summary>
    /// <param name="p">A predicate.</param>
    /// <returns>False.</returns>
    public static bool operator false(Predicate p) => false;

    /// <summary>The predicate as the filter grammar prints it.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => IsAll ? "true" : IsNone ? "false" : ExprText.Format(Node!);

    private enum PredicateShape : byte
    {
        All,
        None,
        Expression,
    }
}
