// One IN expression read over columns of two kinds, as objects of one dataset may store a column: the candidates hashed for
// each kind answer for that kind, whichever is met first, though the sets are kept with the expression. A candidate of -1
// shares its bits with an unsigned column's greatest value, which it does not equal.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class InSetKindTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachKindOfColumnIsAnsweredByItsOwnSet(bool signedFirst)
    {
        InExpr filter = new InExpr(new ColumnFieldExpr("c"), [FilterLiteral.From(-1L), FilterLiteral.From(2L), FilterLiteral.From(3L), FilterLiteral.From(4L), FilterLiteral.From(5L)]);
        if (signedFirst)
        {
            Check(filter, PType.I64);
            Check(filter, PType.U64);
        }
        else
        {
            Check(filter, PType.U64);
            Check(filter, PType.I64);
        }
    }

    private static void Check(InExpr filter, PType ptype)
    {
        long[] values = [-1, 0, 1, 2, 3, 4, 5, 6, 7, 8];
        CanonicalArena arena = new CanonicalArena(64);
        VortexBuffer buffer = arena.Allocate(values.Length * 8, 8, out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(ptype, Nullability.NonNullable);
        int column = arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, ptype, buffer);
        int root = arena.AddStruct(types.Struct(["c"], [dtype], Nullability.NonNullable), values.Length, Validity.NonNullable, [column]);
        byte[] states = new byte[values.Length];
        new FilterEvaluator(filter).Evaluate(arena, root, values.Length, states);
        for (int row = 0; row < values.Length; row++)
        {
            // An unsigned column's first row is its greatest value, which -1 does not equal.
            bool held = values[row] is >= 2 and <= 5 || (values[row] == -1 && ptype == PType.I64);
            Assert.Equal(held ? Trilean.True : Trilean.False, states[row]);
        }
    }
}
