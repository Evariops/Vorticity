using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Encodings;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>
/// The index of a consolidated partition (D13), <c>vorticity.partition.index.v1</c>: an object of the store beside the
/// partition's objects that holds, for the objects it covers, by value rather than by object, the postings of the dataset's
/// identifiers and where the values of its located columns lie (spec D, 7): a lookup of a value reads one page of the
/// partition's index where it read an index of each object. The consolidation writes it; the objects it covers keep no
/// postings of their identifiers, and keep where their columns' values lie, which a prefix reads.
/// </summary>
/// <remarks>
/// <para>
/// The object holds the pages of each entry, one after another, then its directory, then a tail of
/// <see cref="TailBytes"/>: where the directory starts, its length and its XXH3-64, and the magic <c>VXPI</c>. The
/// directory names, behind a version, the objects the index was written for, each by uid, rows and rows a block, its
/// rank being its place; then each entry: its column's name, its kind (<see cref="EntryKind"/>), the blocks a place of it
/// spans (a unit of hashed postings, spec C, 5; a granule where values lie), the bits it keeps of each hash, its postings,
/// the ranks it covers, a bit a rank (an identifier declared after some objects were written covers those written since),
/// and its pages, from the offset of the first, each by its first hash, its length and its XXH3-64.
/// </para>
/// <para>
/// An entry keeps the high bits of each hash, the XXH3-64 of a value's bytes as hashed postings key it, or as the column's
/// sheet keeps it for where values lie: those that number its postings and <see cref="SpareBits"/> more, as a file's hashed
/// postings do, so that a value it lacks meets a hash of it about once in 2¹⁰ lookups. A posting's place numbers the
/// places of every object in rank order, the units of the entry's blocks of each, then one that says the value lies in
/// most of the object's granules, everywhere (<see cref="Everywhere"/>).
/// </para>
/// <para>
/// A page lists its postings by hash, then by place: their count, the bits of a gap and of a place, then the gaps, each
/// hash against the one before, the first against the page's first, which the directory gives, and 0 for a posting of the
/// hash before; then the places; each array packed at its bits, from the least significant bit of each byte. The gaps
/// average about 2¹⁰ whatever the entry's postings, about 14 bits, where the hashes as varints took six bytes and more. A
/// hash's postings lie in one page, which ends past <see cref="PageBytes"/>.
/// </para>
/// </remarks>
internal sealed class PartitionIndex
{
    /// <summary>The version of the directory: 4 since pages pack the hashes' gaps and the places at their bits.</summary>
    internal const ulong Version = 4;

    /// <summary>The block of a value that lies in most of an object's granules, everywhere.</summary>
    internal const uint Everywhere = uint.MaxValue;

    /// <summary>The bits an entry keeps of each hash beyond those its postings need (<see cref="Indexes.KeyRunOptions.HashedSpareBits"/>).</summary>
    internal const int SpareBits = Indexes.KeyRunOptions.HashedSpareBits;

    /// <summary>What an entry of the index holds of its column.</summary>
    internal enum EntryKind
    {
        /// <summary>An identifier's postings: the units of granules its values lie at.</summary>
        Postings = 0,

        /// <summary>Where a located column's values lie, granule by granule (<see cref="Indexes.ColumnLocate"/>).</summary>
        Locate = 1,
    }

    /// <summary>The bytes of the tail: the directory's offset, length and XXH3-64, and the magic.</summary>
    internal const int TailBytes = 24;

    /// <summary>The bytes past which a page ends at the next hash: what a lookup reads of an identifier.</summary>
    internal const int PageBytes = 64 << 10;

    /// <summary>The postings a page's arrays are unpacked by at a time: a whole number of bytes at any width.</summary>
    private const int Stride = 1024;

    /// <summary>The magic that ends the object, <c>VXPI</c>.</summary>
    private const uint Magic = 0x49505856;

    private readonly Covered[] _objects;
    private readonly Entry[] _entries;

    // The objects' ranks by uid, made by the first lookup that asks (RankOf).
    private Dictionary<UInt128, int>? _ranks;

    private PartitionIndex(Covered[] objects, Entry[] entries)
    {
        _objects = objects;
        _entries = entries;
    }

    /// <summary>The objects the index was written for, by rank.</summary>
    internal IReadOnlyList<Covered> Objects => _objects;

    /// <summary>
    /// The rank of the object of uid <paramref name="uid"/>, -1 when the index was not written for it: by a table of the
    /// objects' uids the first lookup that asks makes, kept with the directory a handle's cache holds, where each lookup of
    /// each partition made one.
    /// </summary>
    internal int RankOf(UInt128 uid)
    {
        return (Volatile.Read(ref _ranks) ?? Made()).TryGetValue(uid, out int rank) ? rank : -1;

        Dictionary<UInt128, int> Made()
        {
            Dictionary<UInt128, int> ranks = new Dictionary<UInt128, int>(_objects.Length);
            for (int rank = 0; rank < _objects.Length; rank++)
            {
                ranks[_objects[rank].Uid] = rank;
            }

            return Interlocked.CompareExchange(ref _ranks, ranks, null) ?? ranks;
        }
    }

    /// <summary>The entry of kind <paramref name="kind"/> of the column named <paramref name="name"/>, or null when the index holds none.</summary>
    internal Entry? Of(string name, EntryKind kind)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Kind == kind && string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>An object the index was written for: its uid, its rows, and the rows of its blocks.</summary>
    internal readonly record struct Covered(UInt128 Uid, long Rows, int BlockRows);

    /// <summary>A page of an entry: its first hash, cut to the entry's bits, where it lies, and its XXH3-64.</summary>
    internal readonly record struct Page(ulong FirstHash, long Offset, int Length, ulong Checksum);

    /// <summary>A posting: a hash cut to its entry's bits, the rank of the object that holds a value of it, and the block there.</summary>
    internal readonly record struct Posting(ulong Hash, int Rank, uint Block) : IComparable<Posting>
    {
        public int CompareTo(Posting other)
        {
            int order = Hash.CompareTo(other.Hash);
            if (order != 0)
            {
                return order;
            }

            order = Rank.CompareTo(other.Rank);
            return order != 0 ? order : Block.CompareTo(other.Block);
        }
    }

    /// <summary>The bits an entry of <paramref name="postings"/> postings keeps of each hash: those that number them, rounded up, and <see cref="SpareBits"/> more.</summary>
    internal static int HashBitsFor(long postings) =>
        Math.Min(64, (postings <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(postings - 1))) + SpareBits);

    /// <summary>
    /// Where the places of each of <paramref name="objects"/> start, by rank, for an entry whose places span
    /// <paramref name="unitBlocks"/> blocks, and past the last: each object's units of its rows, then its everywhere.
    /// </summary>
    /// <exception cref="VortexFormatException">The places pass 2³² − 1.</exception>
    internal static uint[] Bases(IReadOnlyList<Covered> objects, int unitBlocks)
    {
        uint[] bases = new uint[objects.Count + 1];
        ulong at = 0;
        for (int rank = 0; rank < objects.Count; rank++)
        {
            bases[rank] = (uint)at;
            Covered held = objects[rank];
            long rowsAPlace = (long)Math.Max(held.BlockRows, 0) * unitBlocks;
            at += (rowsAPlace <= 0 ? 0UL : (ulong)((held.Rows + rowsAPlace - 1) / rowsAPlace)) + 1;
            if (at >= uint.MaxValue)
            {
                throw new VortexFormatException("An index of a partition numbers more places than 2³² − 1.");
            }
        }

        bases[objects.Count] = (uint)at;
        return bases;
    }

    /// <summary>An entry of the index: its column, its kind, the blocks a place spans, the bits of its hashes, its postings, the ranks it covers and its pages.</summary>
    internal sealed class Entry(string name, EntryKind kind, int unitBlocks, int hashBits, long postings, ulong[] ranks, Page[] pages, uint[] bases)
    {
        /// <summary>The column, as the dataset names it.</summary>
        internal string Name => name;

        /// <summary>What it holds of the column.</summary>
        internal EntryKind Kind => kind;

        /// <summary>The blocks of an object a place of its postings spans (<see cref="Indexes.KeyRunOptions.HashedUnitBlocks"/>).</summary>
        internal int UnitBlocks => unitBlocks;

        /// <summary>The high bits it keeps of each hash.</summary>
        internal int HashBits => hashBits;

        /// <summary>The postings its pages hold.</summary>
        internal long Postings => postings;

        /// <summary>Its pages, by first hash.</summary>
        internal IReadOnlyList<Page> Pages => pages;

        /// <summary>Whether its postings cover the object of rank <paramref name="rank"/>.</summary>
        internal bool Covers(int rank) => (uint)(rank >> 6) < (uint)ranks.Length && (ranks[rank >> 6] & (1UL << (rank & 63))) != 0;

        /// <summary><paramref name="hash"/> cut to the bits the entry keeps.</summary>
        internal ulong Cut(ulong hash) => hashBits >= 64 ? hash : hash >> (64 - hashBits);

        /// <summary>The page that may hold <paramref name="hash"/>, a whole hash: the last whose first hash is not past its cut; -1 for none.</summary>
        internal int PageOf(ulong hash)
        {
            ulong cut = Cut(hash);
            int lo = 0;
            int hi = pages.Length;
            while (lo < hi)
            {
                int middle = lo + ((hi - lo) >> 1);
                if (pages[middle].FirstHash <= cut)
                {
                    lo = middle + 1;
                }
                else
                {
                    hi = middle;
                }
            }

            return lo - 1;
        }

        /// <summary>
        /// Where each stride of the postings of <paramref name="page"/>, the bytes of <paramref name="at"/>, starts: the hash
        /// of the posting before its first, the page's first hash for the first. A lookup that holds them begins at the stride
        /// its hash lies in (<see cref="Find"/>), where it summed the gaps from the page's first: half a page of 64 KiB,
        /// 4.2 µs a lookup, for one of a month's thirty pages a value.
        /// </summary>
        /// <exception cref="VortexFormatException">The page does not decode.</exception>
        [SkipLocalsInit]
        internal ulong[] Restarts(ReadOnlySpan<byte> page, Page at)
        {
            int count = Arrays(page, out int gapBits, out _, out ReadOnlySpan<byte> gaps, out _);
            ulong[] restarts = new ulong[(count + Stride - 1) / Stride];
            Span<uint> values = stackalloc uint[Stride];
            ulong running = at.FirstHash;
            for (int stride = 0; stride < restarts.Length; stride++)
            {
                restarts[stride] = running;
                int start = stride * Stride;
                Span<uint> held = values[..Math.Min(Stride, count - start)];
                BitPacking.Unpack32(gaps[(int)BitPacking.PackedBytes(start, gapBits)..], gapBits, held);
                foreach (uint gap in held)
                {
                    running += gap;
                }
            }

            return restarts;
        }

        /// <summary>
        /// Adds to <paramref name="into"/> the rank and block of every posting of <paramref name="hash"/>, a whole hash, in
        /// <paramref name="page"/>, the bytes of <paramref name="at"/>: the gaps unpacked a stride at a time up to the first
        /// past it, from the stride it lies in when <paramref name="restarts"/> says where each starts
        /// (<see cref="Restarts"/>), from the first otherwise; then the places of those that hold it.
        /// </summary>
        /// <exception cref="VortexFormatException">The page does not decode.</exception>
        [SkipLocalsInit]
        internal void Find(ReadOnlySpan<byte> page, Page at, ulong hash, List<(int Rank, uint Block)> into, ReadOnlySpan<ulong> restarts = default)
        {
            ulong cut = Cut(hash);
            if (cut < at.FirstHash)
            {
                return;
            }

            int count = Arrays(page, out int gapBits, out int placeBits, out ReadOnlySpan<byte> gaps, out ReadOnlySpan<byte> places);
            Span<uint> values = stackalloc uint[Stride];
            ulong running = at.FirstHash;
            int from = 0;
            if (restarts.Length == (count + Stride - 1) / Stride)
            {
                // The last stride the posting before whose first hashes below the cut: none before it holds the cut.
                int lo = 0;
                int hi = restarts.Length;
                while (lo < hi)
                {
                    int middle = lo + ((hi - lo) >> 1);
                    if (restarts[middle] < cut)
                    {
                        lo = middle + 1;
                    }
                    else
                    {
                        hi = middle;
                    }
                }

                int stride = Math.Max(lo - 1, 0);
                (from, running) = (stride * Stride, restarts[stride]);
            }

            int first = -1;
            int end = -1;
            for (int start = from; start < count && end < 0; start += Stride)
            {
                int length = Math.Min(Stride, count - start);
                Span<uint> stride = values[..length];
                BitPacking.Unpack32(gaps[(int)BitPacking.PackedBytes(start, gapBits)..], gapBits, stride);
                for (int i = 0; i < length; i++)
                {
                    running += stride[i];
                    if (running > cut)
                    {
                        end = start + i;
                        break;
                    }

                    if (running == cut && first < 0)
                    {
                        first = start + i;
                    }
                }
            }

            if (first < 0)
            {
                return;
            }

            end = end < 0 ? count : end;
            for (int start = first & ~(Stride - 1); start < end; start += Stride)
            {
                int length = Math.Min(Stride, count - start);
                Span<uint> stride = values[..length];
                BitPacking.Unpack32(places[(int)BitPacking.PackedBytes(start, placeBits)..], placeBits, stride);
                for (int i = Math.Max(first, start) - start; i < Math.Min(end - start, length); i++)
                {
                    into.Add(Placed(stride[i]));
                }
            }
        }

        /// <summary>Adds every posting of <paramref name="page"/>, the bytes of <paramref name="at"/>, to <paramref name="into"/>, in order, each hash cut to the entry's bits.</summary>
        /// <exception cref="VortexFormatException">The page does not decode.</exception>
        internal void Decode(ReadOnlySpan<byte> page, Page at, List<Posting> into)
        {
            int count = Arrays(page, out int gapBits, out int placeBits, out ReadOnlySpan<byte> gaps, out ReadOnlySpan<byte> places);
            uint[] rented = ArrayPool<uint>.Shared.Rent(2 * Stride);
            try
            {
                Span<uint> hashes = rented.AsSpan(0, Stride);
                Span<uint> placed = rented.AsSpan(Stride, Stride);
                ulong running = at.FirstHash;
                for (int start = 0; start < count; start += Stride)
                {
                    int length = Math.Min(Stride, count - start);
                    BitPacking.Unpack32(gaps[(int)BitPacking.PackedBytes(start, gapBits)..], gapBits, hashes[..length]);
                    BitPacking.Unpack32(places[(int)BitPacking.PackedBytes(start, placeBits)..], placeBits, placed[..length]);
                    for (int i = 0; i < length; i++)
                    {
                        running += hashes[i];
                        (int rank, uint block) = Placed(placed[i]);
                        into.Add(new Posting(running, rank, block));
                    }
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rented);
            }
        }

        /// <summary>The place of block <paramref name="block"/> of the object of rank <paramref name="rank"/>, <see cref="Everywhere"/> its last.</summary>
        /// <exception cref="InvalidOperationException">The object has no such block.</exception>
        internal uint Place(int rank, uint block)
        {
            uint units = bases[rank + 1] - bases[rank] - 1;
            return block == Everywhere ? bases[rank] + units
                : block < units ? bases[rank] + block
                : throw new InvalidOperationException($"Block {block} of the object of rank {rank} is past its {units} places in the index of '{name}'.");
        }

        /// <summary>The rank and block of place <paramref name="place"/>.</summary>
        /// <exception cref="VortexFormatException">The place is past the last object's.</exception>
        private (int Rank, uint Block) Placed(uint place)
        {
            if (place >= bases[^1])
            {
                throw new VortexFormatException($"A posting of '{name}' in an index of a partition names place {place}, past its {bases[^1]}.");
            }

            // The last rank whose places start at it or before it.
            int lo = 0;
            int hi = bases.Length - 1;
            while (lo < hi)
            {
                int middle = lo + ((hi - lo + 1) >> 1);
                if (bases[middle] <= place)
                {
                    lo = middle;
                }
                else
                {
                    hi = middle - 1;
                }
            }

            uint block = place - bases[lo];
            return (lo, block == bases[lo + 1] - bases[lo] - 1 ? Everywhere : block);
        }

        /// <summary>A page's postings, the bits of their gaps and places, and the two packed arrays.</summary>
        private static int Arrays(ReadOnlySpan<byte> page, out int gapBits, out int placeBits, out ReadOnlySpan<byte> gaps, out ReadOnlySpan<byte> places)
        {
            ProtoReader reader = new ProtoReader(page);
            ulong count = reader.ReadVarint();
            int header = reader.Position;
            if (count == 0 || count > (ulong)page.Length * 8 || page.Length < header + 2)
            {
                throw new VortexFormatException("A page of an index of a partition holds no postings it can read.");
            }

            gapBits = page[header];
            placeBits = page[header + 1];
            long gapBytes = BitPacking.PackedBytes((long)count, gapBits);
            long placeBytes = BitPacking.PackedBytes((long)count, placeBits);
            if (gapBits > 32 || placeBits > 32 || header + 2 + gapBytes + placeBytes != page.Length)
            {
                throw new VortexFormatException("A page of an index of a partition does not hold the arrays its header gives.");
            }

            ReadOnlySpan<byte> arrays = page[(header + 2)..];
            gaps = arrays[..(int)gapBytes];
            places = arrays[(int)gapBytes..];
            return (int)count;
        }
    }

    /// <summary>Where the directory lies, by the tail: false when the bytes are no tail of an index.</summary>
    internal static bool TryTail(ReadOnlySpan<byte> tail, out long offset, out int length, out ulong checksum)
    {
        offset = 0;
        length = 0;
        checksum = 0;
        if (tail.Length != TailBytes || BinaryPrimitives.ReadUInt32LittleEndian(tail[20..]) != Magic)
        {
            return false;
        }

        offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail);
        length = (int)BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        checksum = BinaryPrimitives.ReadUInt64LittleEndian(tail[12..]);
        return offset >= 0 && length > 0;
    }

    /// <summary>The index a directory describes; null when its bytes are not one this version reads.</summary>
    internal static PartitionIndex? Read(ReadOnlySpan<byte> directory, ulong checksum)
    {
        if (XxHash3.HashToUInt64(directory) != checksum)
        {
            return null;
        }

        try
        {
            ProtoReader reader = new ProtoReader(directory);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            Covered[] objects = new Covered[checked((int)reader.ReadVarint())];
            for (int rank = 0; rank < objects.Length; rank++)
            {
                ulong low = reader.ReadFixed64();
                ulong high = reader.ReadFixed64();
                objects[rank] = new Covered(new UInt128(high, low), checked((long)reader.ReadVarint()), checked((int)reader.ReadVarint()));
            }

            Entry[] entries = new Entry[checked((int)reader.ReadVarint())];
            for (int i = 0; i < entries.Length; i++)
            {
                string name = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                ulong kind = reader.ReadVarint();
                int unitBlocks = checked((int)reader.ReadVarint());
                int hashBits = checked((int)reader.ReadVarint());
                long postings = checked((long)reader.ReadVarint());
                if (kind > (ulong)EntryKind.Locate || unitBlocks is < 1 or > Indexes.KeyRunOptions.MostUnitBlocks || hashBits is < 1 or > 64)
                {
                    return null;
                }

                ulong[] ranks = new ulong[checked((int)reader.ReadVarint())];
                for (int word = 0; word < ranks.Length; word++)
                {
                    ranks[word] = reader.ReadFixed64();
                }

                Page[] pages = new Page[checked((int)reader.ReadVarint())];
                long offset = checked((long)reader.ReadVarint());
                for (int page = 0; page < pages.Length; page++)
                {
                    ulong first = reader.ReadFixed64();
                    int length = checked((int)reader.ReadVarint());
                    pages[page] = new Page(first, offset, length, reader.ReadFixed64());
                    offset += length;
                }

                entries[i] = new Entry(name, (EntryKind)kind, unitBlocks, hashBits, postings, ranks, pages, Bases(objects, unitBlocks));
            }

            return reader.End ? new PartitionIndex(objects, entries) : null;
        }
        catch (Exception e) when (e is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes an index: each entry's postings in order, cut in pages that go to <paramref name="output"/> as they fill, then the
    /// directory and the tail.
    /// </summary>
    internal sealed class Writer(ObjectSegmentSink output) : IDisposable
    {
        private readonly List<(string Name, EntryKind Kind, int UnitBlocks, int HashBits, long Postings, ulong[] Ranks, long Offset, List<Page> Pages)> _entries = [];

        // The pages sealed and not yet written, their first hash and their bytes, which the next flush sends in order.
        private readonly Queue<(ulong First, byte[] Bytes, int Length)> _sealed = new();

        // The open entry: the places of its objects, and its postings so far.
        private Entry? _entry;
        private long _postings;

        // The open page: each posting's gap and place, the page's first hash, the hash before, and the largest of each.
        private uint[] _gaps = ArrayPool<uint>.Shared.Rent(Stride);
        private uint[] _places = ArrayPool<uint>.Shared.Rent(Stride);
        private int _count;
        private ulong _pageFirst;
        private ulong _previous;
        private uint _mostGap;
        private uint _mostPlace;

        // The open hash: its cut and its places, closed into the page when the next hash comes.
        private readonly List<uint> _group = [];
        private ulong _hash;
        private bool _grouping;

        /// <summary>
        /// Starts the entry of kind <paramref name="kind"/> of <paramref name="name"/>, a place <paramref name="unitBlocks"/>
        /// blocks of <paramref name="objects"/>, which covers the ranks of <paramref name="ranks"/> and keeps
        /// <paramref name="hashBits"/> bits of each hash.
        /// </summary>
        internal void Begin(string name, EntryKind kind, int unitBlocks, ulong[] ranks, int hashBits, IReadOnlyList<Covered> objects)
        {
            _entry = new Entry(name, kind, unitBlocks, hashBits, 0, ranks, [], Bases(objects, unitBlocks));
            _postings = 0;
            _entries.Add((name, kind, unitBlocks, hashBits, 0, ranks, -1, []));
        }

        /// <summary>
        /// Adds a posting of hash <paramref name="hash"/>, cut to the entry's bits, past every one added to the entry; a place
        /// its hash holds already is left out.
        /// </summary>
        /// <exception cref="InvalidOperationException">The object of rank <paramref name="rank"/> has no block <paramref name="block"/>.</exception>
        internal void Add(ulong hash, int rank, uint block)
        {
            uint place = _entry!.Place(rank, block);
            if (_grouping && hash == _hash)
            {
                if (_group[^1] != place)
                {
                    _group.Add(place);
                }

                return;
            }

            CloseGroup();
            _grouping = true;
            _hash = hash;
            _group.Add(place);
        }

        /// <summary>Sends out the pages sealed so far.</summary>
        internal ValueTask FlushIfFullAsync(CancellationToken cancellationToken) =>
            _sealed.Count > 0 ? FlushAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Ends the entry's postings: its last hash, its last page.</summary>
        internal async ValueTask EndAsync(CancellationToken cancellationToken)
        {
            CloseGroup();
            Seal();
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            (string name, EntryKind kind, int unitBlocks, int hashBits, long _, ulong[] ranks, long offset, List<Page> pages) = _entries[^1];
            _entries[^1] = (name, kind, unitBlocks, hashBits, _postings, ranks, offset, pages);
        }

        /// <summary>Writes the directory, over <paramref name="objects"/>, and the tail.</summary>
        internal async ValueTask FinishAsync(IReadOnlyList<Covered> objects, CancellationToken cancellationToken)
        {
            ProtoWriter directory = new ProtoWriter();
            try
            {
                Span<byte> fixed64 = stackalloc byte[sizeof(ulong)];
                directory.WriteVarint(Version);
                directory.WriteVarint((ulong)objects.Count);
                foreach (Covered held in objects)
                {
                    Fixed(ref directory, fixed64, (ulong)held.Uid);
                    Fixed(ref directory, fixed64, (ulong)(held.Uid >> 64));
                    directory.WriteVarint((ulong)held.Rows);
                    directory.WriteVarint((ulong)held.BlockRows);
                }

                directory.WriteVarint((ulong)_entries.Count);
                foreach ((string name, EntryKind kind, int unitBlocks, int hashBits, long postings, ulong[] ranks, long offset, List<Page> pages) in _entries)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(name);
                    directory.WriteVarint((ulong)utf8.Length);
                    directory.WriteRawBytes(utf8);
                    directory.WriteVarint((ulong)kind);
                    directory.WriteVarint((ulong)unitBlocks);
                    directory.WriteVarint((ulong)hashBits);
                    directory.WriteVarint((ulong)postings);
                    directory.WriteVarint((ulong)ranks.Length);
                    foreach (ulong word in ranks)
                    {
                        Fixed(ref directory, fixed64, word);
                    }

                    directory.WriteVarint((ulong)pages.Count);
                    directory.WriteVarint((ulong)Math.Max(offset, 0));
                    foreach (Page page in pages)
                    {
                        Fixed(ref directory, fixed64, page.FirstHash);
                        directory.WriteVarint((ulong)page.Length);
                        Fixed(ref directory, fixed64, page.Checksum);
                    }
                }

                long at = output.Position;
                byte[] bytes = directory.WrittenSpan.ToArray();
                byte[] tail = new byte[TailBytes];
                BinaryPrimitives.WriteUInt64LittleEndian(tail, (ulong)at);
                BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(8), checked((uint)bytes.Length));
                BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(12), XxHash3.HashToUInt64(bytes));
                BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(20), Magic);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(tail, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                directory.Dispose();
            }

            static void Fixed(ref ProtoWriter writer, Span<byte> bytes, ulong value)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
                writer.WriteRawBytes(bytes);
            }
        }

        public void Dispose()
        {
            foreach ((ulong _, byte[] bytes, int _) in _sealed)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            _sealed.Clear();
            ArrayPool<uint>.Shared.Return(_gaps);
            ArrayPool<uint>.Shared.Return(_places);
            _gaps = [];
            _places = [];
        }

        /// <summary>
        /// Lays the open hash's places in the page, sealed first when it reached <see cref="PageBytes"/> or when the hash's gap
        /// passes 32 bits; the hash starts a page when none is open.
        /// </summary>
        private void CloseGroup()
        {
            if (!_grouping)
            {
                return;
            }

            if (_count > 0 && (PageSize(_mostGap, _mostPlace, _count) >= PageBytes || _hash - _previous > uint.MaxValue))
            {
                Seal();
            }

            if (_count == 0)
            {
                _pageFirst = _hash;
                _previous = _hash;
            }

            // The places of a hash come in order but for whole hashes cut alike, which interleave theirs.
            _group.Sort();
            int kept = 1;
            for (int i = 1; i < _group.Count; i++)
            {
                if (_group[i] != _group[kept - 1])
                {
                    _group[kept++] = _group[i];
                }
            }

            _group.RemoveRange(kept, _group.Count - kept);
            if (_count + _group.Count > _gaps.Length)
            {
                int size = Math.Max(_count + _group.Count, _gaps.Length * 2);
                _gaps = Grown(_gaps, _count, size);
                _places = Grown(_places, _count, size);
            }

            uint gap = (uint)(_hash - _previous);
            for (int i = 0; i < _group.Count; i++)
            {
                _gaps[_count] = i == 0 ? gap : 0;
                _places[_count] = _group[i];
                _mostPlace = Math.Max(_mostPlace, _group[i]);
                _count++;
            }

            _mostGap = Math.Max(_mostGap, gap);
            _postings += _group.Count;
            _previous = _hash;
            _group.Clear();
            _grouping = false;
        }

        /// <summary>Packs the open page into bytes that wait for the next flush, and opens none.</summary>
        private void Seal()
        {
            if (_count == 0)
            {
                return;
            }

            int gapBits = Bits(_mostGap);
            int placeBits = Bits(_mostPlace);
            int length = PageSize(_mostGap, _mostPlace, _count);
            byte[] page = ArrayPool<byte>.Shared.Rent(length);
            int header = Serialization.Varint.Write(page, (ulong)_count);
            page[header] = (byte)gapBits;
            page[header + 1] = (byte)placeBits;
            Span<byte> arrays = page.AsSpan(header + 2, length - header - 2);
            int gapBytes = (int)BitPacking.PackedBytes(_count, gapBits);
            BitPacking.Pack32(_gaps.AsSpan(0, _count), gapBits, arrays[..gapBytes]);
            BitPacking.Pack32(_places.AsSpan(0, _count), placeBits, arrays[gapBytes..]);
            _sealed.Enqueue((_pageFirst, page, length));
            _count = 0;
            _mostGap = 0;
            _mostPlace = 0;
        }

        /// <summary>Sends the sealed pages out, past the entry's pages before them.</summary>
        private async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            while (_sealed.TryDequeue(out (ulong First, byte[] Bytes, int Length) page))
            {
                try
                {
                    ReadOnlyMemory<byte> bytes = page.Bytes.AsMemory(0, page.Length);
                    (string Name, EntryKind Kind, int UnitBlocks, int HashBits, long Postings, ulong[] Ranks, long Offset, List<Page> Pages) entry = _entries[^1];
                    if (entry.Offset < 0)
                    {
                        _entries[^1] = entry with { Offset = output.Position };
                    }

                    entry.Pages.Add(new Page(page.First, output.Position, page.Length, XxHash3.HashToUInt64(bytes.Span)));
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(page.Bytes);
                }
            }
        }

        /// <summary>The bytes of a page of <paramref name="count"/> postings whose gaps and places reach <paramref name="mostGap"/> and <paramref name="mostPlace"/>.</summary>
        private static int PageSize(uint mostGap, uint mostPlace, int count) =>
            Serialization.Varint.Size((ulong)count) + 2
            + (int)BitPacking.PackedBytes(count, Bits(mostGap)) + (int)BitPacking.PackedBytes(count, Bits(mostPlace));

        /// <summary>The bits <paramref name="value"/> takes, 0 for 0.</summary>
        private static int Bits(uint value) => 32 - BitOperations.LeadingZeroCount(value);

        private static uint[] Grown(uint[] rented, int count, int size)
        {
            uint[] grown = ArrayPool<uint>.Shared.Rent(size);
            rented.AsSpan(0, count).CopyTo(grown);
            ArrayPool<uint>.Shared.Return(rented);
            return grown;
        }
    }
}
