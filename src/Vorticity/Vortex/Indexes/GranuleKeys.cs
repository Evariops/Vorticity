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

    /// <summary>
    /// The bytes the index holds in memory: each page's first key, the last key, and the keys of the pages in hand with
    /// where each ends.
    /// </summary>
    internal long ResidentBytes
    {
        get
        {
            long bytes = LastKey.Length;
            for (int page = 0; page < _keys.Length; page++)
            {
                bytes += _pageKeys[page].Length + (System.Threading.Volatile.Read(ref _keys[page])?.Bytes ?? 0);
            }

            return bytes;
        }
    }

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
    /// Takes page <paramref name="page"/>'s keys from its bytes, which their checksum vouches for; false when they do not
    /// decode, or are not the bytes the table names. The page is kept as its bytes, coded forward, with every
    /// <see cref="RestartEvery"/>th key whole: a key a few bytes rather than the whole of it.
    /// </summary>
    internal bool TakePage(int page, ReadOnlySpan<byte> bytes)
    {
        (long _, int length, ulong checksum) = _pages[page];
        if (bytes.Length != length || XxHash3.HashToUInt64(bytes) != checksum)
        {
            return false;
        }

        int count = Math.Min(PageEntries, Granules - (page * PageEntries));
        int restarts = (count + RestartEvery - 1) / RestartEvery;
        ReadOnlySpan<byte> pageKey = _pageKeys[page];

        // The keys' lengths first, which size the whole keys kept and the longest; then the keys, each from the one before.
        int previous = pageKey.Length;
        int longest = previous;
        int whole = 0;
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            if (!Record(bytes, ref at, previous, out int shared, out int restAt, out int rest))
            {
                return false;
            }

            previous = shared + rest;
            longest = Math.Max(longest, previous);
            whole += i % RestartEvery == 0 ? previous : 0;
        }

        if (at != bytes.Length)
        {
            return false;
        }

        byte[] key = new byte[longest];
        byte[] keys = new byte[whole];
        int[] ends = new int[restarts];
        int[] next = new int[restarts];
        pageKey.CopyTo(key);
        at = 0;
        for (int i = 0, kept = 0; i < count; i++)
        {
            Record(bytes, ref at, longest, out int shared, out int restAt, out int rest);
            bytes.Slice(restAt, rest).CopyTo(key.AsSpan(shared));
            if (i % RestartEvery == 0)
            {
                key.AsSpan(0, shared + rest).CopyTo(keys.AsSpan(kept));
                kept += shared + rest;
                ends[i / RestartEvery] = kept;
                next[i / RestartEvery] = at;
            }

            if (i == 0 && !key.AsSpan(0, shared + rest).SequenceEqual(pageKey))
            {
                return false;
            }
        }

        System.Threading.Interlocked.CompareExchange(ref _keys[page], new PageKeys(bytes.ToArray(), keys, ends, next, longest), null);
        return true;
    }

    /// <summary>The granules a page keeps one key of whole for, the first of each run: a key decodes from at most this many records before it.</summary>
    internal const int RestartEvery = 16;

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

    /// <summary>The keys of page <paramref name="page"/>, which it holds.</summary>
    /// <exception cref="InvalidOperationException">The page was not taken.</exception>
    private PageKeys PageOf(int page) =>
        System.Threading.Volatile.Read(ref _keys[page]) ?? throw new InvalidOperationException($"Page {page} of the key index was not read.");

    /// <summary>The runs a page of the index holds, a whole key each: <see cref="RestartEvery"/> granules a run, the last fewer.</summary>
    private int RunsOf(int page) => (Math.Min(PageEntries, Granules - (page * PageEntries)) + RestartEvery - 1) / RestartEvery;

    /// <summary>The runs a full page holds.</summary>
    private int RunsAPage => (PageEntries + RestartEvery - 1) / RestartEvery;

    /// <summary>The runs of the whole index.</summary>
    private int Runs => ((Pages - 1) * RunsAPage) + RunsOf(Pages - 1);

    /// <summary>The first granule of run <paramref name="run"/>, whose key the page keeps whole.</summary>
    private int RunStart(int run) => ((run / RunsAPage) * PageEntries) + ((run % RunsAPage) * RestartEvery);

    /// <summary>The run granule <paramref name="granule"/> lies in.</summary>
    private int RunOf(int granule) => ((granule / PageEntries) * RunsAPage) + ((granule % PageEntries) / RestartEvery);

    /// <summary>The whole key of the first granule of run <paramref name="run"/>.</summary>
    private ReadOnlySpan<byte> RunKey(int run) => PageOf(run / RunsAPage).Whole(run % RunsAPage);

    /// <summary>
    /// The first granule whose key lies at or past <paramref name="key"/>, <see cref="Granules"/> when none does: the one
    /// before it may hold keys at or past it too, those before that none. Every page in hand first.
    /// </summary>
    internal int FirstAtOrPast(ReadOnlySpan<byte> key)
    {
        int compared = 0;
        return First(key, 0, past: false, ref compared);
    }

    /// <summary>
    /// The first granule from <paramref name="from"/> on whose key reaches <paramref name="bound"/> (<see cref="Reaches"/>),
    /// <see cref="Granules"/> when none does, every page in hand; each key compared added to <paramref name="compared"/>.
    /// The runs' whole keys first, which need no decoding: from the first granule a bisection, from a later one a gallop
    /// then a bisection, so that the ascending bounds of one search each cost the logarithm of how far the next lies; then
    /// the keys of one run, each decoded from the one before.
    /// </summary>
    internal int First(ReadOnlySpan<byte> bound, int from, bool past, ref int compared)
    {
        int granules = Granules;
        if (from >= granules)
        {
            return granules;
        }

        // The first run past the one that holds `from` whose first key reaches; every key before it, from its own run's
        // first, falls short.
        int runs = Runs;
        int holding = RunOf(from);
        int lo = holding + 1;
        int hi = runs;
        if (from > 0)
        {
            int probe = lo;
            int step = 1;
            while (probe < runs)
            {
                compared++;
                if (Reaches(RunKey(probe), bound, past))
                {
                    break;
                }

                lo = probe + 1;
                probe = lo + step - 1;
                step <<= 1;
            }

            hi = Math.Min(probe, runs);
        }

        while (lo < hi)
        {
            int middle = lo + ((hi - lo) >> 1);
            compared++;
            if (Reaches(RunKey(middle), bound, past))
            {
                hi = middle;
            }
            else
            {
                lo = middle + 1;
            }
        }

        // The granule lies in the run before, from `from` in its own run, past the run's first otherwise, which falls short:
        // its keys decoded one from the one before, on the stack.
        int end = lo == runs ? granules : RunStart(lo);
        int start = lo - 1 == holding ? from : RunStart(lo - 1) + 1;
        if (start >= end)
        {
            return end;
        }

        int page = start / PageEntries;
        PageKeys held = PageOf(page);
        int run = (start - (page * PageEntries)) / RestartEvery;
        int first = (page * PageEntries) + (run * RestartEvery);
        Span<byte> key = held.Longest <= 512 ? stackalloc byte[512] : new byte[held.Longest];
        ReadOnlySpan<byte> whole = held.Whole(run);
        whole.CopyTo(key);
        int length = whole.Length;
        int at = held.Next(run);
        ReadOnlySpan<byte> records = held.Records;
        for (int granule = first; granule < end; granule++)
        {
            if (granule > first)
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

    /// <summary>
    /// The keys of a page, read: its bytes as written, each key coded forward against the one before; the whole key of the
    /// first granule of each run of <see cref="RestartEvery"/>, end to end, where each ends, and where the record past it
    /// starts; and the longest key's length.
    /// </summary>
    private sealed class PageKeys(byte[] records, byte[] whole, int[] ends, int[] next, int longest)
    {
        /// <summary>The bytes the page's keys take.</summary>
        internal long Bytes => records.Length + whole.Length + ((long)(ends.Length + next.Length) * sizeof(int));

        /// <summary>The bytes of the longest of its keys.</summary>
        internal int Longest => longest;

        /// <summary>The records of its keys, coded forward.</summary>
        internal ReadOnlySpan<byte> Records => records;

        /// <summary>The whole key of the first granule of run <paramref name="run"/>.</summary>
        internal ReadOnlySpan<byte> Whole(int run)
        {
            int start = run == 0 ? 0 : ends[run - 1];
            return whole.AsSpan(start, ends[run] - start);
        }

        /// <summary>Where the record past the first key of run <paramref name="run"/> starts.</summary>
        internal int Next(int run) => next[run];
    }

    /// <summary>
    /// Every granule's key decoded, one record each, end to end: for a reader that compares the keys of several indexes in
    /// any order for a while, a consolidation's cuts, rather than decoding each from its run's whole key again. Every page
    /// in hand first.
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

    /// <summary>The bytes the longest key of granules <paramref name="from"/> to <paramref name="to"/> takes, every page in hand.</summary>
    internal int Longest(int from, int to)
    {
        int longest = 0;
        for (int page = from / PageEntries; page <= (to - 1) / PageEntries && page < Pages; page++)
        {
            longest = Math.Max(longest, PageOf(page).Longest);
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
        private PageKeys? _page;
        private int _entry;
        private int _length;
        private int _at;

        /// <summary>The key of granule <paramref name="granule"/> of <paramref name="keys"/>, row-encoded; its page in hand.</summary>
        /// <exception cref="InvalidOperationException">Its page was not taken.</exception>
        internal ReadOnlySpan<byte> Key(GranuleKeys keys, int granule)
        {
            int page = granule / keys.PageEntries;
            int entry = granule - (page * keys.PageEntries);
            PageKeys held = keys.PageOf(page);
            int run = entry / RestartEvery;
            if (_buffer is null || _buffer.Length < held.Longest)
            {
                if (_buffer is not null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
                }

                _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(held.Longest, 64));
                _page = null;
            }

            if (!ReferenceEquals(held, _page) || entry < _entry || _entry < run * RestartEvery)
            {
                ReadOnlySpan<byte> whole = held.Whole(run);
                whole.CopyTo(_buffer);
                _page = held;
                _entry = run * RestartEvery;
                _length = whole.Length;
                _at = held.Next(run);
            }

            ReadOnlySpan<byte> records = held.Records;
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
                System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
                _page = null;
            }
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
