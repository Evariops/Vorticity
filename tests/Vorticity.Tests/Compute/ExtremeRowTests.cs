// The row of a column's least or greatest value, every row valid: the first of the rows that hold it, for integers of
// every width, columns shorter and longer than a vector, the extreme held more than once.
using System;
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

            // The extremes held twice or more, at places drawn at random.
            for (int copy = 0; copy < 3 && rows > 2; copy++)
            {
                values[random.Next(rows)] = values[random.Next(rows)];
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
}
