using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Numerics;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The granules of an object where a filter evaluated on its rows kept one, a filter's pages (spec D, 5): a bit a granule of
/// the object's scan, kept by the handle and the session's synopsis cache, so that the filter asked again starts its scan's
/// pruning from them, a source of the granules' algebra (spec D, 4.1), the granules between never read. An object never
/// changes, so its pages stay exact as long as it lives, under the identity its postscript carries.
/// </summary>
/// <remarks>
/// A filter that kept rows in more than half the object's granules keeps no pages, which would spare little; and when they
/// lie in half its blocks of 1 024 or more too, it is noted wide there: a read of the object in two passes would read most of
/// it twice (<see cref="TwoPhaseReads"/>). In the cache, the words are followed by their XXH3-128, which a read checks.
/// </remarks>
internal sealed class PredicatePages
{
    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Filter), ulong[]> _held = new();

    // The filters whose pages in an object would spare little: their questions read the object whole, in one pass.
    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Filter), bool> _wide = new();

    /// <summary>Whether filter <paramref name="filter"/> kept rows in too much of the object <paramref name="uid"/> for its pages to spare a read.</summary>
    internal bool Wide(UInt128 uid, UInt128 filter) => _wide.ContainsKey((uid, filter));

    /// <summary>
    /// The pages of filter <paramref name="filter"/> in the object <paramref name="uid"/> of <paramref name="granules"/>
    /// granules, a bit a granule, from the handle or the cache; null when neither holds them.
    /// </summary>
    internal ulong[]? Of(UInt128 uid, UInt128 filter, int granules, SynopsisCache? cache)
    {
        int words = (granules + 63) / 64;
        if (_held.TryGetValue((uid, filter), out ulong[]? held))
        {
            return held.Length == words ? held : null;
        }

        if (cache is null || !cache.TryReadSealed(NameOf(uid, filter), (words * sizeof(ulong)) + 16, out SegmentOwner owner))
        {
            return null;
        }

        using (owner)
        {
            ReadOnlySpan<byte> bytes = owner.Buffer.Span;
            if (bytes.Length != (words * sizeof(ulong)) + 16 || XxHash128.HashToUInt128(bytes[..^16]) != Hash(bytes[^16..]))
            {
                cache.DropSealed(NameOf(uid, filter));
                return null;
            }

            held = new ulong[words];
            for (int i = 0; i < words; i++)
            {
                held[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * sizeof(ulong))..]);
            }
        }

        _held[(uid, filter)] = held;
        return held;
    }

    /// <summary>
    /// Keeps <paramref name="kept"/>, a bit for each of the <paramref name="granules"/> granules of the object
    /// <paramref name="uid"/> where filter <paramref name="filter"/> kept a row, when they are half its granules or fewer; past
    /// them, notes the filter wide there unless <paramref name="byPlaces"/> says a second pass reads its rows by their places.
    /// </summary>
    internal void Keep(UInt128 uid, UInt128 filter, ulong[] kept, int granules, bool byPlaces, SynopsisCache? cache)
    {
        long held = 0;
        foreach (ulong word in kept)
        {
            held += BitOperations.PopCount(word);
        }

        if (held * 2 > granules)
        {
            if (!byPlaces)
            {
                _wide.TryAdd((uid, filter), true);
            }

            return;
        }

        _held[(uid, filter)] = kept;
        if (cache is not null)
        {
            byte[] bytes = new byte[(kept.Length * sizeof(ulong)) + 16];
            for (int i = 0; i < kept.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong)), kept[i]);
            }

            UInt128 hash = XxHash128.HashToUInt128(bytes.AsSpan(0, kept.Length * sizeof(ulong)));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(kept.Length * sizeof(ulong)), (ulong)hash);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((kept.Length * sizeof(ulong)) + 8), (ulong)(hash >> 64));
            cache.AdmitSealed(uid, NameOf(uid, filter), bytes);
        }
    }

    /// <summary>
    /// Marks in <paramref name="granules"/>, a bit a granule of <paramref name="granuleRows"/> rows, or a block, those that
    /// hold a row <paramref name="selection"/> keeps of the batch of <paramref name="rows"/> rows from row
    /// <paramref name="start"/> of its object, every row when it is empty; the granules newly marked.
    /// </summary>
    internal static int Note(ReadOnlySpan<ulong> selection, long start, int rows, long granuleRows, ulong[] granules)
    {
        int marked = 0;
        long end = start + rows;
        for (long first = start; first < end;)
        {
            long granule = first / granuleRows;
            long last = Math.Min(end, (granule + 1) * granuleRows);
            if ((ulong)granule < (ulong)granules.Length * 64 && Any(selection, (int)(first - start), (int)(last - start)))
            {
                ulong bit = 1UL << (int)(granule & 63);
                ref ulong word = ref granules[granule >> 6];
                marked += (word & bit) == 0 ? 1 : 0;
                word |= bit;
            }

            first = last;
        }

        return marked;
    }

    /// <summary>Whether <paramref name="selection"/> keeps a row of <c>[from, to)</c>, every row when it is empty.</summary>
    private static bool Any(ReadOnlySpan<ulong> selection, int from, int to)
    {
        if (selection.IsEmpty)
        {
            return to > from;
        }

        int word = from >> 6;
        int lastWord = (to - 1) >> 6;
        for (int w = word; w <= lastWord && w < selection.Length; w++)
        {
            ulong bits = selection[w];
            if (w == word)
            {
                bits &= ulong.MaxValue << (from & 63);
            }

            if (w == lastWord && (to & 63) != 0)
            {
                bits &= (1UL << (to & 63)) - 1;
            }

            if (bits != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static UInt128 Hash(ReadOnlySpan<byte> bytes) =>
        new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes));

    private static string NameOf(UInt128 uid, UInt128 filter) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-{filter:x32}-granules");
}
