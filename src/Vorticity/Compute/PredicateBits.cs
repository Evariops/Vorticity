using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Compute;

/// <summary>
/// An encoding's answer to a predicate, a bit a row: <see cref="Truth"/> the rows it selects, and, when some row is
/// unknown, <see cref="Known"/> the rows that are not. Bit <c>i % 64</c> of word <c>i / 64</c> is row <c>i</c>, and the
/// bits past the rows are clear in both.
/// </summary>
/// <remarks>
/// Two bitmaps rather than a state a row: an encoding that answers a block of rows at a time writes a word where a
/// state a row would take a byte a row, and the scan's answer column is these two bitmaps, a boolean and its validity,
/// so nothing is spread out to be packed again. An unknown row's truth bit is clear.
/// </remarks>
internal ref struct PredicateBits
{
    /// <summary>Room for the answer over <paramref name="rows"/> rows: <c>ceil(rows / 64)</c> words each.</summary>
    internal PredicateBits(Span<ulong> truth, Span<ulong> known, int rows)
    {
        int words = WordsOf(rows);
        Truth = truth[..words];
        Known = known[..words];
        Rows = rows;
    }

    /// <summary>The rows selected.</summary>
    internal Span<ulong> Truth { get; }

    /// <summary>The rows whose answer is known, written when <see cref="Unknown"/> is set; meaningless otherwise.</summary>
    internal Span<ulong> Known { get; }

    /// <summary>The rows answered.</summary>
    internal int Rows { get; }

    /// <summary>Whether some row is unknown, which <see cref="Known"/> then tells.</summary>
    internal bool Unknown { get; private set; }

    /// <summary>The words a bit a row takes over <paramref name="rows"/> rows.</summary>
    internal static int WordsOf(int rows) => (rows + 63) >> 6;

    /// <summary>Every row selected, or none, and every row known.</summary>
    internal void Fill(bool selected)
    {
        Truth.Fill(selected ? ulong.MaxValue : 0);
        ClearTail(Truth);
        Unknown = false;
    }

    /// <summary>Every row unknown: a null constant's answer.</summary>
    internal void FillUnknown()
    {
        Truth.Clear();
        Known.Clear();
        Unknown = Rows > 0;
    }

    /// <summary>Rows <c>[from, from + count)</c> selected, the others left as they are.</summary>
    internal readonly void SetRange(int from, int count) =>
        BitmapKernels.SetRange(MemoryMarshal.AsBytes(Truth), from, count);

    /// <summary>The selection turned over: the rows selected become the rows not, unknown rows staying unselected.</summary>
    internal void Invert()
    {
        Span<ulong> truth = Truth;
        for (int w = 0; w < truth.Length; w++)
        {
            truth[w] = ~truth[w];
        }

        ClearTail(truth);
        if (Unknown)
        {
            AndKnown();
        }
    }

    /// <summary>The truth's bits past the rows cleared, after a kernel that answers whole blocks wrote them.</summary>
    internal readonly void Trim() => ClearTail(Truth);

    /// <summary>Row <paramref name="row"/> selected or not.</summary>
    internal readonly void Set(int row, bool selected)
    {
        ulong bit = 1UL << (row & 63);
        ref ulong word = ref Truth[row >> 6];
        word = selected ? word | bit : word & ~bit;
    }

    /// <summary>
    /// The rows of <paramref name="validity"/>, a null row unknown and unselected: nothing to do for a column without
    /// nulls.
    /// </summary>
    internal void ApplyValidity(ValidityMask validity)
    {
        if (validity.AllValid)
        {
            return;
        }

        if (validity.AllInvalid)
        {
            Truth.Clear();
            Known.Clear();
            Unknown = Rows > 0;
            return;
        }

        Span<byte> known = MemoryMarshal.AsBytes(Known);
        known.Clear();
        BitmapKernels.CopyRange(validity.Bits, validity.BitOffset, known, 0, Rows);
        AndKnown();
    }

    /// <summary>The rows <paramref name="known"/> says are known, a row of no other unknown and unselected.</summary>
    internal void ApplyKnown(ReadOnlySpan<ulong> known)
    {
        known[..Known.Length].CopyTo(Known);
        AndKnown();
    }

    /// <summary>
    /// The answer of a state a row (<see cref="Trilean"/>), for an encoding that answers a value at a time.
    /// </summary>
    internal void FromStates(ReadOnlySpan<byte> states)
    {
        states = states[..Rows];
        Trilean.ToWords(states, Trilean.True, equal: true, Truth);
        int known = Trilean.ToWords(states, Trilean.Unknown, equal: false, Known);
        Unknown = known < Rows;
    }

    /// <summary>The truth cut to the known rows, and whether some row is unknown.</summary>
    private void AndKnown()
    {
        Span<ulong> truth = Truth;
        ReadOnlySpan<ulong> known = Known;
        int count = 0;
        for (int w = 0; w < truth.Length; w++)
        {
            truth[w] &= known[w];
            count += BitOperations.PopCount(known[w]);
        }

        Unknown = count < Rows;
    }

    /// <summary>The bits of the last word past the rows cleared.</summary>
    private readonly void ClearTail(Span<ulong> words)
    {
        int used = Rows & 63;
        if (used != 0 && words.Length > 0)
        {
            words[^1] &= (1UL << used) - 1;
        }
    }
}
