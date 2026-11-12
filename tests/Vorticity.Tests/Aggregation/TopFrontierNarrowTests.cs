// A batch narrowed to the rows that reach a top's frontier, against each row weighed alone: a group key's frontier over
// every integer width, signed and unsigned, and a column's extreme over every signed width, from the least and from the
// greatest, the frontier at either end of the type and inside it, and for the extreme past the type's range too. Two
// hundred rows make three whole words, compared over vectors, and a part word, a row at a time.
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class TopFrontierNarrowTests
{
    private const int Rows = 200;

    private static readonly DTypeArena Types = new DTypeArena();

    [Fact]
    public void AKeysFrontierKeepsTheRowsThatReachIt()
    {
        Keys<long>(PType.I64, VortexType.Int64);
        Keys<int>(PType.I32, VortexType.Int32);
        Keys<short>(PType.I16, VortexType.Int16);
        Keys<sbyte>(PType.I8, VortexType.Int8);
        Keys<ulong>(PType.U64, VortexType.UInt64);
        Keys<uint>(PType.U32, VortexType.UInt32);
        Keys<ushort>(PType.U16, VortexType.UInt16);
        Keys<byte>(PType.U8, VortexType.UInt8);
    }

    [Fact]
    public void AnExtremesFrontierKeepsTheRowsThatReachIt()
    {
        Extremes<long>(PType.I64, VortexType.Int64);
        Extremes<int>(PType.I32, VortexType.Int32);
        Extremes<short>(PType.I16, VortexType.Int16);
        Extremes<sbyte>(PType.I8, VortexType.Int8);
    }

    private static void Keys<T>(PType ptype, VortexType type)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        T[] values = Draw<T>(ptype);
        (CanonicalArena arena, int node) = Column(values, ptype);
        foreach (T edge in new[] { T.MinValue, T.MaxValue, values[17], values[Rows - 3] })
        {
            foreach (bool descending in new[] { false, true })
            {
                FixedKeys<T> keys = new FixedKeys<T>(Shape(type), sorted: false);
                (CanonicalArena first, int one) = Column(new[] { edge }, ptype);
                int[] groups = new int[1];
                keys.Assign(first, [one], 1, [], groups, new GroupRanges());
                ulong[] narrowed = new ulong[(Rows + 63) >> 6];
                Assert.True(keys.Narrow(arena, [node], Rows, [], groups[0], descending, narrowed));
                for (int row = 0; row < Rows; row++)
                {
                    bool reached = descending ? values[row] >= edge : values[row] <= edge;
                    Assert.True(reached == ((narrowed[row >> 6] >> (row & 63) & 1) != 0), $"{typeof(T).Name} row {row}, edge {edge}, descending {descending}");
                }
            }
        }
    }

    private static void Extremes<T>(PType ptype, VortexType type)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        T[] values = Draw<T>(ptype);
        (CanonicalArena arena, int node) = Column(values, ptype);
        long least = long.CreateTruncating(T.MinValue);
        long most = long.CreateTruncating(T.MaxValue);
        long[] bounds = [least, most, long.CreateTruncating(values[17]), long.CreateTruncating(values[Rows - 3])];
        if (most < long.MaxValue)
        {
            bounds = [.. bounds, least - 1, most + 1, least - 1000, most + 1000];
        }

        foreach (long bound in bounds)
        {
            foreach (bool descending in new[] { false, true })
            {
                // From the greatest, the edge is the complement of the least value a row must reach.
                long edge = descending ? ~bound : bound;
                ulong[] narrowed = new ulong[(Rows + 63) >> 6];
                Assert.True(ValueFrontier.Narrow(arena, node, Shape(type), Rows, [], edge, descending, narrowed));
                for (int row = 0; row < Rows; row++)
                {
                    long value = long.CreateTruncating(values[row]);
                    bool reached = descending ? value >= bound : value <= bound;
                    Assert.True(reached == ((narrowed[row >> 6] >> (row & 63) & 1) != 0), $"{typeof(T).Name} row {row}, bound {bound}, descending {descending}");
                }
            }
        }
    }

    private static T[] Draw<T>(PType ptype)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        Random random = new Random((int)ptype + 31);
        byte[] bytes = new byte[Rows * Marshal.SizeOf<T>()];
        random.NextBytes(bytes);
        T[] values = MemoryMarshal.Cast<byte, T>(bytes).ToArray();
        values[5] = T.MinValue;
        values[70] = T.MaxValue;
        values[140] = values[17];
        return values;
    }

    private static (CanonicalArena Arena, int Node) Column<T>(T[] values, PType ptype)
        where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        CanonicalArena arena = new CanonicalArena(values.Length * size + 4096);
        VortexBuffer buffer = arena.Allocate(values.Length * size, size, out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        int node = arena.AddPrimitive(Types.Primitive(ptype, Nullability.NonNullable), values.Length, Validity.NonNullable, ptype, buffer);
        return (arena, node);
    }

    private static ColumnShape Shape(VortexType type) =>
        new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, []));
}
