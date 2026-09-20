using System;

namespace Vorticity.RowEncoding;

/// <summary>
/// How one input column contributes to a row key. Two encoded rows are comparable only when they
/// were produced from the same schema with the same <see cref="RowSortField"/> per column: the
/// bytes carry no type tags, no field names and no sort options of their own.
/// </summary>
public readonly struct RowSortField : IEquatable<RowSortField>
{
    /// <summary>Creates a field with explicit options.</summary>
    public RowSortField(bool descending, bool nullsFirst)
    {
        Descending = descending;
        NullsFirst = nullsFirst;
    }

    /// <summary>
    /// Ascending, nulls first: the default. There is no matching <c>Descending</c> factory because
    /// the property takes the name; use <c>Ascending.WithDescending(true)</c> instead.
    /// </summary>
    public static RowSortField Ascending => new(descending: false, nullsFirst: true);

    /// <summary>Whether this column sorts descending; only value bytes are inverted.</summary>
    public bool Descending { get; }

    /// <summary>Whether nulls sort before non-null values, in either direction.</summary>
    public bool NullsFirst { get; }

    /// <summary>This field with nulls ordered before non-null values.</summary>
    public RowSortField WithNullsFirst() => new(Descending, nullsFirst: true);

    /// <summary>This field with nulls ordered after non-null values.</summary>
    public RowSortField WithNullsLast() => new(Descending, nullsFirst: false);

    /// <summary>This field with the given direction.</summary>
    public RowSortField WithDescending(bool descending) => new(descending, NullsFirst);

    /// <inheritdoc/>
    public bool Equals(RowSortField other) =>
        Descending == other.Descending && NullsFirst == other.NullsFirst;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RowSortField other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Descending ? 2 : 0) | (NullsFirst ? 1 : 0);

    /// <summary>Equality.</summary>
    public static bool operator ==(RowSortField left, RowSortField right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(RowSortField left, RowSortField right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        $"descending={(Descending ? "true" : "false")}, nulls_first={(NullsFirst ? "true" : "false")}";
}
