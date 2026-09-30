using System;

namespace Vorticity.Dataset;

/// <summary>
/// What a subtree of one level holds, carried up the tree beside its rows so that compaction plans
/// by descending rather than by reading every leaf: its objects' bytes, the largest of them, the rows
/// deleted out of them, the most index fragments one of them carries, and the most any one of them
/// has marked, as a share of its rows and as the bytes of its deletion vector.
/// </summary>
/// <param name="Bytes">The bytes of every object under it.</param>
/// <param name="Largest">The bytes of the largest object under it.</param>
/// <param name="DeletedRows">The rows marked deleted in every object under it.</param>
/// <param name="MostFragments">The most index fragments one object under it carries.</param>
/// <param name="MostMarked">
/// The largest share of its own rows one object under it has marked deleted, in parts of
/// <see cref="Whole"/>, rounded down.
/// </param>
/// <param name="LargestVector">The bytes of the largest deletion vector under it.</param>
internal readonly record struct ObjectTally(
    long Bytes, long Largest, long DeletedRows, long MostFragments, long MostMarked, long LargestVector)
{
    /// <summary>The share of an object's rows <see cref="MostMarked"/> counts as all of them.</summary>
    public const long Whole = 1L << 20;

    /// <summary>The tally of one object, read from its entry's bytes without parsing the rest of them.</summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectTally Of(ReadOnlySpan<byte> entry)
    {
        (long bytes, long rows, long deleted, long fragments, long vector) = ObjectEntry.TallyOf(entry);
        return new ObjectTally(bytes, bytes, deleted, fragments, ShareOf(deleted, rows + deleted), vector);
    }

    /// <summary>
    /// <paramref name="deleted"/> rows of <paramref name="physical"/> as parts of <see cref="Whole"/>,
    /// rounded down, so that a share at or past a whole fraction of <see cref="Whole"/> is never
    /// counted below it.
    /// </summary>
    public static long ShareOf(long deleted, long physical) =>
        physical <= 0 ? 0 : (long)(((UInt128)(ulong)deleted * (ulong)Whole) / (ulong)physical);

    /// <summary>The tally of this subtree and <paramref name="other"/> together.</summary>
    public ObjectTally With(ObjectTally other) => new ObjectTally(
        checked(Bytes + other.Bytes),
        Math.Max(Largest, other.Largest),
        checked(DeletedRows + other.DeletedRows),
        Math.Max(MostFragments, other.MostFragments),
        Math.Max(MostMarked, other.MostMarked),
        Math.Max(LargestVector, other.LargestVector));

    /// <summary>The bytes <see cref="Write"/> takes.</summary>
    public int EncodedBytes =>
        TreePage.VarintBytes((ulong)Bytes) + TreePage.VarintBytes((ulong)Largest)
        + TreePage.VarintBytes((ulong)DeletedRows) + TreePage.VarintBytes((ulong)MostFragments)
        + TreePage.VarintBytes((ulong)MostMarked) + TreePage.VarintBytes((ulong)LargestVector);

    /// <summary>Writes the tally as six varints; what follows it.</summary>
    public Span<byte> Write(Span<byte> destination)
    {
        destination = TreePage.WriteVarint(destination, (ulong)Bytes);
        destination = TreePage.WriteVarint(destination, (ulong)Largest);
        destination = TreePage.WriteVarint(destination, (ulong)DeletedRows);
        destination = TreePage.WriteVarint(destination, (ulong)MostFragments);
        destination = TreePage.WriteVarint(destination, (ulong)MostMarked);
        return TreePage.WriteVarint(destination, (ulong)LargestVector);
    }

    /// <summary>Reads a tally <see cref="Write"/> wrote at <paramref name="at"/>, moving past it.</summary>
    /// <exception cref="CommitFormatException">
    /// The bytes are not a tally: cut short, a largest object larger than the whole, or a share past all.
    /// </exception>
    public static ObjectTally Read(ReadOnlySpan<byte> value, ref int at)
    {
        ObjectTally tally = new ObjectTally(
            Long(value, ref at), Long(value, ref at), Long(value, ref at), Long(value, ref at), Long(value, ref at), Long(value, ref at));
        return tally.Largest <= tally.Bytes && tally.MostMarked <= Whole
            ? tally
            : throw new CommitFormatException("A page's tally holds an object larger than every object under it, or more than all its rows marked.");
    }

    private static long Long(ReadOnlySpan<byte> value, ref int at)
    {
        ulong read = TreePage.ReadVarint(value, ref at, "A page's tally");
        return read <= long.MaxValue
            ? (long)read
            : throw new CommitFormatException("A page's tally counts past a long.");
    }
}
