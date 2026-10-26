using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of an object where a filter evaluated on its rows kept one, a filter's pages: the batches of the object's own
/// scan that held a row it kept, by their ranges of the object's rows, kept by the handle and the session's synopsis cache,
/// so that the filter asked again reads them alone, the rows between never read. An object never changes, so its pages
/// stay exact as long as it lives, under the identity its postscript carries.
/// </summary>
/// <remarks>
/// A filter that kept rows in more than half the object's rows' batches reads them all again: its pages would spare
/// little. In the cache, the ranges are followed by their XXH3-128, which a read checks.
/// </remarks>
internal sealed class PredicatePages
{
    /// <summary>The ranges an object's pages hold at most: some 66 KB of them.</summary>
    internal const int MaxRanges = 4_096;

    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Filter), RowRange[]> _held = new();

    // The filters whose pages in an object would spare little: their questions read the object's batches whole, noting none.
    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Filter), bool> _wide = new();

    /// <summary>Whether filter <paramref name="filter"/> kept rows in too much of the object <paramref name="uid"/> for its pages to spare a read.</summary>
    internal bool Wide(UInt128 uid, UInt128 filter) => _wide.ContainsKey((uid, filter));

    /// <summary>The pages of filter <paramref name="filter"/> in the object <paramref name="uid"/>, from the handle or the cache; null when neither holds them.</summary>
    internal RowRange[]? Of(UInt128 uid, UInt128 filter, SynopsisCache? cache)
    {
        if (_held.TryGetValue((uid, filter), out RowRange[]? ranges))
        {
            return ranges;
        }

        if (cache is null || !cache.TryReadSealed(NameOf(uid, filter), (MaxRanges * 16) + 16, out SegmentOwner owner))
        {
            return null;
        }

        using (owner)
        {
            ReadOnlySpan<byte> bytes = owner.Buffer.Span;
            if (bytes.Length < 16 || (bytes.Length - 16) % 16 != 0 || XxHash128.HashToUInt128(bytes[..^16]) != Hash(bytes[^16..]))
            {
                cache.DropSealed(NameOf(uid, filter));
                return null;
            }

            ranges = new RowRange[(bytes.Length - 16) / 16];
            for (int i = 0; i < ranges.Length; i++)
            {
                long start = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes[(i * 16)..]);
                long end = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes[((i * 16) + 8)..]);
                ranges[i] = new RowRange(start, end);
            }
        }

        _held[(uid, filter)] = ranges;
        return ranges;
    }

    /// <summary>
    /// Keeps <paramref name="ranges"/>, the batches where filter <paramref name="filter"/> kept a row in the object
    /// <paramref name="uid"/> of <paramref name="rows"/> rows, ascending, when they hold half its rows or fewer.
    /// </summary>
    internal void Keep(UInt128 uid, UInt128 filter, List<RowRange> ranges, long rows, SynopsisCache? cache)
    {
        long held = 0;
        foreach (RowRange range in ranges)
        {
            held += range.Length;
        }

        if (ranges.Count > MaxRanges || held * 2 > rows)
        {
            _wide.TryAdd((uid, filter), true);
            return;
        }

        RowRange[] kept = [.. ranges];
        _held[(uid, filter)] = kept;
        if (cache is not null)
        {
            byte[] bytes = new byte[(kept.Length * 16) + 16];
            for (int i = 0; i < kept.Length; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 16), kept[i].Start);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((i * 16) + 8), kept[i].End);
            }

            UInt128 hash = XxHash128.HashToUInt128(bytes.AsSpan(0, kept.Length * 16));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(kept.Length * 16), (ulong)hash);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((kept.Length * 16) + 8), (ulong)(hash >> 64));
            cache.AdmitSealed(uid, NameOf(uid, filter), bytes);
        }
    }

    private static UInt128 Hash(ReadOnlySpan<byte> bytes) =>
        new UInt128(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes));

    private static string NameOf(UInt128 uid, UInt128 filter) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-{filter:x32}-pages");
}
