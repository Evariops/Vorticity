using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Dataset;

/// <summary>
/// The runs of equal buckets of a chunk of a synopsis table, whose rows lie in the order of their buckets: where each
/// run ends, found once a chunk, where its instants' column, then its selection, scanned the buckets for them again.
/// </summary>
/// <remarks>
/// Four buckets compared with the one before each at a time, a mask of those that start a run: a chunk of runs of
/// thousands of rows a bucket compares a vector a cycle or two, against a branch a row (<see cref="EndsScalar"/>, the
/// twin the vector path is held to).
/// </remarks>
internal static class BucketRuns
{
    /// <summary>
    /// Writes into <paramref name="ends"/> where each run of equal values of <paramref name="values"/> ends, one past its
    /// last row, ascending, the last <paramref name="values"/>' length; returns how many runs. <paramref name="ends"/>
    /// holds as many as <paramref name="values"/> at least; an empty span has no run.
    /// </summary>
    internal static int Ends(ReadOnlySpan<long> values, Span<int> ends)
    {
        int count = values.Length;
        if (count == 0)
        {
            return 0;
        }

        if (ends.Length < count)
        {
            throw new ArgumentException($"{ends.Length} ends cannot hold the runs of {count} values.", nameof(ends));
        }

        int runs = 0;
        int row = 1;
        if (Vector256.IsHardwareAccelerated && count > Vector256<long>.Count)
        {
            ref long first = ref MemoryMarshal.GetReference(values);
            for (; row + Vector256<long>.Count <= count; row += Vector256<long>.Count)
            {
                Vector256<long> current = Vector256.LoadUnsafe(ref first, (nuint)row);
                Vector256<long> previous = Vector256.LoadUnsafe(ref first, (nuint)(row - 1));
                uint starts = (~Vector256.Equals(current, previous)).ExtractMostSignificantBits();
                while (starts != 0)
                {
                    ends[runs++] = row + BitOperations.TrailingZeroCount(starts);
                    starts &= starts - 1;
                }
            }
        }

        for (; row < count; row++)
        {
            if (values[row] != values[row - 1])
            {
                ends[runs++] = row;
            }
        }

        ends[runs++] = count;
        return runs;
    }

    /// <summary>
    /// Sets in <paramref name="kept"/>, a bit a row, the rows of <c>[<paramref name="start"/>, <paramref name="end"/>)</c>
    /// whose count in <paramref name="stood"/> is positive and whose state in <paramref name="states"/> is true, every
    /// state true when there are none; returns how many it set.
    /// </summary>
    /// <remarks>
    /// The rows of a bucket a table folds are kept a word of 64 at a time: four counts compared at once, and thirty-two
    /// states, where a row at a time, its bit set alone, took a fifth of a question that filters a table of 612 000 rows
    /// an hour for one host (<see cref="KeepScalar"/>, the twin the vector path is held to).
    /// </remarks>
    internal static int Keep(ReadOnlySpan<long> stood, ReadOnlySpan<byte> states, int start, int end, Span<ulong> kept)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            return KeepScalar(stood, states, start, end, kept);
        }

        int selected = 0;
        int row = start;
        int head = Math.Min(end, (start + 63) & ~63);
        if (head > row)
        {
            selected += KeepScalar(stood, states, row, head, kept);
            row = head;
        }

        ref long counts = ref MemoryMarshal.GetReference(stood);
        ref byte truths = ref MemoryMarshal.GetReference(states);
        Vector256<byte> truth = Vector256.Create(Compute.Trilean.True);
        for (; row + 64 <= end; row += 64)
        {
            ulong word = 0;
            for (int lane = 0; lane < 64; lane += Vector256<long>.Count)
            {
                Vector256<long> count = Vector256.LoadUnsafe(ref counts, (nuint)(row + lane));
                word |= (ulong)Vector256.GreaterThan(count, Vector256<long>.Zero).ExtractMostSignificantBits() << lane;
            }

            if (!states.IsEmpty)
            {
                ulong low = Vector256.Equals(Vector256.LoadUnsafe(ref truths, (nuint)row), truth).ExtractMostSignificantBits();
                ulong high = Vector256.Equals(Vector256.LoadUnsafe(ref truths, (nuint)(row + 32)), truth).ExtractMostSignificantBits();
                word &= low | (high << 32);
            }

            kept[row >> 6] |= word;
            selected += BitOperations.PopCount(word);
        }

        if (row < end)
        {
            selected += KeepScalar(stood, states, row, end, kept);
        }

        return selected;
    }

    /// <summary><see cref="Keep"/> a row at a time: what the vector path must keep.</summary>
    internal static int KeepScalar(ReadOnlySpan<long> stood, ReadOnlySpan<byte> states, int start, int end, Span<ulong> kept)
    {
        int selected = 0;
        for (int row = start; row < end; row++)
        {
            ulong bit = stood[row] > 0 ? 1UL : 0UL;
            bit &= states.IsEmpty || states[row] == Compute.Trilean.True ? 1UL : 0UL;
            kept[row >> 6] |= bit << (row & 63);
            selected += (int)bit;
        }

        return selected;
    }

    /// <summary><see cref="Ends"/> a row at a time: what the vector path must find.</summary>
    internal static int EndsScalar(ReadOnlySpan<long> values, Span<int> ends)
    {
        int count = values.Length;
        if (count == 0)
        {
            return 0;
        }

        int runs = 0;
        for (int row = 1; row < count; row++)
        {
            if (values[row] != values[row - 1])
            {
                ends[runs++] = row;
            }
        }

        ends[runs++] = count;
        return runs;
    }
}
