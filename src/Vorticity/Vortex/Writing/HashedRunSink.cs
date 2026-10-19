using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>
/// Lays one hashed postings run: its (hash, block) pairs in hash order, cut into segments of about
/// the segment size at the end of a key, each segment two arrays. The first holds the gaps between
/// the pairs' hashes cut to what the run keeps (<see cref="KeyRunOptions.HashShift"/>), the first
/// pair's gap 0 since the segment's first key bounds it. The second holds each pair's block, from the
/// run's first. The gaps average about 2¹⁰ whatever the run's size, so they pack into about 13 bits,
/// where the hashes themselves would take the 64.
/// </summary>
internal sealed class HashedRunSink : IRunSink
{
    private readonly int _segmentEntries;
    private readonly int _firstBlock;
    private readonly int _blockCount;
    private readonly long _declared;
    private readonly int _shift;
    private readonly List<KeySegment> _segments = [];
    private readonly List<PendingPayload> _payloads = [];
    private uint[] _gaps = [];
    private uint[] _blocks = [];
    private int _count;
    private long _entries;

    // The open segment: its first and last hash, the last pair's cut hash, and the gap the next block
    // of the key at hand carries -- its first block carries the key's, the rest 0.
    private ulong _first;
    private ulong _last;
    private ulong _previous;
    private uint _gap;

    /// <summary>
    /// A sink for a run of <paramref name="pairs"/> pairs over <paramref name="blockCount"/> blocks from
    /// <paramref name="firstBlock"/>: the count sets the cut, and the run lists exactly that many.
    /// </summary>
    internal HashedRunSink(int segmentEntries, int firstBlock, int blockCount, long pairs)
    {
        _segmentEntries = Math.Max(segmentEntries, 1);
        _firstBlock = firstBlock;
        _blockCount = blockCount;
        _declared = pairs;
        _shift = KeyRunOptions.HashShift((ulong)Math.Max(pairs, 0));
        Open();
    }

    public bool Add(ReadOnlySpan<byte> key, long row) =>
        throw new InvalidOperationException("Hashed postings place a value at its blocks, never at its rows.");

    public void BeginKey(ReadOnlySpan<byte> key)
    {
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(key);
        ulong cut = hash >> _shift;

        // A gap past 32 bits, which only a run of very few pairs spread over the whole hash range
        // could meet, opens a segment of its own rather than widen the array.
        if (_count > 0 && cut - _previous > uint.MaxValue)
        {
            Cut();
            Open();
        }

        if (_count == 0)
        {
            _first = hash;
            _previous = cut;
        }

        _gap = (uint)(cut - _previous);
        _previous = cut;
        _last = hash;
    }

    public void AddBlocks(ReadOnlySpan<uint> blocks)
    {
        if (_count + blocks.Length > _gaps.Length)
        {
            int size = Math.Max(_count + blocks.Length, _gaps.Length * 2);
            _gaps = Grown(_gaps, _count, size);
            _blocks = Grown(_blocks, _count, size);
        }

        for (int i = 0; i < blocks.Length; i++)
        {
            _gaps[_count] = _gap;
            _gap = 0;
            _blocks[_count] = checked(blocks[i] - (uint)_firstBlock);
            _count++;
        }

        _entries += blocks.Length;
    }

    public bool EndKey()
    {
        if (_count >= _segmentEntries)
        {
            Cut();
            Open();
        }

        return false;
    }

    /// <summary>Never due: its segments are cut in memory.</summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Cuts the last segment and returns the run.</summary>
    /// <exception cref="InvalidOperationException">The merge gave other pairs than the run was declared with, whose cut would then mislead a probe.</exception>
    internal KeyRun Finish()
    {
        if (_count > 0 || _segments.Count == 0)
        {
            Cut();
        }
        else
        {
            ArrayPool<uint>.Shared.Return(_gaps);
            ArrayPool<uint>.Shared.Return(_blocks);
            _gaps = [];
            _blocks = [];
        }

        if (_entries != _declared)
        {
            throw new InvalidOperationException(
                $"A hashed run declared with {_declared} pairs received {_entries}: its hashes would be cut to the wrong width.");
        }

        return new KeyRun(_firstBlock, _blockCount, _entries, _segments, _payloads);
    }

    private void Open()
    {
        int capacity = Math.Min(_segmentEntries, 1 << 16) + 64;
        _gaps = ArrayPool<uint>.Shared.Rent(capacity);
        _blocks = ArrayPool<uint>.Shared.Rent(capacity);
        _count = 0;
    }

    /// <summary>Hands the open segment's arrays to its payloads, its bounds the first and last hash.</summary>
    private void Cut()
    {
        int count = _count;
        _segments.Add(new KeySegment((ulong)count, count == 0 ? [] : Key(_first), count == 0 ? [] : Key(_last)));
        _payloads.Add(PendingPayload.RentedU32(_gaps, count, compress: true));
        _payloads.Add(PendingPayload.RentedU32(_blocks, count, compress: true));
        _gaps = [];
        _blocks = [];
        _count = 0;
    }

    private static byte[] Key(ulong hash)
    {
        byte[] key = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(key, hash);
        return key;
    }

    private static uint[] Grown(uint[] rented, int count, int size)
    {
        uint[] grown = ArrayPool<uint>.Shared.Rent(size);
        rented.AsSpan(0, count).CopyTo(grown);
        ArrayPool<uint>.Shared.Return(rented);
        return grown;
    }
}
