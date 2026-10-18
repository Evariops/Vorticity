using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// An exact float sum that takes more values than <see cref="IndexedSum"/>: some 35 trillion rather than
/// 69 billion, in eight bytes more. It is an indexed sum whose bins are settled every
/// <see cref="Epoch"/> values and when two sums merge: each bin's multiples of 2^56 move into a high part
/// of its own, which leaves the bin within 2^55 of zero. A settling moves value within a bin, never
/// between two, so it changes no bit of the answer, whenever it happens. A high part moves up with its
/// bin when the top moves, and falls out with it, as the bin does.
/// </summary>
/// <remarks>
/// <para>
/// The sum inside holds the values since the last settling, fewer than an epoch between two folds: a
/// fold of at most an epoch takes it to fewer than 2^36, which it takes, and its bins to within
/// 2^55 + 2^62 of zero, which a long holds. The epochs settled are counted beside the high parts.
/// </para>
/// <para>
/// The engine takes it where a source does not prove its rows within the narrow sum's 2^36 values, and
/// for states kept and merged past a scan. Forty bytes a group: a million groups of them fold some 15 %
/// slower than of the narrow sum's 32, whose states fit twice as often in a line of cache
/// (vortex-queries --micro floatsum).
/// </para>
/// </remarks>
internal struct WideIndexedSum : IFloatSumState<WideIndexedSum>
{
    /// <summary>The values between two settlings, at most.</summary>
    internal const long Epoch = 1L << 35;

    /// <summary>
    /// The values a sum takes: the epochs a high word counts, 1,023, and one more short of its end. Each
    /// bin's exact sum stays within 2^71 units, its high part within 2^16, and their total within 2^126.
    /// </summary>
    internal const long Capacity = (1L << 45) - 1;

    private const int SettledShift = 56;
    private const long SettleHalf = 1L << (SettledShift - 1);
    private const int HighBits = 18;
    private const long HighField = (1L << HighBits) - 1;
    private const int EpochShift = 3 * HighBits;
    private const long HighParts = (1L << EpochShift) - 1;
    private const long MaxEpochs = (1L << (64 - EpochShift)) - 1;

    /// <summary>The values since the last settling, and the bins below their high parts.</summary>
    internal IndexedSum Sum;

    // The bins' high parts, 18 bits each, the top bin's lowest, and the epochs settled above bit 54.
    internal long High;

    public readonly long Count => Sum.Count + (Epochs * Epoch);

    public readonly double Value =>
        (High & HighParts) == 0 ? Sum.Value : Sum.ValueWith(HighOf(High, 0), HighOf(High, 1), HighOf(High, 2));

    private readonly long Epochs => (long)((ulong)High >> EpochShift);

    public void Add(double value)
    {
        int top = Sum.Top;
        Sum.Add(value);
        Settled(top);
    }

    public void AddWeighted(double value, long count)
    {
        // An epoch at a time: each part times its weight stays within 2^61, and the sum inside below 2^36.
        while (count > 0)
        {
            long weight = Math.Min(count, Epoch);
            int top = Sum.Top;
            Sum.AddWeighted(value, weight);
            Settled(top);
            count -= weight;
        }
    }

    /// <remarks>A span holds fewer than 2^31 values, so the sum inside stays below 2^36.</remarks>
    public void AddSpan(ReadOnlySpan<double> values)
    {
        int top = Sum.Top;
        Sum.AddSpan(values);
        Settled(top);
    }

    public void Merge(in WideIndexedSum other)
    {
        if (Count + other.Count > Capacity)
        {
            throw Outgrown();
        }

        // The high parts lined up first, as the sums inside line their bins up: those of the lower top move.
        WideIndexedSum theirs = other;
        int mine = Sum.Top;
        int top = theirs.Sum.Top;
        if (mine != 0 && top > mine)
        {
            Move(top - mine);
        }
        else if (top != 0 && mine > top)
        {
            theirs.Move(mine - top);
        }

        // Each sum inside holds fewer than an epoch of values since its settling, its bins within
        // 2^55 + 2^61: their sum within 2^63, which the merge takes and the settling after it brings back.
        Sum.Merge(in theirs.Sum);
        High = Highs(
            HighOf(High, 0) + HighOf(theirs.High, 0),
            HighOf(High, 1) + HighOf(theirs.High, 1),
            HighOf(High, 2) + HighOf(theirs.High, 2),
            Epochs + theirs.Epochs);
        if (Sum.Count >= Epoch)
        {
            Settle();
        }
    }

    /// <summary>After a fold: the high parts moved up as far as the top moved, and an epoch completed settled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Settled(int top)
    {
        int now = Sum.Top;
        if (now != top && top != 0 && (High & HighParts) != 0)
        {
            Move(now - top);
        }

        if (Sum.Count >= Epoch)
        {
            Settle();
        }
    }

    /// <summary>Moves the high parts up <paramref name="steps"/> bins, as the bins moved; those left below the three fall out.</summary>
    private void Move(int steps)
    {
        long parts = steps >= 3 ? 0 : ((High & HighParts) << (HighBits * steps)) & HighParts;
        High = (High & ~HighParts) | parts;
    }

    /// <summary>
    /// Moves each bin's multiples of 2^56 into its high part and an epoch of the count into the epochs:
    /// the bins back within 2^55 of zero, the sum inside below an epoch, the total and every bit of the
    /// answer the same.
    /// </summary>
    private void Settle()
    {
        long epochs = Epochs + 1;
        if (epochs > MaxEpochs)
        {
            throw Outgrown();
        }

        long first = (Sum.M0 + SettleHalf) >> SettledShift;
        long second = (Sum.M1 + SettleHalf) >> SettledShift;
        long third = (Sum.M2 + SettleHalf) >> SettledShift;
        Sum.M0 -= first << SettledShift;
        Sum.M1 -= second << SettledShift;
        Sum.M2 -= third << SettledShift;
        Sum.Meta -= Epoch;
        High = Highs(HighOf(High, 0) + first, HighOf(High, 1) + second, HighOf(High, 2) + third, epochs);
    }

    /// <summary>Bin <paramref name="bin"/>'s high part out of <paramref name="high"/>, its sign extended.</summary>
    private static long HighOf(long high, int bin) => (high << (64 - (HighBits * (bin + 1)))) >> (64 - HighBits);

    /// <summary>The high word of three high parts and a count of epochs.</summary>
    private static long Highs(long first, long second, long third, long epochs) =>
        (first & HighField) | ((second & HighField) << HighBits) | ((third & HighField) << (2 * HighBits)) | (epochs << EpochShift);

    private static OverflowException Outgrown() =>
        new OverflowException($"A float sum takes {Capacity} values at most, in one group or one scan: past them its bins' total would lose its exactness.");
}

/// <summary>
/// A sum of floating-point values in a <see cref="WideIndexedSum"/>, for a source that does not prove its
/// rows within what an <see cref="IndexedSum"/> takes: the same bits as the narrow sum's.
/// </summary>
internal readonly struct WideIndexedFloatSum<TValue> : IValueOp<TValue, WideIndexedSum>
    where TValue : unmanaged, INumberBase<TValue>
{
    public WideIndexedSum Seed() => default;

    public void Add(ref WideIndexedSum state, TValue value) => state.Add(double.CreateTruncating(value));

    public void AddSelected(ref WideIndexedSum state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref WideIndexedSum state, TValue value, long count) => state.AddWeighted(double.CreateTruncating(value), count);

    [SkipLocalsInit]
    public void AddSpan(ref WideIndexedSum state, ReadOnlySpan<TValue> values)
    {
        if (typeof(TValue) == typeof(double))
        {
            state.AddSpan(System.Runtime.InteropServices.MemoryMarshal.Cast<TValue, double>(values));
            return;
        }

        // Widened a chunk at a time, exactly: a float or a half is a double.
        Span<double> widened = stackalloc double[IndexedSum.Chunk];
        for (int start = 0; start < values.Length; start += IndexedSum.Chunk)
        {
            ReadOnlySpan<TValue> chunk = values.Slice(start, Math.Min(IndexedSum.Chunk, values.Length - start));
            for (int i = 0; i < chunk.Length; i++)
            {
                widened[i] = double.CreateTruncating(chunk[i]);
            }

            state.AddSpan(widened[..chunk.Length]);
        }
    }

    public void Merge(ref WideIndexedSum into, in WideIndexedSum other) => into.Merge(in other);

    public double? Mean(in WideIndexedSum state) => state.Count == 0 ? null : state.Value / state.Count;

    /// <remarks>
    /// The rows left out are left out, not counted and taken back as the narrow sum does: a settling
    /// between the two would take back values of an epoch already settled.
    /// </remarks>
    [SkipLocalsInit]
    public void AddWords(ref WideIndexedSum state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        int count = 0;
        for (int w = 0; w < words.Length; w++)
        {
            ulong word = words[w];
            while (word != 0)
            {
                selected[count++] = block[(w << 6) + BitOperations.TrailingZeroCount(word)];
                word &= word - 1;
            }
        }

        AddSpan(ref state, selected[..count]);
    }
}
