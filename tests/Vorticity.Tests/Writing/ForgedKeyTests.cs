using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// Values that the write path's hashes, their seeds taken out, would all send to one bucket: the
/// tables must spread them as they spread any values, and what the tables hand out must not depend
/// on where the values landed.
/// </summary>
public sealed class ForgedKeyTests
{
    private const int Count = 4_096;

    /// <summary>
    /// A run of held slots that values placed at random, half the slots held, all but never reach;
    /// forged values that all start at one slot make a single run of every value.
    /// </summary>
    private const int LongestRun = 200;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>How the values are forged.</summary>
    public enum Forgery
    {
        /// <summary>Integers the mix without its seed sends to hashes that share their low 32 bits.</summary>
        Unmixed,

        /// <summary>Strings whose words make the first operand of every fold zero, without its seed.</summary>
        FirstZero,

        /// <summary>Strings whose words make the second operand of every fold zero, without its seed.</summary>
        SecondZero,
    }

    public static TheoryData<int, Forgery> Columns => new TheoryData<int, Forgery>
    {
        { 8, Forgery.Unmixed },
        { 12, Forgery.FirstZero },
        { 16, Forgery.FirstZero },
        { 16, Forgery.SecondZero },
        { 32, Forgery.FirstZero },
        { 32, Forgery.SecondZero },
    };

    public static TheoryData<int, Forgery> Keys => new TheoryData<int, Forgery>
    {
        { 16, Forgery.FirstZero },
        { 16, Forgery.SecondZero },
        { 32, Forgery.FirstZero },
        { 32, Forgery.SecondZero },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public void ForgedValuesSpreadInTheDistinctTable(int width, Forgery forgery)
    {
        CanonicalArena arena = new CanonicalArena();
        try
        {
            int node = width == 8 ? Integers(arena) : Strings(arena, width, forgery);
            DistinctTable table = DistinctTable.For(arena.GetNode(node))!;
            table.Probe(arena, arena.GetNode(node), 0, Count);

            Assert.Equal(Count, table.Distinct);
            Assert.InRange(LongestHeld<int>(SlotCodes(table).AsSpan(0, TableMask(table) + 1), 0), 1, LongestRun);

            // Codes follow first appearance, whichever slots the values landed in.
            for (int row = 0; row < Count; row++)
            {
                Assert.Equal(row, table.Codes[row]);
            }

            table.Reset();
        }
        finally
        {
            arena.Reset();
        }
    }

    // Hashes sharing their low bits, as a slot read from them bare would place them, and hashes
    // that the mix without its seed sends to low bits they share.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForgedHashesSpreadInTheBloomSet(bool unmixed)
    {
        using HashSet64 set = new HashSet64();
        ulong[] hashes = new ulong[Count];
        for (int i = 0; i < Count; i++)
        {
            hashes[i] = unmixed ? Unmix((ulong)(i + 1) << 32) : (ulong)(i + 1) << 32;
            set.Add(hashes[i]);
        }

        Assert.Equal(Count, set.Count);
        Assert.InRange(LongestHeld<ulong>(SetSlots(set).AsSpan(0, SetMask(set) + 1), 0), 1, LongestRun);

        // The filter is the one its hashes make, in whatever order the slots hold them.
        uint[] expected = new uint[64 * SplitBlockBloom.WordsPerBlock];
        foreach (ulong hash in hashes)
        {
            SplitBlockBloom.Insert(expected, hash);
        }

        uint[] filter = new uint[expected.Length];
        set.InsertInto(filter);
        Assert.Equal(expected, filter);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void ForgedKeysSpreadInTheIndexTable(int length, Forgery forgery)
    {
        using ChunkKeys keys = new ChunkKeys();
        byte[] key = new byte[length];
        for (int i = 0; i < Count; i++)
        {
            Forge(key, (ulong)(i + 1), forgery);
            Assert.Equal(i, keys.Intern(key));
        }

        Assert.InRange(LongestHeld<int>(KeySlots(keys), -1), 1, LongestRun);

        // Every key found again under the id it was given first.
        for (int i = 0; i < Count; i++)
        {
            Forge(key, (ulong)(i + 1), forgery);
            Assert.Equal(i, keys.Intern(key));
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_slotCode")]
    private static extern ref int[] SlotCodes(DistinctTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_mask")]
    private static extern ref int TableMask(DistinctTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_slots")]
    private static extern ref ulong[] SetSlots(HashSet64 set);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_mask")]
    private static extern ref int SetMask(HashSet64 set);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_slots")]
    private static extern ref int[] KeySlots(ChunkKeys keys);

    /// <summary>The longest run of held slots, around the end as linear probing wraps.</summary>
    private static int LongestHeld<T>(ReadOnlySpan<T> slots, T empty)
        where T : IEquatable<T>
    {
        int longest = 0;
        int run = 0;
        for (int i = 0; i < slots.Length * 2; i++)
        {
            run = slots[i % slots.Length].Equals(empty) ? 0 : run + 1;
            longest = Math.Max(longest, Math.Min(run, slots.Length));
        }

        return longest;
    }

    private static int Integers(CanonicalArena arena)
    {
        DType dtype = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(Count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<ulong> values = MemoryMarshal.Cast<byte, ulong>(bytes);
        for (int i = 0; i < Count; i++)
        {
            values[i] = Unmix((ulong)(i + 1) << 32);
        }

        return arena.AddPrimitive(dtype, Count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Strings(CanonicalArena arena, int length, Forgery forgery)
    {
        DType dtype = new DTypeArena().Utf8(Nullability.NonNullable);
        VortexBuffer heap = arena.Allocate(Count * length, 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(Count * 16, 16, out Span<byte> viewBytes);
        for (int i = 0; i < Count; i++)
        {
            Span<byte> value = heapBytes.Slice(i * length, length);
            Forge(value, (ulong)(i + 1), forgery);
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], i * length);
        }

        return arena.AddVarBinView(dtype, Count, Validity.NonNullable, views, [heap]);
    }

    /// <summary>The value the mix without its seed sends to <paramref name="hash"/>.</summary>
    private static ulong Unmix(ulong hash)
    {
        hash ^= hash >> 32;
        hash *= Inverse(Golden);
        hash ^= (hash >> 29) ^ (hash >> 58);
        return hash * Inverse(Golden);
    }

    /// <summary>
    /// The <paramref name="index"/>th string of <paramref name="value"/>'s length whose words zero
    /// one operand of each fold its hash takes, the rest of it the index and fixed words.
    /// </summary>
    /// <remarks>
    /// Twelve bytes are one fold of the first eight and the last four; sixteen, one fold of the
    /// first and the last eight; thirty-two, two folds, of words 0 and 2 and of words 1 and 3.
    /// </remarks>
    private static void Forge(Span<byte> value, ulong index, Forgery forgery)
    {
        value.Clear();
        switch (value.Length, forgery)
        {
            case (12, Forgery.FirstZero):
                BinaryPrimitives.WriteUInt32LittleEndian(value[8..], (uint)index);
                return;

            case (16, Forgery.FirstZero):
                BinaryPrimitives.WriteUInt64LittleEndian(value[8..], index);
                return;

            case (16, Forgery.SecondZero):
                BinaryPrimitives.WriteUInt64LittleEndian(value, index);
                return;

            case (32, Forgery.FirstZero):
                BinaryPrimitives.WriteUInt64LittleEndian(value[8..], 0x0123_4567_89AB_CDEFUL);
                BinaryPrimitives.WriteUInt64LittleEndian(value[16..], index);
                BinaryPrimitives.WriteUInt64LittleEndian(value[24..], 0xFEDC_BA98_7654_3210UL);
                return;

            case (32, Forgery.SecondZero):
                BinaryPrimitives.WriteUInt64LittleEndian(value, index);
                BinaryPrimitives.WriteUInt64LittleEndian(value[8..], 0x0123_4567_89AB_CDEFUL);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(forgery), forgery, $"no forgery for {value.Length} bytes");
        }
    }

    private static ulong Inverse(ulong odd)
    {
        ulong inverse = odd;
        for (int i = 0; i < 5; i++)
        {
            inverse *= 2 - (odd * inverse);
        }

        return inverse;
    }
}
