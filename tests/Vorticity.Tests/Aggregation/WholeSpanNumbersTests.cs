// A key numbered whole once every value of its span is met: each row's group is its value less the least, for every
// integer width, signed and unsigned, over vectors and a row at a time; a value past the span, the type's extremes among
// them, is left to the lookup, which gives it a group of its own past the span.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class WholeSpanNumbersTests
{
    private const int Rows = 1_000;

    private static readonly DTypeArena Types = new DTypeArena();

    [Fact]
    public void EachRowsGroupIsItsValueLessTheLeast()
    {
        Check<sbyte>(PType.I8, VortexType.Int8, -100, 100);
        Check<byte>(PType.U8, VortexType.UInt8, 20, 230);
        Check<short>(PType.I16, VortexType.Int16, -1_000, 3_000);
        Check<ushort>(PType.U16, VortexType.UInt16, 40_000, 44_000);
        Check<int>(PType.I32, VortexType.Int32, -1_000, 3_000);
        Check<uint>(PType.U32, VortexType.UInt32, 3_000_000_000, 3_000_004_000);
        Check<long>(PType.I64, VortexType.Int64, -1_000, 3_000);
        Check<ulong>(PType.U64, VortexType.UInt64, 5_000, 9_000);
    }

    // The pass itself, which leaves a row to the lookup with -1: a value it weighs wrongly the lookup would still group.
    [Fact]
    public void ThePassNumbersTheRowsWithinTheSpanAndLeavesTheOthers()
    {
        Pass<sbyte>(-100, 100);
        Pass<byte>(20, 230);
        Pass<short>(-1_000, 3_000);
        Pass<ushort>(40_000, 44_000);
        Pass<int>(-1_000, 3_000);
        Pass<uint>(3_000_000_000, 3_000_004_000);
        Pass<long>(-1_000, 3_000);
        Pass<ulong>(5_000, 9_000);
    }

    // A span not numbered whole, of one page: each row's group gathered from the page at its number, -1 past the span
    // or where the page holds none yet.
    [Fact]
    public void TheGatheredPassReadsEachRowsGroupFromThePage()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            return;
        }

        Gather<sbyte>(-100, 100);
        Gather<byte>(20, 230);
        Gather<short>(-1_000, 3_000);
        Gather<ushort>(40_000, 44_000);
        Gather<int>(-1_000, 3_000);
        Gather<uint>(3_000_000_000, 3_000_004_000);
        Gather<long>(-1_000, 3_000);
        Gather<ulong>(5_000, 9_000);
    }

    private static void Gather<T>(long min, long max)
        where T : unmanaged, IEquatable<T>, IComparable<T>, IBinaryInteger<T>, IMinMaxValue<T>
    {
        int span = (int)(max - min + 1);
        Random random = new Random(span + 1);
        int[] page = new int[span];
        for (int n = 0; n < span; n++)
        {
            page[n] = random.Next(10) == 0 ? -1 : random.Next(1_000_000);
        }

        T[] outside = [T.MinValue, T.MaxValue, T.CreateTruncating(min - 1), T.CreateTruncating(max + 1)];
        T[] values = new T[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.Next(8) == 0 ? outside[random.Next(outside.Length)] : T.CreateTruncating(min + random.Next(span));
        }

        int[] groups = new int[Rows];
        int done = FixedKeys<T>.Gathered(values, groups, ref page[0], min, (ulong)span, out int missed);
        Assert.Equal(Rows & ~7, done);
        bool anyLeft = false;
        for (int i = 0; i < done; i++)
        {
            long value = long.CreateTruncating(values[i]);
            bool within = typeof(T) == typeof(ulong) ? (ulong)value - (ulong)min < (ulong)span : value >= min && value <= max;
            int expected = within ? page[value - min] : -1;
            anyLeft |= expected < 0;
            Assert.True(expected == groups[i], $"{typeof(T).Name} row {i}: {values[i]} gathered {groups[i]}, not {expected}");
        }

        Assert.Equal(anyLeft, missed < 0);

        // Every row within the span and met: none left.
        page.AsSpan().Fill(7);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = T.CreateTruncating(min + random.Next(span));
        }

        FixedKeys<T>.Gathered(values, groups, ref page[0], min, (ulong)span, out missed);
        Assert.Equal(0, missed);
    }

    private static void Pass<T>(long min, long max)
        where T : unmanaged, IEquatable<T>, IComparable<T>, IBinaryInteger<T>, IMinMaxValue<T>
    {
        int span = (int)(max - min + 1);
        Random random = new Random(span);
        T[] outside = [T.MinValue, T.MaxValue, T.CreateTruncating(min - 1), T.CreateTruncating(max + 1)];
        T[] values = new T[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.Next(8) == 0 ? outside[random.Next(outside.Length)] : T.CreateTruncating(min + random.Next(span));
        }

        int[] groups = new int[Rows];
        int left = FixedKeys<T>.Numbers(values, groups, min, (ulong)span);
        Assert.True(left < 0);
        for (int i = 0; i < Rows; i++)
        {
            long value = long.CreateTruncating(values[i]);
            bool within = typeof(T) == typeof(ulong) ? (ulong)value - (ulong)min < (ulong)span : value >= min && value <= max;
            Assert.True((within ? value - min : -1) == groups[i], $"{typeof(T).Name} row {i}: {values[i]} numbered {groups[i]}");
        }

        // Every row within the span: none left.
        for (int i = 0; i < Rows; i++)
        {
            values[i] = T.CreateTruncating(min + random.Next(span));
        }

        Assert.True(FixedKeys<T>.Numbers(values, groups, min, (ulong)span) >= 0);
    }

    private static void Check<T>(PType ptype, VortexType type, long min, long max)
        where T : unmanaged, IEquatable<T>, IComparable<T>, IBinaryInteger<T>, IMinMaxValue<T>
    {
        FixedKeys<T> keys = new FixedKeys<T>(new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, [])), sorted: false, new KeyBounds(min, max));
        Assert.True(keys.NumberWhole(groupBytes: 16));
        int span = (int)(max - min + 1);
        T[] all = new T[span];
        for (int i = 0; i < span; i++)
        {
            all[i] = T.CreateTruncating(min + i);
        }

        Assign(keys, all, ptype);

        // Most rows within the span; one in eight past it, at either end of the type or just outside the span.
        Random random = new Random((int)ptype + 77);
        T[] values = new T[Rows];
        T[] outside = [T.MinValue, T.MaxValue, T.CreateTruncating(min - 1), T.CreateTruncating(max + 1)];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.Next(8) == 0 ? outside[random.Next(outside.Length)] : T.CreateTruncating(min + random.Next(span));
        }

        // A chunk after one that made groups goes through the lookup: the second pass is the one numbered whole.
        int[] looked = Assign(keys, values, ptype);
        int[] groups = Assign(keys, values, ptype);
        Assert.Equal(looked, groups);
        Func<int, T> key = keys.Reader<T>(0);
        Dictionary<T, int> past = new Dictionary<T, int>();
        for (int i = 0; i < Rows; i++)
        {
            Assert.Equal(values[i], key(groups[i]));
            long value = long.CreateTruncating(values[i]);
            bool within = typeof(T) == typeof(ulong) ? (ulong)value - (ulong)min < (ulong)span : value >= min && value <= max;
            if (within)
            {
                Assert.True(value - min == groups[i], $"{typeof(T).Name} row {i}: {values[i]} in group {groups[i]}");
            }
            else
            {
                Assert.True(groups[i] >= span, $"{typeof(T).Name} row {i}: {values[i]} past the span in group {groups[i]}");
                Assert.Equal(past.TryAdd(values[i], groups[i]) ? groups[i] : past[values[i]], groups[i]);
            }
        }
    }

    private static int[] Assign<T>(FixedKeys<T> keys, T[] values, PType ptype)
        where T : unmanaged, IEquatable<T>, IComparable<T>
    {
        int size = Marshal.SizeOf<T>();
        CanonicalArena arena = new CanonicalArena(values.Length * size + 4096);
        VortexBuffer buffer = arena.Allocate(values.Length * size, size, out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        int node = arena.AddPrimitive(Types.Primitive(ptype, Nullability.NonNullable), values.Length, Validity.NonNullable, ptype, buffer);
        int[] groups = new int[values.Length];
        keys.Assign(arena, [node], values.Length, [], groups, new GroupRanges());
        return groups;
    }
}
