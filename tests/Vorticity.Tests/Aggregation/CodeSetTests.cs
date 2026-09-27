using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class CodeSetTests
{
    private static readonly DTypeArena Types = new DTypeArena();

    [Fact]
    public void AShortRangeListsEachCodeOnceInTheOrderItMeetsThem()
    {
        CodeSet set = default;
        uint[] codes = [9, 3, 1, 3, 9, 2, 1, 7];

        Assert.Equal([3, 1, 9, 2, 7], set.Few(codes, default, 1, 8, entries: 10).ToArray());
        Assert.Equal([9, 3], set.Few(codes, default, 0, 2, entries: 10).ToArray());
    }

    [Fact]
    public void AShortRangeUnderASelectionListsOnlyTheRowsSelected()
    {
        CodeSet set = default;
        uint[] codes = [9, 3, 1, 3, 9, 2, 1, 7];
        ulong[] selected = [0b1010_0100];

        Assert.Equal([1, 2, 7], set.Few(codes, selected, 0, 8, entries: 10).ToArray());
    }

    // A long range leaves its marks in the table; the short one after it must not take them for
    // its own, nor miss a code the long one marked.
    [Fact]
    public void AShortRangeAfterALongOneFindsNoMarkOfIt()
    {
        CodeSet set = default;
        Span<byte> table = set.Table(10);
        table[4] = 1;
        table[6] = 1;

        Assert.Equal([6, 5], set.Few([6, 5, 6], default, 0, 3, entries: 10).ToArray());
    }

    // A long range of a larger dictionary marks codes past a smaller one's; a short range of the
    // smaller one comes between, and the next short range of the larger one must still find them
    // unmarked.
    [Fact]
    public void AShortRangeAfterALargerDictionaryFindsNoMarkPastItsOwn()
    {
        CodeSet set = default;
        Span<byte> table = set.Table(100);
        table[70] = 1;
        Assert.Equal([3], set.Few([3, 3], default, 0, 2, entries: 40).ToArray());

        Assert.Equal([70, 5], set.Few([70, 5], default, 0, 2, entries: 100).ToArray());
    }

    // Groups over ranges of both kinds, shorter and longer than the dictionary, each count its own
    // distinct values: a code met in one range is met again in the next.
    [Fact]
    public void DistinctCountsOverShortAndLongRangesMatchTheRows()
    {
        long[] values = new long[12];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 100 + i;
        }

        uint[] codes = Codes(96, values.Length);
        CanonicalArena arena = new CanonicalArena();
        int node = Integers(arena, values, codes);
        AggregateSlot<long> slot = new FixedDistinctSlot<long>(StorageKind.Primitive);
        int[] bounds = Bounds;
        slot.EnsureGroups(bounds.Length - 1);

        BatchInput input = new BatchInput(1, arena, node, codes.Length, default);
        for (int g = 0; g < bounds.Length - 1; g++)
        {
            slot.StepRange(input, bounds[g], bounds[g + 1], g);
        }

        for (int g = 0; g < bounds.Length - 1; g++)
        {
            Assert.Equal(DistinctIn(codes, bounds[g], bounds[g + 1]), slot.Result(g));
        }
    }

    [Fact]
    public void TextDistinctCountsOverShortAndLongRangesMatchTheRows()
    {
        string[] values =
        [
            "alpha", "beta", "gamma", "delta", "a value longer than a view holds", "epsilon",
            "zeta", "eta", "theta", "iota", "kappa", "another value longer than a view",
        ];
        uint[] codes = Codes(96, values.Length);
        CanonicalArena arena = new CanonicalArena();
        int node = Strings(arena, values, codes);
        AggregateSlot<long> slot = new BytesDistinctSlot();
        int[] bounds = Bounds;
        slot.EnsureGroups(bounds.Length - 1);

        BatchInput input = new BatchInput(1, arena, node, codes.Length, default);
        for (int g = 0; g < bounds.Length - 1; g++)
        {
            slot.StepRange(input, bounds[g], bounds[g + 1], g);
        }

        for (int g = 0; g < bounds.Length - 1; g++)
        {
            Assert.Equal(DistinctIn(codes, bounds[g], bounds[g + 1]), slot.Result(g));
        }
    }

    /// <summary>
    /// Ranges shorter and longer than a dictionary of twelve: two long ones in a row, the second
    /// naming none of the codes the first did, then a short one and a long one.
    /// </summary>
    private static int[] Bounds => [0, 5, 30, 60, 64, 96];

    /// <summary>
    /// Codes over <see cref="Bounds"/>: the first long range names codes 0 to 5 alone, the second
    /// 6 to 11 alone, the others any code.
    /// </summary>
    private static uint[] Codes(int rows, int entries)
    {
        uint[] codes = new uint[rows];
        for (int row = 0; row < rows; row++)
        {
            codes[row] = row switch
            {
                >= 5 and < 30 => (uint)(row % 6),
                >= 30 and < 60 => (uint)(6 + (row % 6)),
                _ => (uint)((row * 5 + (row / 7)) % entries),
            };
        }

        return codes;
    }

    private static long DistinctIn(uint[] codes, int start, int end)
    {
        HashSet<uint> seen = [];
        for (int row = start; row < end; row++)
        {
            seen.Add(codes[row]);
        }

        return seen.Count;
    }

    private static int Integers(CanonicalArena arena, long[] values, uint[] codes)
    {
        DType dtype = Types.Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        int entries = arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.I64, buffer);
        return Dictionary(arena, dtype, entries, codes);
    }

    private static int Strings(CanonicalArena arena, string[] values, uint[] codes)
    {
        DType dtype = Types.Utf8(Nullability.NonNullable);
        VortexBuffer heap = arena.Allocate(values.Length * 64, 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        int used = 0;
        for (int i = 0; i < values.Length; i++)
        {
            byte[] value = Encoding.UTF8.GetBytes(values[i]);
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value.CopyTo(heapBytes[used..]);
            value.AsSpan(0, 4).CopyTo(view.Slice(4, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
            used += value.Length;
        }

        int entries = arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, [heap]);
        return Dictionary(arena, dtype, entries, codes);
    }

    private static int Dictionary(CanonicalArena arena, DType dtype, int entries, uint[] codes)
    {
        VortexBuffer buffer = arena.Allocate(codes.Length * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        codes.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(bytes));
        return arena.AddDictionary(dtype, codes.Length, Validity.NonNullable, buffer, entries);
    }
}
