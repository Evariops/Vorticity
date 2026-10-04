using System;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// One column of an aggregation's result: its name, its type, and how the values of a batch of
/// groups reach its store. A result is these columns side by side, one per element of the
/// <c>select</c>, built a batch of groups at a time (<see cref="GroupBatches"/>).
/// </summary>
internal abstract class ResultColumn
{
    protected ResultColumn(string name, VortexType type)
    {
        Name = name;
        Type = type;
    }

    internal string Name { get; }

    internal VortexType Type { get; }

    /// <summary>How the column's values are read and written when they are records: a custom aggregate's state.</summary>
    internal virtual IVortexRecord? Record => null;

    /// <summary>Appends the values of <paramref name="groups"/>, in order, to <paramref name="store"/>, a store of <see cref="Type"/>.</summary>
    internal abstract void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups);
}

/// <summary>A component of the key, in a column of the key column's own type: its values written as the index holds them.</summary>
internal sealed class KeyResultColumn : ResultColumn
{
    private readonly int _component;

    internal KeyResultColumn(string name, VortexType type, int component)
        : base(name, type)
    {
        _component = component;
    }

    internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups) =>
        (outcome.Keys ?? throw new InvalidOperationException("A group key is a result of a grouped scan only.")).Append(_component, store, groups);
}

/// <summary>
/// A result read as <typeparamref name="T"/> and written through its conversion: an aggregate's
/// answers, read a batch of groups at a time from its slot.
/// </summary>
internal sealed class ValueResultColumn<T> : ResultColumn
{
    private readonly ResultNode<T> _node;
    private readonly IVortexRecord? _record;
    private T[] _values = [];
    private AggregationOutcome? _outcome;
    private AggregateSlot<T>? _slot;
    private Func<int, T>? _read;

    internal ValueResultColumn(string name, VortexType type, ResultNode<T> node, IVortexRecord? record)
        : base(name, type)
    {
        _node = node;
        _record = record;
    }

    internal override IVortexRecord? Record => _record;

    internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups)
    {
        if (!ReferenceEquals(outcome, _outcome))
        {
            // Bound once per run, not per batch: the slot lookup and the reader are the same for every batch.
            _outcome = outcome;
            _slot = _node is IAggregateNode aggregate ? (AggregateSlot<T>)outcome.SlotOf(aggregate) : null;
            _read = _slot is null ? _node.Bind(outcome) : null;
        }

        if (_values.Length < groups.Length)
        {
            _values = new T[Math.Max(groups.Length, _values.Length * 2)];
        }

        Span<T> values = _values.AsSpan(0, groups.Length);
        if (_slot is not null)
        {
            _slot.Results(groups, values);
        }
        else
        {
            for (int i = 0; i < groups.Length; i++)
            {
                values[i] = _read!(groups[i]);
            }
        }

        ResultValues.Append(store, _values, groups.Length, _record);
    }
}

/// <summary>The type of a result's column when no record names it: the key column's own, or what the aggregate delivers.</summary>
internal static class ResultTypes
{
    /// <summary>The column of an aggregate delivered as <typeparamref name="T"/>.</summary>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> is not a type a column holds.</exception>
    internal static VortexType Of<T>(IAggregateNode aggregate, IVortexRecord? record)
    {
        switch (aggregate.Kind)
        {
            case AggregateKind.Count:
            case AggregateKind.CountDistinct:
                return VortexType.Int64;
            case AggregateKind.Any:
            case AggregateKind.All:
                return VortexType.Bool;
            case AggregateKind.Average:
            case AggregateKind.Variance:
            case AggregateKind.StandardDeviation:
                return VortexType.Float64.Nullable;
            case AggregateKind.Min:
            case AggregateKind.Max:
            {
                // The column's own type: a minimum is one of its values, and null only where the
                // column holds nulls, or where a value type says so.
                VortexType input = aggregate.Input!.Type;
                return input.IsNullable || Nullable.GetUnderlyingType(typeof(T)) is not null ? input.Nullable : input.NonNullable;
            }

            case AggregateKind.Sum:
                return SumOf<T>(aggregate.Input!.Type);
            default:
                return Of<T>(record, $"the state of {aggregate}");
        }
    }

    /// <summary>The column a value of <typeparamref name="T"/> is kept in, with the units a writer chooses by default.</summary>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> is not a type a column holds.</exception>
    internal static VortexType Of<T>(IVortexRecord? record, string what)
    {
        VortexType type;
        if (record is not null)
        {
            type = VortexType.Struct(record.RecordSchema.FieldArray);
        }
        else
        {
            ClrShape shape = ClrShape.For<T>.Value;
            type = shape.Kind switch
            {
                ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float => VortexType.Primitive(shape.PType),
                ClrKind.Bool => VortexType.Bool,
                ClrKind.String => VortexType.Utf8,
                ClrKind.Binary => VortexType.Binary,
                ClrKind.Decimal => VortexType.Decimal(28, 10),
                ClrKind.VortexDecimal => VortexType.Decimal(76, 38),
                ClrKind.DateOnly => VortexType.Date,
                ClrKind.TimeOnly => VortexType.Time(TimeUnit.Nanoseconds),
                ClrKind.DateTime => VortexType.Timestamp(TimeUnit.Microseconds),
                ClrKind.DateTimeOffset => VortexType.Timestamp(TimeUnit.Microseconds, "UTC"),
                ClrKind.Guid => VortexType.Uuid,
                ClrKind.TimeSpan => VortexType.Int64,
                ClrKind.Integer128 or ClrKind.UInteger128 => VortexType.Decimal(39, 0),
                ClrKind.BigInteger => VortexType.Decimal(76, 0),
                _ => throw new VortexSchemaException(
                    $"{char.ToUpperInvariant(what[0])}{what[1..]} is a {ClrFit.Name(typeof(T))}, which no column holds: a result is a number, a decimal, text, " +
                    $"a date or a time, a uuid, a bool, or a [VortexRecord]."),
            };
        }

        // A record that is a value type is a struct column of no null; a string, a class or a
        // nullable value may be null.
        return MayBeNull<T>() ? type.Nullable : type;
    }

    /// <summary>The column of a sum delivered as <typeparamref name="T"/>, over a column of <paramref name="input"/>.</summary>
    private static VortexType SumOf<T>(VortexType input)
    {
        ClrShape shape = ClrShape.For<T>.Value;
        return shape.Kind switch
        {
            // A decimal sum keeps the column's scale; its digits are what the type it is delivered as holds.
            ClrKind.Decimal => VortexType.Decimal(28, input.Scale),
            ClrKind.VortexDecimal => VortexType.Decimal(76, input.Scale),
            _ => Of<T>(null, "a sum"),
        };
    }

    /// <summary>Whether a value of <typeparamref name="T"/> may be null: a reference, or a <see cref="Nullable{T}"/>.</summary>
    internal static bool MayBeNull<T>() => default(T) is null;
}
