using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Vorticity.Buffers;

namespace Vorticity.Aggregating;

/// <summary>
/// Byte strings numbered in the order they are first seen: an open-addressing table over XxHash3,
/// the keys packed in pages. It holds text keys, the row-encoded tuples of a composite key and
/// the (group, value) pairs of a distinct count, with no allocation per key.
/// </summary>
/// <remarks>
/// <para>
/// A probe reads two lines: its slot, the high half of the key's hash
/// and where the key lies, then, when the half matches, the key itself, its number and its length
/// ahead of its bytes. A slot holding the number alone sent the probe on to the key's hash, place and
/// length, each in an array of its own: five lines. A slot of 8 bytes, where one of 16 held the number
/// and the length too, took 24 MiB more at a million keys.
/// </para>
/// <para>
/// The keys lie in pages of <see cref="PageBytes"/>, which a key never straddles, one longer than a
/// page in a page of its own: the table grows by a page, its keys never copied, and holds more than an
/// array does, up to 16 GiB of keys. In one array a lane's table stopped near 2 GiB, 20 million URLs of a
/// hundred bytes, and every doubling held the old keys beside the new ones for the copy. The first page
/// starts at a kilobyte and doubles up to a page, so that a table of a few keys takes a few kilobytes. A
/// key's place is where it starts over the pages, in units of four bytes, plus one: what a slot of 8
/// bytes holds beside the half of the hash.
/// </para>
/// <para>
/// A key comes hashed under <see cref="MergeHash.Seed"/>, the hash a merge cuts the parts by, so that
/// it is hashed once. Keys can be built to share the low bits of that hash, and so one run of the
/// probe sequence, by whoever learns the seed: a key that would land <see cref="MaxProbes"/> slots
/// past its own reseeds the hash with a value of the table's own and rehashes every key, which such
/// keys cannot have been built against. Until then every key sits closer than that to its own slot,
/// so no lookup probes further: a growth places the keys again in the order they came, and none
/// lands further than it was.
/// </para>
/// </remarks>
internal sealed class ByteKeyTable
{
    /// <summary>How far past its own slot a key lands before the table reseeds: linear probing at half load keeps its runs far shorter.</summary>
    private const int MaxProbes = 128;

    /// <summary>The bits of a position within a page.</summary>
    private const int PageBits = 20;

    /// <summary>The bytes of a page: a megabyte, a dozen pages at a million keys of fifty bytes.</summary>
    internal const int PageBytes = 1 << PageBits;

    /// <summary>The bytes of the first page at first, which doubles up to a page.</summary>
    private const int FirstBytes = 1024;

    /// <summary>The bits a key's position drops in its place: positions are multiples of four.</summary>
    private const int UnitBits = 2;

    // Each key's number, its length and its bytes, one after the other, in the order keys came, in
    // pages; a page past the last a key took, null, or kept from before a Clear or a Retain. And where
    // the next key goes, a position over the pages.
    // The pages are the bytes of blocks the table rents whole, a class each, so that a page's length is its bytes'.
    private byte[]?[] _pages = new byte[]?[4];
    private long _used;

    // Where each key lies, its place, 0 for a detached number; the hash it was placed by.
    private Block<uint> _places;
    private Block<ulong> _hashes;
    private Block<Slot> _slots;
    private long _seed;

    // The lane's account its blocks are rented under, the query's memory's.
    private readonly TableArrays _account;

    /// <summary>A table whose blocks are rented under <paramref name="account"/>, which reserves each before it comes; under none, uncounted.</summary>
    internal ByteKeyTable(TableArrays? account = null)
    {
        _account = account ?? TableArrays.Uncounted;

        // The first blocks made at their size, uncounted, as a table's first arrays were made outside its account: given
        // back, they join the pool; a table never released leaves them to the collector, never taking the pool's.
        _pages[0] = Blocks.Allocate<byte>(FirstBytes).Bytes;
        _places = Blocks.Allocate<uint>(16);
        _hashes = Blocks.Allocate<ulong>(16);
        _slots = Blocks.Allocate<Slot>(32);
    }

    /// <summary>The number of distinct keys.</summary>
    internal int Count { get; private set; }

    /// <summary>
    /// The bytes the table would take more were <paramref name="more"/> new keys to come: its slots, at most
    /// half full, its places and hashes, and the pages their bytes take, at the mean of the keys it holds,
    /// past what its pages hold.
    /// </summary>
    internal long GrowthFor(int more)
    {
        long bytes = TableGrowth.Of(Count, more, _slots.Length, _slots.Length / 2, Unsafe.SizeOf<Slot>())
            + TableGrowth.Of(Count, more, _places.Length, _places.Length, sizeof(uint) + sizeof(ulong));
        long held = 0;
        foreach (byte[]? page in _pages)
        {
            held += page?.Length ?? 0;
        }

        long mean = Count > 0 ? (_used / Count) + 1 : 64;
        long past = _used + (more * mean) - held;
        return past <= 0 ? bytes : bytes + (((past + PageBytes - 1) >> PageBits) << PageBits);
    }

    /// <summary>Whether a key landed far enough from its own slot for the table to take a seed.</summary>
    internal bool Reseeded => _seed != 0;

    /// <summary>The bytes the table holds at its capacity: the pages of the keys, where each lies, their hashes and the slots.</summary>
    internal long Footprint
    {
        get
        {
            long pages = (long)_pages.Length * IntPtr.Size;
            foreach (byte[]? page in _pages)
            {
                pages += page?.Length ?? 0;
            }

            return pages + ((long)_places.Length * sizeof(uint)) + ((long)_hashes.Length * sizeof(ulong)) + ((long)_slots.Length * Unsafe.SizeOf<Slot>());
        }
    }

    /// <summary>The number of <paramref name="key"/>, which is added when it is new.</summary>
    internal int GetOrAdd(ReadOnlySpan<byte> key, out bool added) => GetOrAdd(key, MergeHash.Of(key, MergeHash.Seed), out added);

    /// <summary>The number of <paramref name="key"/>, whose hash under <see cref="MergeHash.Seed"/> is <paramref name="hash"/>; added when it is new.</summary>
    internal int GetOrAdd(ReadOnlySpan<byte> key, ulong hash, out bool added)
    {
        if (_seed != 0)
        {
            hash = XxHash3.HashToUInt64(key, _seed);
        }

        uint half = (uint)(hash >> 32);
        int mask = _slots.Length - 1;
        int at = (int)hash & mask;
        while (true)
        {
            Slot slot = _slots[at];
            if (slot.Place == 0)
            {
                break;
            }

            if (slot.Half == half)
            {
                ReadOnlySpan<byte> stored = Stored(slot.Place);
                if (stored.SequenceEqual(key))
                {
                    added = false;
                    return NumberAt(slot.Place);
                }
            }

            at = (at + 1) & mask;
        }

        if (((at - (int)hash) & mask) >= MaxProbes && _seed == 0)
        {
            Reseed();
            return GetOrAdd(key, hash, out added);
        }

        int number = Count;
        Append(key, hash);
        _slots[at] = new Slot { Half = half, Place = _places[number] };
        added = true;
        if (Count * 2 > _slots.Length)
        {
            Rehash(NewSlots(GroupKeys.Doubled(_slots.Length)));
        }

        return number;
    }

    /// <summary>The hash key <paramref name="index"/> was placed by: under <see cref="MergeHash.Seed"/> until the table is <see cref="Reseeded"/>.</summary>
    internal ulong HashOf(int index) => _hashes[index];

    /// <summary>
    /// The first half of a chunk's lookups: each key's number where its home
    /// slot holds it, -1 where it does not (past its home, new, or null). Every row's slot is read before
    /// any key is compared, and every candidate's key compared after, no row waiting on another: at a
    /// million keys, a lookup's two lines, its slot then its key, each a miss, one after the other. The
    /// rows left go through <see cref="GetOrAdd(ReadOnlySpan{byte}, ulong, out bool)"/> in their order.
    /// </summary>
    /// <param name="hashes">Each row's hash under <see cref="MergeHash.Seed"/>; the table not <see cref="Reseeded"/>.</param>
    /// <param name="keys">The rows' keys, row <paramref name="first"/> the chunk's first.</param>
    /// <param name="first">The chunk's first row in <paramref name="keys"/> and <paramref name="validity"/>.</param>
    /// <param name="validity">The rows' validity; empty when none is null.</param>
    /// <param name="numbers">Each row's number, or -1.</param>
    internal void FindAtHome(ReadOnlySpan<ulong> hashes, BytesBlock keys, int first, ReadOnlySpan<ulong> validity, Span<int> numbers)
    {
        // The first pass keeps each candidate's place, its bits as an int, 0 for none.
        Span<Slot> slots = _slots.AsSpan();
        int mask = slots.Length - 1;
        numbers = numbers[..hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            ulong hash = hashes[i];
            Slot slot = slots[(int)hash & mask];
            numbers[i] = slot.Half == (uint)(hash >> 32) ? unchecked((int)slot.Place) : 0;
        }

        for (int i = 0; i < numbers.Length; i++)
        {
            uint place = unchecked((uint)numbers[i]);
            int row = first + i;
            numbers[i] = place != 0 && StorageValues.IsValid(validity, row) && Stored(place).SequenceEqual(keys[row]) ? NumberAt(place) : -1;
        }
    }

    /// <summary>
    /// A number no key finds: an entry without bytes that no lookup reaches, for a group with no key,
    /// the null of a text key, whose groups are then the table's entries. Its bytes are never read.
    /// </summary>
    internal int AddDetached()
    {
        int number = Count;
        Grow(number + 1);
        _places[number] = 0;
        _hashes[number] = 0;
        Count = number + 1;
        return number;
    }

    /// <summary>Forgets every key, keeping the pages, the buffers and the seed.</summary>
    internal void Clear()
    {
        Count = 0;
        _used = 0;
        _slots.Clear();
    }

    /// <summary>
    /// Gives the pages, the places, the hashes and the slots back to the account they were rented under:
    /// the keys let go, read no more.
    /// </summary>
    internal void Release()
    {
        for (int p = 0; p < _pages.Length; p++)
        {
            if (_pages[p] is { } page)
            {
                Give(page);
                _pages[p] = null;
            }
        }

        _account.Return(_places);
        _account.Return(_hashes);
        _account.Return(_slots);
        _places = default;
        _hashes = default;
        _slots = default;
        Count = 0;
        _used = 0;
    }

    /// <summary>The bytes of key <paramref name="index"/>.</summary>
    internal ReadOnlySpan<byte> KeyOf(int index) => Stored(_places[index]);

    /// <summary>
    /// Keeps keys <paramref name="entries"/> alone, key <c>entries[i]</c> becoming key <c>i</c>: their
    /// bytes moved toward the front in place, page after page, their numbers written again, and the slots
    /// filled again from the hashes kept, so that no key is copied out or hashed again. <paramref name="entries"/> ascend.
    /// </summary>
    internal void Retain(ReadOnlySpan<int> entries)
    {
        // Placed again as they were placed first, in their order: a key only moves toward the front,
        // into the room of those left out before it, and never past where it lies.
        long used = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            int entry = entries[i];
            uint place = _places[entry];
            _hashes[i] = _hashes[entry];
            if (place == 0)
            {
                _places[i] = 0;
                continue;
            }

            long from = Position(place);
            byte[] source = _pages[from >> PageBits]!;
            int offset = (int)(from & (PageBytes - 1));
            int length = Entry(Ahead(source, offset, out int keyLength) + keyLength);
            long to = Placed(used, length);
            if (length > PageBytes)
            {
                // A page of its own: the page itself moves, its bytes stay where they are.
                if (to != from)
                {
                    if (_pages[to >> PageBits] is { } stale)
                    {
                        Give(stale);
                    }

                    _pages[to >> PageBits] = source;
                    _pages[from >> PageBits] = null;
                }
            }
            else if (to != from)
            {
                source.AsSpan(offset, length).CopyTo(Page(to, length).AsSpan((int)(to & (PageBytes - 1))));
            }

            byte[] target = _pages[to >> PageBits]!;
            Unsafe.WriteUnaligned(ref target[(int)(to & (PageBytes - 1))], i);
            _places[i] = PlaceOf(to);
            used = to + (length > PageBytes ? RoundToPage(length) : length);
        }

        Count = entries.Length;
        _used = used;
        _slots.Clear();
        Rehash(_slots);
    }

    /// <summary>The key whose place is <paramref name="place"/>: its bytes, past its number and length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Stored(uint place)
    {
        long position = Position(place);
        byte[] page = _pages[position >> PageBits]!;
        int offset = (int)(position & (PageBytes - 1));
        int ahead = Ahead(page, offset, out int length);
        return page.AsSpan(offset + sizeof(int) + ahead, length);
    }

    /// <summary>The number of the key whose place is <paramref name="place"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NumberAt(uint place)
    {
        long position = Position(place);
        return Unsafe.ReadUnaligned<int>(ref _pages[position >> PageBits]![(int)(position & (PageBytes - 1))]);
    }

    /// <summary>Where a key whose place is <paramref name="place"/> starts over the pages.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Position(uint place) => (long)(place - 1) << UnitBits;

    /// <summary>The place of a key that starts at <paramref name="position"/>, a multiple of four.</summary>
    private static uint PlaceOf(long position) => (uint)(position >> UnitBits) + 1;

    /// <summary>The bytes an entry of <paramref name="bytes"/> takes, its next one starting on a multiple of four.</summary>
    private static int Entry(int bytes) => (sizeof(int) + bytes + 3) & ~3;

    /// <summary>The bytes of the pages a key longer than a page takes: a whole number of pages.</summary>
    private static long RoundToPage(long bytes) => (bytes + PageBytes - 1) & ~(long)(PageBytes - 1);

    /// <summary>
    /// Where an entry of <paramref name="length"/> bytes goes from <paramref name="used"/>: there when it
    /// fits the rest of its page, at the next page's start when it does not, and a page's start when it
    /// is longer than a page.
    /// </summary>
    private static long Placed(long used, int length)
    {
        long within = used & (PageBytes - 1);
        return within == 0 || (length <= PageBytes && within + length <= PageBytes) ? used : RoundToPage(used);
    }

    /// <summary>The length of the key at <paramref name="offset"/> of <paramref name="page"/>, in 7-bit groups past its number; the bytes they take.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Ahead(byte[] page, int offset, out int length)
    {
        ReadOnlySpan<byte> bytes = page.AsSpan(offset + sizeof(int));
        byte first = bytes[0];
        if (first < 0x80)
        {
            length = first;
            return 1;
        }

        uint value = 0;
        int shift = 0;
        int read = 0;
        byte part;
        do
        {
            part = bytes[read++];
            value |= (uint)(part & 0x7F) << shift;
            shift += 7;
        }
        while ((part & 0x80) != 0);

        length = (int)value;
        return read;
    }

    private void Append(ReadOnlySpan<byte> key, ulong hash)
    {
        int index = Count;
        Grow(index + 1);
        int ahead = 1;
        for (uint rest = (uint)key.Length; rest >= 0x80; rest >>= 7)
        {
            ahead++;
        }

        int length = Entry(ahead + key.Length);
        long at = Placed(_used, length);
        if (at >> UnitBits >= uint.MaxValue)
        {
            throw new VortexUnsupportedException(
                "group by of more than 16 GiB of key bytes on one lane",
                ComponentKind.Feature,
                "A table of text keys, composite keys or distinct pairs holds 16 GiB of their bytes at most on a lane. Group by fewer keys at once, or on more lanes.");
        }

        byte[] page = Page(at, length);
        int offset = (int)(at & (PageBytes - 1));
        Unsafe.WriteUnaligned(ref page[offset], index);
        int write = offset + sizeof(int);
        uint rest2 = (uint)key.Length;
        while (rest2 >= 0x80)
        {
            page[write++] = (byte)(rest2 | 0x80);
            rest2 >>= 7;
        }

        page[write++] = (byte)rest2;
        key.CopyTo(page.AsSpan(write));
        _places[index] = PlaceOf(at);
        _hashes[index] = hash;
        _used = at + (length > PageBytes ? RoundToPage(length) : length);
        Count = index + 1;
    }

    /// <summary>
    /// The page an entry of <paramref name="length"/> bytes at <paramref name="at"/> goes in: the first,
    /// doubled up to a page when it falls short; a page kept from before, or a new one; a page of its own
    /// for an entry longer than a page.
    /// </summary>
    private byte[] Page(long at, int length)
    {
        int index = (int)(at >> PageBits);
        if (index >= _pages.Length)
        {
            Array.Resize(ref _pages, Math.Max(index + 1, 2 * _pages.Length));
        }

        byte[]? page = _pages[index];
        if (length > PageBytes)
        {
            if (page is null || page.Length < length)
            {
                if (page is not null)
                {
                    Give(page);
                }

                page = _pages[index] = Take(length);
            }

            return page;
        }

        int end = (int)(at & (PageBytes - 1)) + length;
        if (page is null)
        {
            page = _pages[index] = Take(PageBytes);
        }
        else if (page.Length < end)
        {
            // The first page alone is shorter than a page: a few kilobytes for a table of a few keys. Grown its whole class
            // long, as every page is.
            Block<byte> grown = Block<byte>.Over(page, page.Length);
            TableArrays.Resize(_account, ref grown, (int)Blocks.BytesFor<byte>(Math.Min(PageBytes, Math.Max(end, 2 * page.Length))));
            page = _pages[index] = grown.Bytes!;
        }

        return page;
    }

    /// <summary>
    /// The bytes of a block of <paramref name="length"/> bytes at least for keys, rented under the account its whole
    /// class long, so that a page's length is all its bytes and what the account counts of it.
    /// </summary>
    private byte[] Take(int length) => _account.Rent<byte>((int)Blocks.BytesFor<byte>(length), zeroed: false).Bytes!;

    /// <summary>A page of the table's given back to its account.</summary>
    private void Give(byte[] page) => _account.Return(Block<byte>.Over(page, page.Length));

    /// <summary>Room for <paramref name="count"/> numbers.</summary>
    private void Grow(int count)
    {
        if (count > _places.Length)
        {
            int grown = Math.Max(count, GroupKeys.Doubled(_places.Length));
            TableArrays.Resize(_account, ref _places, grown);
            TableArrays.Resize(_account, ref _hashes, grown);
        }
    }

    /// <summary>Hashes every key again under a seed of the table's own.</summary>
    private void Reseed()
    {
        _seed = Random.Shared.NextInt64(1, long.MaxValue);
        for (int index = 0; index < Count; index++)
        {
            if (_places[index] != 0)
            {
                _hashes[index] = XxHash3.HashToUInt64(KeyOf(index), _seed);
            }
        }

        Rehash(NewSlots(_slots.Length));
    }

    /// <summary>Empty slots, rented under the table's account.</summary>
    private Block<Slot> NewSlots(int length) => _account.Rent<Slot>(length, zeroed: true);

    /// <summary>Places every key in <paramref name="slots"/>, empty, by the hash it keeps, and makes them the table's; the slots it held given back.</summary>
    private void Rehash(Block<Slot> slots)
    {
        Block<Slot> old = _slots;
        int mask = slots.Length - 1;
        for (int index = 0; index < Count; index++)
        {
            uint place = _places[index];
            if (place == 0)
            {
                continue;
            }

            ulong hash = _hashes[index];
            int at = (int)hash & mask;
            while (slots[at].Place != 0)
            {
                at = (at + 1) & mask;
            }

            slots[at] = new Slot { Half = (uint)(hash >> 32), Place = place };
        }

        _slots = slots;
        if (!old.SameBytes(slots))
        {
            _account.Return(old);
        }
    }

    /// <summary>What a probe reads first: the high half of a key's hash, and where it lies, its place (zero, an empty slot).</summary>
    private struct Slot
    {
        public uint Half;
        public uint Place;
    }
}
