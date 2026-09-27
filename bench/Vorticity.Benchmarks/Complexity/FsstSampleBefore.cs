// The FSST trainer's sampler as it walked from every random row to the next non-empty one: the
// original that `FsstSampleBenchmarks` measures the library against.
using System;

using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>The sampler of the FSST trainer, each draw walking to its row.</summary>
internal static class FsstSampleBefore
{
    private const int SampleTarget = 1 << 14;

    private const int SampleLine = 512;

    internal static void MakeSample(
        ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, Span<FsstSymbols.Line> into, out int drawn,
        out bool sampled)
    {
        long total = 0;
        int nonEmpty = 0;
        int rows = lengths.Length;
        for (int i = 0; i < rows; i++)
        {
            total += lengths[i];
            if (lengths[i] > 0)
            {
                nonEmpty++;
            }
        }

        drawn = 0;
        if (nonEmpty == 0)
        {
            sampled = false;
            return;
        }

        if (total < SampleTarget)
        {
            sampled = false;
            for (int i = 0; i < rows; i++)
            {
                if (lengths[i] > 0)
                {
                    into[drawn++] = new FsstSymbols.Line(starts[i], lengths[i]);
                }
            }

            return;
        }

        sampled = true;
        ulong random = Hash(4637947);
        long bytes = 0;
        while (bytes < SampleTarget)
        {
            random = Hash(random);
            int start = (int)(random % (ulong)rows);

            int found = -1;
            for (int offset = 0; offset < rows; offset++)
            {
                int candidate = start + offset;
                if (candidate >= rows)
                {
                    candidate -= rows;
                }

                if (lengths[candidate] > 0)
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                break;
            }

            int lineLength = lengths[found];
            int chunks = 1 + ((lineLength - 1) / SampleLine);
            random = Hash(random);
            int chunk = SampleLine * (int)(random % (ulong)chunks);
            int length = Math.Min(SampleLine, lineLength - chunk);
            into[drawn++] = new FsstSymbols.Line(starts[found] + chunk, length);
            bytes += length;
        }
    }

    private static ulong Hash(ulong value) => (value * 2971215073UL) ^ (value >> 15);
}
