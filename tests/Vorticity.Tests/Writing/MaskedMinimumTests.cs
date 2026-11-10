// A bit-packing plan's frame of reference over a column with nulls: the least of the valid values alone, in the element's
// own reading, whatever the null rows hold - here values below every valid one - for every integer type, rows on and off
// the words of 64, nulls spread or in runs, and a column whose last row alone is valid.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class MaskedMinimumTests
{
    [Theory]
    [InlineData((int)PType.U8)]
    [InlineData((int)PType.U16)]
    [InlineData((int)PType.U32)]
    [InlineData((int)PType.U64)]
    [InlineData((int)PType.I8)]
    [InlineData((int)PType.I16)]
    [InlineData((int)PType.I32)]
    [InlineData((int)PType.I64)]
    public void TheReferenceIsTheLeastValidValue(int type)
    {
        PType ptype = (PType)type;
        int width = ptype.ByteWidth();
        bool signed = ptype.IsSignedInteger();
        Random random = new Random(type * 13);
        DTypeArena types = new DTypeArena();
        foreach (int length in (int[])[64, 65, 128, 200, 1_000, 4_097])
        {
            for (int pattern = 0; pattern < 3; pattern++)
            {
                CanonicalArena arena = new CanonicalArena();
                VortexBuffer buffer = arena.Allocate(length * width, width, out Span<byte> bytes);
                VortexBuffer bits = arena.Allocate((length + 7) / 8, 8, out Span<byte> valid);
                valid.Clear();
                long? least = null;
                for (int i = 0; i < length; i++)
                {
                    bool kept = pattern switch { 0 => random.Next(4) != 0, 1 => (i / 70) % 2 == 0, _ => i == length - 1 };

                    // A null row holds a value below every valid one; a valid row one well inside the type.
                    long value = kept ? random.Next(20, 100) : (signed ? -120 : 3);
                    if (kept)
                    {
                        valid[i >> 3] |= (byte)(1 << (i & 7));
                        least = Math.Min(least ?? long.MaxValue, value);
                    }

                    Write(bytes, i, width, value);
                }

                int mask = arena.AddBool(types.Bool(Nullability.NonNullable), length, Validity.NonNullable, bits, 0);
                int node = arena.AddPrimitive(types.Primitive(ptype, Nullability.Nullable), length, Validity.Bitmap(mask), ptype, buffer);
                ValidityMask validity = ValidityMask.From(arena, arena.GetNode(node).Validity);
                Assert.True(BitPackPlan.Minimum(length, validity, bytes, ptype, out ulong reference));
                Assert.Equal((ulong)least!.Value, reference);
            }
        }
    }

    private static void Write(Span<byte> bytes, int row, int width, long value)
    {
        switch (width)
        {
            case 1:
                bytes[row] = (byte)value;
                break;
            case 2:
                MemoryMarshal.Cast<byte, short>(bytes)[row] = (short)value;
                break;
            case 4:
                MemoryMarshal.Cast<byte, int>(bytes)[row] = (int)value;
                break;
            default:
                MemoryMarshal.Cast<byte, long>(bytes)[row] = value;
                break;
        }
    }
}
