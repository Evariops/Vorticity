using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The pinned memory the pools' blocks are cut from: slabs, pinned arrays of one size, that of a region of the collector's
/// pinned heap, split in halves down to a page, a block one piece of a slab (a buddy allocator). The collector sees one
/// size of pinned array only, and a slab whose pieces all came back is let go whole: alone in its region, the region is
/// free for any generation of the heap; beside other slabs in a larger one, the hole it leaves fits the next slab.
/// </summary>
/// <remarks>
/// <para>
/// Each block a pinned array of its own, of every size the classes have, left the pinned heap with holes that only a
/// pinned array of their size could fill: 452 MB free of 1 190 MB, when an allocation of a compaction's writer failed
/// under a heap held to 1.5 GiB, with 700 MiB of blocks in all (2026-11-15). The collector neither compacts the pinned
/// heap nor lends its free space to the other generations, and a hard limit counts what it keeps committed.
/// </para>
/// <para>
/// A piece of a slab given back joins its free half, and the two their free half, up to half a slab: what a slab holds
/// free serves a block of any size that fits, where a free array served its own size only. One lock covers the slabs: a
/// rent reaches them only when its class keeps no block parked, and a block reaches them back only when the pool lets it
/// go.
/// </para>
/// </remarks>
internal static class Slabs
{
    /// <summary>
    /// The bytes of a slab's array: a region of the collector's pinned heap, 32 MiB, less the region's header and the
    /// array's. Measured in fresh processes, with and without a hard limit: an array of this size fills one region, one of
    /// 32 MiB takes a region of 64 MiB, two of 16 MiB share one. Under load the collector also lays slabs side by side in a
    /// larger region.
    /// </summary>
    internal const int SlabBytes = (32 << 20) - 64;

    /// <summary>The span a slab is cut in, 32 MiB: its last page, past the end of the array, is never handed out.</summary>
    private const int SpanShift = 25;

    /// <summary>The least piece: a page, the pools' least block.</summary>
    internal const int PageShift = 12;

    /// <summary>The sizes of piece, a page to half a slab: the span itself is never whole.</summary>
    private const int Orders = SpanShift - PageShift;

    /// <summary>The pages of a slab's span.</summary>
    private const int Pages = 1 << (SpanShift - PageShift);

    /// <summary>The least block a slab gives: a page.</summary>
    internal const int LeastBytes = 1 << PageShift;

    /// <summary>The largest block a slab gives: half a slab, 16 MiB. A larger block is a pinned array of its own.</summary>
    internal const int LargestBytes = 1 << (SpanShift - 1);

    private static readonly Lock Gate = new Lock();

    // The slabs, the oldest first: a piece is cut from the first that holds one, so that the blocks pack into the oldest
    // slabs and the newest come back empty first.
    private static readonly List<Slab> s_slabs = [];

    private static long s_heldBytes;
    private static long s_pieceBytes;

    /// <summary>The bytes of the slabs alive: what the blocks cut from them hold, and what they hold free between them.</summary>
    internal static long HeldBytes => Interlocked.Read(ref s_heldBytes);

    /// <summary>The bytes of the pieces cut from the slabs and not given back: the blocks they are.</summary>
    internal static long PieceBytes => Interlocked.Read(ref s_pieceBytes);

    /// <summary>
    /// The bytes the slabs alive hold free between their pieces: what the next blocks are cut from without a new slab, and
    /// what the process's pool counts as kept (<see cref="BlockPool.HeldBytes"/>).
    /// </summary>
    internal static long FreeBytes => Math.Max(0, HeldBytes - PieceBytes);

    /// <summary>
    /// Fills <paramref name="into"/> with the slabs alive, each with its pages taken as they are now and the rest of its census
    /// cleared (<see cref="Slab.CensusTaken"/>): what a pool orders its parked blocks by. Under the caller's census lock.
    /// </summary>
    internal static void Census(List<Slab> into)
    {
        into.Clear();
        lock (Gate)
        {
            foreach (Slab slab in s_slabs)
            {
                slab.CensusTaken = slab.TakenPages;
                slab.CensusParked = 0;
                slab.CensusRank = 0;
                slab.CensusKept = false;
                slab.CensusFreeing = false;
                into.Add(slab);
            }
        }
    }

    /// <summary>Whether <paramref name="slab"/> is alive: some of its pieces are not back.</summary>
    internal static bool IsAlive(Slab slab)
    {
        lock (Gate)
        {
            return s_slabs.Contains(slab);
        }
    }

    /// <summary>Whether a block of <paramref name="capacity"/> bytes is cut from a slab: a power of two from a page to half a slab.</summary>
    internal static bool Cuts(int capacity) => capacity is >= LeastBytes and <= LargestBytes && BitOperations.IsPow2(capacity);

    /// <summary>
    /// A piece of <paramref name="bytes"/> (<see cref="Cuts"/>): the slab it is cut from, its array, and where the piece
    /// starts in it, aligned to 64 bytes. A new slab when none holds one; null when the heap has no room for a new slab,
    /// and the block takes an array of its own, of its size.
    /// </summary>
    /// <remarks>
    /// A slab is 32 MiB where the block may be a page: a heap held to 384 MiB and full of what the host held refused a
    /// slab to a block of a few kilobytes that an array of its own would have fitted (2026-11-15). An allocation the
    /// collector refuses leaves the process whole: the block falls back to what it was before the slabs.
    /// </remarks>
    internal static (Slab Slab, byte[] Array, int Offset)? TryTake(int bytes)
    {
        int order = BitOperations.Log2((uint)bytes) - PageShift;
        lock (Gate)
        {
            // The oldest slab that holds one: the fullest instead was 6 to 10 % slower on a backlog's jobs, and left more
            // slabs at rest under 4 and 8 GiB (2026-11-16).
            foreach (Slab slab in s_slabs)
            {
                if (slab.Holds(order))
                {
                    int page = slab.TryTake(order);
                    Interlocked.Add(ref s_pieceBytes, bytes);
                    return (slab, slab.Array, slab.Base + (page << PageShift));
                }
            }

            Slab made;
            try
            {
                made = new Slab();
            }
            catch (OutOfMemoryException)
            {
                return null;
            }

            s_slabs.Add(made);
            Interlocked.Add(ref s_heldBytes, SlabBytes);
            Interlocked.Add(ref s_pieceBytes, bytes);
            return (made, made.Array, made.Base + (made.TryTake(order) << PageShift));
        }
    }

    /// <summary>
    /// Gives back the piece of <paramref name="bytes"/> at <paramref name="offset"/> in <paramref name="slab"/>: a slab
    /// whose pieces all came back is let go, its region free for the collector to take.
    /// </summary>
    internal static void Give(Slab slab, int offset, int bytes)
    {
        bool empty;
        lock (Gate)
        {
            Interlocked.Add(ref s_pieceBytes, -bytes);
            slab.Give((offset - slab.Base) >> PageShift, BitOperations.Log2((uint)bytes) - PageShift);
            empty = slab.IsEmpty;
            if (empty)
            {
                s_slabs.Remove(slab);
                Interlocked.Add(ref s_heldBytes, -SlabBytes);
            }
        }

        if (empty)
        {
            QueryMemoryBudget.BlockFreed(SlabBytes);
        }
    }

    /// <summary>
    /// One slab: its pinned array, where its span starts in it, aligned to 64 bytes, and its free pieces, a list of their
    /// first pages for each size, linked through the pages themselves.
    /// </summary>
    internal sealed class Slab
    {
        private static long s_made;

        // For the first page of a free piece, the piece's order plus one; 0 for any other page.
        private readonly sbyte[] _free = new sbyte[Pages];

        // The free pieces of each order, a list linked both ways through their first pages; -1 ends a list.
        private readonly int[] _heads = new int[Orders];
        private readonly int[] _next = new int[Pages];
        private readonly int[] _previous = new int[Pages];

        // The pages handed out, and those past the end of the array, which never are.
        private int _taken;
        private readonly int _past;

        // The orders whose list holds a free piece, a bit each: whether a piece fits is one test.
        private int _orders;

        internal unsafe Slab()
        {
            Id = Interlocked.Increment(ref s_made);
            Array = GC.AllocateUninitializedArray<byte>(SlabBytes, pinned: true);
            nint address = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(Array));
            Base = (int)(Alignment.AlignUp(address, VortexLimits.MaxAlignment) - address);
            _heads.AsSpan().Fill(-1);

            // The pages the array holds whole, cut in the largest aligned pieces; the pages past them are never free.
            int whole = (SlabBytes - Base) >> PageShift;
            _past = Pages - whole;
            _taken = _past;
            for (int page = 0; page < whole;)
            {
                int order = Math.Min(Orders - 1, BitOperations.TrailingZeroCount(page | Pages));
                while (page + (1 << order) > whole)
                {
                    order--;
                }

                Push(order, page);
                page += 1 << order;
            }
        }

        /// <summary>The slab's pinned array.</summary>
        internal byte[] Array { get; }

        /// <summary>Where the slab's span starts in its array: the first byte aligned to 64.</summary>
        internal int Base { get; }

        /// <summary>Whether every piece came back.</summary>
        internal bool IsEmpty => _taken == _past;

        /// <summary>The slab's number, in the order the slabs were made: the older, the lower.</summary>
        internal long Id { get; }

        /// <summary>The slabs made since the process started: what a bench tells the churn of slabs by.</summary>
        internal static long Made => Interlocked.Read(ref s_made);

        /// <summary>The pages of the pieces handed out and not back, rented or parked.</summary>
        internal int TakenPages => _taken - _past;

        /// <summary>Whether a free piece of <paramref name="order"/>, or of a larger one to cut it from, is left.</summary>
        internal bool Holds(int order) => (_orders >> order) != 0;

        /// <summary>
        /// A census's pages taken, read when the census began (<see cref="Census"/>): with the fields below, written and read
        /// under a pool's census lock only.
        /// </summary>
        internal int CensusTaken;

        /// <summary>The census's pages of the slab that the pool it is of keeps parked.</summary>
        internal int CensusParked;

        /// <summary>The slab's rank in the census, from 1 for the first to let go; 0 for none yet.</summary>
        internal int CensusRank;

        /// <summary>Whether a block the pool keeps whatever it is asked, within a class's floor, lies on the slab.</summary>
        internal bool CensusKept;

        /// <summary>Whether the pool is letting go of every block it keeps parked on the slab, to give the slab back whole.</summary>
        internal bool CensusFreeing;

        /// <summary>The first page of a free piece of <paramref name="order"/>, cut from a larger one when need be; -1 when the slab holds none.</summary>
        internal int TryTake(int order)
        {
            int above = _orders >> order;
            if (above == 0)
            {
                return -1;
            }

            int from = order + BitOperations.TrailingZeroCount(above);
            int page = _heads[from];
            Remove(from, page);

            // The half past the piece goes free, at each cut down to the order asked.
            while (from > order)
            {
                from--;
                Push(from, page + (1 << from));
            }

            _taken += 1 << order;
            return page;
        }

        /// <summary>Gives back the piece of <paramref name="order"/> at <paramref name="page"/>, joined to its free halves.</summary>
        internal void Give(int page, int order)
        {
            _taken -= 1 << order;
            while (order < Orders - 1)
            {
                int buddy = page ^ (1 << order);
                if (_free[buddy] != order + 1)
                {
                    break;
                }

                Remove(order, buddy);
                page = Math.Min(page, buddy);
                order++;
            }

            Push(order, page);
        }

        private void Push(int order, int page)
        {
            int head = _heads[order];
            _next[page] = head;
            _previous[page] = -1;
            if (head >= 0)
            {
                _previous[head] = page;
            }

            _heads[order] = page;
            _free[page] = (sbyte)(order + 1);
            _orders |= 1 << order;
        }

        private void Remove(int order, int page)
        {
            int next = _next[page];
            int previous = _previous[page];
            if (previous >= 0)
            {
                _next[previous] = next;
            }
            else
            {
                _heads[order] = next;
                if (next < 0)
                {
                    _orders &= ~(1 << order);
                }
            }

            if (next >= 0)
            {
                _previous[next] = previous;
            }

            _free[page] = 0;
        }
    }
}
