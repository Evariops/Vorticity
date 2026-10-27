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
/// The postings of a consolidation's identifiers, gathered from the writers of its objects for the index of the partition
/// (<see cref="PartitionIndex"/>, D13): each object's hashed postings hand their hashes here through a tap a column, sorted
/// runs of them wait in a scratch, in memory up to a budget and on disk past it, and once the objects are sealed the runs
/// are merged with the postings of the index before, for the objects it covers that the job left, into the new index.
/// </summary>
/// <remarks>
/// A tap keeps its pairs of hash and block until a chunk of its object closes past <see cref="RunPairs"/> of them, then
/// sorts them and lays them in the scratch as a run: what a tap holds stays near a run, whatever the object's size, and
/// the merge reads each run a block of pairs at a time.
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
    private readonly RunScratch _scratch;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly List<Run> _runs = [];
    private readonly ConcurrentDictionary<UInt128, Tap[]> _taps = new();
    private readonly ConcurrentDictionary<UInt128, int> _blockRows = new();

    /// <summary>
    /// The postings of <paramref name="identifiers"/>, a place of each the blocks <paramref name="unitBlocks"/> gives in the
    /// same order (spec C, 5), their runs sealed in the scratch when <paramref name="seal"/> asks.
    /// </summary>
    internal PartitionPostings(IReadOnlyList<string> identifiers, IReadOnlyList<int> unitBlocks, bool seal)
    {
        _identifiers = [.. identifiers];
        _unitBlocks = [.. unitBlocks];
        _scratch = new RunScratch(ScratchMemoryBytes, null, seal);
    }

    /// <summary>The identifiers the postings are of.</summary>
    internal IReadOnlyList<string> Identifiers => _identifiers;

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
    }

    /// <summary>
    /// Writes the index of <paramref name="partition"/> for the consolidation that wrote <paramref name="outputs"/>: the
    /// objects the index <paramref name="previous"/> covers that <paramref name="consumed"/> leaves, by their rank there,
    /// then the outputs, in order; each identifier's postings merged from the outputs' runs and the previous index's pages,
    /// those of the objects it no longer covers left out. The entry's version is the commit's to stamp.
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
                    PartitionIndex.Identifier? held = old?.Of(_identifiers[i]) is { } named && named.UnitBlocks == _unitBlocks[i] ? named : null;
                    ulong[] covered = new ulong[(objects.Count + 63) / 64];
                    for (int rank = 0; rank < renamed.Length; rank++)
                    {
                        if (renamed[rank] >= 0 && held is not null && held.Covers(rank))
                        {
                            covered[renamed[rank] >> 6] |= 1UL << (renamed[rank] & 63);
                        }
                    }

                    for (int rank = kept; rank < objects.Count; rank++)
                    {
                        covered[rank >> 6] |= 1UL << (rank & 63);
                    }

                    writer.Begin(_identifiers[i], _unitBlocks[i], covered);
                    await MergeAsync(writer, i, ranks, held, before, renamed, cancellationToken).ConfigureAwait(false);
                    await writer.EndAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>The index an object holds: its tail, then its directory.</summary>
    /// <exception cref="VortexFormatException">The object holds no index this version reads.</exception>
    internal static async ValueTask<PartitionIndex> ReadAsync(ISegmentReader reader, long length, CancellationToken cancellationToken)
    {
        using Buffers.SegmentOwner tail = await reader.ReadAsync(
            new Serialization.Schemas.SegmentSpec((ulong)(length - PartitionIndex.TailBytes), PartitionIndex.TailBytes, 0, 0, 0), cancellationToken).ConfigureAwait(false);
        if (!PartitionIndex.TryTail(tail.Buffer.Span, out long offset, out int bytes, out ulong checksum) || offset + bytes > length)
        {
            throw new VortexFormatException("An index of a partition ends with no tail this version reads.");
        }

        using Buffers.SegmentOwner directory = await reader.ReadAsync(
            new Serialization.Schemas.SegmentSpec((ulong)offset, (uint)bytes, 0, 0, 0), cancellationToken).ConfigureAwait(false);
        return PartitionIndex.Read(directory.Buffer.Span, checksum)
            ?? throw new VortexFormatException("The directory of an index of a partition does not decode.");
    }

    /// <summary>
    /// Merges identifier <paramref name="identifier"/>'s postings into <paramref name="writer"/>: those of the outputs'
    /// runs, an output's at its rank, and those of the previous index's pages, each at its object's new rank, the ones of
    /// objects it no longer covers left out.
    /// </summary>
    private async ValueTask MergeAsync(
        PartitionIndex.Writer writer,
        int identifier,
        Dictionary<UInt128, int> ranks,
        PartitionIndex.Identifier? held,
        ISegmentReader? before,
        int[] renamed,
        CancellationToken cancellationToken)
    {
        List<Cursor> cursors = [];
        foreach (Run run in _runs)
        {
            if (run.Identifier == identifier && ranks.TryGetValue(run.Uid, out int rank))
            {
                cursors.Add(new RunCursor(_scratch, run, rank));
            }
        }

        if (held is not null && before is not null && held.Pages.Count > 0)
        {
            cursors.Add(new PageCursor(before, held, renamed));
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

    /// <summary>Lays a run of sorted pairs in the scratch.</summary>
    private async ValueTask AppendAsync(UInt128 uid, int identifier, ReadOnlyMemory<byte> pairs, int count, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long offset = await _scratch.AppendAsync(pairs, cancellationToken).ConfigureAwait(false);
            _runs.Add(new Run(uid, identifier, offset, count));
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

    /// <summary>A sorted run of an object's pairs for an identifier, where it lies in the scratch.</summary>
    private readonly record struct Run(UInt128 Uid, int Identifier, long Offset, int Pairs);

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

    /// <summary>An output's run, its pairs at the output's rank, read a block of pairs at a time.</summary>
    private sealed class RunCursor(RunScratch scratch, Run run, int rank) : Cursor
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
            Current = new PartitionIndex.Posting(pair.Hash, rank, pair.Block);
        }
    }

    /// <summary>
    /// The pages of the previous index's postings of an identifier, read a few mebibytes at a time, each posting at its
    /// object's new rank, those of an object no longer covered left out.
    /// </summary>
    private sealed class PageCursor(ISegmentReader reader, PartitionIndex.Identifier identifier, int[] renamed) : Cursor
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
                        Current = posting with { Rank = rank };
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

                PartitionIndex.Decode(span, held.FirstHash, _postings);
            }

            _page = end;
        }
    }
}
