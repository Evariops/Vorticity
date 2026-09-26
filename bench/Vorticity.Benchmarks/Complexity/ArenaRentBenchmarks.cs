// What a batch pays to take back the blocks the batch before kept, as the blocks per batch grow.
//
// A scan's arena keeps the blocks of one batch, idle, for the next one's allocations of the same size
// class. `Original` holds them in one chain with the blocks already handed out this batch, which each
// rent walks from its head: the k-th rent of a batch steps over the k - 1 before it, so a batch of A
// blocks costs A squared over two steps. `Library` is the arena itself, whose ring keeps the idle
// blocks just ahead of the block rented last, so that a rent looks at one block when the batch rents
// what the batch before rented, and at a bounded few when it does not.
//
// A stack of idle blocks per size class answers in one step too, and measures about a third slower
// than the ring; it also needs a table the arena would carry, and a scan builds some of its contexts
// per call, so a field on every arena is bytes on every such call.
using System;
using System.Numerics;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One batch of allocations and the reset that keeps its blocks, against the blocks per batch.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ArenaRentBenchmarks
{
    /// <summary>
    /// Blocks rented per batch, which is about the columns decoded by it.
    /// </summary>
    /// <remarks>
    /// Sixteen is a narrow table and a thousand a wide one; four thousand is past what a batch
    /// decodes, and is there to show the shape the other points only suggest.
    /// </remarks>
    [Params(16, 256, 1_024, 4_096)]
    public int Blocks { get; set; }

    /// <summary>
    /// Whether each batch rents the sizes the batch before rented, in the same order, or a sequence
    /// that moves: the classes shifted by one column and a column in eight skipped, at a place that
    /// changes from batch to batch, as columns whose chunks another batch decoded are.
    /// </summary>
    [Params(Sequence.Same, Sequence.Moving)]
    public Sequence Order { get; set; }

    /// <summary>How one batch's rents follow the last one's.</summary>
    public enum Sequence
    {
        /// <summary>The same sizes in the same order.</summary>
        Same,

        /// <summary>Shifted and thinned differently every batch.</summary>
        Moving,
    }

    // Three size classes in turn, as columns of different widths rent them, and one small buffer
    // per column carved from a slab, as a validity bitmap is.
    private static readonly int[] Sizes = [2 * 1024 + 64, 6 * 1024, 12 * 1024];

    private const int SmallBytes = 1024;

    private AlignedBufferPool _pool = null!;
    private ChainedBlocks _original = null!;
    private CanonicalArena _library = null!;
    private int _batch;

    /// <summary>A pool of its own, so that no other class's blocks are parked in it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _pool = AlignedBufferPool.Graded(32 * 1024 * 1024, 8, demandBudget: 1L << 30);
        _original = new ChainedBlocks(_pool);
        _library = new CanonicalArena(64, _pool);
    }

    /// <summary>Gives every block back and frees the pool.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _original.Reset();
        _library.Reset();
        _pool.Trim();
    }

    /// <summary>The single chain, walked from its head by every rent.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        int bytes = 0;
        int batch = ++_batch;
        for (int i = 0; i < Blocks; i++)
        {
            if (Size(i, batch) is int size)
            {
                bytes += _original.Allocate(SmallBytes, 8);
                bytes += _original.Allocate(size, 64);
            }
        }

        _original.ResetKeepingBlocks();
        return bytes;
    }

    /// <summary>The arena of the library.</summary>
    [Benchmark]
    public int Library()
    {
        int bytes = 0;
        int batch = ++_batch;
        for (int i = 0; i < Blocks; i++)
        {
            if (Size(i, batch) is int size)
            {
                bytes += _library.AllocateUninitialized(SmallBytes, 8, out _).Length;
                bytes += _library.AllocateUninitialized(size, 64, out _).Length;
            }
        }

        _library.ResetKeepingBlocks();
        return bytes;
    }

    /// <summary>What column <paramref name="i"/> of batch <paramref name="batch"/> rents, or nothing.</summary>
    private int? Size(int i, int batch)
    {
        if (Order == Sequence.Same)
        {
            return Sizes[i % Sizes.Length];
        }

        return (i + batch) % 8 == 0 ? null : Sizes[(i + batch) % Sizes.Length];
    }

    /// <summary>A block in one of the chains below, and whether it was kept idle.</summary>
    /// <remarks>
    /// The arena chained its blocks through fields of the block owners themselves. A node of its
    /// own here carries the same three fields a rent reads, so that a step of the walk touches one
    /// object, as it did.
    /// </remarks>
    private sealed class Block(NativeSegmentOwner owner)
    {
        internal readonly NativeSegmentOwner Owner = owner;
        internal readonly int Capacity = owner.Capacity;
        internal Block? Next;
        internal bool Idle;
    }

    /// <summary>The arena's blocks before the change: one chain, in service and idle together.</summary>
    private sealed class ChainedBlocks(AlignedBufferPool pool)
    {
        private readonly AlignedBufferPool _pool = pool;
        private Block? _owned;
        private int _slabUsed = -1;

        internal int Allocate(int byteLength, int alignment)
        {
            if ((uint)byteLength <= SmallAllocation)
            {
                return Carve(byteLength, alignment);
            }

            return Own(Rent(byteLength, alignment)).Owner.WritableSpan.Length;
        }

        internal void ResetKeepingBlocks()
        {
            Block? kept = null;
            Block? owner = _owned;
            while (owner is not null)
            {
                Block? next = owner.Next;
                if (!owner.Idle && owner.Owner.RefCount == 1)
                {
                    owner.Idle = true;
                    owner.Next = kept;
                    kept = owner;
                }
                else
                {
                    owner.Next = null;
                    owner.Idle = false;
                    _pool.Return(owner.Owner);
                }

                owner = next;
            }

            _owned = kept;
            _slabUsed = -1;
        }

        internal void Reset()
        {
            Block? owner = _owned;
            while (owner is not null)
            {
                Block? next = owner.Next;
                owner.Next = null;
                owner.Idle = false;
                _pool.Return(owner.Owner);
                owner = next;
            }

            _owned = null;
            _slabUsed = -1;
        }

        private Block Rent(int length, int alignment)
        {
            Block? previous = null;
            Block? idle = _owned;
            int capacity = idle is null ? 0 : CapacityFor(_pool, length);
            while (idle is not null && !(idle.Idle && idle.Capacity == capacity))
            {
                previous = idle;
                idle = idle.Next;
            }

            if (idle is null)
            {
                return new Block(_pool.Rent(length, alignment));
            }

            if (previous is null)
            {
                _owned = idle.Next;
            }
            else
            {
                previous.Next = idle.Next;
            }

            idle.Next = null;
            idle.Idle = false;
            NativeSegmentOwner.CheckLengthAndAlignment(length, alignment);
            idle.Owner.ResetForRent(length);
            return idle;
        }

        private int Carve(int byteLength, int alignment)
        {
            NativeSegmentOwner.CheckLengthAndAlignment(byteLength, alignment);
            int start = (_slabUsed + alignment - 1) & -alignment;
            Block? slab = _owned;
            if (_slabUsed < 0 || start + byteLength > slab!.Owner.Length)
            {
                int slabBytes = _slabUsed < 0 ? FirstSlabBytes : Math.Min(slab!.Owner.Length * 2, LargestSlabBytes);
                slab = Rent(slabBytes, 64);
                slab.Next = _owned;
                _owned = slab;
                start = 0;
            }

            _slabUsed = start + byteLength;
            return slab.Owner.WritableSpan.Slice(start, byteLength).Length;
        }

        private Block Own(Block owner)
        {
            if (_slabUsed < 0)
            {
                owner.Next = _owned;
                _owned = owner;
            }
            else
            {
                owner.Next = _owned!.Next;
                _owned.Next = owner;
            }

            return owner;
        }
    }

    private const int SmallAllocation = 1024;

    private const int FirstSlabBytes = 4 * 1024;

    private const int LargestSlabBytes = 64 * 1024;

    private static int CapacityFor(AlignedBufferPool pool, int length) =>
        length > pool.MaxPooledLength ? length
            : length <= 4096 ? 4096 : (int)BitOperations.RoundUpToPowerOf2((uint)length);
}
