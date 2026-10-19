using Vorticity.Types.Variants;

namespace Vorticity.Indexes;

/// <summary>
/// A path of a variant column the synopsis counts: a top-level key of the objects of variant field
/// <paramref name="Field"/>, read at <paramref name="Class"/>, null where a row's object does not hold it
/// or holds a value of another class. An object's slice table lists its paths, and an entry of its order,
/// a table's key or a table's measure names one as <see cref="Entry"/> of its place in that list.
/// </summary>
/// <remarks>
/// A path the object shreds may sort its slices, since its values then lie in a typed column whose runs a
/// question reads. A path left in the residual never does: it keys value counts and is summed as a measure,
/// from the rows parsed when the slices are sorted.
/// </remarks>
/// <param name="Field">The variant column, a top-level field.</param>
/// <param name="Key">The key, a top-level key of the column's objects.</param>
/// <param name="Class">The class its values are read at.</param>
/// <param name="Residual">Whether the object leaves the path in the residual rather than shred it.</param>
internal sealed record SynopsisPath(int Field, string Key, VariantClass Class, bool Residual = false)
{
    /// <summary>The first entry that names a path rather than a field: the path at place <c>entry - Base</c>.</summary>
    internal const int Base = 1 << 24;

    /// <summary>Whether <paramref name="entry"/>, of an order or of a table's keys or measures, names a path.</summary>
    internal static bool Names(int entry) => entry >= Base;

    /// <summary>The entry that names the path at place <paramref name="place"/> of an object's paths.</summary>
    internal static int Entry(int place) => Base + place;

    /// <summary>The place in an object's paths of the path <paramref name="entry"/> names.</summary>
    internal static int Place(int entry) => entry - Base;
}
