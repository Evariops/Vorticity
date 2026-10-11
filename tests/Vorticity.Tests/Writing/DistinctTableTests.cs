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
/// code from a run measured a vector at a time, the others from the hash, and while the table holds
/// few values every row from a compare with each of them, eight rows at a time. Held to what a code
/// means -- equal values, equal codes, and a code's first row the first of its value -- on runs of
/// every length, a run reaching the last row, a column with no runs, and columns of few values
/// that pass the table's few in the middle of a block, a null's code among them. (Below three bytes
/// of width a column is offered no table: its codes would be as wide as its values.)
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

    [Theory]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    [InlineData(PType.F64)]
    internal void RowsOfFewValuesTakeTheirValuesCodes(PType ptype)
    {
        Random random = new Random((int)ptype * 31);
        int width = ptype.ByteWidth();
        DTypeArena types = new DTypeArena();
        foreach (int distinct in new[] { 1, 2, 7, 31, 32, 33, 40 })
        {
            foreach (bool withNull in new[] { false, true })
            {
                // Blocks of rows that end inside a vector of eight, the values drawn in no order,
                // some of them only past the middle of a block, and a block that holds a null
                // before a block of rows equal to zero, which the null's code must not take.
                const int Rows = 3_001;
                ulong[] pool = new ulong[distinct];
                for (int i = 0; i < distinct; i++)
                {
                    pool[i] = ptype == PType.F64 ? BitConverter.DoubleToUInt64Bits(i * 0.25) : (ulong)i;
                }

                ulong[] values = new ulong[Rows];
                bool[] valid = new bool[Rows];
                for (int i = 0; i < Rows; i++)
                {
                    int reach = i < Rows / 2 ? Math.Max(1, distinct / 2) : distinct;
                    values[i] = pool[random.Next(reach)];
                    valid[i] = !withNull || i != 700;
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

                Validity validity = Validity.NonNullable;
                if (withNull)
                {
                    VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> mask);
                    for (int i = 0; i < Rows; i++)
                    {
                        if (valid[i])
                        {
                            mask[i >> 3] |= (byte)(1 << (i & 7));
                        }
                    }

                    validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
                }

                int node = arena.AddPrimitive(
                    types.Primitive(ptype, withNull ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, buffer);
                DistinctTable table = DistinctTable.For(arena.GetNode(node))!;
                for (int start = 0; start < Rows; start += 333)
                {
                    table.Probe(arena, arena.GetNode(node), start, Math.Min(333, Rows - start));
                }

                ReadOnlySpan<int> codes = table.Codes;
                ReadOnlySpan<int> firstRows = table.FirstRows;
                Assert.Equal(Rows, codes.Length);
                Dictionary<(bool Valid, ulong Value), int> seen = [];
                for (int i = 0; i < Rows; i++)
                {
                    (bool, ulong) key = valid[i] ? (true, values[i]) : (false, 0UL);
                    if (seen.TryGetValue(key, out int code))
                    {
                        Assert.Equal(code, codes[i]);
                    }
                    else
                    {
                        Assert.DoesNotContain(codes[i], seen.Values);
                        Assert.Equal(i, firstRows[codes[i]]);
                        seen[key] = codes[i];
                    }
                }

                table.Reset();
                arena.Reset();
            }
        }
    }
}
