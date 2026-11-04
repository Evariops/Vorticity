using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Vorticity.Buffers;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// The sparse index of a sorted object, <see cref="IndexKinds.KeyIndex"/>: the key of the first row of each of its granules,
/// the prefix of the sort key the object indexes, row-encoded; and the key of its last row. A granule's rows hold keys
/// from its key to the next granule's, both included, since granules are not cut where the key changes.
/// </summary>
/// <remarks>
/// <para>
/// The keys lie in pages of <see cref="PageEntries"/> granules. The table holds, behind a version byte, the coding of the
/// keys for version 2 (<see cref="KeyCoding"/>; version 1 holds them as the row encoding writes them), the rows of a
/// granule, the object's rows and the components a key holds; then each page's first key, coded forward against the page
/// before (the bytes they share as a varint, then the rest as a varint of its length and its bytes), where its bytes lie in
/// the pages and their XXH3-64; then the last row's key whole. In a page, each granule's key is coded forward against the
/// one before, the first against the key the table gives the page. An object of one page keeps it in the table, and a
/// reader reads nothing past it.
/// </para>
/// <para>
/// With its texts unpadded, 12.6 bytes a granule for the reference store's requests and 8.2 for its spans, where row-encoded
/// keys took 31.9 and 19.4: an object of a gibibyte, some 3 300 granules, holds an index of some forty kilobytes, read in
/// one request.
/// </para>
/// <para>
/// An index is read for one search, over the bytes read of it, the table's and the pages', which <see cref="Dispose"/> gives
/// back: nothing of it outlives the search, and its bytes stay where an index's regions stay, in the object's open, the
/// segment cache and the disk cache. A page is checked against its checksum, and the whole key of the first granule of
/// each of its runs of <see cref="RestartEvery"/> taken, when a search first reaches it: a bisection for one bound reads
/// the pages' first keys, which the table gives, then a page. An index is read by one thread at a time.
/// </para>
/// </remarks>
internal sealed class GranuleKeys : IDisposable
{
    /// <summary>The version of a table whose keys are row-encoded, as every reader reads them.</summary>
    private const byte RowsVersion = 1;

    /// <summary>The version of a table that says how it codes its keys, behind the version byte.</summary>
    private const byte CodedVersion = 2;

    /// <summary>The granules a page holds by default.</summary>
    internal const int DefaultPageEntries = 128;

    /// <summary>The granules a page keeps one key of whole for, the first of each run: a key decodes from at most this many records before it.</summary>
    internal const int RestartEvery = 16;

    private Bytes _table;
    private Bytes _pages;
    private readonly int _lastKeyAt;
    private readonly int _lastKeyLength;
    private readonly bool _inline;

    // Rented, given back by Dispose. Each page's first key, end to end. By page, where its first key ends there; then, by
    // page, the longest key of a page taken, -1 before; then, by run, where its whole key lies in _whole, how long it is,
    // and where the record past it starts in its page. By page, its place among the pages (its offset above its length),
    // then its XXH3-64. The whole keys of the runs of the pages taken, end to end.
    private byte[]? _pageKeys;
    private int[]? _ints;
    private ulong[]? _places;
    private byte[]? _whole;
    private int _wholeUsed;

    private GranuleKeys(
        Bytes table, KeyCoding coding, int granuleRows, long rows, int components, int pageEntries, int pages, byte[] pageKeys, int[] ints,
        ulong[] places, int lastKeyAt, int lastKeyLength, bool inline)
    {
        _table = table;
        Coding = coding;
        GranuleRows = granuleRows;
        Rows = rows;
        Components = components;
        PageEntries = pageEntries;
        Pages = pages;
        _pageKeys = pageKeys;
        _ints = ints;
        _places = places;
        _lastKeyAt = lastKeyAt;
        _lastKeyLength = lastKeyLength;
        _inline = inline;
    }

    /// <summary>
    /// How the keys are coded: as the row encoding writes them, or with their texts unpadded. A key, a bound and a page's
    /// first key compare in it alike; a reader that compares them with keys of rows codes those the same way.
    /// </summary>
    internal KeyCoding Coding { get; }

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
    internal int Pages { get; }

    /// <summary>The granules of the object, in rows: what a reader of its kept granules needs once the index is given back.</summary>
    internal GranuleGrid Grid => new GranuleGrid(GranuleRows, Rows);

    /// <summary>The key of the object's last row: the upper bound of its last granule.</summary>
    internal ReadOnlySpan<byte> LastKey => _table.Span.Slice(_lastKeyAt, _lastKeyLength);

    /// <summary>Whether the pages lie beside the table, to be taken (<see cref="TakePages(SegmentOwner)"/>); false when the table holds the one page.</summary>
    internal bool WantsPages => !_inline && !_pages.Held;

    /// <summary>
    /// The bytes the index holds besides those read of it: the pages' first keys, where each page lies, and the whole keys of
    /// the runs of the pages a search reached, with where each lies.
    /// </summary>
    internal long HeldBytes =>
        PageKeysEnd(Pages) + _wholeUsed + ((long)IntsLength * sizeof(int)) + ((long)Pages * 2 * sizeof(ulong));

    /// <summary>The bytes read of it: the table's and the pages'.</summary>
    internal long ReadBytes => _table.Length + (_inline ? 0 : _pages.Length);

    /// <summary>The first key of page <paramref name="page"/>, its first granule's.</summary>
    internal ReadOnlySpan<byte> PageKey(int page)
    {
        int start = page == 0 ? 0 : _ints![page - 1];
        return _pageKeys.AsSpan(start, _ints![page] - start);
    }

    /// <summary>Where page <paramref name="page"/>'s bytes lie among the pages, and their XXH3-64.</summary>
    internal (int Offset, int Length, ulong Checksum) PageAt(int page)
    {
        ulong place = _places![2 * page];
        return ((int)(place >> 32), (int)(uint)place, _places[(2 * page) + 1]);
    }

    /// <summary>
    /// The index the table <paramref name="table"/> holds, which owns it from then on and gives it back when disposed; null
    /// when it does not decode, the table then still the caller's. Its pages, unless the table holds its one, are taken next
    /// (<see cref="TakePages(SegmentOwner)"/>).
    /// </summary>
    internal static GranuleKeys? Over(SegmentOwner table) => Parse(new Bytes(table, null, 0, table.Length));

    /// <summary>The index the table <paramref name="table"/> holds; null when it does not decode.</summary>
    internal static GranuleKeys? From(byte[] table) => Parse(new Bytes(null, table, 0, table.Length));

    /// <summary>
    /// Takes the pages' bytes, which the index owns from then on and gives back when disposed; false, the bytes then still
    /// the caller's, when a page the table names lies past them.
    /// </summary>
    internal bool TakePages(SegmentOwner pages) => Take(new Bytes(pages, null, 0, pages.Length));

    /// <summary>Takes the pages' bytes; false when a page the table names lies past them.</summary>
    internal bool TakePages(byte[] pages) => Take(new Bytes(null, pages, 0, pages.Length));

    private bool Take(Bytes pages)
    {
        for (int page = 0; page < Pages; page++)
        {
            (int offset, int length, ulong _) = PageAt(page);
            if ((long)offset + length > pages.Length)
            {
                return false;
            }
        }

        _pages = pages;
        return true;
    }

    /// <summary>Gives back the bytes read and the buffers rented.</summary>
    public void Dispose()
    {
        if (_ints is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_pageKeys!);
        ArrayPool<int>.Shared.Return(_ints);
        ArrayPool<ulong>.Shared.Return(_places!);
        if (_whole is not null)
        {
            ArrayPool<byte>.Shared.Return(_whole);
        }

        _pageKeys = null;
        _ints = null;
        _places = null;
        _whole = null;
        SegmentOwner? table = _table.Owner;
        SegmentOwner? pages = _pages.Owner;
        _table = default;
        _pages = default;
        table?.Dispose();
        if (pages is not null && !ReferenceEquals(pages, table))
        {
            pages.Dispose();
        }
    }

    /// <summary>The runs a page of the index holds, a whole key each: <see cref="RestartEvery"/> granules a run, the last fewer.</summary>
    private int RunsOf(int page) => (Math.Min(PageEntries, Granules - (page * PageEntries)) + RestartEvery - 1) / RestartEvery;

    /// <summary>The runs a full page holds.</summary>
    private int RunsAPage => (PageEntries + RestartEvery - 1) / RestartEvery;

    /// <summary>The runs of the whole index.</summary>
    private int Runs => ((Pages - 1) * RunsAPage) + RunsOf(Pages - 1);

    /// <summary>The integers <see cref="_ints"/> holds: two a page, three a run.</summary>
    private int IntsLength => (2 * Pages) + (3 * Runs);

    /// <summary>Where the first keys of the pages before <paramref name="page"/> end.</summary>
    private int PageKeysEnd(int page) => page == 0 ? 0 : _ints![page - 1];

    /// <summary>The records of page <paramref name="page"/>, its keys coded forward.</summary>
    private ReadOnlySpan<byte> Records(int page)
    {
        (int offset, int length, ulong _) = PageAt(page);
        return _pages.Span.Slice(offset, length);
    }

    /// <summary>The whole key of the first granule of run <paramref name="run"/>, its page taken; it lasts until another page is.</summary>
    private ReadOnlySpan<byte> RunKey(int run)
    {
        int at = (2 * Pages) + (3 * run);
        return _whole.AsSpan(_ints![at], _ints[at + 1]);
    }

    /// <summary>Where the record past the first key of run <paramref name="run"/> starts in its page, its page taken.</summary>
    private int RunNext(int run) => _ints![(2 * Pages) + (3 * run) + 2];

    /// <summary>
    /// The bytes of the longest key of page <paramref name="page"/>, taken first when no search reached it yet: its bytes
    /// checked against their checksum, and the whole key of each of its runs kept.
    /// </summary>
    /// <exception cref="VortexFormatException">The page's bytes are not those the table names, or do not decode.</exception>
    private int Page(int page)
    {
        int longest = _ints![Pages + page];
        return longest >= 0 ? longest : TakePage(page);
    }

    [SkipLocalsInit]
    private int TakePage(int page)
    {
        ReadOnlySpan<byte> bytes = Records(page);
        if (XxHash3.HashToUInt64(bytes) != _places![(2 * page) + 1])
        {
            throw Corrupt(page);
        }

        // One pass: each key decoded from the one before into a buffer that grows past the stack's, the first of each run
        // kept whole.
        int count = Math.Min(PageEntries, Granules - (page * PageEntries));
        ReadOnlySpan<byte> pageKey = PageKey(page);
        int[] ints = _ints!;
        int runs = (2 * Pages) + (3 * page * RunsAPage);
        byte[]? rented = pageKey.Length > 512 ? ArrayPool<byte>.Shared.Rent(pageKey.Length) : null;
        Span<byte> key = rented ?? stackalloc byte[512];
        try
        {
            pageKey.CopyTo(key);
            int length = pageKey.Length;
            int longest = length;
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                if (!Varint(bytes, ref at, out int shared) || shared > length || !Varint(bytes, ref at, out int rest) || rest > bytes.Length - at)
                {
                    throw Corrupt(page);
                }

                int next = shared + rest;
                if (next > key.Length)
                {
                    byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(next, key.Length * 2));
                    key[..shared].CopyTo(grown);
                    if (rented is not null)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }

                    rented = grown;
                    key = grown;
                }

                bytes.Slice(at, rest).CopyTo(key[shared..]);
                at += rest;
                length = next;
                longest = Math.Max(longest, length);
                if ((i & (RestartEvery - 1)) != 0)
                {
                    continue;
                }

                if (i == 0 && !key[..length].SequenceEqual(pageKey))
                {
                    throw Corrupt(page);
                }

                if (_whole is null || _whole.Length - _wholeUsed < length)
                {
                    GrowWhole(length);
                }

                int run = runs + (3 * (i / RestartEvery));
                key[..length].CopyTo(_whole.AsSpan(_wholeUsed));
                ints[run] = _wholeUsed;
                ints[run + 1] = length;
                ints[run + 2] = at;
                _wholeUsed += length;
            }

            if (at != bytes.Length)
            {
                throw Corrupt(page);
            }

            ints[Pages + page] = longest;
            return longest;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Makes room for <paramref name="bytes"/> more bytes of whole keys, those kept copied over.</summary>
    private void GrowWhole(int bytes)
    {
        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_wholeUsed + bytes, Math.Max((_whole?.Length ?? 0) * 2, 1_024)));
        if (_whole is not null)
        {
            _whole.AsSpan(0, _wholeUsed).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_whole);
        }

        _whole = grown;
    }

    private static VortexFormatException Corrupt(int page) =>
        new VortexFormatException($"Page {page} of the key index does not hold the keys its table names.");

    /// <summary>
    /// Reads the record of a key coded forward at <paramref name="at"/> in <paramref name="bytes"/>, which moves past it: the
    /// bytes it shares with the key before, of <paramref name="previous"/> bytes, and where its rest lies and how long it
    /// is; false when it does not decode, or shares more than the key before holds.
    /// </summary>
    private static bool Record(ReadOnlySpan<byte> bytes, ref int at, int previous, out int shared, out int restAt, out int rest)
    {
        restAt = 0;
        rest = 0;
        if (!Varint(bytes, ref at, out shared) || shared > previous || !Varint(bytes, ref at, out rest) || rest > bytes.Length - at)
        {
            return false;
        }

        restAt = at;
        at += rest;
        return true;
    }

    /// <summary>A varint of at most 31 bits at <paramref name="at"/>, which moves past it; false past the bytes or the bits.</summary>
    private static bool Varint(ReadOnlySpan<byte> bytes, ref int at, out int value)
    {
        value = 0;
        for (int shift = 0; shift < 32 && at < bytes.Length; shift += 7)
        {
            byte b = bytes[at++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value >= 0 && (shift < 28 || b < 0x08);
            }
        }

        return false;
    }

    /// <summary>
    /// The first granule whose key lies at or past <paramref name="key"/>, <see cref="Granules"/> when none does: the one
    /// before it may hold keys at or past it too, those before that none.
    /// </summary>
    internal int FirstAtOrPast(ReadOnlySpan<byte> key)
    {
        int compared = 0;
        return First(key, 0, past: false, ref compared);
    }

    /// <summary>
    /// The first granule from <paramref name="from"/> on whose key reaches <paramref name="bound"/> (<see cref="Reaches"/>),
    /// <see cref="Granules"/> when none does; each key compared added to <paramref name="compared"/>. The pages' first keys
    /// first, which the table gives: from the first granule a bisection, from a later one a gallop then a bisection, so that
    /// the ascending bounds of one search each cost the logarithm of how far the next lies; then the whole keys of the runs
    /// of one page; then the keys of one run, each decoded from the one before.
    /// </summary>
    [SkipLocalsInit]
    internal int First(ReadOnlySpan<byte> bound, int from, bool past, ref int compared)
    {
        int granules = Granules;
        if (from >= granules)
        {
            return granules;
        }

        // The first page past the one that holds `from` whose first key reaches; every key before it, from its own page's
        // first, falls short.
        int pages = Pages;
        int holding = from / PageEntries;
        int lo = holding + 1;
        int hi = pages;
        if (from > 0)
        {
            int probe = lo;
            int step = 1;
            while (probe < pages)
            {
                compared++;
                if (Reaches(PageKey(probe), bound, past))
                {
                    break;
                }

                lo = probe + 1;
                probe = lo + step - 1;
                step <<= 1;
            }

            hi = Math.Min(probe, pages);
        }

        while (lo < hi)
        {
            int middle = lo + ((hi - lo) >> 1);
            compared++;
            if (Reaches(PageKey(middle), bound, past))
            {
                hi = middle;
            }
            else
            {
                lo = middle + 1;
            }
        }

        // The granule lies in the page before, from `from` in its own page, past the page's first otherwise, which falls
        // short; or it is the first of the page found.
        int page = lo - 1;
        int end = lo == pages ? granules : lo * PageEntries;
        int start = page == holding ? from : (page * PageEntries) + 1;
        if (start >= end)
        {
            return end;
        }

        // Then the first run of that page past the one that holds `start` whose first key reaches.
        int longest = Page(page);
        int firstRun = page * RunsAPage;
        int runs = RunsOf(page);
        int pageStart = page * PageEntries;
        int holdingRun = (start - pageStart) / RestartEvery;
        int low = holdingRun + 1;
        int high = runs;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            compared++;
            if (Reaches(RunKey(firstRun + middle), bound, past))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        // The granule lies in the run before, from `start` in its own run, past the run's first otherwise; its keys decoded
        // one from the one before, on the stack.
        int run = low - 1;
        int runFirst = pageStart + (run * RestartEvery);
        end = low == runs ? end : pageStart + (low * RestartEvery);
        start = run == holdingRun ? start : runFirst + 1;
        if (start >= end)
        {
            return end;
        }

        byte[]? rented = longest > 512 ? ArrayPool<byte>.Shared.Rent(longest) : null;
        try
        {
            Span<byte> key = rented ?? stackalloc byte[512];
            ReadOnlySpan<byte> whole = RunKey(firstRun + run);
            whole.CopyTo(key);
            int length = whole.Length;
            int at = RunNext(firstRun + run);
            ReadOnlySpan<byte> records = Records(page);
            for (int granule = runFirst; granule < end; granule++)
            {
                if (granule > runFirst)
                {
                    Record(records, ref at, length, out int shared, out int restAt, out int rest);
                    records.Slice(restAt, rest).CopyTo(key[shared..]);
                    length = shared + rest;
                }

                if (granule >= start)
                {
                    compared++;
                    if (Reaches(key[..length], bound, past))
                    {
                        return granule;
                    }
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return end;
    }

    /// <summary>
    /// Whether <paramref name="key"/> lies at or past <paramref name="bound"/>, a key or its leading values; with
    /// <paramref name="past"/>, whether its leading bytes, as many as the bound holds, lie past it. A value's encoding ends
    /// on its own and orders as the value does, so the leading bytes of a key compare as its leading values.
    /// </summary>
    internal static bool Reaches(ReadOnlySpan<byte> key, ReadOnlySpan<byte> bound, bool past) =>
        past
            ? (key.Length > bound.Length ? key[..bound.Length] : key).SequenceCompareTo(bound) > 0
            : key.SequenceCompareTo(bound) >= 0;

    /// <summary>
    /// The table and the pages of the index of an object of <paramref name="rows"/> rows in granules of
    /// <paramref name="granuleRows"/>, from the key of each granule's first row and the last row's key, each of
    /// <paramref name="components"/> components, coded as <paramref name="coding"/> says; the pages empty when the table keeps
    /// the one page. Keys as the row encoding writes them make a table of version 1, which every reader reads.
    /// </summary>
    /// <exception cref="ArgumentException">The keys are not one a granule, or go back.</exception>
    internal static (byte[] Table, byte[] Pages) Build(
        int granuleRows, long rows, int components, IReadOnlyList<byte[]> keys, byte[] lastKey, int pageEntries = DefaultPageEntries,
        KeyCoding coding = KeyCoding.Rows)
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

            table.WriteVarint(coding == KeyCoding.Rows ? RowsVersion : CodedVersion);
            if (coding != KeyCoding.Rows)
            {
                table.WriteVarint((ulong)coding);
            }

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

    /// <summary>
    /// The index <paramref name="table"/> holds, its page taken when it rides in it; null when the table does not decode. One
    /// pass: each page's first key decoded from the one before into a buffer that grows past a guess of their bytes.
    /// </summary>
    private static GranuleKeys? Parse(Bytes table)
    {
        byte[]? pageKeys = null;
        int[]? ints = null;
        ulong[]? places = null;
        try
        {
            ReadOnlySpan<byte> bytes = table.Span;
            int at = 0;
            if (!Varint(bytes, ref at, out int version) || version is not (RowsVersion or CodedVersion)
                || !CodingOf(version, bytes, ref at, out KeyCoding coding)
                || !Varint(bytes, ref at, out int granuleRows) || !Varint64(bytes, ref at, out ulong rows)
                || !Varint(bytes, ref at, out int components) || !Varint(bytes, ref at, out int pageEntries)
                || !Varint(bytes, ref at, out int pages))
            {
                return null;
            }

            ulong granules = granuleRows == 0 ? 0 : (rows + (ulong)granuleRows - 1) / (ulong)granuleRows;
            if (granuleRows == 0 || rows is 0 or > long.MaxValue || components is 0 or > 64 || pageEntries == 0 || granules > int.MaxValue
                || (ulong)pages != (granules + (ulong)pageEntries - 1) / (ulong)pageEntries || pages > bytes.Length)
            {
                return null;
            }

            int runsAPage = (pageEntries + RestartEvery - 1) / RestartEvery;
            int lastPage = (int)Math.Min((ulong)pageEntries, granules - ((ulong)(pages - 1) * (ulong)pageEntries));
            long runs = ((long)(pages - 1) * runsAPage) + ((lastPage + RestartEvery - 1) / RestartEvery);
            if ((2 * (long)pages) + (3 * runs) > int.MaxValue)
            {
                return null;
            }

            ints = ArrayPool<int>.Shared.Rent((2 * pages) + (3 * (int)runs));
            places = ArrayPool<ulong>.Shared.Rent(2 * pages);
            pageKeys = ArrayPool<byte>.Shared.Rent(Math.Max(bytes.Length * 2, 256));
            int end = 0;
            int before = 0;
            for (int page = 0; page < pages; page++)
            {
                if (!Varint(bytes, ref at, out int shared) || shared > end - before || !Varint(bytes, ref at, out int rest) || rest > bytes.Length - at)
                {
                    return null;
                }

                if (pageKeys.Length - end < shared + rest)
                {
                    byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(end + shared + rest, pageKeys.Length * 2));
                    pageKeys.AsSpan(0, end).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(pageKeys);
                    pageKeys = grown;
                }

                pageKeys.AsSpan(before, shared).CopyTo(pageKeys.AsSpan(end));
                bytes.Slice(at, rest).CopyTo(pageKeys.AsSpan(end + shared));
                at += rest;
                before = end;
                end += shared + rest;
                ints[page] = end;
                if (!Varint(bytes, ref at, out int offset) || !Varint(bytes, ref at, out int length) || bytes.Length - at < sizeof(ulong))
                {
                    return null;
                }

                places[2 * page] = ((ulong)offset << 32) | (uint)length;
                places[(2 * page) + 1] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
                at += sizeof(ulong);
            }

            if (!Varint(bytes, ref at, out int lastKeyLength) || lastKeyLength > bytes.Length - at)
            {
                return null;
            }

            int lastKeyAt = at;
            at += lastKeyLength;
            if (!Varint(bytes, ref at, out int inline) || inline > 1)
            {
                return null;
            }

            Bytes page0 = default;
            if (inline == 1)
            {
                if (pages != 1 || !Varint(bytes, ref at, out int held) || held > bytes.Length - at)
                {
                    return null;
                }

                page0 = table.Slice(at, held);
                at += held;
            }

            if (at != bytes.Length)
            {
                return null;
            }

            ints.AsSpan(pages, pages).Fill(-1);
            GranuleKeys keys = new GranuleKeys(
                table, coding, granuleRows, (long)rows, components, pageEntries, pages, pageKeys, ints, places, lastKeyAt, lastKeyLength, inline == 1);
            pageKeys = null;
            ints = null;
            places = null;
            if (inline == 1 && !keys.Take(page0))
            {
                keys.ForgetTable();
                keys.Dispose();
                return null;
            }

            return keys;
        }
        finally
        {
            if (pageKeys is not null)
            {
                ArrayPool<byte>.Shared.Return(pageKeys);
            }

            if (ints is not null)
            {
                ArrayPool<int>.Shared.Return(ints);
            }

            if (places is not null)
            {
                ArrayPool<ulong>.Shared.Return(places);
            }
        }
    }

    /// <summary>The coding of a table of <paramref name="version"/>, read past the version byte for version 2; false for a coding unknown.</summary>
    private static bool CodingOf(int version, ReadOnlySpan<byte> bytes, ref int at, out KeyCoding coding)
    {
        coding = KeyCoding.Rows;
        if (version == RowsVersion)
        {
            return true;
        }

        if (!Varint(bytes, ref at, out int read) || read != (int)KeyCoding.UnpaddedTexts)
        {
            return false;
        }

        coding = KeyCoding.UnpaddedTexts;
        return true;
    }

    /// <summary>A varint of at most 64 bits at <paramref name="at"/>, which moves past it; false past the bytes or the bits.</summary>
    private static bool Varint64(ReadOnlySpan<byte> bytes, ref int at, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && at < bytes.Length; shift += 7)
        {
            byte b = bytes[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return shift < 63 || b < 0x02;
            }
        }

        return false;
    }

    /// <summary>Lets go of the table without giving it back: a parse refused, whose table stays its caller's.</summary>
    private void ForgetTable() => _table = default;

    /// <summary>A key coded forward against <paramref name="before"/>: the bytes they share, then the rest's length and bytes.</summary>
    private static void WriteForward(ref ProtoWriter writer, ReadOnlySpan<byte> before, ReadOnlySpan<byte> key)
    {
        int shared = key.CommonPrefixLength(before);
        writer.WriteVarint((ulong)shared);
        writer.WriteVarint((ulong)(key.Length - shared));
        writer.WriteRawBytes(key[shared..]);
    }

    /// <summary>
    /// Every granule's key decoded, one record each, end to end: for a reader that compares the keys of several indexes in
    /// any order for a while, a consolidation's cuts, rather than decoding each from its run's whole key again.
    /// </summary>
    internal DecodedKeys DecodeAll()
    {
        int granules = Granules;
        int[] ends = new int[granules];
        long total = 0;
        KeyReader reader = default;
        try
        {
            for (int granule = 0; granule < granules; granule++)
            {
                total += reader.Key(this, granule).Length;
                ends[granule] = checked((int)total);
            }

            byte[] keys = new byte[total];
            for (int granule = 0; granule < granules; granule++)
            {
                reader.Key(this, granule).CopyTo(keys.AsSpan(granule == 0 ? 0 : ends[granule - 1]));
            }

            return new DecodedKeys(keys, ends);
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>The bytes the longest key of granules <paramref name="from"/> to <paramref name="to"/> takes, their pages taken.</summary>
    internal int Longest(int from, int to)
    {
        int longest = 0;
        for (int page = from / PageEntries; page <= (to - 1) / PageEntries && page < Pages; page++)
        {
            longest = Math.Max(longest, Page(page));
        }

        return longest;
    }

    /// <summary>
    /// Decodes the keys of granules <paramref name="from"/> to <paramref name="to"/>, one record each, end to end into
    /// <paramref name="into"/>, with where each ends in <paramref name="ends"/>, granule <c>from + i</c>'s at <c>i</c>:
    /// for a search that reads them in any order, an exclusion search's boxes. <paramref name="into"/> holds
    /// <see cref="Longest"/> bytes a granule.
    /// </summary>
    internal void Decode(int from, int to, Span<byte> into, Span<int> ends)
    {
        KeyReader reader = default;
        try
        {
            int at = 0;
            for (int granule = from; granule < to; granule++)
            {
                ReadOnlySpan<byte> key = reader.Key(this, granule);
                key.CopyTo(into[at..]);
                at += key.Length;
                ends[granule - from] = at;
            }
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>An index's keys decoded (<see cref="DecodeAll"/>): granule <c>i</c>'s key at <c>[i]</c>.</summary>
    internal sealed class DecodedKeys(byte[] keys, int[] ends)
    {
        internal ReadOnlySpan<byte> this[int granule] => keys.AsSpan(granule == 0 ? 0 : ends[granule - 1], ends[granule] - (granule == 0 ? 0 : ends[granule - 1]));
    }

    /// <summary>
    /// A reader of an index's keys into a buffer of its own, which decodes a key from the last it read when it lies after it
    /// in the same run, and from its run's whole key otherwise: keys read in order cost a record each. A key it returns
    /// lasts until it reads another; two keys held at once take two readers. Its buffer is rented and given back by
    /// <see cref="Dispose"/>.
    /// </summary>
    internal struct KeyReader : IDisposable
    {
        private byte[]? _buffer;
        private GranuleKeys? _keys;
        private int _page;
        private int _entry;
        private int _length;
        private int _at;

        /// <summary>The key of granule <paramref name="granule"/> of <paramref name="keys"/>, row-encoded.</summary>
        /// <exception cref="VortexFormatException">Its page does not hold the keys the table names.</exception>
        internal ReadOnlySpan<byte> Key(GranuleKeys keys, int granule)
        {
            int page = granule / keys.PageEntries;
            int entry = granule - (page * keys.PageEntries);
            int longest = keys.Page(page);
            int run = entry / RestartEvery;
            if (_buffer is null || _buffer.Length < longest)
            {
                if (_buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                }

                _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(longest, 64));
                _keys = null;
            }

            if (!ReferenceEquals(keys, _keys) || page != _page || entry < _entry || _entry < run * RestartEvery)
            {
                int global = (page * keys.RunsAPage) + run;
                ReadOnlySpan<byte> whole = keys.RunKey(global);
                whole.CopyTo(_buffer);
                _keys = keys;
                _page = page;
                _entry = run * RestartEvery;
                _length = whole.Length;
                _at = keys.RunNext(global);
            }

            ReadOnlySpan<byte> records = keys.Records(page);
            while (_entry < entry)
            {
                Record(records, ref _at, _length, out int shared, out int restAt, out int rest);
                records.Slice(restAt, rest).CopyTo(_buffer.AsSpan(shared));
                _length = shared + rest;
                _entry++;
            }

            return _buffer.AsSpan(0, _length);
        }

        /// <summary>Gives the buffer back.</summary>
        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
                _keys = null;
            }
        }
    }

    /// <summary>Bytes read of an index: an owner's, given back with the index, or an array's.</summary>
    private readonly struct Bytes(SegmentOwner? owner, byte[]? array, int start, int length)
    {
        /// <summary>The owner of the bytes, null for an array's.</summary>
        internal SegmentOwner? Owner => owner;

        /// <summary>Whether any bytes are held.</summary>
        internal bool Held => owner is not null || array is not null;

        internal int Length => length;

        internal ReadOnlySpan<byte> Span => owner is not null ? owner.Buffer.Span.Slice(start, length) : array.AsSpan(start, length);

        internal Bytes Slice(int at, int count) => new Bytes(owner, array, start + at, count);
    }
}

/// <summary>The granules of an object, in rows: the rows of each, the last fewer, and the object's.</summary>
internal readonly record struct GranuleGrid(int GranuleRows, long Rows)
{
    /// <summary>The object's granules.</summary>
    internal int Granules => (int)((Rows + GranuleRows - 1) / GranuleRows);
}

/// <summary>How a sparse index codes its keys (<see cref="GranuleKeys.Coding"/>).</summary>
internal enum KeyCoding : byte
{
    /// <summary>As the row encoding writes them, a text padded to blocks of 32 bytes: a table of version 1.</summary>
    Rows = 0,

    /// <summary>
    /// Their texts and binaries unpadded, a zero and a one escaped and a zero ending each (Vorticity.RowEncoding's
    /// UnpaddedKeys): a table of version 2.
    /// </summary>
    UnpaddedTexts = 1,
}
