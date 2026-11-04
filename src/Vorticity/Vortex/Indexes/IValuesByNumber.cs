using System;

namespace Vorticity.Indexes;

/// <summary>
/// Values by their numbers, each read where it lies: what an index that lists a column's values writes them from, a
/// sheet's values in place or a list of arrays.
/// </summary>
internal interface IValuesByNumber
{
    /// <summary>How many values there are.</summary>
    int Count { get; }

    /// <summary>The bytes of value <paramref name="index"/>.</summary>
    ReadOnlySpan<byte> this[int index] { get; }
}

/// <summary>Values held as arrays, every one of them there.</summary>
internal readonly struct ArrayValues(byte[]?[] values) : IValuesByNumber
{
    public int Count => values.Length;

    public ReadOnlySpan<byte> this[int index] => values[index];
}
