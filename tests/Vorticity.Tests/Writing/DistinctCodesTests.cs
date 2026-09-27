using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The writer's distinct table hands every row the code of its value's first appearance, over a
/// chunk probed in several ranges: a value seen again, in the same range or a later one, finds the
/// entry its first row made.
/// </summary>
public sealed class DistinctCodesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4_096)]
    public void RepeatedIntegersTakeTheirFirstRowsCode(int rows)
    {
        long[] column = new long[rows];
        Random random = new Random(rows);
        for (int row = 0; row < rows; row++)
        {
            column[row] = random.Next(Math.Max(rows / 8, 1)) * 0x1_0000_0001L;
        }

        CanonicalArena arena = new CanonicalArena();
        try
        {
            DType dtype = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
            VortexBuffer buffer = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            column.CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
            int node = arena.AddPrimitive(dtype, rows, Validity.NonNullable, PType.I64, buffer);
            DistinctTable table = DistinctTable.For(arena.GetNode(node))!;

            // Two ranges, the second starting where the first stopped, as the batches of a chunk do.
            int half = rows / 2;
            table.Probe(arena, arena.GetNode(node), 0, half);
            table.Probe(arena, arena.GetNode(node), half, rows - half);

            Dictionary<long, int> first = [];
            for (int row = 0; row < rows; row++)
            {
                int code = first.TryGetValue(column[row], out int seen) ? seen : first[column[row]] = first.Count;
                Assert.Equal(code, table.Codes[row]);
            }

            Assert.Equal(first.Count, table.Distinct);
            table.Reset();
        }
        finally
        {
            arena.Reset();
        }
    }
}
