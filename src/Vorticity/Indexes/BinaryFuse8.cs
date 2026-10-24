using System;
using System.Buffers.Binary;

namespace Vorticity.Indexes;

/// <summary>
/// A binary fuse filter of 8-bit fingerprints over a set of 64-bit hashes: about 1.125 bytes a key for a large set, a
/// false positive in 256, and three lookups a question. A key the set holds is always found.
/// </summary>
/// <remarks>
/// <para>
/// T. M. Graf and D. Lemire, <i>Binary Fuse Filters: Fast and Smaller Than Xor Filters</i> (2022): each key lands in
/// three slots of consecutive segments, and the slots are peeled in an order that lets each key's fingerprint be the
/// exclusive or of its three slots. The construction here follows the authors' reference, its seeds drawn from a fixed
/// sequence, so the same keys give the same bytes.
/// </para>
/// <para>
/// The keys are hashes already; a key is mixed with the filter's seed before it is placed, so that a set whose keys
/// collide in their slots under one seed is built again under the next.
/// </para>
/// </remarks>
internal sealed class BinaryFuse8
{
    /// <summary>The bytes before the fingerprints: the seed, the segment's length, the segments' count and the slots'.</summary>
    internal const int HeaderBytes = 20;

    /// <summary>The seeds a construction may try before it gives up, which no set of distinct keys reaches.</summary>
    private const int MaxSeeds = 100;

    private readonly ulong _seed;
    private readonly uint _segmentLength;
    private readonly uint _segmentLengthMask;
    private readonly uint _segmentCountLength;
    private readonly byte[] _fingerprints;

    private BinaryFuse8(ulong seed, uint segmentLength, uint segmentCount, byte[] fingerprints)
    {
        _seed = seed;
        _segmentLength = segmentLength;
        _segmentLengthMask = segmentLength - 1;
        SegmentCount = segmentCount;
        _segmentCountLength = segmentCount * segmentLength;
        _fingerprints = fingerprints;
    }

    /// <summary>The segments a key's first slot may fall in.</summary>
    internal uint SegmentCount { get; }

    /// <summary>The bytes the filter takes written.</summary>
    internal int Bytes => HeaderBytes + _fingerprints.Length;

    /// <summary>The filter of <paramref name="keys"/>, each once; null when they cannot be placed, which distinct keys never are.</summary>
    internal static BinaryFuse8? Build(ReadOnlySpan<ulong> keys)
    {
        int size = keys.Length;
        uint segmentLength = size == 0 ? 4 : Math.Min(1u << (int)Math.Floor((Math.Log(size) / Math.Log(3.33)) + 2.25), 1u << 18);
        double sizeFactor = size <= 1 ? 0 : Math.Max(1.125, 0.875 + (0.25 * Math.Log(1_000_000.0) / Math.Log(size)));
        uint capacity = size <= 1 ? 0 : (uint)Math.Round(size * sizeFactor);
        // As the reference does it, a capacity under a segment wraps to no slot before the segments are counted.
        uint initial = unchecked(((capacity + segmentLength - 1) / segmentLength) - 2);
        uint arrayLength = unchecked((initial + 2) * segmentLength);
        uint segmentCount = (arrayLength + segmentLength - 1) / segmentLength;
        segmentCount = segmentCount <= 2 ? 1 : segmentCount - 2;
        arrayLength = (segmentCount + 2) * segmentLength;

        byte[] fingerprints = new byte[arrayLength];
        ulong counter = 0x726b2b9d438b9d4d;
        ulong seed = SplitMix(ref counter);
        BinaryFuse8 filter = new BinaryFuse8(seed, segmentLength, segmentCount, fingerprints);
        if (size == 0)
        {
            return filter;
        }

        ulong[] order = new ulong[size + 1];
        uint[] alone = new uint[arrayLength];
        byte[] counts = new byte[arrayLength];
        byte[] found = new byte[size];
        ulong[] xors = new ulong[arrayLength];
        int blockBits = 1;
        while ((1u << blockBits) < segmentCount)
        {
            blockBits++;
        }

        uint block = 1u << blockBits;
        uint[] starts = new uint[block];
        Span<uint> slots = stackalloc uint[5];
        int placed = 0;
        for (int attempt = 0; ; attempt++)
        {
            if (attempt == MaxSeeds)
            {
                return null;
            }

            Array.Clear(order);
            Array.Clear(counts);
            Array.Clear(xors);
            order[size] = 1;
            for (uint i = 0; i < block; i++)
            {
                starts[i] = (uint)(((ulong)i * (ulong)size) >> blockBits);
            }

            ulong mask = block - 1;
            for (int i = 0; i < size; i++)
            {
                ulong hash = Murmur(keys[i] + seed);
                ulong index = hash >> (64 - blockBits);
                while (order[starts[index]] != 0)
                {
                    index = (index + 1) & mask;
                }

                order[starts[index]] = hash;
                starts[index]++;
            }

            bool failed = false;
            for (int i = 0; i < size; i++)
            {
                ulong hash = order[i];
                uint h0 = filter.Slot(0, hash);
                uint h1 = filter.Slot(1, hash);
                uint h2 = filter.Slot(2, hash);
                counts[h0] += 4;
                xors[h0] ^= hash;
                counts[h1] += 4;
                counts[h1] ^= 1;
                xors[h1] ^= hash;
                counts[h2] += 4;
                counts[h2] ^= 2;
                xors[h2] ^= hash;
                failed |= counts[h0] < 4 || counts[h1] < 4 || counts[h2] < 4;
            }

            if (failed)
            {
                seed = SplitMix(ref counter);
                continue;
            }

            int queued = 0;
            for (uint i = 0; i < arrayLength; i++)
            {
                alone[queued] = i;
                queued += (counts[i] >> 2) == 1 ? 1 : 0;
            }

            placed = 0;
            while (queued > 0)
            {
                uint index = alone[--queued];
                if ((counts[index] >> 2) != 1)
                {
                    continue;
                }

                ulong hash = xors[index];
                slots[1] = filter.Slot(1, hash);
                slots[2] = filter.Slot(2, hash);
                slots[3] = filter.Slot(0, hash);
                slots[4] = slots[1];
                byte at = (byte)(counts[index] & 3);
                found[placed] = at;
                order[placed] = hash;
                placed++;

                uint other = slots[at + 1];
                alone[queued] = other;
                queued += (counts[other] >> 2) == 2 ? 1 : 0;
                counts[other] -= 4;
                counts[other] ^= Mod3((byte)(at + 1));
                xors[other] ^= hash;

                other = slots[at + 2];
                alone[queued] = other;
                queued += (counts[other] >> 2) == 2 ? 1 : 0;
                counts[other] -= 4;
                counts[other] ^= Mod3((byte)(at + 2));
                xors[other] ^= hash;
            }

            if (placed == size)
            {
                break;
            }

            seed = SplitMix(ref counter);
        }

        filter = new BinaryFuse8(seed, segmentLength, segmentCount, fingerprints);
        for (int i = placed - 1; i >= 0; i--)
        {
            ulong hash = order[i];
            byte xor = (byte)Fingerprint(hash);
            byte at = found[i];
            slots[0] = filter.Slot(0, hash);
            slots[1] = filter.Slot(1, hash);
            slots[2] = filter.Slot(2, hash);
            slots[3] = slots[0];
            slots[4] = slots[1];
            fingerprints[slots[at]] = (byte)(xor ^ fingerprints[slots[at + 1]] ^ fingerprints[slots[at + 2]]);
        }

        return filter;
    }

    /// <summary>Whether <paramref name="key"/> may be in the set: false is a proof that it is not.</summary>
    internal bool MayContain(ulong key)
    {
        if (_fingerprints.Length == 0)
        {
            return false;
        }

        ulong hash = Murmur(key + _seed);
        byte f = (byte)Fingerprint(hash);
        ulong high = Math.BigMul(hash, _segmentCountLength, out _);
        uint h0 = (uint)high;
        uint h1 = h0 + _segmentLength;
        uint h2 = h1 + _segmentLength;
        h1 ^= (uint)(hash >> 18) & _segmentLengthMask;
        h2 ^= (uint)hash & _segmentLengthMask;
        return (byte)(f ^ _fingerprints[h0] ^ _fingerprints[h1] ^ _fingerprints[h2]) == 0;
    }

    /// <summary>Writes the filter into <paramref name="into"/>, which holds <see cref="Bytes"/>.</summary>
    internal void Write(Span<byte> into)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(into, _seed);
        BinaryPrimitives.WriteUInt32LittleEndian(into[8..], _segmentLength);
        BinaryPrimitives.WriteUInt32LittleEndian(into[12..], SegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(into[16..], (uint)_fingerprints.Length);
        _fingerprints.CopyTo(into[HeaderBytes..]);
    }

    /// <summary>The filter <see cref="Write"/> wrote at the start of <paramref name="bytes"/>, and the bytes it took.</summary>
    /// <exception cref="VortexFormatException">The bytes do not hold a filter.</exception>
    internal static BinaryFuse8 Read(ReadOnlySpan<byte> bytes, out int read)
    {
        if (bytes.Length < HeaderBytes)
        {
            throw new VortexFormatException("A presence filter ends before its header.");
        }

        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        uint segmentLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint segmentCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (segmentLength == 0 || (segmentLength & (segmentLength - 1)) != 0 || segmentLength > (1u << 18)
            || length != (ulong)(segmentCount + 2) * segmentLength || length > (uint)(bytes.Length - HeaderBytes))
        {
            throw new VortexFormatException($"A presence filter declares {length} slots in segments of {segmentLength}, which its bytes do not hold.");
        }

        read = HeaderBytes + (int)length;
        return new BinaryFuse8(seed, segmentLength, segmentCount, bytes.Slice(HeaderBytes, (int)length).ToArray());
    }

    /// <summary>Slot <paramref name="index"/> of a mixed hash, of the three it lands in.</summary>
    private uint Slot(int index, ulong hash)
    {
        ulong h = Math.BigMul(hash, _segmentCountLength, out _);
        h += (ulong)index * _segmentLength;
        ulong low = hash & ((1UL << 36) - 1);
        h ^= (low >> (36 - (18 * index))) & _segmentLengthMask;
        return (uint)h;
    }

    private static ulong Fingerprint(ulong hash) => hash ^ (hash >> 32);

    private static byte Mod3(byte x) => x > 2 ? (byte)(x - 3) : x;

    private static ulong Murmur(ulong h)
    {
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }

    private static ulong SplitMix(ref ulong seed)
    {
        ulong z = seed += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
