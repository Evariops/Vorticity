using System;

namespace Vorticity.Aggregating;

internal enum AggregateKind : byte
{
    Count,
    CountDistinct,
    Sum,
    Min,
    Max,
    Average,
    Custom,
}

/// <summary>A result of an aggregation, whatever its type: what an element of a selection of several values is.</summary>
internal interface IResultNode
{
    /// <summary>The result's column, of its own type, named <paramref name="name"/>.</summary>
    /// <param name="name">The column's name.</param>
    /// <param name="keys">The columns of the group key.</param>
    ResultColumn Column(string name, ColumnShape[] keys);

    /// <summary>The result's column as a record's member takes it: named and typed by the member.</summary>
    /// <param name="member">The member's field of the record's schema.</param>
    /// <param name="keys">The columns of the group key.</param>
    /// <param name="position">The element's position in the selection, from 0, for a message.</param>
    /// <param name="record">The record type, for a message.</param>
    /// <exception cref="VortexSchemaException">The member's column does not take the result's values.</exception>
    ResultColumn Column(VortexField member, ColumnShape[] keys, int position, Type record);
}

/// <summary>A symbol that stands for a result of an aggregation: an aggregate, or a group's key or one of its components.</summary>
/// <typeparam name="T">The result's type.</typeparam>
internal abstract class ResultNode<T> : SymNode, IResultNode
{
    /// <summary>How to read the result of each group once the aggregation has run.</summary>
    internal abstract Func<int, T> Bind(AggregationOutcome outcome);

    /// <summary>The result's column, of its own type, named <paramref name="name"/>.</summary>
    /// <param name="name">The column's name.</param>
    /// <param name="keys">The columns of the group key.</param>
    public abstract ResultColumn Column(string name, ColumnShape[] keys);

    /// <summary>The result as a filter compares it, a column of the groups' results; null for a result that is not compared this way.</summary>
    internal virtual ColumnSym? Comparable => null;

    public ResultColumn Column(VortexField member, ColumnShape[] keys, int position, Type record)
    {
        VortexType natural = Column(member.Name, keys).Type;
        VortexType target = member.Type;
        if (!Takes(target, out string? reason))
        {
            throw new VortexSchemaException(
                $"Element {position + 1} of the selection, {this}, is of type {ClrFit.Name(typeof(T))}, which member '{member.Name}' of {record.Name}, a column of {target}, does not take: {reason}");
        }

        if (natural.IsNullable && !target.IsNullable)
        {
            throw new VortexSchemaException(
                $"Element {position + 1} of the selection, {this}, may be null, and member '{member.Name}' of {record.Name} is a column of {target}, which holds none: declare the member nullable.");
        }

        return Typed(member, keys);
    }

    /// <summary>The result's column of the member's type, once the member is known to take it.</summary>
    private protected abstract ResultColumn Typed(VortexField member, ColumnShape[] keys);

    /// <summary>Whether a column of <paramref name="target"/> takes the result's values: by the mapping table, or as the struct of a record.</summary>
    private protected virtual bool Takes(VortexType target, out string? reason) => ClrFit.Fits(ClrShape.For<T>.Value, target, null, out reason);
}

/// <summary>
/// What makes two aggregates one: the same function over the same input, delivered as the same type,
/// by the same aggregator. The <c>g.Count()</c> of a filter, an order and a selection is one state.
/// </summary>
/// <param name="Kind">The function.</param>
/// <param name="Input">The input's path; null for a count of rows.</param>
/// <param name="Result">The type the answer is delivered as.</param>
/// <param name="Detail">The caller's aggregator, for a custom aggregate.</param>
internal readonly record struct AggregateIdentity(AggregateKind Kind, string? Input, Type Result, Type? Detail);

/// <summary>An aggregate, whatever its result type: what the engine plans and steps.</summary>
internal interface IAggregateNode
{
    AggregateKind Kind { get; }

    /// <summary>What makes this aggregate the same as another, written elsewhere in the query.</summary>
    AggregateIdentity Identity { get; }

    /// <summary>The column it reads; null for a count of rows.</summary>
    ColumnShape? Input { get; }

    /// <summary>A fresh, empty state for one partition.</summary>
    AggregateSlot Create();

    /// <summary>The answer the file statistics give for the whole file, or null when they do not give it exactly.</summary>
    AggregateSlot? Settle(StatisticsView view);
}

/// <summary>An answer read from the file statistics.</summary>
internal delegate bool Settler<T>(StatisticsView view, out T value);

internal sealed class AggregateNode<T> : ResultNode<T>, IAggregateNode
{
    private readonly Func<AggregateSlot<T>> _create;
    private readonly Settler<T>? _settle;

    internal AggregateNode(AggregateKind kind, ColumnShape? input, Func<AggregateSlot<T>> create, Settler<T>? settle, Type? detail = null, IVortexRecord? record = null)
    {
        Kind = kind;
        Input = input;
        _create = create;
        _settle = settle;
        Record = record;
        Identity = new AggregateIdentity(kind, input?.Path, typeof(T), detail);
    }

    /// <summary>How a state that is a record is read and written, for a custom aggregate whose state is one.</summary>
    internal IVortexRecord? Record { get; }

    /// <summary>The name of the aggregate's column where a filter or an order on groups reads it, the same for one aggregate wherever it is written.</summary>
    internal string HiddenName => $"${Identity.Kind}({Identity.Input}):{Identity.Result.FullName}:{Identity.Detail?.FullName}";

    internal override ColumnSym? Comparable => _comparable ??= Kind == AggregateKind.Custom
        ? throw new InvalidOperationException($"'{this}' is the state of a custom aggregator, which has no order: compare a value a built-in aggregate delivers.")
        : new ColumnSym(new ResultFieldExpr(HiddenName, this, -1), ResultTypes.Of<T>(this, Record), null, null, -1, []);

    private ColumnSym? _comparable;

    public AggregateKind Kind { get; }

    public AggregateIdentity Identity { get; }

    public ColumnShape? Input { get; }

    public AggregateSlot Create() => _create();

    public AggregateSlot? Settle(StatisticsView view) =>
        _settle is not null && _settle(view, out T value) ? new SettledSlot<T>(value) : null;

    internal override Func<int, T> Bind(AggregationOutcome outcome) => ((AggregateSlot<T>)outcome.SlotOf(this)).Result;

    public override ResultColumn Column(string name, ColumnShape[] keys) =>
        new ValueResultColumn<T>(name, ResultTypes.Of<T>(this, Record), this, Record);

    private protected override ResultColumn Typed(VortexField member, ColumnShape[] keys) =>
        new ValueResultColumn<T>(member.Name, member.Type, this, Record);

    private protected override bool Takes(VortexType target, out string? reason)
    {
        if (Record is null)
        {
            return base.Takes(target, out reason);
        }

        // A state that is a record is a struct of its columns, which only the same struct holds.
        VortexType state = VortexType.Struct(Record.RecordSchema.FieldArray);
        bool same = state.Equals(target.NonNullable);
        reason = same ? null : $"a {typeof(T).Name} is a column of {state}.";
        return same;
    }

    public override string ToString() => Input is null ? $"{Kind}()" : $"{Kind}({Input.Path})";
}

/// <summary>A component of a group's key, delivered as a result.</summary>
internal interface IKeyNode
{
    /// <summary>The component's position in the key.</summary>
    int Component { get; }
}

internal sealed class KeyNode<T> : ResultNode<T>, IKeyNode
{
    private readonly string _name;

    internal KeyNode(int component, string name)
    {
        Component = component;
        _name = name;
    }

    public int Component { get; }

    internal override Func<int, T> Bind(AggregationOutcome outcome) =>
        (outcome.Keys ?? throw new InvalidOperationException("A group key is a result of a grouped scan only.")).Reader<T>(Component);

    public override ResultColumn Column(string name, ColumnShape[] keys) => new KeyResultColumn(name, keys[Component].Type, Component);

    // The key's values as the index holds them when the member's column stores them alike, through
    // their .NET type when it does not: a timestamp of another unit, an integer of another width.
    private protected override ResultColumn Typed(VortexField member, ColumnShape[] keys) =>
        keys[Component].Type.NonNullable.Equals(member.Type.NonNullable)
            ? new KeyResultColumn(member.Name, member.Type, Component)
            : new ValueResultColumn<T>(member.Name, member.Type, this, null);

    public override string ToString() => $"Key({_name})";
}
