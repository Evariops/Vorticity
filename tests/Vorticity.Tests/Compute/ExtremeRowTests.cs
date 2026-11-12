// The row of a column's least or greatest value, every row valid: the first of the rows that hold it, for integers of
// every width, columns shorter and longer than a vector, the extreme held more than once from four rows on.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class ExtremeRowTests
{
    [Fact]
    public void TheFirstRowOfTheExtremeIsFound()
    {
        Check<sbyte>(PType.I8);
        Check<byte>(PType.U8);
        Check<short>(PType.I16);
        Check<ushort>(PType.U16);
        Check<int>(PType.I32);
        Check<uint>(PType.U32);
        Check<long>(PType.I64);
        Check<ulong>(PType.U64);
    }

    private static void Check<T>(PType ptype)
        where T : unmanaged, IComparable<T>
    {
        Random random = new Random(151 + (int)ptype);
        int width = ptype.ByteWidth();
        foreach (int rows in new[] { 1, 3, 31, 64, 100, 1_000 })
        {
            CanonicalArena arena = new CanonicalArena(64);
            VortexBuffer buffer = arena.Allocate(rows * width, 64, out Span<byte> bytes);
            random.NextBytes(bytes);
            Span<T> values = MemoryMarshal.Cast<byte, T>(bytes);

            // The extremes held twice or more, copied to rows drawn at random among the others, before and after the first
            // row that held them, neither over the other. Drawn at random, a value of 32 or 64 bits is held once.
            if (rows > 3)
            {
                (int least, int most) = Ends(values);
                T low = values[least];
                T high = values[most];
                int[] others = [.. Enumerable.Range(0, rows).Where(row => row != least && row != most)];
                random.Shuffle(others);
                int copies = Math.Min(3, others.Length / 2);
                for (int copy = 0; copy < copies; copy++)
                {
                    values[others[copy]] = low;
                    values[others[copies + copy]] = high;
                }

                Assert.True(Held(values, low) >= 2 && Held(values, high) >= 2);
            }

            int node = arena.AddPrimitive(new DTypeArena().Primitive(ptype, Nullability.NonNullable), rows, Validity.NonNullable, ptype, buffer);
            foreach (bool wantMin in new[] { true, false })
            {
                int expected = 0;
                for (int row = 1; row < rows; row++)
                {
                    int order = values[row].CompareTo(values[expected]);
                    if (wantMin ? order < 0 : order > 0)
                    {
                        expected = row;
                    }
                }

                Assert.True(Extremes.TryFind(arena, node, default, listed: false, wantMin, out int found));
                Assert.Equal(expected, found);
            }
        }
    }

    /// <summary>The first rows of the least and the greatest values.</summary>
    private static (int Least, int Most) Ends<T>(ReadOnlySpan<T> values)
        where T : unmanaged, IComparable<T>
    {
        int least = 0;
        int most = 0;
        for (int row = 1; row < values.Length; row++)
        {
            least = values[row].CompareTo(values[least]) < 0 ? row : least;
            most = values[row].CompareTo(values[most]) > 0 ? row : most;
        }

        return (least, most);
    }

    /// <summary>The rows that hold <paramref name="value"/>.</summary>
    private static int Held<T>(ReadOnlySpan<T> values, T value)
        where T : unmanaged, IComparable<T>
    {
        int held = 0;
        foreach (T each in values)
        {
            held += each.CompareTo(value) == 0 ? 1 : 0;
        }

        return held;
    }
}
