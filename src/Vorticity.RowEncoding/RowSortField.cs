// Per-column ordering options, transcribed from vortex-row/src/options.rs at 0.86.1.
//
// `Descending` and `NullsFirst` are INDEPENDENT, and that independence is the whole reason the
// null sentinel is never inverted: a descending column with nulls first must still put its nulls
// first, so reversing the sentinel along with the value bytes would silently move them to the end.
using System;

namespace Vorticity.RowEncoding;

/// <summary>How one input column contributes to a row key.</summary>
/// <remarks>
/// Two encoded rows are comparable only when they were produced from the same schema with the same
/// <see cref="RowSortField"/> per column: the bytes carry no type tags, no field names and no sort
/// options of their own.
/// </remarks>
public readonly struct RowSortField : IEquatable<RowSortField>
{
    /// <summary>Creates a field with explicit options.</summary>
    /// <param name="descending">Whether this column sorts descending.</param>
    /// <param name="nullsFirst">Whether nulls sort before non-null values.</param>
    public RowSortField(bool descending, bool nullsFirst)
    {
        Descending = descending;
        NullsFirst = nullsFirst;
    }

    /// <summary>
    /// Ascending, nulls first - the default in both this library and upstream. There is no
    /// matching <c>Descending</c> factory because the name is taken by the property; build one
    /// with <c>RowSortField.Ascending.WithDescending(true)</c> or the constructor.
    /// </summary>
    public static RowSortField Ascending => new(descending: false, nullsFirst: true);

    /// <summary>Whether this column sorts descending; only value bytes are inverted.</summary>
    public bool Descending { get; }

    /// <summary>Whether nulls sort before non-null values, in either direction.</summary>
    public bool NullsFirst { get; }

    /// <summary>This field with nulls ordered before non-null values.</summary>
    /// <returns>A copy with <see cref="NullsFirst"/> set.</returns>
    public RowSortField WithNullsFirst() => new(Descending, nullsFirst: true);

    /// <summary>This field with nulls ordered after non-null values.</summary>
    /// <returns>A copy with <see cref="NullsFirst"/> cleared.</returns>
    public RowSortField WithNullsLast() => new(Descending, nullsFirst: false);

    /// <summary>This field with the given direction.</summary>
    /// <param name="descending">Whether to sort descending.</param>
    /// <returns>A copy with <see cref="Descending"/> set as asked.</returns>
    public RowSortField WithDescending(bool descending) => new(descending, NullsFirst);

    /// <inheritdoc/>
    public bool Equals(RowSortField other) =>
        Descending == other.Descending && NullsFirst == other.NullsFirst;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RowSortField other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Descending ? 2 : 0) | (NullsFirst ? 1 : 0);

    /// <summary>Equality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Whether both options match.</returns>
    public static bool operator ==(RowSortField left, RowSortField right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Whether either option differs.</returns>
    public static bool operator !=(RowSortField left, RowSortField right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        $"descending={(Descending ? "true" : "false")}, nulls_first={(NullsFirst ? "true" : "false")}";
}
