// The pool's typed face: a block is read as the array it replaces, holds the length it was rented at whatever its
// element's size, and its bytes go back to the pool, which lends them again to a rent of any type.
using System;
using Vorticity.Aggregating;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class BlocksTests
{
    [Fact]
    public void A_block_holds_the_length_it_was_rented_at_and_room_to_its_class()
    {
        Block<int> block = Blocks.Rent<int>(100);
        try
        {
            Assert.Equal(100, block.Length);
            Assert.Equal(512, block.Bytes!.Length);
            Assert.Equal(128, block.Capacity);
            Assert.Equal(100, block.AsSpan().Length);
        }
        finally
        {
            Blocks.Return(block);
        }
    }

    [Fact]
    public void A_block_of_an_element_whose_size_is_no_power_of_two_keeps_its_length()
    {
        // A table whose mask is its length minus one: twelve bytes an entry round the bytes up, never the length.
        Block<Triple> block = Blocks.Rent<Triple>(1024);
        try
        {
            Assert.Equal(1024, block.Length);
            Assert.True(block.Capacity >= 1024);
            Assert.Equal(16384, block.Bytes!.Length);
        }
        finally
        {
            Blocks.Return(block);
        }
    }

    [Fact]
    public void An_empty_block_has_no_bytes_and_reads_as_an_empty_array()
    {
        Block<long> block = Blocks.Rent<long>(0);

        Assert.True(block.IsEmpty);
        Assert.Null(block.Bytes);
        Assert.True(block.AsSpan().IsEmpty);
        Assert.True(block.AsSpan(0).IsEmpty);
        Assert.True(block.AsSpan(0, 0).IsEmpty);
        Assert.Equal(0, block.Capacity);
        Blocks.Return(block);
    }

    [Fact]
    public void A_negative_length_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Blocks.Rent<int>(-1));

    [Fact]
    public void An_index_past_the_length_is_refused_even_within_the_bytes()
    {
        Block<int> block = Blocks.Rent<int>(3);
        try
        {
            block[2] = 7;
            Assert.Equal(7, block[2]);
            Assert.Throws<IndexOutOfRangeException>(() => block[3]);
            Assert.Throws<IndexOutOfRangeException>(() => block[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => block.AsSpan(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => block.AsSpan(1, 3));
        }
        finally
        {
            Blocks.Return(block);
        }
    }

    [Fact]
    public void Writes_through_the_index_are_read_through_the_span_and_under_fixed()
    {
        Block<ulong> block = Blocks.Rent<ulong>(16);
        try
        {
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = (ulong)i * 3;
            }

            Span<ulong> span = block;
            Assert.Equal(45UL, span[15]);
            unsafe
            {
                fixed (ulong* first = block)
                {
                    Assert.Equal(6UL, first[2]);
                }
            }
        }
        finally
        {
            Blocks.Return(block);
        }
    }

    [Fact]
    public void Resize_keeps_the_elements_and_zeroes_the_new_ones()
    {
        // Nine elements of four bytes: a block of 64 bytes, room for sixteen.
        Block<int> block = Blocks.Rent<int>(9);
        block.AsSpan().Fill(9);

        // Within its bytes: the same bytes, longer.
        byte[] bytes = block.Bytes!;
        Blocks.Resize(ref block, 12);
        Assert.Same(bytes, block.Bytes);
        Assert.Equal([9, 9, 9, 9, 9, 9, 9, 9, 9, 0, 0, 0], block.AsSpan().ToArray());

        // Past them: a block rented for it.
        Blocks.Resize(ref block, 100);
        Assert.NotSame(bytes, block.Bytes);
        Assert.Equal(100, block.Length);
        Assert.Equal(9, block[2]);
        Assert.Equal(0, block[99]);
        Blocks.Return(block);
    }

    [Fact]
    public void A_block_no_pool_lent_is_let_go()
    {
        Block<int> block = Blocks.Allocate<int>(3);
        Assert.Equal(12, block.Bytes!.Length);
        Assert.All(block.AsSpan().ToArray(), v => Assert.Equal(0, v));
        Blocks.Return(block);

        // Not a class's size: neither poisoned nor kept.
        Assert.Equal(0, block[0]);
    }

    [Fact]
    public void A_rent_under_a_query_reserves_its_bytes_and_its_return_gives_them_back()
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(64L << 20);
        using QueryMemory memory = new QueryMemory(budget);

        Block<long> block = Blocks.Rent<long>(memory, 1000, "a test");
        Assert.Equal(8192, memory.Held);
        Blocks.Return(memory, block);
        Assert.Equal(0, memory.Held);

        // Under a page, nothing is counted.
        Block<long> small = Blocks.Rent<long>(memory, 10, "a test");
        Assert.Equal(0, memory.Held);
        Blocks.Return(memory, small);
    }

    [Fact]
    public void A_rent_past_the_query_budget_is_refused()
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 20);
        using QueryMemory memory = new QueryMemory(budget);

        Assert.False(Blocks.TryRent(memory, 1 << 20, out Block<long> block));
        Assert.True(block.IsEmpty);
        Assert.Equal(0, memory.Held);
        Assert.Throws<VortexMemoryException>(() => Blocks.Rent<long>(memory, 1 << 20, "a test"));
    }

    private readonly record struct Triple(int A, int B, int C);
}

/// <summary>What the pool keeps between rents, which the shared pool's other users would move under a test's feet.</summary>
[Collection(nameof(SharedPoolCollection))]
[Trait("Category", "RunsAlone")]
public sealed class BlocksKeptTests
{
    [Fact]
    public void A_block_given_back_is_lent_again_to_a_rent_of_another_type()
    {
        Blocks.Trim();
        Block<long> longs = Blocks.Rent<long>(1 << 17);
        byte[] bytes = longs.Bytes!;
        Blocks.Return(longs);
        Assert.Equal(1L << 20, Blocks.HeldBytes);

        Block<double> doubles = Blocks.Rent<double>(1 << 17);
        Assert.Same(bytes, doubles.Bytes);
        Assert.Equal(0, Blocks.HeldBytes);
        Blocks.Return(doubles);
    }

    [Fact]
    public void A_block_given_back_is_poisoned_in_tests()
    {
        Block<byte> block = Blocks.Rent<byte>(64);
        block.Clear();
        byte[] bytes = block.Bytes!;
        Blocks.Return(block);

        Assert.All(bytes, b => Assert.Equal(0xDB, b));
    }

    [Fact]
    public void Trim_lets_go_of_every_block_kept()
    {
        Block<int> block = Blocks.Rent<int>(4096);
        Blocks.Return(block);
        Assert.True(Blocks.HeldBytes > 0);

        Blocks.Trim();
        Assert.Equal(0, Blocks.HeldBytes);
    }
}
