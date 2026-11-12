// A struct's field under the struct's validity: a row of the field is valid where both the struct's row and the field's
// own are, the two bitmaps at offsets of their own, at row counts that leave a partial word.
using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class MaskedByTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1_000)]
    public void AFieldRowIsValidWhereBothRowsAre(int rows)
    {
        Random random = new Random(181 + rows);
        CanonicalArena arena = new CanonicalArena(64);
        DTypeArena types = new DTypeArena();
        bool[] own = new bool[rows];
        bool[] parent = new bool[rows];
        Validity Bits(bool[] valid, int offset)
        {
            VortexBuffer bits = arena.Allocate(((rows + offset) / 8) + 1, 8, out Span<byte> into);
            into.Clear();
            for (int row = 0; row < rows; row++)
            {
                valid[row] = random.Next(4) != 0;
                if (valid[row])
                {
                    into[(row + offset) >> 3] |= (byte)(1 << ((row + offset) & 7));
                }
            }

            return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, offset));
        }

        VortexBuffer values = arena.Allocate(rows * 8, 8, out _);
        int child = arena.AddPrimitive(types.Primitive(PType.I64, Nullability.Nullable), rows, Bits(own, 3), PType.I64, values);
        int masked = FilterEvaluator.MaskedBy(arena, child, Bits(parent, 5));
        ValidityMask mask = ValidityMask.From(arena, arena.GetNode(masked).Validity);
        for (int row = 0; row < rows; row++)
        {
            Assert.Equal(own[row] && parent[row], mask.IsValid(row));
        }
    }
}
