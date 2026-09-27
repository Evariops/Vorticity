using System;
using System.Collections.Generic;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The ingest's distinct table over fixed-width columns: a row equal to the one before takes its
/// code from a run measured a vector at a time, the others from the hash. Held to what a code
/// means -- equal values, equal codes, and a code's first row the first of its value -- on runs of
/// every length, a run reaching the last row, and a column with no runs. (Below three bytes of
/// width a column is offered no table: its codes would be as wide as its values.)
/// </summary>
public sealed class DistinctTableTests
{
    [Theory]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    internal void EqualValuesTakeEqualCodes(PType ptype)
    {
        Random random = new Random((int)ptype * 17);
        foreach (int longest in new[] { 1, 3, 40, 200 })
        {
            const int Rows = 2_000;
            int width = ptype.ByteWidth();
            ulong[] values = new ulong[Rows];
            int at = 0;
            while (at < Rows)
            {
                ulong value = (ulong)random.Next(64);
                int run = random.Next(1, longest + 1);
                for (int k = 0; k < run && at < Rows; k++)
                {
                    values[at++] = value;
                }
            }

            CanonicalArena arena = new CanonicalArena();
            VortexBuffer buffer = arena.Allocate(Rows * width, width, out Span<byte> bytes);
            for (int i = 0; i < Rows; i++)
            {
                for (int b = 0; b < width; b++)
                {
                    bytes[(i * width) + b] = (byte)(values[i] >> (8 * b));
                }
            }

            int node = arena.AddPrimitive(
                new DTypeArena().Primitive(ptype, Nullability.NonNullable), Rows, Validity.NonNullable, ptype, buffer);
            DistinctTable table = DistinctTable.For(arena.GetNode(node))!;
            table.Probe(arena, arena.GetNode(node), 0, Rows);

            ReadOnlySpan<int> codes = table.Codes;
            ReadOnlySpan<int> firstRows = table.FirstRows;
            Assert.Equal(Rows, codes.Length);
            Dictionary<ulong, int> seen = [];
            for (int i = 0; i < Rows; i++)
            {
                if (seen.TryGetValue(values[i], out int code))
                {
                    Assert.Equal(code, codes[i]);
                }
                else
                {
                    Assert.DoesNotContain(codes[i], seen.Values);
                    Assert.Equal(i, firstRows[codes[i]]);
                    seen[values[i]] = codes[i];
                }
            }

            table.Reset();
        }
    }
}
