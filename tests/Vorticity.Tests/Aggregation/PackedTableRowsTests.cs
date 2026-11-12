// A key of two columns whose numbers index a table of groups, every row selected: each row's group read from the table,
// eight rows a step where AVX2 is, a step that meets a tuple new to the table a row at a time. Tuples new to the table
// arrive in every batch, often several in one step and twice in one step; equal tuples share a group and distinct ones
// do not, the columns bound by statistics or not.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class PackedTableRowsTests
{
    private const int Rows = 1_003;

    private static readonly DTypeArena Types = new DTypeArena();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualTuplesShareAGroupAndDistinctOnesDoNot(bool bounded)
    {
        ColumnShape Shape(string name) => new ColumnShape(new ColumnSym(Expr.Field(name), VortexType.Int32, null, null, -1, []));
        KeyFacts? facts = bounded ? new KeyFacts([false, false], [new KeyBounds(0, 31), new KeyBounds(0, 31)]) : null;
        PackedKeys<ulong> keys = new PackedKeys<ulong>([Shape("a"), Shape("b")], facts);
        Dictionary<(int, int), int> groupOf = new Dictionary<(int, int), int>();
        HashSet<int> groups = new HashSet<int>();
        Random random = new Random(bounded ? 5 : 6);
        for (int batch = 0; batch < 4; batch++)
        {
            // Each batch reaches further into the values: its rows meet tuples the table does not hold yet.
            int reach = 8 * (batch + 1);
            int[] a = new int[Rows];
            int[] b = new int[Rows];
            for (int i = 0; i < Rows; i++)
            {
                a[i] = random.Next(reach);
                b[i] = random.Next(reach);
            }

            CanonicalArena arena = new CanonicalArena(Rows * 8 + 4096);
            int[] nodes = [Column(arena, a), Column(arena, b)];
            int[] rowGroups = new int[Rows];
            keys.Assign(arena, nodes, Rows, [], rowGroups, new GroupRanges());
            for (int i = 0; i < Rows; i++)
            {
                if (groupOf.TryGetValue((a[i], b[i]), out int known))
                {
                    Assert.True(known == rowGroups[i], $"batch {batch} row {i}: ({a[i]}, {b[i]}) in group {rowGroups[i]}, before {known}");
                }
                else
                {
                    Assert.True(groups.Add(rowGroups[i]), $"batch {batch} row {i}: ({a[i]}, {b[i]}) shares group {rowGroups[i]}");
                    groupOf[(a[i], b[i])] = rowGroups[i];
                }
            }
        }

        Assert.Equal(groupOf.Count, keys.Count);
    }

    private static int Column(CanonicalArena arena, int[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * 4, 8, out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        return arena.AddPrimitive(Types.Primitive(PType.I32, Nullability.NonNullable), values.Length, Validity.NonNullable, PType.I32, buffer);
    }
}
