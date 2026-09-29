using System;
using System.Runtime.InteropServices;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class CanonicalOriginTests
{
    private static readonly DType Int64 = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);

    [Fact]
    public void AnArenaNotPublishedNamesNoOrigin()
    {
        RetainingArena arena = new RetainingArena();
        int node = Integers(arena, 8);

        Assert.False(arena.OriginOf(node).IsKnown);
    }

    [Fact]
    public void ANodeLentWholeKeepsTheOriginOfThePublishedNode()
    {
        RetainingArena chunk = new RetainingArena();
        int node = Integers(chunk, 8);
        chunk.Seal();
        RetainingArena batch = new RetainingArena();

        int lent = CanonicalSlice.LendAcross(chunk, batch, node, 0, 8);

        Assert.True(chunk.OriginOf(node).IsKnown);
        Assert.Equal(chunk.OriginOf(node), batch.OriginOf(lent));
    }

    // Only what is lent keeps its origin: a window a batch cuts from a retained chunk pays nothing
    // for a name no consumer asks of it.
    [Fact]
    public void ASliceThatIsNotALoanKeepsNoOrigin()
    {
        RetainingArena chunk = new RetainingArena();
        int node = Integers(chunk, 8);
        chunk.Seal();
        RetainingArena batch = new RetainingArena();

        int window = CanonicalSlice.SliceAcross(chunk, batch, node, 0, 8);

        Assert.False(batch.OriginOf(window).IsKnown);
    }

    [Fact]
    public void AnArenaOutsideRetentionKeepsNoOrigin()
    {
        RetainingArena chunk = new RetainingArena();
        int node = Integers(chunk, 8);
        chunk.Seal();
        CanonicalArena owned = new CanonicalArena();

        int lent = CanonicalSlice.LendAcross(chunk, owned, node, 0, 8);

        Assert.False(owned.OriginOf(lent).IsKnown);
    }

    [Fact]
    public void AWindowOfANodeKeepsNoOrigin()
    {
        RetainingArena chunk = new RetainingArena();
        int node = Integers(chunk, 8);
        chunk.Seal();
        RetainingArena batch = new RetainingArena();

        int window = CanonicalSlice.LendAcross(chunk, batch, node, 1, 6);

        Assert.False(batch.OriginOf(window).IsKnown);
    }

    [Fact]
    public void ADictionaryWindowViewsTheOriginOfItsValues()
    {
        RetainingArena chunk = new RetainingArena();
        int values = Integers(chunk, 4);
        int dictionary = Dictionary(chunk, values, 16);
        chunk.Seal();
        RetainingArena batch = new RetainingArena();

        int window = CanonicalSlice.SliceAcross(chunk, batch, dictionary, 4, 8);

        Assert.False(batch.OriginOf(window).IsKnown);
        Assert.Equal(chunk.OriginOf(values), batch.OriginOf(batch.GetNode(window).EncodedValuesIndex));
    }

    // Values decoded once for a chunk are lent to the arena of each window decoded from it, and
    // each window to the batches: a batch names the values' own publication, not the window's.
    [Fact]
    public void AnOriginSurvivesBeingLentOnward()
    {
        RetainingArena shared = new RetainingArena();
        int values = Integers(shared, 4);
        shared.Seal();
        RetainingArena window = new RetainingArena();
        int dictionary = Dictionary(window, CanonicalSlice.LendAcross(shared, window, values, 0, 4), 16);
        window.Seal();
        RetainingArena batch = new RetainingArena();

        int lent = CanonicalSlice.SliceAcross(window, batch, dictionary, 8, 8);

        Assert.Equal(shared.OriginOf(values), batch.OriginOf(batch.GetNode(lent).EncodedValuesIndex));
    }

    [Fact]
    public void EachPublicationIsAnotherOrigin()
    {
        RetainingArena chunk = new RetainingArena();
        int node = Integers(chunk, 8);
        chunk.Seal();
        CanonicalOrigin first = chunk.OriginOf(node);

        chunk.Seal();

        Assert.True(chunk.OriginOf(node).IsKnown);
        Assert.NotEqual(first, chunk.OriginOf(node));
    }

    [Fact]
    public void AResetForgetsThePublicationAndTheNodesLent()
    {
        RetainingArena chunk = new RetainingArena();
        int node = Integers(chunk, 8);
        chunk.Seal();
        RetainingArena batch = new RetainingArena();
        int lent = CanonicalSlice.LendAcross(chunk, batch, node, 0, 8);

        chunk.Reset();
        batch.ResetKeepingBlocks();
        int again = Integers(chunk, 8);
        int fresh = Integers(batch, 8);

        Assert.Equal(node, again);
        Assert.Equal(lent, fresh);
        Assert.False(chunk.OriginOf(again).IsKnown);
        Assert.False(batch.OriginOf(fresh).IsKnown);
    }

    private static int Integers(CanonicalArena arena, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = i * 3L;
        }

        return arena.AddPrimitive(Int64, count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Dictionary(CanonicalArena arena, int values, int rows)
    {
        int entries = arena.GetNode(values).Length;
        VortexBuffer buffer = arena.Allocate(rows * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        Span<uint> codes = MemoryMarshal.Cast<byte, uint>(bytes);
        for (int row = 0; row < rows; row++)
        {
            codes[row] = (uint)(row % entries);
        }

        return arena.AddDictionary(Int64, rows, Validity.NonNullable, buffer, values);
    }
}
