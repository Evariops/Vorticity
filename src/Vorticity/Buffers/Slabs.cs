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
    private const int PageShift = 12;

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

    /// <summary>Whether a block of <paramref name="capacity"/> bytes is cut from a slab: a power of two from a page to half a slab.</summary>
    internal static bool Cuts(int capacity) => capacity is >= LeastBytes and <= LargestBytes && BitOperations.IsPow2(capacity);

    /// <summary>
    /// A piece of <paramref name="bytes"/> (<see cref="Cuts"/>): the slab it is cut from, its array, and where the piece
    /// starts in it, aligned to 64 bytes. A new slab when none holds one.
    /// </summary>
    internal static (Slab Slab, byte[] Array, int Offset) Take(int bytes)
    {
        int order = BitOperations.Log2((uint)bytes) - PageShift;
        lock (Gate)
        {
            Interlocked.Add(ref s_pieceBytes, bytes);
            foreach (Slab slab in s_slabs)
            {
                if (slab.TryTake(order) is >= 0 and int page)
                {
                    return (slab, slab.Array, slab.Base + (page << PageShift));
                }
            }

            Slab made = new Slab();
            s_slabs.Add(made);
            Interlocked.Add(ref s_heldBytes, SlabBytes);
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
        // For the first page of a free piece, the piece's order plus one; 0 for any other page.
        private readonly sbyte[] _free = new sbyte[Pages];

        // The free pieces of each order, a list linked both ways through their first pages; -1 ends a list.
        private readonly int[] _heads = new int[Orders];
        private readonly int[] _next = new int[Pages];
        private readonly int[] _previous = new int[Pages];

        // The pages handed out, and those past the end of the array, which never are.
        private int _taken;
        private readonly int _past;

        internal unsafe Slab()
        {
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

        /// <summary>The first page of a free piece of <paramref name="order"/>, cut from a larger one when need be; -1 when the slab holds none.</summary>
        internal int TryTake(int order)
        {
            int from = order;
            while (from < Orders && _heads[from] < 0)
            {
                from++;
            }

            if (from == Orders)
            {
                return -1;
            }

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
            }

            if (next >= 0)
            {
                _previous[next] = previous;
            }

            _free[page] = 0;
        }
    }
}
