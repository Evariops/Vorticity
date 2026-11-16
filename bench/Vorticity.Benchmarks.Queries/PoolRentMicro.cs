using System;
using System.Diagnostics;
using System.Globalization;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a rent of the process's pools costs, by where its block comes from. The pool of blocks: parked in its class, cut from
/// a slab the pool keeps, or from a new slab once everything was let go, for the blocks of 4 to 256 KiB a question rents, 64
/// of each class. The typed pool: parked in its class, or allocated once everything was let go, for blocks of 16 bytes to
/// 64 KiB, 64 of each class. What the pools' floors keep, and what they spare.
/// </summary>
internal static class PoolRentMicro
{
    private const int Each = 64;

    private static readonly int[] Sizes = [4 << 10, 8 << 10, 16 << 10, 32 << 10, 64 << 10, 128 << 10, 256 << 10];

    // In longs: 16 bytes to 64 KiB, one in four classes.
    private static readonly int[] Lengths = [2, 8, 32, 128, 512, 2048, 8192];

    internal static int Run(int rounds)
    {
        BlockPool pool = MemoryAccount.Pool;
        BlockOwner[] held = new BlockOwner[Sizes.Length * Each];
        Block<long>[] typed = new Block<long>[Lengths.Length * Each];
        double parked = 0, cut = 0, fresh = 0, typedParked = 0, typedFresh = 0;
        int n = Math.Max(rounds, 3);
        for (int round = 0; round < n; round++)
        {
            // Parked: the blocks of the round before, back in their classes.
            RentAll(pool, held);
            ReturnAll(pool, held);
            parked += Time(pool, held);

            // Cut from a slab kept: the classes emptied, one block held so that its slab stays, with its room free.
            BlockOwner keeper = pool.Rent(MemoryAccount.Kept, 4 << 10, 64);
            pool.Trim();
            cut += Time(pool, held);
            pool.Trim();
            pool.Return(keeper);

            // From new slabs: everything let go and collected.
            pool.Trim();
            GC.Collect();
            fresh += Time(pool, held);
            pool.Trim();

            // The typed pool: parked, then allocated once everything was let go.
            RentAll(typed);
            ReturnAll(typed);
            typedParked += Time(typed);
            Blocks.Trim();
            GC.Collect();
            typedFresh += Time(typed);
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"poolrent: {held.Length} rents of 4 to 256 KiB, a question's: parked {parked / n:F0} µs, cut from a slab kept {cut / n:F0} µs, from new slabs {fresh / n:F0} µs"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"poolrent: {typed.Length} typed rents of 16 bytes to 64 KiB: parked {typedParked / n:F0} µs, allocated {typedFresh / n:F0} µs"));
        return 0;
    }

    /// <summary>Microseconds to rent every block, given back after.</summary>
    private static double Time(BlockPool pool, BlockOwner[] held)
    {
        long started = Stopwatch.GetTimestamp();
        RentAll(pool, held);
        double micros = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        ReturnAll(pool, held);
        return micros;
    }

    /// <summary>Microseconds to rent every typed block, given back after.</summary>
    private static double Time(Block<long>[] typed)
    {
        long started = Stopwatch.GetTimestamp();
        RentAll(typed);
        double micros = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        ReturnAll(typed);
        return micros;
    }

    private static void RentAll(BlockPool pool, BlockOwner[] held)
    {
        for (int i = 0; i < held.Length; i++)
        {
            held[i] = pool.Rent(MemoryAccount.Kept, Sizes[i / Each], 64);
        }
    }

    private static void ReturnAll(BlockPool pool, BlockOwner[] held)
    {
        foreach (BlockOwner owner in held)
        {
            pool.Return(owner);
        }
    }

    private static void RentAll(Block<long>[] typed)
    {
        for (int i = 0; i < typed.Length; i++)
        {
            typed[i] = Blocks.Rent<long>(Lengths[i / Each]);
        }
    }

    private static void ReturnAll(Block<long>[] typed)
    {
        foreach (Block<long> block in typed)
        {
            Blocks.Return(block);
        }
    }
}
