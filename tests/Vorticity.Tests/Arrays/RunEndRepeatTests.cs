// A run-end node's values expanded over their rows: every run's rows its value, for runs of one row to past a vector's
// length, values of every width a vector holds; an end that goes back is refused before a row is written.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class RunEndRepeatTests
{
    [Fact]
    public void EveryRunsRowsAreItsValue()
    {
        Check<byte>();
        Check<ushort>();
        Check<uint>();
        Check<ulong>();
    }

    // Runs that end before the rows do: the rows past the last end keep what they held, every vector store kept within the
    // runs, whatever the width and wherever the last end falls against a vector's length.
    [Fact]
    public void TheRowsPastTheLastRunKeepWhatTheyHeld()
    {
        Untouched<byte>();
        Untouched<ushort>();
        Untouched<uint>();
        Untouched<ulong>();
    }

    [Fact]
    public void AnEndThatGoesBackIsRefused()
    {
        uint[] ends = [10, 40, 30, 64];
        byte[] values = new byte[4 * sizeof(ulong)];
        byte[] rows = new byte[64 * sizeof(ulong)];
        Assert.ThrowsAny<ArgumentException>(() => CanonicalArena.Repeat<ulong>(ends, values, rows));
    }

    private static void Untouched<T>()
        where T : unmanaged
    {
        int width = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        foreach (int covered in new[] { 1, 5, 33, 64, 65, 200 })
        {
            uint[] ends = covered < 4 ? [(uint)covered] : [(uint)(covered / 4), (uint)(covered / 2), (uint)covered];
            byte[] values = new byte[ends.Length * width];
            values.AsSpan().Fill(0x11);
            byte[] rows = new byte[(covered + 40) * width];
            rows.AsSpan().Fill(0xEE);
            CanonicalArena.Repeat<T>(ends, values, rows);
            Assert.All(rows.AsSpan(0, covered * width).ToArray(), b => Assert.Equal(0x11, b));
            Assert.All(rows.AsSpan(covered * width).ToArray(), b => Assert.Equal(0xEE, b));
        }
    }

    private static void Check<T>()
        where T : unmanaged
    {
        Random random = new Random(107 + System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        foreach (int rows in new[] { 1, 3, 31, 200, 1_000 })
        {
            List<uint> ends = [];
            int at = 0;
            while (at < rows)
            {
                at = Math.Min(rows, at + random.Next(1, 41));
                ends.Add((uint)at);
            }

            byte[] values = new byte[ends.Count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
            random.NextBytes(values);
            byte[] expanded = new byte[rows * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
            random.NextBytes(expanded);
            CanonicalArena.Repeat<T>([.. ends], values, expanded);

            ReadOnlySpan<T> value = MemoryMarshal.Cast<byte, T>(values);
            ReadOnlySpan<T> row = MemoryMarshal.Cast<byte, T>(expanded);
            int start = 0;
            for (int run = 0; run < ends.Count; run++)
            {
                for (int r = start; r < (int)ends[run]; r++)
                {
                    Assert.Equal(value[run], row[r]);
                }

                start = (int)ends[run];
            }
        }
    }
}
