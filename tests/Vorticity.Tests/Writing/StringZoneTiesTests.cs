// A text column's block bounds where values tie a bound's first four bytes: a value that opens as the least or the greatest
// so far but ends below or above it, met late in a block, after the rows settled a vector at a time, moves the bound, as
// the block's extremes cut say, with and without nulls, under every instruction set the test runs under.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class StringZoneTiesTests
{
    private const int Block = 4_096;
    private const int Limit = 16;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AValueThatTiesABoundsPrefixMovesIt(bool nulls, bool greatest)
    {
        Random random = new Random((nulls ? 3 : 2) + (greatest ? 10 : 0));
        string[] prefixes = ["mmmm", "mmmn", "pppp", "pppq", "ssss"];
        for (int round = 0; round < 6; round++)
        {
            byte[]?[] values = new byte[]?[Block * 3];
            for (int i = 0; i < values.Length; i++)
            {
                if (nulls && random.Next(9) == 0)
                {
                    continue;
                }

                // Most rows open between the bounds' prefixes; late in each block, rows that tie the least's, or the
                // greatest's, four first bytes and end beyond it: one side at a time, so that every other group of rows
                // settles a vector at a time.
                int inBlock = i % Block;
                string text = inBlock > Block - 300 && random.Next(4) == 0
                    ? (greatest ? "ssss" + new string('z', random.Next(1, 30)) : "mmmm" + new string('a', random.Next(1, 6)))
                    : prefixes[random.Next(1, 4)] + new string((char)('b' + random.Next(20)), random.Next(1, 20));
                if (inBlock < 2)
                {
                    text = inBlock == 0 ? "mmmm" + "q" : "ssss" + "c";
                }

                values[i] = Encoding.ASCII.GetBytes(text);
            }

            CanonicalArena arena = new CanonicalArena(64);
            int node = Node(arena, values);
            StringZones zones = new StringZones(Limit);
            for (int start = 0; start < values.Length; start += Block)
            {
                zones.Accumulate(arena, arena.GetNode(node), start, Block);
                zones.Close();
            }

            IReadOnlyList<ZoneString> all = zones.All(values.Length / Block)!;
            for (int b = 0; b < all.Count; b++)
            {
                List<byte[]> held = [.. values.Skip(b * Block).Take(Block).OfType<byte[]>()];
                byte[] least = held.Aggregate((x, y) => x.AsSpan().SequenceCompareTo(y) <= 0 ? x : y);
                byte[] most = held.Aggregate((x, y) => x.AsSpan().SequenceCompareTo(y) >= 0 ? x : y);
                Assert.True(all[b].Present);
                Assert.Equal(StringBounds.LowerBound(least, Limit, utf8: true), all[b].Min.ToArray());
                Assert.Equal(StringBounds.UpperBound(most, Limit, utf8: true), all[b].Max?.ToArray());
            }

            zones.Release();
        }
    }

    private static int Node(CanonicalArena arena, byte[]?[] values)
    {
        DTypeArena types = new DTypeArena();
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> view);
        VortexBuffer heap = arena.Allocate(values.Sum(v => v?.Length ?? 0) + 16, 16, out Span<byte> bytes);
        VortexBuffer bits = arena.Allocate((values.Length + 7) / 8, 8, out Span<byte> valid);
        view.Clear();
        valid.Clear();
        int at = 0;
        for (int row = 0; row < values.Length; row++)
        {
            if (values[row] is not { } value)
            {
                continue;
            }

            valid[row >> 3] |= (byte)(1 << (row & 7));
            Span<byte> v = view.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(v, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(v[4..]);
                continue;
            }

            value.AsSpan(0, 4).CopyTo(v[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(v[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(v[12..], at);
            value.CopyTo(bytes[at..]);
            at += value.Length;
        }

        int mask = arena.AddBool(types.Bool(Nullability.NonNullable), values.Length, Validity.NonNullable, bits, 0);
        return arena.AddVarBinView(types.Utf8(Nullability.Nullable), values.Length, Validity.Bitmap(mask), views, [heap]);
    }
}
