using System;

namespace Vorticity;

/// <summary>
/// A .NET type that is a schema: its members are columns, in order. The <c>[VortexRecord]</c>
/// generator implements it; it can also be written by hand.
/// </summary>
/// <typeparam name="TSelf">The record type.</typeparam>
/// <remarks>
/// A record is bound to a file once, at the first sink of a scan or when a writer is created: each
/// member is matched to a column by exact name, then by a unique case-insensitive match, and its
/// type must fit the column's. A file with more columns than the record is fine: the record is the
/// projection.
/// </remarks>
public interface IVortexRecord<TSelf>
    where TSelf : IVortexRecord<TSelf>
{
    /// <summary>The record's columns, in member order.</summary>
    static abstract VortexSchema Schema { get; }

    /// <summary>Fills <paramref name="rows"/> from a batch, one column at a time.</summary>
    /// <param name="columns">The batch, bound to the record.</param>
    /// <param name="rows">Exactly <c>columns.RowCount</c> rows to fill.</param>
    static abstract void ReadRows(Columns<TSelf> columns, Span<TSelf> rows);

    /// <summary>Appends <paramref name="rows"/> to a builder, one column at a time.</summary>
    /// <param name="builder">The builder, bound to the record.</param>
    /// <param name="rows">The rows.</param>
    static abstract void WriteRows(ColumnsBuilder<TSelf> builder, ReadOnlySpan<TSelf> rows);
}

/// <summary>Marks a <c>partial</c> type whose members become columns; the generator implements <see cref="IVortexRecord{TSelf}"/> for it.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class VortexRecordAttribute : Attribute
{
}

/// <summary>Names a member's column, and fixes the parameters its .NET type does not carry.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = false)]
public sealed class VortexColumnAttribute : Attribute
{
    /// <summary>Keeps the member's own name.</summary>
    public VortexColumnAttribute()
    {
    }

    /// <summary>Names the member's column.</summary>
    /// <param name="name">The column name, such as <c>session_id</c>.</param>
    public VortexColumnAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The column name, or null for the member's own.</summary>
    public string? Name { get; }

    /// <summary>The digits of a <see cref="decimal"/> member's column; 28 by default.</summary>
    public int Precision { get; set; } = 28;

    /// <summary>The scale of a <see cref="decimal"/> member's column; 10 by default.</summary>
    public int Scale { get; set; } = 10;

    /// <summary>The unit of a <see cref="DateTime"/>, <see cref="DateTimeOffset"/> or <see cref="TimeOnly"/> member's column; microseconds by default.</summary>
    public TimeUnit Unit { get; set; } = TimeUnit.Microseconds;

    /// <summary>
    /// The zone of a <see cref="DateTimeOffset"/> member's column, UTC when null; a <see cref="DateTime"/>
    /// member's column is naive, or UTC when this says <c>UTC</c>, and another zone is refused.
    /// </summary>
    public string? TimeZone { get; set; }
}

/// <summary>Leaves a member out of the record's columns.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = false)]
public sealed class VortexIgnoreAttribute : Attribute
{
}
