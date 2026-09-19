// Vtable deduplication for the FlatBuffers builder.
//
// This is NOT an optimization that can be skipped. docs/01-scope.md §3 names vtable dedup as
// mandatory: "without it, wide-schema metadata inflates and the 105% size target starts with a
// self-inflicted handicap". A Vortex file with a 5 000-column struct emits one ArrayNode table per
// column; without dedup that is 5 000 vtables of identical bytes.
//
// The reference FlatBuffers builders scan every previously written vtable linearly, which is
// O(tables^2) and is exactly the case that hurts on a wide schema. This cache instead hashes the
// candidate vtable's bytes and probes a bucket chain, so a match costs one hash plus one
// SequenceEqual.
using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace Vorticity.Serialization.FlatBuffers;

/// <summary>
/// Index of the vtables already written into a <see cref="FlatBufferBuilder"/>'s scratch buffer,
/// keyed by their exact bytes.
/// </summary>
/// <remarks>
/// <para>
/// Vtables are addressed by their <em>back-offset</em> — the builder's <c>Offset</c> at the moment
/// the vtable was finished — never by an absolute index, because the scratch array is reallocated
/// as it grows and every absolute position moves. A back-offset <c>o</c> in an array of
/// <c>capacity</c> bytes starts at absolute index <c>capacity - o</c>.
/// </para>
/// <para>
/// Two vtables are interchangeable only when every byte matches: a vtable encodes
/// <c>table_size</c> and the per-field byte offset of each slot, not merely which fields are
/// present. Two tables with the same field ids but different value widths therefore produce
/// different vtables and must not be merged.
/// </para>
/// </remarks>
internal sealed class VTableCache
{
    private const int MinimumCapacity = 8;

    /// <summary>Bucket heads, as a 1-based index into the entry arrays. 0 means "empty".</summary>
    private int[] _buckets;

    /// <summary>Chain links, as a 1-based index into the entry arrays. 0 terminates the chain.</summary>
    private int[] _next;

    /// <summary>Back-offset of each stored vtable.</summary>
    private int[] _offsets;

    /// <summary>Byte length of each stored vtable, so a length mismatch is rejected without a read.</summary>
    private int[] _lengths;

    private uint[] _hashes;
    private int _count;

    internal VTableCache(int capacity = MinimumCapacity)
    {
        int entries = capacity < MinimumCapacity ? MinimumCapacity : RoundUpToPowerOfTwo(capacity);
        _offsets = new int[entries];
        _lengths = new int[entries];
        _hashes = new uint[entries];
        _next = new int[entries];
        _buckets = new int[entries * 2];
    }

    /// <summary>Number of distinct vtables written so far. Drives the builder's dedup diagnostics.</summary>
    internal int Count => _count;

    /// <summary>Forgets every entry, keeping the arrays for reuse.</summary>
    internal void Clear()
    {
        _count = 0;
        Array.Clear(_buckets);
    }

    /// <summary>XxHash3-64 over the candidate vtable's bytes, truncated to a bucket index.</summary>
    /// <remarks>
    /// <para>
    /// No byte of the file depends on this value. It picks a bucket, an exact comparison settles
    /// every collision, and a distinct vtable is stored once — so the byte-equal entry is the one
    /// found whatever order a chain is walked, and truncating to 32 bits costs nothing but a
    /// slightly longer chain.
    /// </para>
    /// <para>
    /// This was FNV-1a, kept on the grounds that it was deterministic where a randomized hash would
    /// not be. That argument does not separate the two: XxHash3 at a fixed seed is exactly as
    /// reproducible, run to run and machine to machine. What does separate them is speed, measured
    /// on the real shape of the input rather than assumed. A vtable is <c>(slots + 2) * 2</c> bytes,
    /// and the 31 969 the byte-exact write suite produces average 9,6 of them, 96,6 % at or below
    /// sixteen, the longest 22. Over that distribution FNV-1a costs 2,91 ns a hash against 1,71
    /// here. The gain is small in absolute terms — 93 µs against 55 for every vtable that suite
    /// writes — and the reason to take it is that it leaves the writer one hash family instead of
    /// two.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Hash(ReadOnlySpan<byte> bytes) => (uint)XxHash3.HashToUInt64(bytes);

    /// <summary>
    /// Returns the back-offset of a stored vtable whose bytes equal <paramref name="candidate"/>,
    /// or <c>0</c> when there is none. <c>0</c> is never a valid vtable back-offset: a vtable is
    /// preceded by at least the table it belongs to.
    /// </summary>
    /// <param name="buffer">The builder's scratch array.</param>
    /// <param name="capacity">Its current length, used to turn a back-offset into an index.</param>
    /// <param name="candidate">The freshly written vtable's bytes.</param>
    /// <param name="hash"><see cref="Hash"/> of <paramref name="candidate"/>.</param>
    internal int Find(byte[] buffer, int capacity, ReadOnlySpan<byte> candidate, uint hash)
    {
        int entry = _buckets[(int)(hash & (uint)(_buckets.Length - 1))];
        while (entry != 0)
        {
            int i = entry - 1;
            if (_hashes[i] == hash &&
                _lengths[i] == candidate.Length &&
                new ReadOnlySpan<byte>(buffer, capacity - _offsets[i], candidate.Length)
                    .SequenceEqual(candidate))
            {
                return _offsets[i];
            }

            entry = _next[i];
        }

        return 0;
    }

    /// <summary>Records a vtable that has just been written and kept.</summary>
    internal void Add(int offset, int length, uint hash)
    {
        if (_count == _offsets.Length)
        {
            GrowEntries();
        }

        int index = _count++;
        _offsets[index] = offset;
        _lengths[index] = length;
        _hashes[index] = hash;

        int bucket = (int)(hash & (uint)(_buckets.Length - 1));
        _next[index] = _buckets[bucket];
        _buckets[bucket] = index + 1;
    }

    private void GrowEntries()
    {
        int size = _offsets.Length * 2;
        Array.Resize(ref _offsets, size);
        Array.Resize(ref _lengths, size);
        Array.Resize(ref _hashes, size);
        Array.Resize(ref _next, size);

        // The bucket count is tied to the entry count, so growing one rehashes the other. Keeping
        // the load factor at 0.5 keeps the chains at one or two links for realistic vtable counts.
        _buckets = new int[size * 2];
        uint mask = (uint)(_buckets.Length - 1);
        for (int i = 0; i < _count; i++)
        {
            int bucket = (int)(_hashes[i] & mask);
            _next[i] = _buckets[bucket];
            _buckets[bucket] = i + 1;
        }
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        // Bucket indexing masks with (length - 1), which is only a modulo for a power of two.
        int result = MinimumCapacity;
        while (result < value && result < (1 << 30))
        {
            result <<= 1;
        }

        return result;
    }
}
