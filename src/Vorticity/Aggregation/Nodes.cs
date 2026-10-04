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

/// <summary>A symbol that stands for a result of an aggregation: an aggregate, or a group's key or one of its components.</summary>
/// <typeparam name="T">The result's type.</typeparam>
internal abstract class ResultNode<T> : SymNode
{
    /// <summary>How to read the result of each group once the aggregation has run.</summary>
    internal abstract Func<int, T> Bind(AggregationOutcome outcome);

    /// <summary>The result's column, of its own type, named <paramref name="name"/>.</summary>
    /// <param name="name">The column's name.</param>
    /// <param name="keys">The columns of the group key.</param>
    internal abstract ResultColumn Column(string name, ColumnShape[] keys);
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

    public AggregateKind Kind { get; }

    public AggregateIdentity Identity { get; }

    public ColumnShape? Input { get; }

    public AggregateSlot Create() => _create();

    public AggregateSlot? Settle(StatisticsView view) =>
        _settle is not null && _settle(view, out T value) ? new SettledSlot<T>(value) : null;

    internal override Func<int, T> Bind(AggregationOutcome outcome) => ((AggregateSlot<T>)outcome.SlotOf(this)).Result;

    internal override ResultColumn Column(string name, ColumnShape[] keys) =>
        new ValueResultColumn<T>(name, ResultTypes.Of<T>(this, Record), this, Record);

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

    internal override ResultColumn Column(string name, ColumnShape[] keys) => new KeyResultColumn(name, keys[Component].Type, Component);

    public override string ToString() => $"Key({_name})";
}
