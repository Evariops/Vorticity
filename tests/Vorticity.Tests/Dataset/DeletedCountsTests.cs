// The counts of the rows a delete takes, for a column whose batches hold an array no counts key (booleans): its rows of
// nulls count as nulls, and a row taken that holds a value is refused, as a count of a row at a time refused it; a row the
// selection leaves out is never looked at.
using System;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DeletedCountsTests
{
    private const int Rows = 4;

    [Fact]
    public void AColumnNoCountsKeyIsRefusedAtTheFirstValueTakenAlone()
    {
        DTypeArena types = new DTypeArena();
        DType flag = types.Bool(Nullability.Nullable);
        DType schema = types.Struct(["flag"], [flag], Nullability.NonNullable);

        DeletedCounts.Builder nulls = new DeletedCounts.Builder(schema, [(0, new ColumnCounts([], [], [], []))]);
        using (RecordBatch batch = Flags(schema, flag, Validity.AllInvalid))
        {
            nulls.Add(batch);
        }

        Assert.Equal(Rows, nulls.Finish().Rows);

        DeletedCounts.Builder values = new DeletedCounts.Builder(schema, [(0, new ColumnCounts([], [], [], []))]);
        using RecordBatch valued = Flags(schema, flag, Validity.AllValid);
        values.Add(valued, [0UL]);
        Assert.Throws<NotSupportedException>(() => values.Add(valued, [0b0100UL]));
    }

    private static RecordBatch Flags(DType schema, DType flag, Validity validity)
    {
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer bits = arena.Allocate(1, 1, out Span<byte> raw);
        raw[0] = 0b1010;
        int column = arena.AddBool(flag, Rows, validity, bits, 0);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        return new RecordBatch(arena, root, 0);
    }
}
