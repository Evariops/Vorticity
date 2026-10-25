using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The partials a dataset's handle learned of its objects, each of one family (<see cref="Aggregating.LearnedFamily"/>):
/// kept in memory, under a budget of bytes, and in the session's synopsis cache when it has one, which keeps them for the
/// handles and the processes after. An object never changes, so a partial of it stays exact as long as the object lives,
/// under the identity its postscript carries.
/// </summary>
/// <remarks>
/// In the cache, a partial's bytes are followed by their XXH3-128, which a read checks: bytes torn on the disk are no
/// partial, and the object is read as rows again.
/// </remarks>
internal sealed class LearnedPartials
{
    /// <summary>The bytes the handle keeps in memory at most; past them, partials are kept in the cache alone.</summary>
    internal const long Budget = 64L << 20;

    /// <summary>
    /// The bytes of a partial kept at most: some 10⁴ groups of four measures. A family past them in an object, a column added
    /// that holds too many values there, answers the question and is not kept.
    /// </summary>
    internal const int MaxBytes = 1 << 20;

    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Family), AggregationPartial> _held = new();
    private long _bytes;

    // The families whose partial an object would hold past MaxBytes: their questions read its rows, learning nothing.
    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Family), bool> _tooLarge = new();

    /// <summary>Whether family <paramref name="family"/> learned of the object <paramref name="uid"/> a partial too large to keep, which its questions no longer learn.</summary>
    internal bool TooLarge(UInt128 uid, UInt128 family) => _tooLarge.ContainsKey((uid, family));

    /// <summary>The partial of family <paramref name="family"/> of the object <paramref name="uid"/>, from memory or the cache; false when neither holds it.</summary>
    internal bool TryGet(UInt128 uid, UInt128 family, SynopsisCache? cache, out AggregationPartial partial)
    {
        if (_held.TryGetValue((uid, family), out partial!))
        {
            return true;
        }

        if (cache is null || !cache.TryReadSealed(NameOf(uid, family), MaxBytes + 16, out SegmentOwner owner))
        {
            return false;
        }

        using (owner)
        {
            ReadOnlySpan<byte> bytes = owner.Buffer.Span;
            if (bytes.Length < 16 || XxHash128.HashToUInt128(bytes[..^16]) != ReadHash(bytes[^16..]))
            {
                cache.DropSealed(NameOf(uid, family));
                return false;
            }

            partial = AggregationPartial.Of(bytes[..^16].ToArray());
        }

        Hold(uid, family, partial);
        return true;
    }

    /// <summary>Whether the handle or the cache holds the partial of family <paramref name="family"/> of the object <paramref name="uid"/>, with no read of it.</summary>
    internal bool Holds(UInt128 uid, UInt128 family, SynopsisCache? cache) =>
        _held.ContainsKey((uid, family)) || cache?.HoldsSealed(NameOf(uid, family)) == true;

    /// <summary>Keeps <paramref name="partial"/>, of family <paramref name="family"/> of the object <paramref name="uid"/>, when it is no larger than <see cref="MaxBytes"/>.</summary>
    internal void Keep(UInt128 uid, UInt128 family, AggregationPartial partial, SynopsisCache? cache)
    {
        ReadOnlySpan<byte> bytes = partial.Bytes.Span;
        if (bytes.Length > MaxBytes)
        {
            _tooLarge.TryAdd((uid, family), true);
            return;
        }

        Hold(uid, family, partial);
        if (cache is not null)
        {
            byte[] kept = new byte[bytes.Length + 16];
            bytes.CopyTo(kept);
            WriteHash(kept.AsSpan(bytes.Length), XxHash128.HashToUInt128(bytes));
            cache.AdmitSealed(uid, NameOf(uid, family), kept);
        }
    }

    private void Hold(UInt128 uid, UInt128 family, AggregationPartial partial)
    {
        long bytes = partial.Bytes.Length;
        if (Interlocked.Add(ref _bytes, bytes) > Budget || !_held.TryAdd((uid, family), partial))
        {
            Interlocked.Add(ref _bytes, -bytes);
        }
    }

    private static string NameOf(UInt128 uid, UInt128 family) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-{family:x32}-partial");

    private static UInt128 ReadHash(ReadOnlySpan<byte> bytes) =>
        new UInt128(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes));

    private static void WriteHash(Span<byte> destination, UInt128 hash)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)hash);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], (ulong)(hash >> 64));
    }
}
