using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Columns;

/// <summary>What the public accessors of a column read of a node.</summary>
public sealed class ColumnDataTests
{
    /// <summary>
    /// A list's row reads as its elements: none for a null row, whatever offset and size the file left there, which the
    /// format leaves undefined; and a row whose range passes the elements, or whose offset past 2^31 a cut to 32 bits made
    /// another, is a format error rather than other elements.
    /// </summary>
    [Fact]
    public void AListRowIsNoneWhenNullAndHeldToItsElements()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer values = arena.Allocate(4 * sizeof(long), 8, out Span<byte> valueBytes);
        valueBytes.Clear();
        int elements = arena.AddPrimitive(i64, 4, Validity.NonNullable, PType.I64, values);

        int List(ulong[] offsets, ulong[] sizes, byte validBits)
        {
            VortexBuffer offsetBuffer = arena.Allocate(offsets.Length * 8, 8, out Span<byte> offsetBytes);
            VortexBuffer sizeBuffer = arena.Allocate(sizes.Length * 8, 8, out Span<byte> sizeBytes);
            for (int i = 0; i < offsets.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(offsetBytes[(i * 8)..], offsets[i]);
                BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes[(i * 8)..], sizes[i]);
            }

            VortexBuffer bits = arena.Allocate(1, 1, out Span<byte> bitBytes);
            bitBytes[0] = validBits;
            Validity validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), offsets.Length, Validity.NonNullable, bits, 0));
            return arena.AddListView(types.List(i64, Nullability.Nullable), offsets.Length, validity, elements, offsetBuffer, PType.U64, sizeBuffer, PType.U64);
        }

        // Row 1 is null, its offset and size whatever the writer left: no elements.
        int list = List([0, 999, 2], [2, 999, 2], 0b101);
        Assert.Equal(new Range(0, 2), ColumnData.ListRange(arena, list, 0));
        Assert.Equal(default, ColumnData.ListRange(arena, list, 1));
        Assert.Equal(new Range(2, 4), ColumnData.ListRange(arena, list, 2));

        // A valid row past the elements, and one past 2^31 whose low half would land among them.
        int past = List([3, (1UL << 32) + 1], [2, 1], 0b11);
        Assert.Throws<VortexFormatException>(() => ColumnData.ListRange(arena, past, 0));
        Assert.Throws<VortexFormatException>(() => ColumnData.ListRange(arena, past, 1));
    }
}
