// The unscaled values of a decimal chunk at the narrowest signed width that holds them, and back:
// every storage against every width, over lengths either side of a register, with null rows whose
// slots hold whatever a caller left there -- here the storage's own extremes, which would widen
// the answer if a null were counted.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class DecimalPartsTests
{
    private static readonly int[] Lengths = [1, 15, 16, 17, 33, 1_000];

    /// <summary>Storage and width as their tags: a public member's data cannot name internal types.</summary>
    public static TheoryData<byte, byte, int, bool> Cases()
    {
        TheoryData<byte, byte, int, bool> cases = [];
        foreach (DecimalStorageType storage in Enum.GetValues<DecimalStorageType>())
        {
            foreach (PType parts in (PType[])[PType.I8, PType.I16, PType.I32, PType.I64])
            {
                if (parts.ByteWidth() > DecimalStorage.ByteWidth(storage))
                {
                    continue;
                }

                foreach (int rows in Lengths)
                {
                    cases.Add((byte)storage, (byte)parts, rows, false);
                    cases.Add((byte)storage, (byte)parts, rows, true);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachStorageNarrowsToTheWidthItsValuesNeedAndWidensBack(byte storageTag, byte partsTag, int rows, bool nullable)
    {
        DecimalStorageType storage = (DecimalStorageType)storageTag;
        PType parts = (PType)partsTag;
        Random random = new Random((rows * 31) + ((int)storage * 7) + (int)parts + (nullable ? 1 : 0));
        (long min, long max) = Range(parts);
        long[] values = new long[rows];
        bool[] valid = new bool[rows];
        int validRows = 0;
        for (int i = 0; i < rows; i++)
        {
            valid[i] = !nullable || random.Next(4) != 0;
            values[i] = random.NextInt64(min, max);
            validRows += valid[i] ? 1 : 0;
        }

        // The range's two ends on valid rows, so the width is exactly the one the values need.
        int[] kept = Array.FindAll(Rows(rows), i => valid[i]);
        if (kept.Length > 0)
        {
            values[kept[^1]] = max;
            values[kept[0]] = kept.Length > 1 ? min : max;
        }

        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int node = Decimal(arena, types, storage, values, valid, nullable);
        PType expected = validRows == 0 || storage == DecimalStorageType.I8 ? PType.I8 : parts;

        Assert.Equal(expected, DecimalParts.Width(arena, arena.GetNode(node)));

        VortexBuffer narrowed = DecimalParts.Narrow(arena, arena.GetNode(node), expected);
        Assert.Equal(rows * expected.ByteWidth(), narrowed.Length);
        for (int i = 0; i < rows; i++)
        {
            if (valid[i])
            {
                Assert.Equal(values[i], Read(narrowed.Span, expected, i));
            }
        }

        // What a `vortex.decimal_byte_parts` decodes to, stored at the parts' width, widened back to the storage.
        DecimalStorageType partsStorage = StorageOf(expected);
        if (DecimalStorage.ByteWidth(partsStorage) < DecimalStorage.ByteWidth(storage))
        {
            int decoded = arena.AddDecimal(
                arena.GetNode(node).DType, rows, arena.GetNode(node).Validity, partsStorage, Precision(storage), 2, narrowed);
            VortexBuffer widened = DecimalParts.Widen(arena, arena.GetNode(decoded), storage);
            Assert.Equal(rows * DecimalStorage.ByteWidth(storage), widened.Length);
            for (int i = 0; i < rows; i++)
            {
                if (valid[i])
                {
                    Assert.Equal(values[i], ReadWide(widened.Span, storage, i));
                }
            }
        }
    }

    [Theory]
    [InlineData(DecimalStorageType.I128, 0, false)]
    [InlineData(DecimalStorageType.I128, 999, true)]
    [InlineData(DecimalStorageType.I256, 0, true)]
    [InlineData(DecimalStorageType.I256, 999, false)]
    internal void AValidValuePastSixtyFourBitsGivesNoWidthAndANullOneDoesNotCount(DecimalStorageType storage, int at, bool negative)
    {
        const int Rows = 1_000;
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int width = DecimalStorage.ByteWidth(storage);

        // Every value zero but one, 2^63 or -2^63 - 1: a low word whose sign the high words do not repeat.
        VortexBuffer buffer = arena.Allocate(Rows * width, 16, out Span<byte> bytes);
        Span<long> words = MemoryMarshal.Cast<byte, long>(bytes.Slice(at * width, width));
        words.Fill(negative ? -1L : 0L);
        words[0] = negative ? long.MaxValue : long.MinValue;

        DType type = types.Decimal(Precision(storage), 2, Nullability.Nullable);
        bool[] valid = new bool[Rows];
        Array.Fill(valid, true);
        int node = arena.AddDecimal(type, Rows, Nulls(arena, types, valid), storage, Precision(storage), 2, buffer);
        Assert.Null(DecimalParts.Width(arena, arena.GetNode(node)));

        // The same slot under a null row: every valid value is zero, which a byte holds.
        valid[at] = false;
        int masked = arena.AddDecimal(type, Rows, Nulls(arena, types, valid), storage, Precision(storage), 2, buffer);
        Assert.Equal(PType.I8, DecimalParts.Width(arena, arena.GetNode(masked)));
    }

    private static int[] Rows(int rows)
    {
        int[] all = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            all[i] = i;
        }

        return all;
    }

    private static (long Min, long Max) Range(PType parts) => parts switch
    {
        PType.I8 => (sbyte.MinValue, sbyte.MaxValue),
        PType.I16 => (short.MinValue, short.MaxValue),
        PType.I32 => (int.MinValue, int.MaxValue),
        _ => (long.MinValue, long.MaxValue),
    };

    private static DecimalStorageType StorageOf(PType parts) => parts switch
    {
        PType.I8 => DecimalStorageType.I8,
        PType.I16 => DecimalStorageType.I16,
        PType.I32 => DecimalStorageType.I32,
        _ => DecimalStorageType.I64,
    };

    private static byte Precision(DecimalStorageType storage) => storage switch
    {
        DecimalStorageType.I8 => 2,
        DecimalStorageType.I16 => 4,
        DecimalStorageType.I32 => 9,
        DecimalStorageType.I64 => 18,
        DecimalStorageType.I128 => 38,
        _ => 76,
    };

    /// <summary>A decimal node at <paramref name="storage"/>, a null row's slot holding the storage's extremes.</summary>
    private static int Decimal(CanonicalArena arena, DTypeArena types, DecimalStorageType storage, long[] values, bool[] valid, bool nullable)
    {
        int rows = values.Length;
        int width = DecimalStorage.ByteWidth(storage);
        VortexBuffer buffer = arena.Allocate(rows * width, Math.Min(width, 16), out Span<byte> bytes);
        for (int i = 0; i < rows; i++)
        {
            Span<byte> slot = bytes.Slice(i * width, width);
            if (valid[i])
            {
                Write(slot, values[i]);
            }
            else
            {
                // All ones but the sign: the largest value the storage holds, past any narrower width.
                slot.Fill(0xFF);
                slot[^1] = 0x7F;
            }
        }

        Nullability nullability = nullable ? Nullability.Nullable : Nullability.NonNullable;
        DType type = types.Decimal(Precision(storage), 2, nullability);
        Validity validity = nullable ? Nulls(arena, types, valid) : Validity.NonNullable;
        return arena.AddDecimal(type, rows, validity, storage, Precision(storage), 2, buffer);
    }

    private static Validity Nulls(CanonicalArena arena, DTypeArena types, bool[] valid)
    {
        VortexBuffer bits = arena.Allocate((valid.Length + 7) / 8, 1, out Span<byte> bytes);
        for (int i = 0; i < valid.Length; i++)
        {
            if (valid[i])
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), valid.Length, Validity.NonNullable, bits, 0));
    }

    /// <summary>A value sign-extended over a slot of any width.</summary>
    private static void Write(Span<byte> slot, long value)
    {
        Span<byte> word = stackalloc byte[8];
        MemoryMarshal.Write(word, in value);
        for (int b = 0; b < slot.Length; b++)
        {
            slot[b] = b < 8 ? word[b] : (value < 0 ? (byte)0xFF : (byte)0);
        }
    }

    private static long Read(ReadOnlySpan<byte> bytes, PType parts, int i) => parts switch
    {
        PType.I8 => MemoryMarshal.Cast<byte, sbyte>(bytes)[i],
        PType.I16 => MemoryMarshal.Cast<byte, short>(bytes)[i],
        PType.I32 => MemoryMarshal.Cast<byte, int>(bytes)[i],
        _ => MemoryMarshal.Cast<byte, long>(bytes)[i],
    };

    /// <summary>A slot's value, checked to be the sign extension of its low 64 bits.</summary>
    private static long ReadWide(ReadOnlySpan<byte> bytes, DecimalStorageType storage, int i)
    {
        int width = DecimalStorage.ByteWidth(storage);
        ReadOnlySpan<byte> slot = bytes.Slice(i * width, width);
        if (width <= 8)
        {
            return width switch
            {
                1 => (sbyte)slot[0],
                2 => MemoryMarshal.Read<short>(slot),
                4 => MemoryMarshal.Read<int>(slot),
                _ => MemoryMarshal.Read<long>(slot),
            };
        }

        long low = MemoryMarshal.Read<long>(slot);
        for (int b = 8; b < width; b++)
        {
            Assert.Equal(low < 0 ? (byte)0xFF : (byte)0, slot[b]);
        }

        return low;
    }
}
