using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    // The objects a compaction merged whole into one, by the one's identity: their partials sum to its own.
    private readonly ConcurrentDictionary<UInt128, UInt128[]> _merged = new();

    /// <summary>
    /// Records that the object <paramref name="output"/> holds the rows of <paramref name="inputs"/>, all of them and no
    /// other, as a compaction that wrote it alone from them, none with a row marked, says once it committed: the partials of
    /// the inputs, summed, are its own, in the handle and in the session's cache.
    /// </summary>
    internal void Inherit(UInt128 output, UInt128[] inputs, SynopsisCache? cache)
    {
        _merged[output] = inputs;
        if (cache is not null)
        {
            byte[] kept = new byte[(inputs.Length * 16) + 16];
            for (int i = 0; i < inputs.Length; i++)
            {
                WriteHash(kept.AsSpan(i * 16), inputs[i]);
            }

            WriteHash(kept.AsSpan(inputs.Length * 16), XxHash128.HashToUInt128(kept.AsSpan(0, inputs.Length * 16)));
            cache.AdmitSealed(output, LineageName(output), kept);
        }
    }

    /// <summary>The objects whose rows the object <paramref name="uid"/> holds, which a compaction merged into it alone; null when none is known.</summary>
    internal UInt128[]? MergedInto(UInt128 uid, SynopsisCache? cache)
    {
        if (_merged.TryGetValue(uid, out UInt128[]? inputs))
        {
            return inputs;
        }

        if (cache is null || !cache.TryReadSealed(LineageName(uid), MaxInputs * 16 + 16, out SegmentOwner owner))
        {
            return null;
        }

        using (owner)
        {
            ReadOnlySpan<byte> bytes = owner.Buffer.Span;
            if (bytes.Length < 32 || bytes.Length % 16 != 0 || XxHash128.HashToUInt128(bytes[..^16]) != ReadHash(bytes[^16..]))
            {
                cache.DropSealed(LineageName(uid));
                return null;
            }

            inputs = new UInt128[(bytes.Length / 16) - 1];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = ReadHash(bytes[(i * 16)..]);
            }
        }

        _merged[uid] = inputs;
        return inputs;
    }

    /// <summary>The objects a compaction merges whose lineage is kept at most.</summary>
    internal const int MaxInputs = 4_096;

    private static string LineageName(UInt128 uid) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-lineage");

    /// <summary>The families the handle keeps up at most: those its questions asked last.</summary>
    internal const int MaxLive = 16;

    // The families the handle's questions asked, by their identity, with when each was asked last: an object it appends learns
    // their partials as it is written (KeptUpAsync).
    private readonly ConcurrentDictionary<UInt128, (Aggregating.LearnedFamily Family, long Ticks)> _live = new();

    /// <summary>Notes that a question asked <paramref name="family"/> at <paramref name="ticks"/>, the least recent of the families kept up let go past <see cref="MaxLive"/>.</summary>
    internal void Asked(Aggregating.LearnedFamily family, long ticks)
    {
        _live[family.Identity] = (family, ticks);
        if (_live.Count > MaxLive)
        {
            UInt128 oldest = default;
            long least = long.MaxValue;
            foreach (KeyValuePair<UInt128, (Aggregating.LearnedFamily Family, long Ticks)> live in _live)
            {
                if (live.Value.Ticks < least)
                {
                    (oldest, least) = (live.Key, live.Value.Ticks);
                }
            }

            _live.TryRemove(oldest, out _);
        }
    }

    /// <summary>The families the handle keeps up: asked at or after <paramref name="since"/>, in ticks.</summary>
    internal List<Aggregating.LearnedFamily> Live(long since)
    {
        List<Aggregating.LearnedFamily> live = [];
        foreach ((Aggregating.LearnedFamily family, long ticks) in _live.Values)
        {
            if (ticks >= since)
            {
                live.Add(family);
            }
        }

        return live;
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
