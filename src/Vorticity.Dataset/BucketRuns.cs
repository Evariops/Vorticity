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
