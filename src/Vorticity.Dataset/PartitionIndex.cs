using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>
/// The index of a consolidated partition (D13), <c>vorticity.partition.index.v1</c>: an object of the store beside the
/// partition's objects that holds, for the objects it covers, the postings of the dataset's identifiers, by value rather
/// than by object: a lookup of a value reads one page of the partition's index where it read the postings of each object.
/// The consolidation writes it, and the objects it covers keep no postings of their identifiers.
/// </summary>
/// <remarks>
/// <para>
/// The object holds the pages of each identifier, one after another, then its directory, then a tail of
/// <see cref="TailBytes"/>: where the directory starts, its length and its XXH3-64, and the magic <c>VXPI</c>. The
/// directory names, behind a version, the objects the index was written for, each by uid, rows and rows a block, its
/// rank being its place; then each identifier: its name, the ranks it covers, a bit a rank (an identifier declared after
/// some objects were written covers those written since), and its pages, from the offset of the first, each by its first
/// hash, its length and its XXH3-64.
/// </para>
/// <para>
/// A page lists hashes ascending, each the XXH3-64 of a value's bytes as hashed postings key it, coded forward against
/// the one before as a varint, the first against the page's first, which the directory gives; then how many blocks hold
/// it, and each by rank and block, ascending: the rank coded forward against the posting before, the block against the
/// one before when the rank is the same. A hash's postings lie in one page, which ends past <see cref="PageBytes"/>.
/// </para>
/// </remarks>
internal sealed class PartitionIndex
{
    /// <summary>The version of the directory.</summary>
    internal const ulong Version = 1;

    /// <summary>The bytes of the tail: the directory's offset, length and XXH3-64, and the magic.</summary>
    internal const int TailBytes = 24;

    /// <summary>The bytes past which a page ends at the next hash: what a lookup reads of an identifier.</summary>
    internal const int PageBytes = 64 << 10;

    /// <summary>The magic that ends the object, <c>VXPI</c>.</summary>
    private const uint Magic = 0x49505856;

    private readonly Covered[] _objects;
    private readonly Identifier[] _identifiers;

    private PartitionIndex(Covered[] objects, Identifier[] identifiers)
    {
        _objects = objects;
        _identifiers = identifiers;
    }

    /// <summary>The objects the index was written for, by rank.</summary>
    internal IReadOnlyList<Covered> Objects => _objects;

    /// <summary>The identifiers it holds the postings of.</summary>
    internal IReadOnlyList<Identifier> Identifiers => _identifiers;

    /// <summary>The identifier named <paramref name="name"/>, or null when the index holds none of it.</summary>
    internal Identifier? Of(string name)
    {
        foreach (Identifier identifier in _identifiers)
        {
            if (string.Equals(identifier.Name, name, StringComparison.Ordinal))
            {
                return identifier;
            }
        }

        return null;
    }

    /// <summary>An object the index was written for: its uid, its rows, and the rows of its blocks.</summary>
    internal readonly record struct Covered(UInt128 Uid, long Rows, int BlockRows);

    /// <summary>A page of an identifier's postings: its first hash, where it lies, and its XXH3-64.</summary>
    internal readonly record struct Page(ulong FirstHash, long Offset, int Length, ulong Checksum);

    /// <summary>A posting: a hash, the rank of the object that holds a value of it, and the block there.</summary>
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

    /// <summary>An identifier's postings in the index: the ranks it covers and its pages.</summary>
    internal sealed class Identifier(string name, ulong[] ranks, Page[] pages)
    {
        /// <summary>The identifier, as the dataset declares it.</summary>
        internal string Name => name;

        /// <summary>The ranks it covers, a bit a rank.</summary>
        internal ReadOnlySpan<ulong> Ranks => ranks;

        /// <summary>Its pages, by first hash.</summary>
        internal IReadOnlyList<Page> Pages => pages;

        /// <summary>Whether its postings cover the object of rank <paramref name="rank"/>.</summary>
        internal bool Covers(int rank) => (uint)(rank >> 6) < (uint)ranks.Length && (ranks[rank >> 6] & (1UL << (rank & 63))) != 0;

        /// <summary>The page that may hold <paramref name="hash"/>: the last whose first hash is not past it; -1 for none.</summary>
        internal int PageOf(ulong hash)
        {
            int lo = 0;
            int hi = pages.Length;
            while (lo < hi)
            {
                int middle = lo + ((hi - lo) >> 1);
                if (pages[middle].FirstHash <= hash)
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

            Identifier[] identifiers = new Identifier[checked((int)reader.ReadVarint())];
            for (int i = 0; i < identifiers.Length; i++)
            {
                string name = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
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

                identifiers[i] = new Identifier(name, ranks, pages);
            }

            return reader.End ? new PartitionIndex(objects, identifiers) : null;
        }
        catch (Exception e) when (e is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds to <paramref name="into"/> the rank and block of every posting of <paramref name="hash"/> in a page of first
    /// hash <paramref name="first"/>.
    /// </summary>
    /// <exception cref="VortexFormatException">The page does not decode.</exception>
    internal static void Find(ReadOnlySpan<byte> page, ulong first, ulong hash, List<(int Rank, uint Block)> into)
    {
        ProtoReader reader = new ProtoReader(page);
        ulong at = first;
        while (!reader.End)
        {
            at += reader.ReadVarint();
            int count = checked((int)reader.ReadVarint());
            if (at > hash)
            {
                return;
            }

            int rank = 0;
            uint block = 0;
            for (int i = 0; i < count; i++)
            {
                (rank, block) = Next(ref reader, rank, block, i == 0);
                if (at == hash)
                {
                    into.Add((rank, block));
                }
            }

            if (at == hash)
            {
                return;
            }
        }
    }

    /// <summary>Adds every posting of a page of first hash <paramref name="first"/> to <paramref name="into"/>, in order.</summary>
    /// <exception cref="VortexFormatException">The page does not decode.</exception>
    internal static void Decode(ReadOnlySpan<byte> page, ulong first, List<Posting> into)
    {
        ProtoReader reader = new ProtoReader(page);
        ulong hash = first;
        while (!reader.End)
        {
            hash += reader.ReadVarint();
            int count = checked((int)reader.ReadVarint());
            int rank = 0;
            uint block = 0;
            for (int i = 0; i < count; i++)
            {
                (rank, block) = Next(ref reader, rank, block, i == 0);
                into.Add(new Posting(hash, rank, block));
            }
        }
    }

    /// <summary>The posting after the one of <paramref name="rank"/> and <paramref name="block"/>, or a hash's first.</summary>
    private static (int Rank, uint Block) Next(ref ProtoReader reader, int rank, uint block, bool first)
    {
        int step = checked((int)reader.ReadVarint());
        uint value = checked((uint)reader.ReadVarint());
        return first ? (step, value) : step == 0 ? (rank, checked(block + value)) : (checked(rank + step), value);
    }

    /// <summary>
    /// Writes an index: each identifier's postings in order, cut in pages that go to <see cref="Output"/> as they fill,
    /// then the directory and the tail.
    /// </summary>
    internal sealed class Writer(ObjectSegmentSink output) : IDisposable
    {
        private readonly List<(string Name, ulong[] Ranks, long Offset, List<Page> Pages)> _identifiers = [];
        private ProtoWriter _page = new ProtoWriter(PageBytes + 1024);
        private readonly List<(int Rank, uint Block)> _group = [];
        private ulong _pageFirst;
        private ulong _previous;
        private ulong _hash;
        private bool _grouping;
        private bool _paging;

        /// <summary>The sink the index's bytes go to.</summary>
        internal ObjectSegmentSink Output => output;

        /// <summary>Whether a page is full, to go out before the next posting.</summary>
        internal bool PageFull => _page.WrittenSpan.Length >= PageBytes;

        /// <summary>Starts the postings of <paramref name="name"/>, which cover the ranks of <paramref name="ranks"/>.</summary>
        internal void Begin(string name, ulong[] ranks) => _identifiers.Add((name, ranks, output.Position, []));

        /// <summary>Adds a posting, past every one added to the identifier; one that repeats the last is left out.</summary>
        internal void Add(ulong hash, int rank, uint block)
        {
            if (_grouping && hash == _hash)
            {
                if (_group[^1] != (rank, block))
                {
                    _group.Add((rank, block));
                }

                return;
            }

            CloseGroup();
            _grouping = true;
            _hash = hash;
            _group.Add((rank, block));
        }

        /// <summary>Sends the page out once a hash's postings end it.</summary>
        internal ValueTask FlushIfFullAsync(CancellationToken cancellationToken) =>
            PageFull ? FlushAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Ends the identifier's postings: its last hash, its last page.</summary>
        internal async ValueTask EndAsync(CancellationToken cancellationToken)
        {
            CloseGroup();
            await FlushAsync(cancellationToken).ConfigureAwait(false);
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

                directory.WriteVarint((ulong)_identifiers.Count);
                foreach ((string name, ulong[] ranks, long offset, List<Page> pages) in _identifiers)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(name);
                    directory.WriteVarint((ulong)utf8.Length);
                    directory.WriteRawBytes(utf8);
                    directory.WriteVarint((ulong)ranks.Length);
                    foreach (ulong word in ranks)
                    {
                        Fixed(ref directory, fixed64, word);
                    }

                    directory.WriteVarint((ulong)pages.Count);
                    directory.WriteVarint((ulong)offset);
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

        public void Dispose() => _page.Dispose();

        /// <summary>Codes the open hash's postings into the page, which it starts when none is open.</summary>
        private void CloseGroup()
        {
            if (!_grouping)
            {
                return;
            }

            if (!_paging)
            {
                _paging = true;
                _pageFirst = _hash;
                _previous = _hash;
            }

            _page.WriteVarint(_hash - _previous);
            _page.WriteVarint((ulong)_group.Count);
            (int rank, uint block) = (0, 0u);
            for (int i = 0; i < _group.Count; i++)
            {
                (int nextRank, uint nextBlock) = _group[i];
                if (i == 0)
                {
                    _page.WriteVarint((ulong)nextRank);
                    _page.WriteVarint(nextBlock);
                }
                else if (nextRank == rank)
                {
                    _page.WriteVarint(0);
                    _page.WriteVarint(nextBlock - block);
                }
                else
                {
                    _page.WriteVarint((ulong)(nextRank - rank));
                    _page.WriteVarint(nextBlock);
                }

                (rank, block) = (nextRank, nextBlock);
            }

            _previous = _hash;
            _group.Clear();
            _grouping = false;
        }

        /// <summary>Sends the page out, past the identifier's pages before it.</summary>
        private async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            if (!_paging)
            {
                return;
            }

            byte[] page = _page.WrittenSpan.ToArray();
            _identifiers[^1].Pages.Add(new Page(_pageFirst, output.Position, page.Length, XxHash3.HashToUInt64(page)));
            _page.Clear();
            _paging = false;
            await output.WriteAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }
}
