// The best rows of an order, kept as the batches come: a value before the edge's, or the edge's own at an earlier place,
// whatever the order the batches' places come in. Integer rows past the first trim are judged on vectors against the edge
// (TopKeep.Beating): every case here crosses it, with values many rows share, nulls, and rows a selection leaves out.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class TopKeepTests
{
    private const int BatchRows = 1_000;
    private const int Batches = 40;
    private const int Keep = 100;

    public static TheoryData<bool, bool, bool> Cases => new TheoryData<bool, bool, bool>
    {
        { false, false, false },
        { true, false, false },
        { false, true, false },
        { true, true, true },
        { false, false, true },
        { true, true, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void KeepsTheBestRowsTiesToTheEarlierPlace(bool descending, bool nulls, bool selected)
    {
        Random random = new Random((descending ? 1 : 0) + (nulls ? 2 : 0) + (selected ? 4 : 0));

        // Values of few kinds, so that thousands of rows tie with the edge, signed; each batch at its own place.
        long?[] values = new long?[Batches * BatchRows];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = nulls && i % 17 == 0 ? null : random.Next(-25, 25);
        }

        int[] order = [.. Enumerable.Range(0, Batches)];
        random.Shuffle(order);
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, nulls ? Nullability.Nullable : Nullability.NonNullable);
        List<long> kept = [];
        using (TopKeep top = new TopKeep(VortexSession.Default, nulls ? VortexType.Int64.Nullable : VortexType.Int64, descending, Keep, memory: null))
        {
            foreach (int batch in order)
            {
                CanonicalArena arena = new CanonicalArena();
                VortexBuffer buffer = arena.Allocate(BatchRows * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> held = MemoryMarshal.Cast<byte, long>(bytes);
                bool[] valid = new bool[BatchRows];
                for (int row = 0; row < BatchRows; row++)
                {
                    long? value = values[(batch * BatchRows) + row];
                    held[row] = value ?? 0;
                    valid[row] = value is not null;
                }

                int node = arena.AddPrimitive(i64, BatchRows, nulls ? Bitmap(arena, valid) : Validity.NonNullable, PType.I64, buffer);
                ulong[] selection = selected ? Selection(batch) : [];
                top.Add(arena, node, BatchRows, selection, (long)batch * BatchRows, TestContext.Current.CancellationToken);
            }

            kept.AddRange(top.Places(TestContext.Current.CancellationToken));
        }

        // The rows the order ranks first: a null last whichever the direction, a tie to the earlier place.
        IEnumerable<int> candidates = Enumerable.Range(0, values.Length).Where(i => !selected || (Selection(i / BatchRows)[(i % BatchRows) >> 6] >> (i % BatchRows & 63) & 1) != 0);
        List<long> expected = [.. candidates
            .OrderBy(i => values[i] is null)
            .ThenBy(i => descending ? -(values[i] ?? 0) : values[i] ?? 0)
            .ThenBy(i => i)
            .Take(Keep)
            .Select(i => (long)i)
            .Order()];
        Assert.Equal(expected, kept);
    }

    /// <summary>The rows of batch <paramref name="batch"/> a selection keeps: two in three.</summary>
    private static ulong[] Selection(int batch)
    {
        ulong[] words = new ulong[(BatchRows + 63) / 64];
        for (int row = 0; row < BatchRows; row++)
        {
            if ((row + batch) % 3 != 0)
            {
                words[row >> 6] |= 1UL << (row & 63);
            }
        }

        return words;
    }

    private static Validity Bitmap(CanonicalArena arena, bool[] valid)
    {
        VortexBuffer bits = arena.Allocate((valid.Length + 7) / 8, 8, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < valid.Length; i++)
        {
            if (valid[i])
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        DTypeArena types = new DTypeArena();
        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), valid.Length, Validity.NonNullable, bits, 0));
    }
}
