using System;

namespace Vorticity.Indexes;

/// <summary>
/// Where a column's values lie in an object, granule by granule: the object's own index (<see cref="ColumnLocate"/>), or the
/// index of its partition, for the values a filter seeks (spec D, 7). What the granules' algebra asks of either.
/// </summary>
internal interface IValueGranules
{
    /// <summary>The object's granules.</summary>
    int Granules { get; }

    /// <summary>Sets in <paramref name="granules"/>, a bit a granule, those <paramref name="value"/> lies in: none for a value the object does not hold.</summary>
    void OrValue(ReadOnlySpan<byte> value, Span<ulong> granules);

    /// <summary>
    /// Sets in <paramref name="granules"/> those the values that start with <paramref name="prefix"/> lie in; false when it
    /// cannot tell, every granule then left.
    /// </summary>
    bool TryOrPrefix(ReadOnlySpan<byte> prefix, Span<ulong> granules);
}
