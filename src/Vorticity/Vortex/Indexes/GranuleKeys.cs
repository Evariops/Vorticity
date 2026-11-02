using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// The sparse index of a sorted object, <see cref="IndexKinds.KeyIndex"/>: the key of the first row of each of its granules,
/// the prefix of the sort key the object indexes, row-encoded; and the key of its last row. A granule's rows hold keys
/// from its key to the next granule's, both included, since granules are not cut where the key changes.
/// </summary>
/// <remarks>
/// <para>
/// The keys lie in pages of <see cref="PageEntries"/> granules. The table holds, behind a version byte, the rows of a
/// granule, the object's rows and the components a key holds; then each page's first key, coded forward against the page
/// before (the bytes they share as a varint, then the rest as a varint of its length and its bytes), where its bytes lie in
/// the pages and their XXH3-64; then the last row's key whole. In a page, each granule's key is coded forward against the
/// one before, the first against the key the table gives the page. An object of one page keeps it in the table, and a
/// reader reads nothing past it.
/// </para>
/// <para>
/// About sixteen bytes a granule for a prefix of a few components, a few where neighbouring granules share most of their
/// key: an object of a gibibyte, some 3 300 granules, holds an index of some fifty kilobytes, read in one request.
/// </para>
/// </remarks>
internal sealed class GranuleKeys
{
    private const byte Version = 1;

    /// <summary>The granules a page holds by default.</summary>
    internal const int DefaultPageEntries = 128;

    private readonly byte[][] _pageKeys;
    private readonly (long Offset, int Length, ulong Checksum)[] _pages;
    private readonly PageKeys?[] _keys;

    private GranuleKeys(
        int granuleRows, long rows, int components, int pageEntries, byte[][] pageKeys, (long, int, ulong)[] pages, byte[] lastKey)
    {
        GranuleRows = granuleRows;
        Rows = rows;
        Components = components;
        PageEntries = pageEntries;
        _pageKeys = pageKeys;
        _pages = pages;
        LastKey = lastKey;
        _keys = new PageKeys?[pages.Length];
    }

    /// <summary>The rows of a granule, the last fewer.</summary>
    internal int GranuleRows { get; }

    /// <summary>The object's rows.</summary>
    internal long Rows { get; }

    /// <summary>The components of the sort key a key holds: the indexed prefix.</summary>
    internal int Components { get; }

    /// <summary>The granules a page holds, the last fewer.</summary>
    internal int PageEntries { get; }

    /// <summary>The object's granules.</summary>
    internal int Granules => (int)((Rows + GranuleRows - 1) / GranuleRows);

    /// <summary>The pages the keys lie in.</summary>
    internal int Pages => _pages.Length;

    /// <summary>The key of the object's last row: the upper bound of its last granule.</summary>
    internal byte[] LastKey { get; }

    /// <summary>The first key of page <paramref name="page"/>, its first granule's.</summary>
    internal ReadOnlySpan<byte> PageKey(int page) => _pageKeys[page];

    /// <summary>Where page <paramref name="page"/>'s bytes lie among the pages, and their XXH3-64.</summary>
    internal (long Offset, int Length, ulong Checksum) PageAt(int page) => _pages[page];

    /// <summary>Whether page <paramref name="page"/>'s keys are in hand.</summary>
    internal bool HasPage(int page) => System.Threading.Volatile.Read(ref _keys[page]) is not null;

    /// <summary>Whether every page's keys are in hand, so that any granule's key is.</summary>
    internal bool HoldsEvery
    {
        get
        {
            for (int page = 0; page < _keys.Length; page++)
            {
                if (!HasPage(page))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The key of granule <paramref name="granule"/>, row-encoded; its page in hand first (<see cref="TakePage"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Its page was not taken.</exception>
    internal ReadOnlySpan<byte> Key(int granule)
    {
        int page = granule / PageEntries;
        PageKeys keys = System.Threading.Volatile.Read(ref _keys[page]) ?? throw new InvalidOperationException($"Page {page} of the key index was not read.");
        return keys.At(granule - (page * PageEntries));
    }

    /// <summary>
    /// Takes page <paramref name="page"/>'s keys from its bytes, which their checksum vouches for; false when they do not
    /// decode, or are not the bytes the table names.
    /// </summary>
    internal bool TakePage(int page, ReadOnlySpan<byte> bytes)
    {
        (long _, int length, ulong checksum) = _pages[page];
        if (bytes.Length != length || XxHash3.HashToUInt64(bytes) != checksum)
        {
            return false;
        }

        int count = Math.Min(PageEntries, Granules - (page * PageEntries));
        int[] ends = new int[count];
        byte[] keys;
        try
        {
            // The keys' lengths first, which size their bytes, held end to end; then the bytes, each key from the one before.
            ProtoReader reader = new ProtoReader(bytes);
            int previous = _pageKeys[page].Length;
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                int shared = checked((int)reader.ReadVarint());
                int rest = reader.ReadLengthDelimited().Length;
                if (shared > previous)
                {
                    return false;
                }

                previous = shared + rest;
                ends[i] = total = checked(total + previous);
            }

            if (!reader.End)
            {
                return false;
            }

            keys = new byte[total];
            reader = new ProtoReader(bytes);
            ReadOnlySpan<byte> before = _pageKeys[page];
            for (int i = 0, start = 0; i < count; start = ends[i++])
            {
                int shared = (int)reader.ReadVarint();
                Span<byte> key = keys.AsSpan(start, ends[i] - start);
                before[..shared].CopyTo(key);
                reader.ReadLengthDelimited().CopyTo(key[shared..]);
                before = key;
            }
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return false;
        }

        if (!keys.AsSpan(0, ends[0]).SequenceEqual(_pageKeys[page]))
        {
            return false;
        }

        System.Threading.Interlocked.CompareExchange(ref _keys[page], new PageKeys(keys, ends), null);
        return true;
    }

    /// <summary>
    /// The first granule whose key lies at or past <paramref name="key"/>, <see cref="Granules"/> when none does: the one
    /// before it may hold keys at or past it too, those before that none. Every page in hand first.
    /// </summary>
    internal int FirstAtOrPast(ReadOnlySpan<byte> key)
    {
        int lo = 0;
        int hi = Granules;
        while (lo < hi)
        {
            int middle = lo + ((hi - lo) >> 1);
            if (Key(middle).SequenceCompareTo(key) < 0)
            {
                lo = middle + 1;
            }
            else
            {
                hi = middle;
            }
        }

        return lo;
    }

    /// <summary>
    /// The table and the pages of the index of an object of <paramref name="rows"/> rows in granules of
    /// <paramref name="granuleRows"/>, from the key of each granule's first row and the last row's key, each of
    /// <paramref name="components"/> components; the pages empty when the table keeps the one page.
    /// </summary>
    /// <exception cref="ArgumentException">The keys are not one a granule, or go back.</exception>
    internal static (byte[] Table, byte[] Pages) Build(
        int granuleRows, long rows, int components, IReadOnlyList<byte[]> keys, byte[] lastKey, int pageEntries = DefaultPageEntries)
    {
        long granules = (rows + granuleRows - 1) / granuleRows;
        if (granules != keys.Count || granules == 0)
        {
            throw new ArgumentException($"{keys.Count} key(s) for {granules} granule(s) of {rows} rows.", nameof(keys));
        }

        for (int i = 1; i < keys.Count; i++)
        {
            if (keys[i].AsSpan().SequenceCompareTo(keys[i - 1]) < 0)
            {
                throw new ArgumentException($"The key of granule {i} lies below the key of granule {i - 1}.", nameof(keys));
            }
        }

        // The pages, each its granules' keys coded forward from its own first.
        int pageCount = (keys.Count + pageEntries - 1) / pageEntries;
        ProtoWriter pages = new ProtoWriter();
        ProtoWriter table = new ProtoWriter();
        try
        {
            (long Offset, int Length, ulong Checksum)[] regions = new (long, int, ulong)[pageCount];
            for (int page = 0; page < pageCount; page++)
            {
                int start = pages.WrittenSpan.Length;
                byte[] previous = keys[page * pageEntries];
                for (int i = page * pageEntries; i < Math.Min(keys.Count, (page + 1) * pageEntries); i++)
                {
                    WriteForward(ref pages, previous, keys[i]);
                    previous = keys[i];
                }

                ReadOnlySpan<byte> written = pages.WrittenSpan[start..];
                regions[page] = (start, written.Length, XxHash3.HashToUInt64(written));
            }

            table.WriteVarint(Version);
            table.WriteVarint((ulong)granuleRows);
            table.WriteVarint((ulong)rows);
            table.WriteVarint((ulong)components);
            table.WriteVarint((ulong)pageEntries);
            table.WriteVarint((ulong)pageCount);
            byte[] before = [];
            Span<byte> checksum = stackalloc byte[sizeof(ulong)];
            for (int page = 0; page < pageCount; page++)
            {
                byte[] first = keys[page * pageEntries];
                WriteForward(ref table, before, first);
                before = first;
                table.WriteVarint((ulong)regions[page].Offset);
                table.WriteVarint((ulong)regions[page].Length);
                BinaryPrimitives.WriteUInt64LittleEndian(checksum, regions[page].Checksum);
                table.WriteRawBytes(checksum);
            }

            table.WriteVarint((ulong)lastKey.Length);
            table.WriteRawBytes(lastKey);

            // One page rides in the table.
            bool inline = pageCount == 1;
            table.WriteVarint(inline ? 1UL : 0UL);
            if (inline)
            {
                table.WriteVarint((ulong)pages.WrittenSpan.Length);
                table.WriteRawBytes(pages.WrittenSpan);
            }

            return (table.WrittenSpan.ToArray(), inline ? [] : pages.WrittenSpan.ToArray());
        }
        finally
        {
            pages.Dispose();
            table.Dispose();
        }
    }

    /// <summary>The index a table holds, its page taken when it rides in it; null when the table does not decode.</summary>
    internal static GranuleKeys? From(ReadOnlySpan<byte> table)
    {
        try
        {
            ProtoReader reader = new ProtoReader(table);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong granuleRows = reader.ReadVarint();
            ulong rows = reader.ReadVarint();
            ulong components = reader.ReadVarint();
            ulong pageEntries = reader.ReadVarint();
            ulong pageCount = reader.ReadVarint();
            ulong granules = granuleRows == 0 ? 0 : (rows + granuleRows - 1) / granuleRows;
            if (granuleRows is 0 or > int.MaxValue || rows == 0 || rows > long.MaxValue || components is 0 or > 64
                || pageEntries is 0 or > int.MaxValue || pageCount != (granules + pageEntries - 1) / pageEntries || pageCount > (ulong)table.Length)
            {
                return null;
            }

            byte[][] pageKeys = new byte[(int)pageCount][];
            (long, int, ulong)[] pages = new (long, int, ulong)[(int)pageCount];
            byte[] before = [];
            for (int page = 0; page < pageKeys.Length; page++)
            {
                before = pageKeys[page] = Forward(ref reader, before);
                pages[page] = (checked((long)reader.ReadVarint()), checked((int)reader.ReadVarint()), reader.ReadFixed64());
            }

            byte[] lastKey = reader.ReadLengthDelimited().ToArray();
            GranuleKeys keys = new GranuleKeys((int)granuleRows, (long)rows, (int)components, (int)pageEntries, pageKeys, pages, lastKey);
            if (reader.ReadVarint() == 1 && (pageCount != 1 || !keys.TakePage(0, reader.ReadLengthDelimited())))
            {
                return null;
            }

            return reader.End ? keys : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>A key coded forward against <paramref name="before"/>: the bytes they share, then the rest's length and bytes.</summary>
    private static void WriteForward(ref ProtoWriter writer, ReadOnlySpan<byte> before, ReadOnlySpan<byte> key)
    {
        int shared = key.CommonPrefixLength(before);
        writer.WriteVarint((ulong)shared);
        writer.WriteVarint((ulong)(key.Length - shared));
        writer.WriteRawBytes(key[shared..]);
    }

    /// <summary>The keys of a page, read: their bytes end to end, and where each ends.</summary>
    private sealed class PageKeys(byte[] keys, int[] ends)
    {
        internal ReadOnlySpan<byte> At(int entry)
        {
            int start = entry == 0 ? 0 : ends[entry - 1];
            return keys.AsSpan(start, ends[entry] - start);
        }
    }

    /// <summary>A key coded forward against <paramref name="before"/>, read.</summary>
    private static byte[] Forward(ref ProtoReader reader, byte[] before)
    {
        int shared = checked((int)reader.ReadVarint());
        ReadOnlySpan<byte> rest = reader.ReadLengthDelimited();
        if (shared > before.Length)
        {
            throw new VortexFormatException($"A key shares {shared} bytes with one of {before.Length}.");
        }

        byte[] key = new byte[shared + rest.Length];
        before.AsSpan(0, shared).CopyTo(key);
        rest.CopyTo(key.AsSpan(shared));
        return key;
    }
}
