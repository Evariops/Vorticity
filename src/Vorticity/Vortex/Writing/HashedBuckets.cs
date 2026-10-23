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

    /// <summary>The most bits a bucket past twice its aim is cut again by at the end: 1,024 parts, an open page each at most.</summary>
    private const int MostSplitBits = 10;

    private const int PageBytes = PagePairs * (sizeof(ulong) + sizeof(uint));

    private readonly int _bits;
    private readonly long _bucketPairs;
    private readonly ulong[][] _hashes;
    private readonly uint[][] _blocks;
    private readonly int[] _fill;
    private readonly List<long>[] _pages;
    private readonly List<(int Bucket, ulong[] Hashes, uint[] Blocks)> _full = [];

    // The buckets the end sorts, in the hashes' order, once those past twice their aim are cut again (RefineAsync); null
    // before, the buckets noted then being those.
    private List<Part>? _parts;

    // The hashes of the open block, a slot each, live when its stamp is the block's: a slot's hash and stamp side by side,
    // a probe a line of the caches, where two arrays took two.
    private Seen[] _seen = new Seen[1 << 14];
    private int _stamp = 1;
    private int _seenCount;

    private struct Seen
    {
        internal ulong Hash;
        internal int Stamp;
    }

    /// <param name="expectedPairs">The pairs the index is expected to hold, or 0 when unknown: a bucket for every <paramref name="bucketPairs"/>.</param>
    /// <param name="bits">The bits of a hash the buckets are told apart by, past <paramref name="expectedPairs"/>'s, or -1 for those.</param>
    /// <param name="bucketPairs">The pairs a bucket aims at, <see cref="BucketPairs"/> but in tests.</param>
    internal HashedBuckets(long expectedPairs, int bits = -1, long bucketPairs = BucketPairs)
    {
        _bucketPairs = bucketPairs;
        if (bits < 0)
        {
            bits = 0;
            while (bits < MostBits && (expectedPairs >> bits) > bucketPairs)
            {
                bits++;
            }
        }

        _bits = Math.Min(bits, MostBits);
        int buckets = 1 << _bits;
        _hashes = new ulong[buckets][];
        _blocks = new uint[buckets][];
        _fill = new int[buckets];
        _pages = new List<long>[buckets];
        for (int b = 0; b < buckets; b++)
        {
            _pages[b] = [];
        }
    }

    /// <summary>The buckets there are, those cut again at the end each a bucket of its own.</summary>
    internal int Buckets => _parts?.Count ?? _hashes.Length;

    /// <summary>The pairs noted, each (hash, block) once.</summary>
    internal long Pairs { get; private set; }

    /// <summary>The pairs of the largest bucket: what the buffers of a bucket's sort are made for once.</summary>
    internal int Largest
    {
        get
        {
            long largest = 0;
            for (int b = 0; b < Buckets; b++)
            {
                largest = Math.Max(largest, PairsOf(b));
            }

            return checked((int)largest);
        }
    }

    /// <summary>The pairs of bucket <paramref name="bucket"/>, in the scratch and in its open page.</summary>
    private long PairsOf(int bucket) => _parts is { } parts
        ? ((long)parts[bucket].Pages.Count * PagePairs) + parts[bucket].Fill
        : ((long)_pages[bucket].Count * PagePairs) + _fill[bucket];

    /// <summary>Notes <paramref name="hash"/> in <paramref name="block"/>, the open block, once.</summary>
    internal void Note(ulong hash, uint block)
    {
        // The table of the block's hashes: a hash's slot from its low bits, the bucket's are its high ones.
        Seen[] seen = _seen;
        int mask = seen.Length - 1;
        int slot = (int)(hash & (ulong)mask);
        while (seen[slot].Stamp == _stamp)
        {
            if (seen[slot].Hash == hash)
            {
                return;
            }

            slot = (slot + 1) & mask;
        }

        seen[slot].Stamp = _stamp;
        seen[slot].Hash = hash;
        if (++_seenCount * 2 > seen.Length)
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
            Array.Clear(_seen);
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
        (List<long> pages, ulong[]? openHashes, uint[]? openBlocks, int open, int bits) = _parts is { } parts
            ? (parts[bucket].Pages, parts[bucket].Hashes, parts[bucket].Blocks, parts[bucket].Fill, parts[bucket].Bits)
            : (_pages[bucket], _hashes[bucket], _blocks[bucket], _fill[bucket], _bits);
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
                openHashes.AsSpan(0, open).CopyTo(buffers.Hashes.AsSpan(at));
                openBlocks.AsSpan(0, open).CopyTo(buffers.Blocks.AsSpan(at));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }

        RadixSort(buffers, count, 64 - bits);
        return count;
    }

    /// <summary>
    /// Cuts every bucket past twice its aim again by the next bits of its hashes, as many as bring its parts to the aim, up to
    /// <see cref="MostSplitBits"/>: its pages read back from <paramref name="scratch"/> in their order and its open page after
    /// them, each pair to its part's open page, the full ones laid in the scratch. The parts, in the order of those bits, hold
    /// the blocks of a hash in their order as the bucket did, and are sorted alone: an object written with no bound on its
    /// rows, all its pairs in one bucket, sorts parts of the aim rather than every pair at once.
    /// </summary>
    /// <remarks>Every full page is to be spilled first (<see cref="SpillAsync"/>); the buckets are those parts after.</remarks>
    internal async ValueTask RefineAsync(RunScratch scratch, CancellationToken cancellationToken)
    {
        List<Part> parts = new List<Part>(_hashes.Length);
        for (int b = 0; b < _hashes.Length; b++)
        {
            Part whole = new Part(_pages[b], _bits) { Hashes = _hashes[b], Blocks = _blocks[b], Fill = _fill[b] };
            _hashes[b] = null!;
            _blocks[b] = null!;
            long count = ((long)whole.Pages.Count * PagePairs) + whole.Fill;
            int extra = 0;
            while (count > 2 * _bucketPairs && (count >> extra) > _bucketPairs && extra < MostSplitBits && _bits + extra < 64)
            {
                extra++;
            }

            if (extra == 0)
            {
                parts.Add(whole);
                continue;
            }

            parts.AddRange(await SplitAsync(whole, extra, scratch, cancellationToken).ConfigureAwait(false));
        }

        _parts = parts;
    }

    /// <summary>The parts of <paramref name="whole"/> by the <paramref name="extra"/> bits of a hash past its own.</summary>
    private async ValueTask<Part[]> SplitAsync(Part whole, int extra, RunScratch scratch, CancellationToken cancellationToken)
    {
        Part[] parts = new Part[1 << extra];
        for (int p = 0; p < parts.Length; p++)
        {
            parts[p] = new Part([], whole.Bits + extra);
        }

        List<(Part Part, ulong[] Hashes, uint[] Blocks)> full = [];
        byte[] page = ArrayPool<byte>.Shared.Rent(PageBytes);
        try
        {
            foreach (long offset in whole.Pages)
            {
                await scratch.ReadAsync(offset, page.AsMemory(0, PageBytes), cancellationToken).ConfigureAwait(false);
                Route(
                    MemoryMarshal.Cast<byte, ulong>(page.AsSpan(0, PagePairs * sizeof(ulong))),
                    MemoryMarshal.Cast<byte, uint>(page.AsSpan(PagePairs * sizeof(ulong), PagePairs * sizeof(uint))));
                await SpillPartsAsync().ConfigureAwait(false);
            }

            if (whole.Fill > 0)
            {
                Route(whole.Hashes.AsSpan(0, whole.Fill), whole.Blocks.AsSpan(0, whole.Fill));
                await SpillPartsAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
            whole.Return();
            foreach ((Part _, ulong[] hashes, uint[] blocks) in full)
            {
                ArrayPool<ulong>.Shared.Return(hashes);
                ArrayPool<uint>.Shared.Return(blocks);
            }
        }

        return parts;

        // Each pair to its part's open page, in their order; a page that fills waits for the scratch.
        void Route(ReadOnlySpan<ulong> hashes, ReadOnlySpan<uint> blocks)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                Part part = parts[(int)((hashes[i] << whole.Bits) >> (64 - extra))];
                part.Hashes ??= ArrayPool<ulong>.Shared.Rent(PagePairs);
                part.Blocks ??= ArrayPool<uint>.Shared.Rent(PagePairs);
                part.Hashes[part.Fill] = hashes[i];
                part.Blocks[part.Fill] = blocks[i];
                if (++part.Fill == PagePairs)
                {
                    full.Add((part, part.Hashes, part.Blocks));
                    part.Hashes = null;
                    part.Blocks = null;
                    part.Fill = 0;
                }
            }
        }

        // The pages filled, laid in the scratch, each its part noting where.
        async ValueTask SpillPartsAsync()
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(PageBytes);
            try
            {
                while (full.Count > 0)
                {
                    (Part part, ulong[] hashes, uint[] blocks) = full[^1];
                    full.RemoveAt(full.Count - 1);
                    MemoryMarshal.AsBytes(hashes.AsSpan(0, PagePairs)).CopyTo(bytes);
                    MemoryMarshal.AsBytes(blocks.AsSpan(0, PagePairs)).CopyTo(bytes.AsSpan(PagePairs * sizeof(ulong)));
                    ArrayPool<ulong>.Shared.Return(hashes);
                    ArrayPool<uint>.Shared.Return(blocks);
                    part.Pages.Add(await scratch.AppendAsync(bytes.AsMemory(0, PageBytes), cancellationToken).ConfigureAwait(false));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }

    /// <summary>
    /// Sorts the first <paramref name="count"/> pairs of <paramref name="buffers"/> by the low <paramref name="bits"/> bits of
    /// their hashes, stably.
    /// </summary>
    private static void RadixSort(BucketBuffers buffers, int count, int bits)
    {
        if (Compute.RadixSort.Sort(
                buffers.Hashes.AsSpan(0, count), buffers.Blocks.AsSpan(0, count),
                buffers.OtherHashes.AsSpan(0, count), buffers.OtherBlocks.AsSpan(0, count), bits, buffers.Starts))
        {
            buffers.Settle(buffers.OtherHashes, buffers.OtherBlocks);
        }
    }

    private void GrowSeen()
    {
        Seen[] seen = _seen;
        _seen = new Seen[seen.Length * 2];
        int mask = _seen.Length - 1;
        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i].Stamp == _stamp)
            {
                int slot = (int)(seen[i].Hash & (ulong)mask);
                while (_seen[slot].Stamp == _stamp)
                {
                    slot = (slot + 1) & mask;
                }

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
        foreach (Part part in _parts ?? [])
        {
            part.Return();
        }
    }

    /// <summary>A bucket the end sorts: its pages in the scratch, its open page and how full, and the bits of a hash it is told apart by.</summary>
    private sealed class Part(List<long> pages, int bits)
    {
        internal List<long> Pages { get; } = pages;

        internal int Bits { get; } = bits;

        internal ulong[]? Hashes { get; set; }

        internal uint[]? Blocks { get; set; }

        internal int Fill { get; set; }

        /// <summary>Gives its open page back to the pool.</summary>
        internal void Return()
        {
            if (Hashes is { } hashes)
            {
                ArrayPool<ulong>.Shared.Return(hashes);
                Hashes = null;
            }

            if (Blocks is { } blocks)
            {
                ArrayPool<uint>.Shared.Return(blocks);
                Blocks = null;
            }
        }
    }
}

/// <summary>The arrays a bucket is read back and sorted in, two of each, kept from one bucket to the next.</summary>
internal sealed class BucketBuffers
{
    private int[] _starts = new int[Compute.RadixSort.Counts];

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
