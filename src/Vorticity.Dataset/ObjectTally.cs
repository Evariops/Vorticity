using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Dataset;

/// <summary>
/// What a subtree of one level holds, carried up the tree beside its rows so that compaction plans
/// by descending rather than by reading every leaf: its objects' bytes, the largest of them, the rows
/// deleted out of them, the most index fragments one of them carries, and the most any one of them
/// has marked, as a share of its rows and as the bytes of its deletion vector; when the latest of
/// their rows arrived, and how many of them each size class holds (<see cref="ClassOf"/>), which a plan
/// finds the partitions due a merge or calm by.
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
/// <param name="Latest">When the latest rows under it arrived (<see cref="ObjectEntry.Arrival"/>), 0 when none says.</param>
/// <param name="Classes">How many objects under it each size class holds.</param>
internal readonly record struct ObjectTally(
    long Bytes, long Largest, long DeletedRows, long MostFragments, long MostMarked, long LargestVector, long Latest, SizeClasses Classes)
{
    /// <summary>The share of an object's rows <see cref="MostMarked"/> counts as all of them.</summary>
    public const long Whole = 1L << 20;

    /// <summary>
    /// The tally of one object, read from its entry's bytes without parsing the rest of them, its size class under objects
    /// of <paramref name="top"/> bytes merged <paramref name="fanout"/> at a time.
    /// </summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectTally Of(ReadOnlySpan<byte> entry, long top, int fanout)
    {
        (long bytes, long rows, long deleted, long fragments, long vector, long arrival) = ObjectEntry.TallyOf(entry);
        SizeClasses classes = default;
        classes[Math.Min(ClassOf(bytes, top, fanout), SizeClasses.Count - 1)] = 1;
        return new ObjectTally(bytes, bytes, deleted, fragments, ShareOf(deleted, Sum(rows, deleted)), vector, arrival, classes);
    }

    /// <summary>
    /// The size class of an object of <paramref name="bytes"/> under objects of <paramref name="top"/> merged
    /// <paramref name="fanout"/> at a time: how many times its bytes are multiplied by the fan-out to pass
    /// <c>top / fanout</c>, 0 for an object past it already. A merge of <paramref name="fanout"/> objects of a class makes
    /// one of the class below.
    /// </summary>
    public static int ClassOf(long bytes, long top, int fanout)
    {
        int size = 0;
        long grown = Math.Max(bytes, 1);
        while (grown <= top / fanout)
        {
            grown *= fanout;
            size++;
        }

        return size;
    }

    /// <summary>
    /// <paramref name="deleted"/> rows of <paramref name="physical"/> as parts of <see cref="Whole"/>,
    /// rounded down, so that a share at or past a whole fraction of <see cref="Whole"/> is never
    /// counted below it.
    /// </summary>
    public static long ShareOf(long deleted, long physical) =>
        physical <= 0 ? 0 : (long)(((UInt128)(ulong)deleted * (ulong)Whole) / (ulong)physical);

    /// <summary>The tally of this subtree and <paramref name="other"/> together.</summary>
    /// <exception cref="CommitFormatException">The two count past a long, which no objects do.</exception>
    public ObjectTally With(ObjectTally other)
    {
        SizeClasses classes = Classes;
        for (int c = 0; c < SizeClasses.Count; c++)
        {
            classes[c] = checked(classes[c] + other.Classes[c]);
        }

        return new ObjectTally(
            Sum(Bytes, other.Bytes),
            Math.Max(Largest, other.Largest),
            Sum(DeletedRows, other.DeletedRows),
            Math.Max(MostFragments, other.MostFragments),
            Math.Max(MostMarked, other.MostMarked),
            Math.Max(LargestVector, other.LargestVector),
            Math.Max(Latest, other.Latest),
            classes);
    }

    /// <summary>The classes past the last that holds an object, which the encoding leaves out.</summary>
    private int ClassesHeld
    {
        get
        {
            int held = SizeClasses.Count;
            while (held > 0 && Classes[held - 1] == 0)
            {
                held--;
            }

            return held;
        }
    }

    /// <summary>The bytes <see cref="Write"/> takes.</summary>
    public int EncodedBytes
    {
        get
        {
            int bytes = TreePage.VarintBytes((ulong)Bytes) + TreePage.VarintBytes((ulong)Largest)
                + TreePage.VarintBytes((ulong)DeletedRows) + TreePage.VarintBytes((ulong)MostFragments)
                + TreePage.VarintBytes((ulong)MostMarked) + TreePage.VarintBytes((ulong)LargestVector)
                + TreePage.VarintBytes((ulong)Latest);
            int held = ClassesHeld;
            bytes += TreePage.VarintBytes((ulong)held);
            for (int c = 0; c < held; c++)
            {
                bytes += TreePage.VarintBytes(Classes[c]);
            }

            return bytes;
        }
    }

    /// <summary>Writes the tally as seven varints, then the classes held, their count and each one's; what follows it.</summary>
    public Span<byte> Write(Span<byte> destination)
    {
        destination = TreePage.WriteVarint(destination, (ulong)Bytes);
        destination = TreePage.WriteVarint(destination, (ulong)Largest);
        destination = TreePage.WriteVarint(destination, (ulong)DeletedRows);
        destination = TreePage.WriteVarint(destination, (ulong)MostFragments);
        destination = TreePage.WriteVarint(destination, (ulong)MostMarked);
        destination = TreePage.WriteVarint(destination, (ulong)LargestVector);
        destination = TreePage.WriteVarint(destination, (ulong)Latest);
        int held = ClassesHeld;
        destination = TreePage.WriteVarint(destination, (ulong)held);
        for (int c = 0; c < held; c++)
        {
            destination = TreePage.WriteVarint(destination, Classes[c]);
        }

        return destination;
    }

    /// <summary>Reads a tally <see cref="Write"/> wrote at <paramref name="at"/>, moving past it.</summary>
    /// <exception cref="CommitFormatException">
    /// The bytes are not a tally: cut short, a largest object larger than the whole, a share past all, or more classes than
    /// there are.
    /// </exception>
    public static ObjectTally Read(ReadOnlySpan<byte> value, ref int at)
    {
        long bytes = Long(value, ref at);
        long largest = Long(value, ref at);
        long deleted = Long(value, ref at);
        long fragments = Long(value, ref at);
        long marked = Long(value, ref at);
        long vector = Long(value, ref at);
        long latest = Long(value, ref at);
        long held = Long(value, ref at);
        if (held > SizeClasses.Count)
        {
            throw new CommitFormatException("A page's tally counts more size classes than there are.");
        }

        SizeClasses classes = default;
        for (int c = 0; c < held; c++)
        {
            long count = Long(value, ref at);
            classes[c] = count <= uint.MaxValue ? (uint)count : throw new CommitFormatException("A page's tally counts past its classes' counts.");
        }

        ObjectTally tally = new ObjectTally(bytes, largest, deleted, fragments, marked, vector, latest, classes);
        return tally.Largest <= tally.Bytes && tally.MostMarked <= Whole
            ? tally
            : throw new CommitFormatException("A page's tally holds an object larger than every object under it, or more than all its rows marked.");
    }

    /// <summary>Two counts read from pages, added: past a long, the pages are not ones a writer wrote.</summary>
    private static long Sum(long left, long right) =>
        right > long.MaxValue - left
            ? throw new CommitFormatException("A page's tally counts past a long.")
            : left + right;

    private static long Long(ReadOnlySpan<byte> value, ref int at)
    {
        ulong read = TreePage.ReadVarint(value, ref at, "A page's tally");
        return read <= long.MaxValue
            ? (long)read
            : throw new CommitFormatException("A page's tally counts past a long.");
    }
}

/// <summary>How many objects a subtree holds of each size class, the last class counting those of every class past it.</summary>
[InlineArray(Count)]
internal struct SizeClasses
{
    /// <summary>The classes counted: from objects of the top's size down to some 8^-11 of it at a fan-out of 8.</summary>
    internal const int Count = 12;

    private uint _first;
}
