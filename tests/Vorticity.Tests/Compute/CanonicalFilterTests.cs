using System;
using System.Buffers.Binary;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class CanonicalFilterTests
{
    private const int Rows = 300;

    [Theory]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    internal void EachWidthGathersTheRowsItIsGivenInTheirOrder(PType ptype)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        int width = ptype.ByteWidth();
        VortexBuffer values = arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int row = 0; row < Rows; row++)
        {
            Stamp(bytes.Slice(row * width, width), row);
        }

        int node = arena.AddPrimitive(
            types.Primitive(ptype, Nullability.NonNullable), Rows, Validity.NonNullable, ptype, values);
        int[] order = Scattered();

        int gathered = CanonicalFilter.Apply(arena, node, order);

        ReadOnlySpan<byte> result = arena.GetNode(gathered).Values.Span;
        Assert.Equal(order.Length * width, result.Length);
        for (int i = 0; i < order.Length; i++)
        {
            Assert.True(
                result.Slice(i * width, width).SequenceEqual(bytes.Slice(order[i] * width, width)),
                $"row {i} should hold source row {order[i]}");
        }
    }

    [Fact]
    public void ViewsAreGatheredWholeInTheirOrder()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> bytes);
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = bytes.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, 12);
            for (int b = 4; b < 16; b++)
            {
                view[b] = (byte)(row + b);
            }
        }

        int node = arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, default);
        int[] order = Scattered();

        int gathered = CanonicalFilter.Apply(arena, node, order);

        ReadOnlySpan<byte> result = arena.GetNode(gathered).Views.Span;
        for (int i = 0; i < order.Length; i++)
        {
            Assert.True(result.Slice(i * 16, 16).SequenceEqual(bytes.Slice(order[i] * 16, 16)), $"view {i}");
        }
    }

    [Fact]
    public void ARowPastTheEndIsRefused()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer values = arena.Allocate(Rows * sizeof(long), sizeof(long), out _);
        int node = arena.AddPrimitive(
            types.Primitive(PType.I64, Nullability.NonNullable), Rows, Validity.NonNullable, PType.I64, values);

        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalFilter.Apply(arena, node, [0, 5, Rows, 7]));
    }

    [Fact]
    public void TheSelectionIsTheTrueRowsWhateverTheOthersHold()
    {
        byte[] states = new byte[Rows];
        int expected = 0;
        for (int row = 0; row < Rows; row++)
        {
            states[row] = (byte)((uint)row * 2_654_435_761u >> 30 & 3) switch
            {
                0 => Trilean.True,
                1 => Trilean.False,
                2 => Trilean.Unknown,
                _ => Trilean.True,
            };
            expected += states[row] == Trilean.True ? 1 : 0;
        }

        int[] indices = new int[Rows];
        int count = CanonicalFilter.Select(states, indices);

        Assert.Equal(expected, count);
        int next = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (states[row] == Trilean.True)
            {
                Assert.Equal(row, indices[next++]);
            }
        }
    }

    private static void Stamp(Span<byte> value, int row)
    {
        for (int b = 0; b < value.Length; b++)
        {
            value[b] = (byte)((row * 31) + (b * 7) + 1);
        }
    }

    private static int[] Scattered()
    {
        int[] order = new int[Rows + 20];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = (int)((uint)(i * 7_919) % Rows);
        }

        return order;
    }
}
