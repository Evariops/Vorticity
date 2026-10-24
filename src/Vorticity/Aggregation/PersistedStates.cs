using System;

namespace Vorticity.Aggregating;

/// <summary>The states an aggregate keeps past a scan: what a dataset persists for runs and tables, and merges.</summary>
internal enum StateKind : byte
{
    /// <summary>A count of rows, one <c>Int64</c>.</summary>
    Count,

    /// <summary>An exact sum of integers, an <see cref="Int128"/> in two <c>Int64</c> words.</summary>
    IntegerSum,

    /// <summary>An exact sum of unscaled decimals, a <see cref="WideSum"/> in five <c>Int64</c> limbs.</summary>
    DecimalSum,

    /// <summary>An exact float sum and its count, a <see cref="WideIndexedSum"/> in five <c>Int64</c> words.</summary>
    FloatSum,

    /// <summary>The smallest value, in the column's own type, null when there is none.</summary>
    Min,

    /// <summary>The largest value, in the column's own type, null when there is none.</summary>
    Max,
}

/// <summary>
/// How each state is persisted: a kind of the index directory whose name carries its version, and
/// columns of 64-bit integers that hold its words as the engine keeps them, so that a state read back
/// is the same bits and merges as the engine merges, in any order. A minimum or a maximum is a column of
/// the value's own type, null where the group holds none.
/// </summary>
/// <remarks>
/// <para>
/// A state is integers on every machine. A float sum's words are its three bins, a word for its count,
/// its top and the infinities it met, and a word for the bins' high parts and the epochs settled: the
/// second and the last are almost always the same or zero across a column, and compress to nothing.
/// </para>
/// <para>
/// A kind's layout never changes under its name. A reader meeting a version it does not know ignores
/// the state and computes what it needed from the rows, as an index is only a hint; a state written by
/// an earlier version is read under that version's name, by its own rule.
/// </para>
/// </remarks>
internal static class PersistedStates
{
    /// <summary>The kind of a count.</summary>
    internal const string CountKind = "vorticity.state.count.v1";

    /// <summary>The kind of an exact integer sum.</summary>
    internal const string IntegerSumKind = "vorticity.state.sum.int128.v1";

    /// <summary>The kind of an exact decimal sum.</summary>
    internal const string DecimalSumKind = "vorticity.state.sum.int320.v1";

    /// <summary>The kind of an exact float sum.</summary>
    internal const string FloatSumKind = "vorticity.state.sum.indexed.v1";

    /// <summary>The kind of a minimum.</summary>
    internal const string MinKind = "vorticity.state.min.v1";

    /// <summary>The kind of a maximum.</summary>
    internal const string MaxKind = "vorticity.state.max.v1";

    /// <summary>The directory kind of a state.</summary>
    internal static string KindOf(StateKind state) => state switch
    {
        StateKind.Count => CountKind,
        StateKind.IntegerSum => IntegerSumKind,
        StateKind.DecimalSum => DecimalSumKind,
        StateKind.FloatSum => FloatSumKind,
        StateKind.Min => MinKind,
        _ => MaxKind,
    };

    /// <summary>The state a directory kind names, or null for a kind or a version this build does not know.</summary>
    internal static StateKind? Of(string kind) => kind switch
    {
        CountKind => StateKind.Count,
        IntegerSumKind => StateKind.IntegerSum,
        DecimalSumKind => StateKind.DecimalSum,
        FloatSumKind => StateKind.FloatSum,
        MinKind => StateKind.Min,
        MaxKind => StateKind.Max,
        _ => null,
    };

    /// <summary>The names of the <c>Int64</c> columns a state is persisted in; none for an extreme, kept in the value's type.</summary>
    internal static string[] Columns(StateKind state) => state switch
    {
        StateKind.Count => ["count"],
        StateKind.IntegerSum => ["low", "high"],
        StateKind.DecimalSum => ["l0", "l1", "l2", "l3", "l4"],
        StateKind.FloatSum => ["m0", "m1", "m2", "meta", "high"],
        _ => [],
    };

    /// <summary>Writes integer sums into their two columns, the low word and the high one.</summary>
    internal static void Write(ReadOnlySpan<Int128> sums, Span<long> low, Span<long> high)
    {
        Check(sums.Length, low.Length, high.Length);
        for (int i = 0; i < sums.Length; i++)
        {
            low[i] = unchecked((long)(ulong)sums[i]);
            high[i] = (long)(sums[i] >> 64);
        }
    }

    /// <summary>Reads integer sums back from their two columns.</summary>
    internal static void Read(ReadOnlySpan<long> low, ReadOnlySpan<long> high, Span<Int128> sums)
    {
        Check(sums.Length, low.Length, high.Length);
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i] = new Int128(unchecked((ulong)high[i]), unchecked((ulong)low[i]));
        }
    }

    /// <summary>Writes decimal sums into their five limb columns, least significant first.</summary>
    internal static void Write(ReadOnlySpan<WideSum> sums, Span<long> l0, Span<long> l1, Span<long> l2, Span<long> l3, Span<long> l4)
    {
        Check(sums.Length, l0.Length, l1.Length, l2.Length, l3.Length, l4.Length);
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i].GetLimbs(out ulong a, out ulong b, out ulong c, out ulong d, out ulong e);
            (l0[i], l1[i], l2[i], l3[i], l4[i]) = (unchecked((long)a), unchecked((long)b), unchecked((long)c), unchecked((long)d), unchecked((long)e));
        }
    }

    /// <summary>Reads decimal sums back from their five limb columns.</summary>
    internal static void Read(
        ReadOnlySpan<long> l0, ReadOnlySpan<long> l1, ReadOnlySpan<long> l2, ReadOnlySpan<long> l3, ReadOnlySpan<long> l4, Span<WideSum> sums)
    {
        Check(sums.Length, l0.Length, l1.Length, l2.Length, l3.Length, l4.Length);
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i] = WideSum.FromLimbs(
                unchecked((ulong)l0[i]), unchecked((ulong)l1[i]), unchecked((ulong)l2[i]), unchecked((ulong)l3[i]), unchecked((ulong)l4[i]));
        }
    }

    /// <summary>Writes float sums into their five word columns: the three bins, the meta word, the high word.</summary>
    internal static void Write(
        ReadOnlySpan<WideIndexedSum> sums, Span<long> m0, Span<long> m1, Span<long> m2, Span<long> meta, Span<long> high)
    {
        Check(sums.Length, m0.Length, m1.Length, m2.Length, meta.Length, high.Length);
        for (int i = 0; i < sums.Length; i++)
        {
            ref readonly WideIndexedSum sum = ref sums[i];
            (m0[i], m1[i], m2[i], meta[i], high[i]) = (sum.Sum.M0, sum.Sum.M1, sum.Sum.M2, sum.Sum.Meta, sum.High);
        }
    }

    /// <summary>Reads float sums back from their five word columns.</summary>
    internal static void Read(
        ReadOnlySpan<long> m0, ReadOnlySpan<long> m1, ReadOnlySpan<long> m2, ReadOnlySpan<long> meta, ReadOnlySpan<long> high, Span<WideIndexedSum> sums)
    {
        Check(sums.Length, m0.Length, m1.Length, m2.Length, meta.Length, high.Length);
        for (int i = 0; i < sums.Length; i++)
        {
            WideIndexedSum sum = default;
            sum.Sum.M0 = m0[i];
            sum.Sum.M1 = m1[i];
            sum.Sum.M2 = m2[i];
            sum.Sum.Meta = meta[i];
            sum.High = high[i];
            sums[i] = sum;
        }
    }

    private static void Check(int states, params ReadOnlySpan<int> columns)
    {
        foreach (int length in columns)
        {
            if (length != states)
            {
                throw new ArgumentException($"A column holds {length} words for {states} states: a persisted state is a word per column.");
            }
        }
    }
}
