using System;

namespace Vorticity.Aggregating;

/// <summary>How a measure's states are kept: an exact signed or unsigned integer sum, or a float sum.</summary>
internal enum MeasureKind : byte
{
    Signed,
    Unsigned,
    Float,
}

/// <summary>
/// A group's states as words, which a synopsis writes and reads back the same on every architecture: its rows, then each
/// measure's words where <see cref="MeasureLayout"/> lays them out. Two groups' words merge into those of their rows
/// together, in any order: integer sums are exact, float sums are <see cref="WideIndexedSum"/>s, and a float's extremes
/// leave NaN out as the engine's Min and Max do.
/// </summary>
internal static class StateWords
{
    private static readonly long NoFloat = BitConverter.DoubleToInt64Bits(double.NaN);

    // A measure's words, from its first.
    private const int Values = MeasureLayout.Values;
    private const int Sum = MeasureLayout.Sum;
    private const int FloatLeast = MeasureLayout.FloatLeast;
    private const int FloatGreatest = MeasureLayout.FloatGreatest;
    private const int IntegerLeast = MeasureLayout.IntegerLeast;
    private const int IntegerGreatest = MeasureLayout.IntegerGreatest;

    /// <summary>The words a group's states take.</summary>
    internal static int Count(ReadOnlySpan<MeasureKind> kinds)
    {
        int words = 1;
        foreach (MeasureKind kind in kinds)
        {
            words += MeasureLayout.Words(kind == MeasureKind.Float);
        }

        return words;
    }

    /// <summary>The states of a group of no row.</summary>
    internal static void Clear(Span<long> words, ReadOnlySpan<MeasureKind> kinds)
    {
        words.Clear();
        int at = 1;
        foreach (MeasureKind kind in kinds)
        {
            if (kind == MeasureKind.Float)
            {
                words[at + FloatLeast] = NoFloat;
                words[at + FloatGreatest] = NoFloat;
            }

            at += MeasureLayout.Words(kind == MeasureKind.Float);
        }
    }

    /// <summary>Merges the states <paramref name="from"/> into <paramref name="into"/>, as two groups' rows together.</summary>
    internal static void Merge(Span<long> into, ReadOnlySpan<long> from, ReadOnlySpan<MeasureKind> kinds)
    {
        into[0] += from[0];
        int at = 1;
        foreach (MeasureKind kind in kinds)
        {
            if (kind == MeasureKind.Float)
            {
                into[at + Values] += from[at + Values];
                WideIndexedSum sum = SumOf(into, at + Sum);
                WideIndexedSum other = SumOf(from, at + Sum);
                sum.Merge(in other);
                Write(in sum, into, at + Sum);
                double low = BitConverter.Int64BitsToDouble(from[at + FloatLeast]);
                double high = BitConverter.Int64BitsToDouble(from[at + FloatGreatest]);
                double intoLow = BitConverter.Int64BitsToDouble(into[at + FloatLeast]);
                double intoHigh = BitConverter.Int64BitsToDouble(into[at + FloatGreatest]);
                into[at + FloatLeast] = !double.IsNaN(low) && (double.IsNaN(intoLow) || low < intoLow) ? from[at + FloatLeast] : into[at + FloatLeast];
                into[at + FloatGreatest] = !double.IsNaN(high) && (double.IsNaN(intoHigh) || high > intoHigh) ? from[at + FloatGreatest] : into[at + FloatGreatest];
                at += MeasureLayout.Words(floats: true);
                continue;
            }

            if (from[at + Values] != 0)
            {
                bool none = into[at + Values] == 0;
                into[at + Values] += from[at + Values];
                Write(IntegerOf(into, at + Sum) + IntegerOf(from, at + Sum), into, at + Sum);
                bool unsigned = kind == MeasureKind.Unsigned;
                long least = from[at + IntegerLeast];
                long greatest = from[at + IntegerGreatest];
                into[at + IntegerLeast] = none || (unsigned ? (ulong)least < (ulong)into[at + IntegerLeast] : least < into[at + IntegerLeast])
                    ? least
                    : into[at + IntegerLeast];
                into[at + IntegerGreatest] = none || (unsigned ? (ulong)greatest > (ulong)into[at + IntegerGreatest] : greatest > into[at + IntegerGreatest])
                    ? greatest
                    : into[at + IntegerGreatest];
            }

            at += MeasureLayout.Words(floats: false);
        }
    }

    /// <summary>The sum of floats whose words start at <paramref name="at"/>, in <see cref="MeasureLayout.SumWords"/>'s order.</summary>
    internal static WideIndexedSum SumOf(ReadOnlySpan<long> words, int at)
    {
        WideIndexedSum sum = default;
        sum.Sum.M0 = words[at];
        sum.Sum.M1 = words[at + 1];
        sum.Sum.M2 = words[at + 2];
        sum.Sum.Meta = words[at + 3];
        sum.High = words[at + 4];
        return sum;
    }

    /// <summary>Writes the words of a sum of floats from <paramref name="at"/>, in <see cref="MeasureLayout.SumWords"/>'s order.</summary>
    internal static void Write(in WideIndexedSum sum, Span<long> words, int at) =>
        (words[at], words[at + 1], words[at + 2], words[at + 3], words[at + 4]) = (sum.Sum.M0, sum.Sum.M1, sum.Sum.M2, sum.Sum.Meta, sum.High);

    /// <summary>The sum of integers whose words, low then high, start at <paramref name="at"/>.</summary>
    internal static Int128 IntegerOf(ReadOnlySpan<long> words, int at) => new Int128(unchecked((ulong)words[at + 1]), unchecked((ulong)words[at]));

    /// <summary>Writes the words of a sum of integers from <paramref name="at"/>, low then high.</summary>
    internal static void Write(Int128 sum, Span<long> words, int at) => (words[at], words[at + 1]) = (unchecked((long)(ulong)sum), (long)(sum >> 64));
}
