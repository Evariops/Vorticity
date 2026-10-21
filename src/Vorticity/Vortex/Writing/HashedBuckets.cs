using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// The (hash, block) pairs of a hashed postings index, each pair of a block once, partitioned as they come by the high bits
/// of their hash into buckets, a page of each in memory and the full pages in the writer's scratch; at the end each bucket
/// is sorted alone, in the order of the buckets, which is the hashes' order. What a sort of every pair takes, without a run
/// sorted a chunk and the k-way merges of them in passes: two passes over the pairs, the second a bucket at a time.
/// </summary>
/// <remarks>
/// <para>
/// The pairs come in the order of their blocks, and a bucket is sorted by its hashes stably, on the bits under the bucket's,
/// so that the blocks of a hash come out in their order, as the merge of the chunks' runs puts them: the same pairs, cut
/// into the same segments by the same sink, the same bytes.
/// </para>
/// <para>
/// A block's pairs are told apart by a table of the block's hashes, cleared by a stamp at every block, a few hundred
/// kilobytes that stay in the caches: a row costs its hash, a probe and an append, where a table of the chunk's keys
/// interned every row and sorted them at every chunk.
/// </para>
/// </remarks>
internal sealed class HashedBuckets : IDisposable
{
    /// <summary>The pairs a page holds: 12 KiB, the hashes then the blocks.</summary>
    internal const int PagePairs = 1024;

    /// <summary>The most bits of a hash the buckets are told apart by.</summary>
    internal const int MostBits = 10;

    /// <summary>The pairs a bucket aims at by the end, which sets how many buckets an object of a known size takes.</summary>
    internal const long BucketPairs = 1L << 22;

    private const int PageBytes = PagePairs * (sizeof(ulong) + sizeof(uint));

    private readonly int _bits;
    private readonly ulong[][] _hashes;
    private readonly uint[][] _blocks;
    private readonly int[] _fill;
    private readonly List<long>[] _pages;
    private readonly List<(int Bucket, ulong[] Hashes, uint[] Blocks)> _full = [];

    // The hashes of the open block, a slot each, live when its stamp is the block's.
    private ulong[] _seen = new ulong[1 << 14];
    private int[] _stamps = new int[1 << 14];
    private int _stamp = 1;
    private int _seenCount;

    /// <param name="expectedPairs">The pairs the index is expected to hold, or 0 when unknown: a bucket for every <see cref="BucketPairs"/>.</param>
    /// <param name="bits">The bits of a hash the buckets are told apart by, past <paramref name="expectedPairs"/>'s, or -1 for those.</param>
    internal HashedBuckets(long expectedPairs, int bits = -1)
    {
        if (bits < 0)
        {
            bits = 0;
            while (bits < MostBits && (expectedPairs >> bits) > BucketPairs)
            {
                bits++;
            }
        }

        _bits = Math.Min(bits, MostBits);
        int buckets = 1 << bits;
        _hashes = new ulong[buckets][];
        _blocks = new uint[buckets][];
        _fill = new int[buckets];
        _pages = new List<long>[buckets];
        for (int b = 0; b < buckets; b++)
        {
            _pages[b] = [];
        }
    }

    /// <summary>The buckets there are.</summary>
    internal int Buckets => _hashes.Length;

    /// <summary>The pairs noted, each (hash, block) once.</summary>
    internal long Pairs { get; private set; }

    /// <summary>Notes <paramref name="hash"/> in <paramref name="block"/>, the open block, once.</summary>
    internal void Note(ulong hash, uint block)
    {
        // The table of the block's hashes: a hash's slot from its low bits, the bucket's are its high ones.
        int mask = _seen.Length - 1;
        int slot = (int)(hash & (ulong)mask);
        while (_stamps[slot] == _stamp)
        {
            if (_seen[slot] == hash)
            {
                return;
            }

            slot = (slot + 1) & mask;
        }

        _stamps[slot] = _stamp;
        _seen[slot] = hash;
        if (++_seenCount * 2 > _seen.Length)
        {
            GrowSeen();
        }

        int bucket = _bits == 0 ? 0 : (int)(hash >> (64 - _bits));
        ulong[]? hashes = _hashes[bucket];
        if (hashes is null)
        {
            hashes = _hashes[bucket] = ArrayPool<ulong>.Shared.Rent(PagePairs);
            _blocks[bucket] = ArrayPool<uint>.Shared.Rent(PagePairs);
        }

        int at = _fill[bucket];
        hashes[at] = hash;
        _blocks[bucket][at] = block;
        Pairs++;
        if (++at == PagePairs)
        {
            _full.Add((bucket, hashes, _blocks[bucket]));
            _hashes[bucket] = null!;
            _blocks[bucket] = null!;
            at = 0;
        }

        _fill[bucket] = at;
    }

    /// <summary>Closes the open block: the next one's pairs are told apart afresh.</summary>
    internal void CloseBlock()
    {
        _stamp++;
        _seenCount = 0;
        if (_stamp == int.MaxValue)
        {
            Array.Clear(_stamps);
            _stamp = 1;
        }
    }

    /// <summary>Lays every full page in <paramref name="scratch"/>, its bucket noting where.</summary>
    internal async ValueTask SpillAsync(RunScratch scratch, CancellationToken cancellationToken)
    {
        byte[] page = ArrayPool<byte>.Shared.Rent(PageBytes);
        try
        {
            foreach ((int bucket, ulong[] hashes, uint[] blocks) in _full)
            {
                MemoryMarshal.AsBytes(hashes.AsSpan(0, PagePairs)).CopyTo(page);
                MemoryMarshal.AsBytes(blocks.AsSpan(0, PagePairs)).CopyTo(page.AsSpan(PagePairs * sizeof(ulong)));
                _pages[bucket].Add(await scratch.AppendAsync(page.AsMemory(0, PageBytes), cancellationToken).ConfigureAwait(false));
                ArrayPool<ulong>.Shared.Return(hashes);
                ArrayPool<uint>.Shared.Return(blocks);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
            _full.Clear();
        }
    }

    /// <summary>
    /// Bucket <paramref name="bucket"/>'s pairs, read back from <paramref name="scratch"/> and its open page, sorted by hash with
    /// the blocks of a hash in their order, into arrays of at least as many pairs, rented: how many there are.
    /// </summary>
    /// <remarks>Every full page is to be spilled first (<see cref="SpillAsync"/>).</remarks>
    internal async ValueTask<int> SortedAsync(
        int bucket, RunScratch scratch, BucketBuffers buffers, CancellationToken cancellationToken)
    {
        List<long> pages = _pages[bucket];
        int open = _fill[bucket];
        int count = checked((pages.Count * PagePairs) + open);
        buffers.Reserve(count);
        byte[] page = ArrayPool<byte>.Shared.Rent(PageBytes);
        try
        {
            int at = 0;
            foreach (long offset in pages)
            {
                await scratch.ReadAsync(offset, page.AsMemory(0, PageBytes), cancellationToken).ConfigureAwait(false);
                MemoryMarshal.Cast<byte, ulong>(page.AsSpan(0, PagePairs * sizeof(ulong))).CopyTo(buffers.Hashes.AsSpan(at));
                MemoryMarshal.Cast<byte, uint>(page.AsSpan(PagePairs * sizeof(ulong), PagePairs * sizeof(uint))).CopyTo(buffers.Blocks.AsSpan(at));
                at += PagePairs;
            }

            if (open > 0)
            {
                _hashes[bucket].AsSpan(0, open).CopyTo(buffers.Hashes.AsSpan(at));
                _blocks[bucket].AsSpan(0, open).CopyTo(buffers.Blocks.AsSpan(at));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }

        RadixSort(buffers, count, 64 - _bits);
        return count;
    }

    /// <summary>
    /// Sorts the first <paramref name="count"/> pairs of <paramref name="buffers"/> by the low <paramref name="bits"/> bits of
    /// their hashes, stably, eleven bits a pass, a pass skipped where every pair holds the same digit.
    /// </summary>
    private static void RadixSort(BucketBuffers buffers, int count, int bits)
    {
        const int Digit = 11;
        const int Radix = 1 << Digit;
        Span<int> starts = buffers.Starts;
        ulong[] hashes = buffers.Hashes;
        uint[] blocks = buffers.Blocks;
        ulong[] otherHashes = buffers.OtherHashes;
        uint[] otherBlocks = buffers.OtherBlocks;
        for (int shift = 0; shift < bits; shift += Digit)
        {
            starts.Clear();
            for (int i = 0; i < count; i++)
            {
                starts[(int)((hashes[i] >> shift) & (Radix - 1)) + 1]++;
            }

            if (starts[(int)((hashes[0] >> shift) & (Radix - 1)) + 1] == count)
            {
                continue;
            }

            for (int d = 0; d < Radix; d++)
            {
                starts[d + 1] += starts[d];
            }

            for (int i = 0; i < count; i++)
            {
                int at = starts[(int)((hashes[i] >> shift) & (Radix - 1))]++;
                otherHashes[at] = hashes[i];
                otherBlocks[at] = blocks[i];
            }

            (hashes, otherHashes) = (otherHashes, hashes);
            (blocks, otherBlocks) = (otherBlocks, blocks);
        }

        buffers.Settle(hashes, blocks);
    }

    private void GrowSeen()
    {
        ulong[] seen = _seen;
        int[] stamps = _stamps;
        _seen = new ulong[seen.Length * 2];
        _stamps = new int[seen.Length * 2];
        int mask = _seen.Length - 1;
        for (int i = 0; i < seen.Length; i++)
        {
            if (stamps[i] == _stamp)
            {
                int slot = (int)(seen[i] & (ulong)mask);
                while (_stamps[slot] == _stamp)
                {
                    slot = (slot + 1) & mask;
                }

                _stamps[slot] = _stamp;
                _seen[slot] = seen[i];
            }
        }
    }

    public void Dispose()
    {
        for (int b = 0; b < _hashes.Length; b++)
        {
            if (_hashes[b] is { } hashes)
            {
                ArrayPool<ulong>.Shared.Return(hashes);
                ArrayPool<uint>.Shared.Return(_blocks[b]);
                _hashes[b] = null!;
                _blocks[b] = null!;
            }
        }

        foreach ((int _, ulong[] hashes, uint[] blocks) in _full)
        {
            ArrayPool<ulong>.Shared.Return(hashes);
            ArrayPool<uint>.Shared.Return(blocks);
        }

        _full.Clear();
    }
}

/// <summary>The arrays a bucket is read back and sorted in, two of each, kept from one bucket to the next.</summary>
internal sealed class BucketBuffers
{
    private int[] _starts = new int[(1 << 11) + 1];

    /// <summary>The sorted pairs' hashes, valid up to the count the sort returned.</summary>
    internal ulong[] Hashes { get; private set; } = [];

    /// <summary>The sorted pairs' blocks.</summary>
    internal uint[] Blocks { get; private set; } = [];

    internal ulong[] OtherHashes { get; private set; } = [];

    internal uint[] OtherBlocks { get; private set; } = [];

    internal Span<int> Starts => _starts;

    /// <summary>Room for <paramref name="count"/> pairs in every array.</summary>
    internal void Reserve(int count)
    {
        if (Hashes.Length < count)
        {
            int size = Math.Max(count, Hashes.Length * 2);
            Hashes = new ulong[size];
            Blocks = new uint[size];
            OtherHashes = new ulong[size];
            OtherBlocks = new uint[size];
        }
    }

    /// <summary>Takes the arrays the sort ended in as the sorted ones, the others as its scratch.</summary>
    internal void Settle(ulong[] hashes, uint[] blocks)
    {
        if (!ReferenceEquals(hashes, Hashes))
        {
            (OtherHashes, Hashes) = (Hashes, hashes);
            (OtherBlocks, Blocks) = (Blocks, blocks);
        }
    }

    /// <summary>The 8 bytes of <paramref name="hash"/> as a key, little-endian, as the merge's keys are.</summary>
    internal static void Key(ulong hash, Span<byte> key) => BinaryPrimitives.WriteUInt64LittleEndian(key, hash);
}
