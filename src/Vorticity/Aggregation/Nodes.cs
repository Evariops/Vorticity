using System;

namespace Vorticity.Aggregating;

internal enum AggregateKind : byte
{
    Count,
    CountDistinct,
    Sum,
    Min,
    Max,
    Avg,
    Custom,
}

/// <summary>A symbol that stands for a result of an aggregation: an aggregate, or a group's key or one of its components.</summary>
/// <typeparam name="T">The result's type.</typeparam>
internal abstract class ResultNode<T> : SymNode
{
    /// <summary>How to read the result of each group once the aggregation has run.</summary>
    internal abstract Func<int, T> Bind(AggregationOutcome outcome);
}

/// <summary>An aggregate, whatever its result type: what the engine plans and steps.</summary>
internal interface IAggregateNode
{
    AggregateKind Kind { get; }

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

    internal AggregateNode(AggregateKind kind, ColumnShape? input, Func<AggregateSlot<T>> create, Settler<T>? settle)
    {
        Kind = kind;
        Input = input;
        _create = create;
        _settle = settle;
    }

    public AggregateKind Kind { get; }

    public ColumnShape? Input { get; }

    public AggregateSlot Create() => _create();

    public AggregateSlot? Settle(StatisticsView view) =>
        _settle is not null && _settle(view, out T value) ? new SettledSlot<T>(value) : null;

    internal override Func<int, T> Bind(AggregationOutcome outcome) => ((AggregateSlot<T>)outcome.SlotOf(this)).Result;

    public override string ToString() => Input is null ? $"{Kind}()" : $"{Kind}({Input.Path})";
}

/// <summary>A group's key, or one component of a composite key.</summary>
internal interface IKeyNode
{
    /// <summary>The component; -1 for the whole key.</summary>
    int Component { get; }
}

internal sealed class KeyNode<T> : ResultNode<T>, IKeyNode
{
    private readonly Func<GroupKeys, Func<int, T>> _reader;

    internal KeyNode(int component, Func<GroupKeys, Func<int, T>> reader)
    {
        Component = component;
        _reader = reader;
    }

    public int Component { get; }

    internal override Func<int, T> Bind(AggregationOutcome outcome) =>
        _reader(outcome.Keys ?? throw new InvalidOperationException("A group key is a result of a grouped scan only."));

    public override string ToString() => Component < 0 ? "Key" : $"Key.Item{Component + 1}";
}

/// <summary>The components of a composite group key.</summary>
internal static class KeyItems
{
    internal static Sym<TItem> Item<TKey, TItem>(Sym<TKey> key, int component)
    {
        if (key.Node is not KeyNode<TKey> { Component: < 0 })
        {
            throw new InvalidOperationException($"Item{component + 1} reads a component of the key of a group, g.Key.");
        }

        return new Sym<TItem>(new KeyNode<TItem>(component, keys => keys.Reader<TItem>(component)));
    }
}
