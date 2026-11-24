// The pool's typed face: a block is read as the array it replaces, holds the length it was rented at whatever its
// element's size, and its bytes go back to the pool, which lends them again to a rent of any type.
using System;
using System.Threading;
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

    /// <summary>
    /// A block made longer than the longest array holds leaves its owner empty: the bytes it held are back in the pool,
    /// and an owner that still named them would give them back a second time, for two renters to share.
    /// </summary>
    [Fact]
    public void A_block_ensured_past_the_longest_array_is_left_empty()
    {
        Block<long> block = Blocks.Rent<long>(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => Blocks.Ensure(ref block, int.MaxValue));
        Assert.True(block.IsDefault);
    }

    /// <summary>
    /// A growth stays a power of two up to 2^30 elements, and past it is the longest array's length, or the length needed
    /// when it passes that: never the negative length doubling and rounding in 32 bits made of it.
    /// </summary>
    [Fact]
    public void A_growth_past_two_to_the_thirty_is_never_negative()
    {
        Assert.Equal(1024, Blocks.Grown(1, 0));
        Assert.Equal(1 << 30, Blocks.Grown((1 << 29) + 1, 0));
        Assert.Equal(1 << 30, Blocks.Grown(5, 1 << 29));
        Assert.Equal(Array.MaxLength, Blocks.Grown((1 << 30) + 1, 0));
        Assert.Equal(Array.MaxLength, Blocks.Grown(10, 1 << 30));
        Assert.Equal(Array.MaxLength, Blocks.Grown(10, (int.MaxValue / 2) + 1));
        Assert.Equal(int.MaxValue, Blocks.Grown(int.MaxValue, 0));
    }

    /// <summary>
    /// A rent the longest array refuses after its account or its query took the bytes gives them back with the failure:
    /// the reservation would otherwise stay until the work or the question ends.
    /// </summary>
    [Fact]
    public void A_rent_past_the_longest_array_gives_back_what_it_reserved()
    {
        // Just past the longest array, some 2 GiB: a budget whose ceiling is the machine's grants it where it may refuse
        // the 16 GiB of int.MaxValue longs, as a runner of 16 GB did.
        int past = (Array.MaxLength / sizeof(long)) + 1;
        CountedAccount account = new CountedAccount(MemoryAccount.Caller);
        Assert.Throws<ArgumentOutOfRangeException>(() => Blocks.Rent<long>(account, past));
        Assert.Equal(0, account.Held);

        using QueryMemory memory = new QueryMemory(new QueryMemoryBudget(1L << 40, process: null));
        try
        {
            if (!Blocks.TryRent<long>(memory, past, out _))
            {
                // Refused before the rent, the bytes were never taken: the case this test is about did not arise.
                Assert.Equal(0, memory.Held);
                Assert.Skip("The machine's memory grants no 2 GiB: the budget refused the bytes before the rent.");
            }

            Assert.Fail("A block past the longest array was handed out.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        Assert.Equal(0, memory.Held);
    }

    /// <summary>A block over bytes from outside the pool is refused a length its bytes do not hold, or a negative one.</summary>
    [Fact]
    public void A_block_over_bytes_is_refused_a_length_they_do_not_hold()
    {
        byte[] bytes = new byte[8];
        Assert.Equal(2, Block<int>.Over(bytes, 2).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Block<int>.Over(bytes, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Block<int>.Over(bytes, -1));
        Assert.True(Block<int>.Over(null, 3).IsDefault);
    }

    /// <summary>A run of bytes refuses an advance past its block whatever its size, a negative one, and a negative truncation.</summary>
    [Fact]
    public void A_run_of_bytes_refuses_an_advance_past_its_block_whatever_its_size()
    {
        using BlockBytes bytes = new BlockBytes();
        bytes.Write([1, 2, 3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => bytes.Advance(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => bytes.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => bytes.Truncate(-1));
        Assert.Equal(3, bytes.Length);
    }

    /// <summary>The memory over an empty block pins as an empty memory over an array does: an empty handle, no failure.</summary>
    [Fact]
    public unsafe void Pinning_the_memory_of_an_empty_block_gives_an_empty_handle()
    {
        System.Buffers.MemoryHandle handle = new BlockMemory<int>(default).Memory.Pin();
        Assert.True(handle.Pointer == null);
        handle.Dispose();
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

    /// <summary>
    /// A block of 3 MB asked is one of a class of its own, given back on another thread and taken again after a collection and a sweep of
    /// the memory kept: the same bytes, none made new, the class asked for since the last sweep keeping what it holds. In a
    /// process of its own: in the suite's, a full collection another test ran swept the pool between the return and the
    /// test's own sweep, which then found the block unused, and the rent made another.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task A_large_block_given_back_on_another_thread_is_taken_again_past_a_collection_and_a_sweep()
    {
        System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(BlocksKeptTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(BlocksKeptTests).FullName}.{nameof(ALargeBlockTakenAgain)}");
        start.ArgumentList.Add("-noLogo");
        start.Environment[Child] = "1";
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
        System.Threading.Tasks.Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        System.Threading.Tasks.Task<string> errors = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        string said = await output + await errors;
        Assert.True(child.ExitCode == 0, said);
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    /// <summary>The variable that tells the process it is the child.</summary>
    private const string Child = "VORTICITY_BLOCKS_KEPT_CHILD";

    [Fact]
    public void ALargeBlockTakenAgain()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of A_large_block_given_back_on_another_thread_is_taken_again_past_a_collection_and_a_sweep.");
        // Past 64 KiB, a class steps by an eighth of its octave: 3 MB is one of its own, and 3 MB and a byte the next, an
        // eighth of 2 MiB larger.
        const int Asked = 3 << 20;
        Assert.Equal((3 << 20) + (1 << 18), Blocks.Rent<byte>(Asked + 1).Bytes!.Length);
        Block<byte> first = Blocks.Rent<byte>(Asked);
        Assert.Equal(3 << 20, first.Bytes!.Length);
        Thread other = new Thread(() => Blocks.Return(first));
        other.Start();
        other.Join();
        GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        Blocks.LetGoUnused();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Block<byte> again = Blocks.Rent<byte>(Asked);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(first.Bytes, again.Bytes);
        Assert.Equal(0, allocated);
        Blocks.Return(again);
        Console.WriteLine("ENDED");
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
