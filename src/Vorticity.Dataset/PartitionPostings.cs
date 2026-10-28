using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The postings of a consolidation's identifiers, and where the values of its objects' located columns lie, gathered from
/// the writers of its objects for the index of the partition (<see cref="PartitionIndex"/>, D13, spec D, 7): each object's
/// hashed postings hand their hashes here through a tap a column, and each index of where a column's values lie its values'
/// hashes and granules once complete; sorted runs of them wait in a scratch, in memory up to a budget and on disk past it,
/// and once the objects are sealed the runs are merged with the entries of the index before, for the objects it covers
/// that the job left, into the new index.
/// </summary>
/// <remarks>
/// A tap keeps its pairs of hash and block until a chunk of its object closes past <see cref="RunPairs"/> of them, then
/// sorts them and lays them in the scratch as a run: what a tap holds stays near a run, whatever the object's size, and
/// the merge reads each run a block of pairs at a time. An index of where values lie is a run of its own, a pair a value
/// and granule, one a value that lies everywhere.
/// </remarks>
internal sealed class PartitionPostings : IDisposable
{
    /// <summary>The pairs a tap gathers before it lays them in the scratch, once a chunk closes: 12 MiB of them.</summary>
    internal const int RunPairs = 1 << 20;

    /// <summary>The memory the runs take before the scratch moves to a file.</summary>
    private const long ScratchMemoryBytes = 64L << 20;

    /// <summary>The pairs the merge reads of a run at a time.</summary>
    private const int ReadPairs = 4096;

    /// <summary>The bytes of the index's pages the merge reads at a time.</summary>
    private const int ReadPageBytes = 4 << 20;

    private readonly string[] _identifiers;
    private readonly int[] _unitBlocks;
    private readonly string[] _columns;
    private readonly RunScratch _scratch;

    /// <summary>The bytes the postings' runs went to disk with, past what their scratch's memory held.</summary>
    internal long ScratchBytes => _scratch.FileBytes;

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly List<Run> _runs = [];
    private readonly ConcurrentDictionary<UInt128, Tap[]> _taps = new();
    private readonly ConcurrentDictionary<UInt128, int> _blockRows = new();

    // The columns each object handed where its values lie of, by its uid and the column's place.
    private readonly ConcurrentDictionary<(UInt128 Uid, int Field), bool> _located = new();

    /// <summary>
    /// The postings of <paramref name="identifiers"/>, a place of each the blocks <paramref name="unitBlocks"/> gives in the
    /// same order (spec C, 5), and where the values of the columns <paramref name="columns"/> names by place lie, their runs
    /// sealed in the scratch when <paramref name="seal"/> asks.
    /// </summary>
    internal PartitionPostings(IReadOnlyList<string> identifiers, IReadOnlyList<int> unitBlocks, IReadOnlyList<string> columns, bool seal)
    {
        _identifiers = [.. identifiers];
        _unitBlocks = [.. unitBlocks];
        _columns = [.. columns];
        _scratch = new RunScratch(ScratchMemoryBytes, null, seal);
    }

    /// <summary>The taps of the object of uid <paramref name="uid"/>, by identifier: what its writer hands its hashes to.</summary>
    internal Func<string, IHashTap?> TapsOf(UInt128 uid)
    {
        Tap[] taps = new Tap[_identifiers.Length];
        for (int i = 0; i < taps.Length; i++)
        {
            taps[i] = new Tap(this, uid, i);
        }

        _taps[uid] = taps;
        return name => Array.IndexOf(_identifiers, name) is var at && at >= 0 ? taps[at] : null;
    }

    /// <summary>Notes the rows of the blocks of the object of uid <paramref name="uid"/>, which its postings name.</summary>
    internal void BlocksOf(UInt128 uid, int blockRows) => _blockRows[uid] = blockRows;

    /// <summary>
    /// What the writer of the object of uid <paramref name="uid"/> hands each index of where a column's values lie to, by the
    /// column's place, once complete (<see cref="VortexWriteOptions.Located"/>).
    /// </summary>
    internal Func<int, byte[], CancellationToken, ValueTask> LocatesOf(UInt128 uid) =>
        (field, bytes, cancellationToken) => LocatedAsync(uid, field, bytes, cancellationToken);

    /// <summary>
    /// Lays the values of the index <paramref name="bytes"/> of where column <paramref name="field"/>'s values lie in the
    /// object of uid <paramref name="uid"/> in the scratch as a run: a pair a value's hash and granule, sorted, one a value
    /// that lies everywhere (<see cref="PartitionIndex.Everywhere"/>).
    /// </summary>
    private async ValueTask LocatedAsync(UInt128 uid, int field, byte[] bytes, CancellationToken cancellationToken)
    {
        if ((uint)field >= (uint)_columns.Length || Indexes.ColumnLocate.From(bytes) is not { } locate)
        {
            return;
        }

        int pairs = 0;
        for (int place = 0; place < locate.Values.Count; place++)
        {
            pairs += locate.IsEverywhere(place) ? 1 : locate.GranulesOf(place);
        }

        int pairBytes = Unsafe.SizeOf<Pair>();
        byte[] run = ArrayPool<byte>.Shared.Rent(Math.Max(pairs, 1) * pairBytes);
        try
        {
            Span<Pair> laid = MemoryMarshal.Cast<byte, Pair>(run.AsSpan(0, pairs * pairBytes));
            ulong[] granules = new ulong[(locate.Granules + 63) / 64];
            int at = 0;
            for (int place = 0; place < locate.Values.Count; place++)
            {
                ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(locate.Values[place]);
                if (locate.IsEverywhere(place))
                {
                    laid[at++] = new Pair(hash, PartitionIndex.Everywhere);
                    continue;
                }

                Array.Clear(granules);
                locate.Or(place, granules);
                for (int w = 0; w < granules.Length && at < laid.Length; w++)
                {
                    for (ulong bits = granules[w]; bits != 0 && at < laid.Length; bits &= bits - 1)
                    {
                        laid[at++] = new Pair(hash, (uint)((w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits)));
                    }
                }
            }

            laid[..at].Sort();
            _located[(uid, field)] = true;
            await AppendAsync(uid, -1 - field, run.AsMemory(0, at * pairBytes), at, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(run);
        }
    }

    /// <summary>Lays what the taps of the sealed object of uid <paramref name="uid"/> still hold in the scratch.</summary>
    internal async ValueTask CloseAsync(UInt128 uid, CancellationToken cancellationToken)
    {
        if (!_taps.TryRemove(uid, out Tap[]? taps))
        {
            return;
        }

        foreach (Tap tap in taps)
        {
            await tap.SpillAsync(cancellationToken).ConfigureAwait(false);
            tap.Release();
        }
    }

    /// <summary>Gives up what the taps of the object of uid <paramref name="uid"/> hold: the object was given up, and its runs are left out.</summary>
    internal void Drop(UInt128 uid)
    {
        if (_taps.TryRemove(uid, out Tap[]? taps))
        {
            foreach (Tap tap in taps)
            {
                tap.Release();
            }
        }

        for (int field = 0; field < _columns.Length; field++)
        {
            _located.TryRemove((uid, field), out _);
        }
    }

    /// <summary>
    /// Writes the index of <paramref name="partition"/> for the consolidation that wrote <paramref name="outputs"/>: the
    /// objects the index <paramref name="previous"/> covers that <paramref name="consumed"/> leaves, by their rank there,
    /// then the outputs, in order; each identifier's postings, then where each located column's values lie, merged from the
    /// outputs' runs and the previous index's pages, those of the objects it no longer covers left out. The entry's version
    /// is the commit's to stamp.
    /// </summary>
    internal async ValueTask<PartitionIndexEntry> WriteAsync(
        VortexDataset dataset,
        ReadOnlyMemory<byte> partition,
        IReadOnlyList<WrittenObject> outputs,
        PartitionIndexEntry? previous,
        IReadOnlySet<UInt128> consumed,
        CancellationToken cancellationToken)
    {
        // The objects of the previous index it covers still, each to its new rank; then the outputs.
        ISegmentReader? before = null;
        PartitionIndex? old = null;
        if (previous is not null)
        {
            before = await SealedObjectStore.OpenDataAsync(dataset.Store, previous.ObjectKey, previous.Uid, previous.Bytes, cancellationToken).ConfigureAwait(false);
            old = await ReadAsync(before, previous.Bytes, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            List<PartitionIndex.Covered> objects = [];
            int[] renamed = new int[old?.Objects.Count ?? 0];
            for (int rank = 0; rank < renamed.Length; rank++)
            {
                PartitionIndex.Covered held = old!.Objects[rank];
                renamed[rank] = previous!.Covers(held.Uid) && !consumed.Contains(held.Uid) ? objects.Count : -1;
                if (renamed[rank] >= 0)
                {
                    objects.Add(held);
                }
            }

            int kept = objects.Count;
            Dictionary<UInt128, int> ranks = [];
            foreach (WrittenObject output in outputs)
            {
                ranks[output.Entry.Uid] = objects.Count;
                objects.Add(new PartitionIndex.Covered(output.Entry.Uid, output.Entry.Rows, _blockRows.GetValueOrDefault(output.Entry.Uid)));
            }

            Guid identity = Guid.NewGuid();
            UInt128 uid = VortexDataset.Uid(identity);
            string key = CommitKey.ForData(identity.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
            ObjectSegmentSink sink = new ObjectSegmentSink(dataset.Store, key, pool: dataset.Session.Options.MemoryPool);
            using PartitionIndex.Writer writer = new PartitionIndex.Writer(sink);
            try
            {
                for (int i = 0; i < _identifiers.Length; i++)
                {
                    // The previous index's postings of the identifier, unless their places span other blocks than its
                    // objects' now do: those objects are then no longer covered for it, and their rows read as any.
                    PartitionIndex.Entry? held = old?.Of(_identifiers[i], PartitionIndex.EntryKind.Postings) is { } named && named.UnitBlocks == _unitBlocks[i]
                        ? named
                        : null;
                    ulong[] covered = Covered(held, renamed, objects.Count);
                    for (int rank = kept; rank < objects.Count; rank++)
                    {
                        covered[rank >> 6] |= 1UL << (rank & 63);
                    }

                    int hashBits = HashBits(i, ranks, held);
                    writer.Begin(_identifiers[i], PartitionIndex.EntryKind.Postings, _unitBlocks[i], covered, hashBits, objects);
                    await MergeAsync(writer, i, ranks, held, before, renamed, hashBits, cancellationToken).ConfigureAwait(false);
                    await writer.EndAsync(cancellationToken).ConfigureAwait(false);
                }

                // Where each located column's values lie: the outputs that handed theirs, and the objects kept the index
                // before covered for it.
                for (int field = 0; field < _columns.Length; field++)
                {
                    PartitionIndex.Entry? held = old?.Of(_columns[field], PartitionIndex.EntryKind.Locate);
                    ulong[] covered = Covered(held, renamed, objects.Count);
                    bool any = Array.Exists(covered, static word => word != 0);
                    for (int rank = kept; rank < objects.Count; rank++)
                    {
                        if (_located.ContainsKey((objects[rank].Uid, field)))
                        {
                            covered[rank >> 6] |= 1UL << (rank & 63);
                            any = true;
                        }
                    }

                    if (any)
                    {
                        int hashBits = HashBits(-1 - field, ranks, held);
                        writer.Begin(_columns[field], PartitionIndex.EntryKind.Locate, 1, covered, hashBits, objects);
                        await MergeAsync(writer, -1 - field, ranks, held, before, renamed, hashBits, cancellationToken).ConfigureAwait(false);
                        await writer.EndAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await writer.FinishAsync(objects, cancellationToken).ConfigureAwait(false);
                if (await sink.CommitAsync(cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
                {
                    throw new ObjectStoreException($"'{key}' was taken, which a fresh uid cannot be.");
                }
            }
            catch
            {
                sink.Discard();
                throw;
            }

            List<UInt128> coverage = new List<UInt128>(objects.Count);
            foreach (PartitionIndex.Covered held in objects)
            {
                coverage.Add(held.Uid);
            }

            return new PartitionIndexEntry(partition, key, uid, sink.StoredLength, 0, coverage);
        }
        finally
        {
            if (before is not null)
            {
                await before.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The bits entry <paramref name="entry"/> keeps of each hash: those its postings need, the outputs' pairs and the ones the
    /// entry <paramref name="held"/> of the index before holds, but no more than that entry keeps, whose cut hashes are not
    /// whole any more.
    /// </summary>
    private int HashBits(int entry, Dictionary<UInt128, int> ranks, PartitionIndex.Entry? held)
    {
        long pairs = held?.Postings ?? 0;
        foreach (Run run in _runs)
        {
            if (run.Entry == entry && ranks.ContainsKey(run.Uid))
            {
                pairs += run.Pairs;
            }
        }

        int bits = PartitionIndex.HashBitsFor(pairs);
        return held is null ? bits : Math.Min(bits, held.HashBits);
    }

    /// <summary>The ranks of the new index the entry <paramref name="held"/> of the one before covers, renamed by <paramref name="renamed"/>, a bit a rank of <paramref name="objects"/>.</summary>
    private static ulong[] Covered(PartitionIndex.Entry? held, int[] renamed, int objects)
    {
        ulong[] covered = new ulong[(objects + 63) / 64];
        for (int rank = 0; rank < renamed.Length; rank++)
        {
            if (renamed[rank] >= 0 && held is not null && held.Covers(rank))
            {
                covered[renamed[rank] >> 6] |= 1UL << (renamed[rank] & 63);
            }
        }

        return covered;
    }

    /// <summary>The index an object holds (<see cref="OpenAsync"/>), its last bytes let go.</summary>
    /// <exception cref="VortexFormatException">The object holds no index this version reads.</exception>
    internal static async ValueTask<PartitionIndex> ReadAsync(ISegmentReader reader, long length, CancellationToken cancellationToken)
    {
        using OpenedIndex opened = await OpenAsync(reader, length, cancellationToken).ConfigureAwait(false);
        return opened.Index;
    }

    /// <summary>
    /// The index an object of <paramref name="length"/> bytes holds, from its last bytes, an open's tail of them at most
    /// (<see cref="ObjectStoreSource.TailBytes"/>), read in one request: its tail, then its directory, a request more when it
    /// starts before them; the pages that lie in them are served from them (<see cref="OpenedIndex.LoadAsync"/>). An index of
    /// a few pages is read whole in that request.
    /// </summary>
    /// <exception cref="VortexFormatException">The object holds no index this version reads.</exception>
    internal static async ValueTask<OpenedIndex> OpenAsync(ISegmentReader reader, long length, CancellationToken cancellationToken)
    {
        if (length < PartitionIndex.TailBytes)
        {
            throw new VortexFormatException("An index of a partition ends with no tail this version reads.");
        }

        int window = (int)Math.Min(length, ObjectStoreSource.TailBytes);
        long start = length - window;
        Buffers.SegmentOwner last = await reader.ReadAsync(
            new Serialization.Schemas.SegmentSpec((ulong)start, (uint)window, 0, 0, 0), cancellationToken).ConfigureAwait(false);
        try
        {
            if (!PartitionIndex.TryTail(last.Buffer.Span[^PartitionIndex.TailBytes..], out long offset, out int bytes, out ulong checksum) || offset < 0 || offset + bytes > length)
            {
                throw new VortexFormatException("An index of a partition ends with no tail this version reads.");
            }

            PartitionIndex? index;
            if (offset >= start)
            {
                index = PartitionIndex.Read(last.Buffer.Span.Slice((int)(offset - start), bytes), checksum);
            }
            else
            {
                using Buffers.SegmentOwner directory = await reader.ReadAsync(
                    new Serialization.Schemas.SegmentSpec((ulong)offset, (uint)bytes, 0, 0, 0), cancellationToken).ConfigureAwait(false);
                index = PartitionIndex.Read(directory.Buffer.Span, checksum);
            }

            return new OpenedIndex(reader, index ?? throw new VortexFormatException("The directory of an index of a partition does not decode."), last, start);
        }
        catch
        {
            last.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An index of a partition as a lookup opened it (<see cref="OpenAsync"/>): its directory, and the object's last bytes,
    /// which serve the pages lying in them; a page before them is read in one request.
    /// </summary>
    internal sealed class OpenedIndex(ISegmentReader reader, PartitionIndex index, Buffers.SegmentOwner last, long start) : IDisposable
    {
        // The page loaded last, when read before the last bytes; where it starts, -1 for none, and its length.
        private Buffers.SegmentOwner? _read;
        private long _offset = -1;
        private int _length;

        /// <summary>The index's directory.</summary>
        internal PartitionIndex Index => index;

        /// <summary>The bytes of the page loaded last (<see cref="LoadAsync"/>).</summary>
        internal ReadOnlySpan<byte> Page => _read is { } read ? read.Buffer.Span : last.Buffer.Span.Slice((int)(_offset - start), _length);

        /// <summary>
        /// Loads <paramref name="page"/>, unless it is the one loaded: from the object's last bytes when it lies in them,
        /// in one request otherwise. Whether its bytes hold its checksum.
        /// </summary>
        internal async ValueTask<bool> LoadAsync(PartitionIndex.Page page, CancellationToken cancellationToken)
        {
            if (page.Offset != _offset)
            {
                _read?.Dispose();
                (_read, _offset) = (null, -1);
                if (page.Offset < start || page.Offset + page.Length > start + last.Buffer.Span.Length)
                {
                    _read = await reader.ReadAsync(
                        new Serialization.Schemas.SegmentSpec((ulong)page.Offset, (uint)page.Length, 0, 0, 0), cancellationToken).ConfigureAwait(false);
                }

                (_offset, _length) = (page.Offset, page.Length);
            }

            return System.IO.Hashing.XxHash3.HashToUInt64(Page) == page.Checksum;
        }

        public void Dispose()
        {
            _read?.Dispose();
            last.Dispose();
        }
    }

    /// <summary>
    /// Merges entry <paramref name="entry"/>'s postings into <paramref name="writer"/>, an identifier's place or a located
    /// column's, <c>-1 - field</c>: those of the outputs' runs, an output's at its rank, and those of the previous index's
    /// pages, each at its object's new rank, the ones of objects it no longer covers left out; every hash cut to
    /// <paramref name="hashBits"/>, which keeps their order.
    /// </summary>
    private async ValueTask MergeAsync(
        PartitionIndex.Writer writer,
        int entry,
        Dictionary<UInt128, int> ranks,
        PartitionIndex.Entry? held,
        ISegmentReader? before,
        int[] renamed,
        int hashBits,
        CancellationToken cancellationToken)
    {
        List<Cursor> cursors = [];
        foreach (Run run in _runs)
        {
            if (run.Entry == entry && ranks.TryGetValue(run.Uid, out int rank))
            {
                cursors.Add(new RunCursor(_scratch, run, rank, hashBits));
            }
        }

        if (held is not null && before is not null && held.Pages.Count > 0)
        {
            cursors.Add(new PageCursor(before, held, renamed, hashBits));
        }

        PriorityQueue<Cursor, PartitionIndex.Posting> heap = new PriorityQueue<Cursor, PartitionIndex.Posting>(cursors.Count);
        foreach (Cursor cursor in cursors)
        {
            if (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                heap.Enqueue(cursor, cursor.Current);
            }
        }

        while (heap.TryDequeue(out Cursor? cursor, out PartitionIndex.Posting posting))
        {
            writer.Add(posting.Hash, posting.Rank, posting.Block);
            await writer.FlushIfFullAsync(cancellationToken).ConfigureAwait(false);
            if (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                heap.Enqueue(cursor, cursor.Current);
            }
        }
    }

    /// <summary>Lays a run of sorted pairs of entry <paramref name="entry"/> in the scratch.</summary>
    private async ValueTask AppendAsync(UInt128 uid, int entry, ReadOnlyMemory<byte> pairs, int count, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long offset = await _scratch.AppendAsync(pairs, cancellationToken).ConfigureAwait(false);
            _runs.Add(new Run(uid, entry, offset, count));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (UInt128 uid in _taps.Keys)
        {
            Drop(uid);
        }

        _scratch.Dispose();
        _gate.Dispose();
    }

    /// <summary>A sorted run of an object's pairs for an entry, an identifier's or a located column's, where it lies in the scratch.</summary>
    private readonly record struct Run(UInt128 Uid, int Entry, long Offset, int Pairs);

    /// <summary>A value's hash and the block that holds it, twelve bytes as a run lays them.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct Pair(ulong hash, uint block) : IComparable<Pair>
    {
        internal readonly ulong Hash = hash;
        internal readonly uint Block = block;

        public int CompareTo(Pair other)
        {
            int order = Hash.CompareTo(other.Hash);
            return order != 0 ? order : Block.CompareTo(other.Block);
        }
    }

    /// <summary>
    /// What an object's hashed postings of one identifier hand their hashes to: pairs of hash and block, a pair repeating
    /// the one before left out, laid in the scratch as a sorted run once a chunk closes past <see cref="RunPairs"/>.
    /// </summary>
    private sealed class Tap(PartitionPostings owner, UInt128 uid, int identifier) : IHashTap
    {
        private static readonly int PairBytes = Unsafe.SizeOf<Pair>();
        private byte[]? _bytes;
        private int _count;

        public void Note(ulong hash, uint block)
        {
            if (_count > 0)
            {
                ref Pair last = ref Unsafe.As<byte, Pair>(ref _bytes![(_count - 1) * PairBytes]);
                if (last.Hash == hash && last.Block == block)
                {
                    return;
                }
            }

            if (_bytes is null || (_count + 1) * PairBytes > _bytes.Length)
            {
                Grow();
            }

            Unsafe.As<byte, Pair>(ref _bytes![_count * PairBytes]) = new Pair(hash, block);
            _count++;
        }

        public ValueTask ChunkClosedAsync(CancellationToken cancellationToken) =>
            _count >= RunPairs ? SpillAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Sorts the pairs, drops the repeats, and lays them in the scratch as a run.</summary>
        internal async ValueTask SpillAsync(CancellationToken cancellationToken)
        {
            if (_count == 0)
            {
                return;
            }

            int count = Sorted();
            _count = 0;
            await owner.AppendAsync(uid, identifier, _bytes.AsMemory(0, count * PairBytes), count, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Gives the pairs' buffer back.</summary>
        internal void Release()
        {
            if (_bytes is { } bytes)
            {
                _bytes = null;
                _count = 0;
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        private int Sorted()
        {
            Span<Pair> pairs = MemoryMarshal.Cast<byte, Pair>(_bytes.AsSpan(0, _count * PairBytes));
            pairs.Sort();
            int kept = 1;
            for (int i = 1; i < pairs.Length; i++)
            {
                if (pairs[i].Hash != pairs[kept - 1].Hash || pairs[i].Block != pairs[kept - 1].Block)
                {
                    pairs[kept++] = pairs[i];
                }
            }

            return kept;
        }

        private void Grow()
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(64 << 10, (_bytes?.Length ?? 0) * 2));
            if (_bytes is { } held)
            {
                held.AsSpan(0, _count * PairBytes).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(held);
            }

            _bytes = grown;
        }
    }

    /// <summary>A source of postings in order, for the merge.</summary>
    private abstract class Cursor
    {
        /// <summary>The posting it is on.</summary>
        internal PartitionIndex.Posting Current { get; private protected set; }

        /// <summary>Moves to the next posting; false past the last.</summary>
        internal abstract ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken);
    }

    /// <summary>An output's run, its pairs at the output's rank, each hash cut to <paramref name="hashBits"/>, read a block of pairs at a time.</summary>
    private sealed class RunCursor(RunScratch scratch, Run run, int rank, int hashBits) : Cursor
    {
        private readonly byte[] _buffer = new byte[ReadPairs * Unsafe.SizeOf<Pair>()];
        private int _read;
        private int _held;
        private int _at;

        internal override ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
        {
            if (_at < _held)
            {
                Take();
                return new ValueTask<bool>(true);
            }

            return _read < run.Pairs ? FillAsync(cancellationToken) : new ValueTask<bool>(false);
        }

        private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
        {
            int pairs = Math.Min(ReadPairs, run.Pairs - _read);
            int bytes = pairs * Unsafe.SizeOf<Pair>();
            await scratch.ReadAsync(run.Offset + ((long)_read * Unsafe.SizeOf<Pair>()), _buffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
            _read += pairs;
            _held = pairs;
            _at = 0;
            Take();
            return true;
        }

        private void Take()
        {
            Pair pair = Unsafe.As<byte, Pair>(ref _buffer[_at * Unsafe.SizeOf<Pair>()]);
            _at++;
            Current = new PartitionIndex.Posting(hashBits >= 64 ? pair.Hash : pair.Hash >> (64 - hashBits), rank, pair.Block);
        }
    }

    /// <summary>
    /// The pages of the previous index's postings of an entry, read a few mebibytes at a time, each posting at its object's
    /// new rank and its hash cut to <paramref name="hashBits"/>, fewer than the entry keeps or as many, those of an object no
    /// longer covered left out.
    /// </summary>
    private sealed class PageCursor(ISegmentReader reader, PartitionIndex.Entry identifier, int[] renamed, int hashBits) : Cursor
    {
        private readonly List<PartitionIndex.Posting> _postings = [];
        private int _page;
        private int _at;

        internal override async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                while (_at < _postings.Count)
                {
                    PartitionIndex.Posting posting = _postings[_at++];
                    int rank = (uint)posting.Rank < (uint)renamed.Length ? renamed[posting.Rank] : -1;
                    if (rank >= 0)
                    {
                        Current = new PartitionIndex.Posting(posting.Hash >> (identifier.HashBits - hashBits), rank, posting.Block);
                        return true;
                    }
                }

                if (_page >= identifier.Pages.Count)
                {
                    return false;
                }

                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>The next pages, a few mebibytes of them, decoded, each against its checksum.</summary>
        private async ValueTask ReadAsync(CancellationToken cancellationToken)
        {
            int first = _page;
            long start = identifier.Pages[first].Offset;
            int end = first + 1;
            while (end < identifier.Pages.Count && identifier.Pages[end].Offset + identifier.Pages[end].Length - start <= ReadPageBytes)
            {
                end++;
            }

            PartitionIndex.Page last = identifier.Pages[end - 1];
            using Buffers.SegmentOwner bytes = await reader.ReadAsync(
                new Serialization.Schemas.SegmentSpec((ulong)start, checked((uint)(last.Offset + last.Length - start)), 0, 0, 0), cancellationToken).ConfigureAwait(false);
            _postings.Clear();
            _at = 0;
            for (int page = first; page < end; page++)
            {
                PartitionIndex.Page held = identifier.Pages[page];
                ReadOnlySpan<byte> span = bytes.Buffer.Span.Slice((int)(held.Offset - start), held.Length);
                if (System.IO.Hashing.XxHash3.HashToUInt64(span) != held.Checksum)
                {
                    throw new VortexFormatException($"Page {page} of the postings of '{identifier.Name}' in an index of a partition fails its checksum.");
                }

                identifier.Decode(span, held, _postings);
            }

            _page = end;
        }
    }
}
