using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
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

    [Theory]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(200)]
    public void RepeatedStringsTakeTheirFirstRowsCode(int labels)
    {
        // Labels inline in their views, those of one length sharing their first word, and out of
        // line, drawn at random with a null now and then: every row has the code of its value's
        // first appearance, the null's included, whichever recent slots the labels share.
        const int rows = 4_096;
        Random random = new Random(labels);
        string[] pool = new string[labels];
        for (int i = 0; i < labels; i++)
        {
            pool[i] = i % 3 == 2 ? $"a label out of line, {i}" : $"key-{i}";
        }

        string?[] column = new string?[rows];
        for (int row = 0; row < rows; row++)
        {
            column[row] = random.Next(7) == 0 ? null : pool[random.Next(labels)];
        }

        CanonicalArena arena = new CanonicalArena();
        try
        {
            int node = Strings(arena, column);
            DistinctTable table = DistinctTable.For(arena.GetNode(node))!;

            // Two ranges, the second starting where the first stopped, as the batches of a chunk do.
            table.Probe(arena, arena.GetNode(node), 0, rows / 2);
            table.Probe(arena, arena.GetNode(node), rows / 2, rows - (rows / 2));

            Dictionary<string, int> first = [];
            int nullCode = -1;
            int codes = 0;
            for (int row = 0; row < rows; row++)
            {
                int code;
                if (column[row] is not { } value)
                {
                    code = nullCode < 0 ? nullCode = codes++ : nullCode;
                }
                else if (!first.TryGetValue(value, out code))
                {
                    code = first[value] = codes++;
                }

                Assert.Equal(code, table.Codes[row]);
            }

            Assert.Equal(codes, table.Distinct);
            table.Reset();
        }
        finally
        {
            arena.Reset();
        }
    }

    /// <summary>A nullable string column of <paramref name="column"/>, a value past twelve bytes out of line.</summary>
    private static int Strings(CanonicalArena arena, string?[] column)
    {
        int heapBytes = 0;
        foreach (string? value in column)
        {
            heapBytes += value is { Length: > 12 } ? value.Length : 0;
        }

        VortexBuffer heap = arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> data);
        VortexBuffer views = arena.Allocate(column.Length * 16, 16, out Span<byte> viewBytes);
        VortexBuffer bits = arena.Allocate(Math.Max((column.Length + 7) / 8, 1), 1, out Span<byte> valid);
        viewBytes.Clear();
        valid.Clear();
        int written = 0;
        for (int i = 0; i < column.Length; i++)
        {
            if (column[i] is not { } value)
            {
                continue;
            }

            byte[] bytes = Encoding.ASCII.GetBytes(value);
            valid[i >> 3] |= (byte)(1 << (i & 7));
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], written);
            bytes.CopyTo(data[written..]);
            written += bytes.Length;
        }

        DTypeArena types = new DTypeArena();
        int mask = arena.AddBool(types.Bool(Nullability.NonNullable), column.Length, Validity.NonNullable, bits, 0);
        return arena.AddVarBinView(types.Utf8(Nullability.Nullable), column.Length, Validity.Bitmap(mask), views, [heap]);
    }
}
