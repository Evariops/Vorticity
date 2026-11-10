// An equality on a column of UUIDs: a row is equal to the literal by all of its sixteen bytes, a row differing in any one
// byte is not, a null row is unknown, whatever the row count.
using System;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class UuidEqualityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(37)]
    [InlineData(64)]
    public void ARowIsEqualByEveryByte(int copies)
    {
        Random random = new Random(139 + copies);
        byte[] literal = new byte[16];
        random.NextBytes(literal);

        // Rows: the literal, then the literal with each byte changed in turn, all of it repeated, one row in five null.
        int rows = copies * 17;
        CanonicalArena arena = new CanonicalArena(64);
        VortexBuffer bytes = arena.Allocate(rows * 16, 16, out Span<byte> into);
        VortexBuffer bits = arena.Allocate((rows + 7) / 8, 8, out Span<byte> valid);
        byte[] expected = new byte[rows];
        for (int row = 0; row < rows; row++)
        {
            Span<byte> value = into.Slice(row * 16, 16);
            literal.CopyTo(value);
            int changed = (row % 17) - 1;
            if (changed >= 0)
            {
                value[changed] ^= (byte)(1 + random.Next(255));
            }

            bool isValid = row % 5 != 3;
            if (isValid)
            {
                valid[row >> 3] |= (byte)(1 << (row & 7));
            }

            expected[row] = !isValid ? Trilean.Unknown : changed < 0 ? Trilean.True : Trilean.False;
        }

        DTypeArena types = new DTypeArena();
        DType u8 = types.Primitive(PType.U8, Nullability.NonNullable);
        int elements = arena.AddPrimitive(u8, rows * 16, Validity.NonNullable, PType.U8, bytes);
        Validity validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0));
        int node = arena.AddFixedSizeList(types.FixedSizeList(u8, 16, Nullability.Nullable), rows, validity, elements, 16);

        byte[] equal = new byte[rows];
        ComparisonKernels.Compare(arena, node, ComparisonOp.Equal, FilterLiteral.From(literal), equal);
        Assert.Equal(expected, equal);

        byte[] notEqual = new byte[rows];
        ComparisonKernels.Compare(arena, node, ComparisonOp.NotEqual, FilterLiteral.From(literal), notEqual);
        Assert.Equal(Array.ConvertAll(expected, state => state == Trilean.Unknown ? state : (byte)(1 - state)), notEqual);
    }
}
