using System;

namespace Vorticity.Dataset;

/// <summary>
/// What a subtree of one level holds, carried up the tree beside its rows so that compaction plans
/// by descending rather than by reading every leaf: its objects' bytes, the largest of them, the rows
/// deleted out of them, and the most index fragments one of them carries.
/// </summary>
/// <param name="Bytes">The bytes of every object under it.</param>
/// <param name="Largest">The bytes of the largest object under it.</param>
/// <param name="DeletedRows">The rows marked deleted in every object under it.</param>
/// <param name="MostFragments">The most index fragments one object under it carries.</param>
internal readonly record struct ObjectTally(long Bytes, long Largest, long DeletedRows, long MostFragments)
{
    /// <summary>The tally of one object, read from its entry's bytes without parsing the rest of them.</summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectTally Of(ReadOnlySpan<byte> entry)
    {
        (long bytes, long deleted, long fragments) = ObjectEntry.TallyOf(entry);
        return new ObjectTally(bytes, bytes, deleted, fragments);
    }

    /// <summary>The tally of this subtree and <paramref name="other"/> together.</summary>
    public ObjectTally With(ObjectTally other) => new ObjectTally(
        checked(Bytes + other.Bytes),
        Math.Max(Largest, other.Largest),
        checked(DeletedRows + other.DeletedRows),
        Math.Max(MostFragments, other.MostFragments));

    /// <summary>The bytes <see cref="Write"/> takes.</summary>
    public int EncodedBytes =>
        TreePage.VarintBytes((ulong)Bytes) + TreePage.VarintBytes((ulong)Largest)
        + TreePage.VarintBytes((ulong)DeletedRows) + TreePage.VarintBytes((ulong)MostFragments);

    /// <summary>Writes the tally as four varints; what follows it.</summary>
    public Span<byte> Write(Span<byte> destination)
    {
        destination = TreePage.WriteVarint(destination, (ulong)Bytes);
        destination = TreePage.WriteVarint(destination, (ulong)Largest);
        destination = TreePage.WriteVarint(destination, (ulong)DeletedRows);
        return TreePage.WriteVarint(destination, (ulong)MostFragments);
    }

    /// <summary>Reads a tally <see cref="Write"/> wrote at <paramref name="at"/>, moving past it.</summary>
    /// <exception cref="CommitFormatException">The bytes are not a tally: cut short, or a largest object larger than the whole.</exception>
    public static ObjectTally Read(ReadOnlySpan<byte> value, ref int at)
    {
        ObjectTally tally = new ObjectTally(
            Long(value, ref at), Long(value, ref at), Long(value, ref at), Long(value, ref at));
        return tally.Largest <= tally.Bytes
            ? tally
            : throw new CommitFormatException("A page's tally holds an object larger than every object under it.");
    }

    private static long Long(ReadOnlySpan<byte> value, ref int at)
    {
        ulong read = TreePage.ReadVarint(value, ref at, "A page's tally");
        return read <= long.MaxValue
            ? (long)read
            : throw new CommitFormatException("A page's tally counts past a long.");
    }
}
