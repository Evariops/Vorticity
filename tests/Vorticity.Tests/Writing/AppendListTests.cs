using System.Collections.Generic;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The append-only list the writer keeps per block in: every index lands in its own slot across its
/// forms and boundaries -- the single array that doubles, the segments that double past it, and the
/// fixed ones past those.
/// </summary>
public sealed class AppendListTests
{
    /// <summary>
    /// Past the single array (64 entries), the doubling segments (to 512) and across many fixed
    /// ones of 512, so that every branch of the index arithmetic and every boundary is crossed, past
    /// the eight segments the first array of them holds, and past the thirty-odd segments at which a
    /// doubling left unbounded would have shifted out of an int.
    /// </summary>
    private const int Entries = 512 + (40 * 512) + 7;

    [Fact]
    public void EveryEntryReadsBackWhereItWasAdded()
    {
        AppendList<long> list = new AppendList<long>();
        for (int i = 0; i < Entries; i++)
        {
            list.Add(Value(i));
            Assert.Equal(i + 1, list.Count);
        }

        for (int i = 0; i < Entries; i++)
        {
            Assert.Equal(Value(i), list[i]);
        }

        int at = 0;
        foreach (long value in list)
        {
            Assert.Equal(Value(at++), value);
        }

        Assert.Equal(Entries, at);
    }

    [Fact]
    public void AnEntryWrittenByReferenceIsTheOneReadBack()
    {
        AppendList<int> list = new AppendList<int>();
        for (int i = 0; i < Entries; i++)
        {
            list.Add(i);
        }

        for (int i = 0; i < Entries; i++)
        {
            list.At(i) = -i;
        }

        for (int i = 0; i < Entries; i++)
        {
            Assert.Equal(-i, list[i]);
        }
    }

    [Fact]
    public void AnIndexPastTheEndIsRefused()
    {
        AppendList<int> list = new AppendList<int>();
        Assert.Throws<System.ArgumentOutOfRangeException>(() => list[0]);
        list.Add(1);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => list[1]);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => list[-1]);
        IReadOnlyList<int> read = list;
        Assert.Equal(1, read[0]);
    }

    private static long Value(int index) => ((long)index * 2_654_435_761L) ^ 0x5DEECE66DL;
}
