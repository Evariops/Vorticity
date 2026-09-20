using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>One locating run, as its payloads and its segment table.</summary>
internal sealed record KeyRun(
    int FirstBlock, int BlockCount, long Entries, List<KeySegment> Segments, List<PendingPayload> Payloads);

/// <summary>
/// Builds one column's postings or sorted runs, chunk by chunk. Positions are relative to the run:
/// a block id counts from the run's first block, a row from its first row.
/// </summary>
internal sealed class KeyIndexBuilder : IndexBuilder
{
    private readonly bool _rows;
    private readonly KeyLayout _layout;
    private readonly bool _utf8;
    private readonly int _segmentEntries;
    private readonly bool _trigrams;
    private readonly bool _fold;
    private readonly ChunkKeys _spare = new ChunkKeys();
    private readonly List<int> _lastBlock = [];
    private ChunkKeys _table;
    private long _row;
    private int _block;

    /// <summary>Set <c>rows</c> for sorted runs, which hold every row, rather than postings.</summary>
    internal KeyIndexBuilder(bool rows, KeyLayout layout, bool utf8, int segmentEntries = KeyRunOptions.DefaultSegmentEntries)
    {
        _table = new ChunkKeys();
        _rows = rows;
        _layout = layout;
        _utf8 = utf8;
        _segmentEntries = segmentEntries;
    }

    /// <summary>
    /// A trigram postings builder: the keys are every byte trigram of every value, typed
    /// <c>binary</c> because a trigram may cut a UTF-8 code point in two.
    /// </summary>
    internal static KeyIndexBuilder ForTrigrams(bool fold, int segmentEntries) =>
        new KeyIndexBuilder(fold, segmentEntries);

    private KeyIndexBuilder(bool fold, int segmentEntries)
    {
        _table = new ChunkKeys();
        _rows = false;
        _layout = new KeyLayout(KeyShape.Bytes, 0, default);
        _utf8 = false;
        _segmentEntries = segmentEntries;
        _trigrams = true;
        _fold = fold;
    }

    internal string Kind => _trigrams
        ? IndexKinds.PostingsNgram3
        : _rows ? IndexKinds.SortedRuns : IndexKinds.PostingsBlocks;

    /// <summary>Whether trigrams are ASCII-lower-cased.</summary>
    internal bool CaseInsensitive => _fold;

    /// <summary>The runs the merge produced when the data ended, in block order.</summary>
    internal List<KeyRun> Runs { get; } = [];

    /// <summary>Where chunk runs wait for the merge; set by the index writer.</summary>
    internal RunScratch? Scratch { get; set; }

    /// <summary>Rows per block, 0 when the caller's batches are the blocks; set by the index writer.</summary>
    internal long BlockRows { get; set; }

    /// <summary>The row span above which the rows are written at 64 bits; set by the index writer.</summary>
    internal long WideRowsAbove { get; set; } = uint.MaxValue;

    private readonly List<RawRun> _chunkRuns = [];

    /// <summary>
    /// Runs read from the file an append continues, merged in front of the chunk runs because the
    /// entry would otherwise hold more than <see cref="MaxRuns"/>.
    /// </summary>
    internal List<RawRun>? Absorbed { get; set; }

    /// <summary>Where <see cref="Absorbed"/> lives.</summary>
    internal RunScratch? AbsorbedScratch { get; set; }

    /// <summary>The most runs an entry keeps.</summary>
    internal const int MaxRuns = 4;

    internal int SegmentEntries => _segmentEntries;

    /// <summary>Whether the dtype can be keyed, and why not.</summary>
    internal static bool Supports(DType dtype, out KeyLayout layout, out bool utf8, out string? reason)
    {
        DType storage = dtype;
        while (storage.Kind == DTypeKind.Extension)
        {
            storage = storage.StorageType;
        }

        utf8 = storage.Kind == DTypeKind.Utf8;
        if (KeyLayout.TryOf(dtype, out layout))
        {
            reason = null;
            return true;
        }

        reason = storage.Kind == DTypeKind.Decimal
            ? "a decimal has no literal a filter could look up"
            : $"a locating index does not key a {storage.Kind} column";
        return false;
    }

    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        long first = _row;
        _row += count;
        if (Abandoned is not null || count <= 0)
        {
            return;
        }

        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            return;
        }

        bool allValid = own.AllValid && wrapper.AllValid;
        int width = _layout.Width;
        if (_trigrams)
        {
            if (node.Kind != CanonicalKind.VarBinView)
            {
                Abandon($"a trigram index needs text, and this column is {node.Kind}");
                return;
            }

            Span<byte> trigram = stackalloc byte[Trigrams.Length];
            for (int row = start; row < start + count; row++)
            {
                if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                {
                    ReadOnlySpan<byte> value = LiteralReader.ViewAt(node, row);
                    for (int i = 0; i + Trigrams.Length <= value.Length; i++)
                    {
                        Trigrams.Copy(value.Slice(i, Trigrams.Length), _fold, trigram);
                        Note(trigram, first + row - start);
                    }
                }
            }

            return;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Primitive:
                ReadOnlySpan<byte> values = node.Values.Span;
                for (int row = start; row < start + count; row++)
                {
                    if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                    {
                        Note(values.Slice(row * width, width), first + row - start);
                    }
                }

                return;

            case CanonicalKind.Constant:
                // One key for every valid row, so its id is looked up once.
                int constant = -1;
                for (int row = start; row < start + count; row++)
                {
                    if (own.IsValid(row) && wrapper.IsValid(row))
                    {
                        if (constant < 0)
                        {
                            constant = _table.Intern(node.ConstantElement);
                        }

                        NoteId(constant, first + row - start);
                    }
                }

                return;

            case CanonicalKind.VarBinView:
                for (int row = start; row < start + count; row++)
                {
                    if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                    {
                        Note(LiteralReader.ViewAt(node, row), first + row - start);
                    }
                }

                return;

            default:
                Abandon($"a locating index does not key a {node.Kind} column");
                return;
        }
    }

    /// <summary>
    /// Feeds encoded keys, one per row, for a composite key: the rows <paramref name="include"/>
    /// leaves out -- a null in the tuple -- are counted and not entries.
    /// </summary>
    internal void AccumulateEncoded(IEncodedKeys keys, ReadOnlySpan<bool> include)
    {
        long first = _row;
        _row += include.Length;
        if (Abandoned is not null)
        {
            return;
        }

        for (int row = 0; row < include.Length; row++)
        {
            if (include[row])
            {
                Note(keys.Row(row), first + row);
            }
        }
    }

    /// <summary>Counts rows that feed nothing, so the next rows keep their numbers.</summary>
    internal void Skip(int count) => _row += count;

    private void Note(ReadOnlySpan<byte> key, long row) => NoteId(_table.Intern(key), row);

    private void NoteId(int id, long row)
    {
        if (_rows)
        {
            _table.Note(id, row);
            return;
        }

        while (_lastBlock.Count <= id)
        {
            _lastBlock.Add(-1);
        }

        if (_lastBlock[id] != _block)
        {
            _lastBlock[id] = _block;
            _table.Note(id, _block);
        }
    }

    internal override void CloseBlock() => _block++;

    internal override void Start(int block, long row)
    {
        _block = block;
        _row = row;
    }

    internal override void CloseChunk(int firstBlock, int blocks, long firstRow, long rows)
    {
        if (Abandoned is not null)
        {
            return;
        }

        long end = _rows ? firstRow + rows : (long)firstBlock + blocks;
        ChunkKeys chunk = _spare;
        _table.Cut(end, chunk);
        try
        {
            RunScratch scratch = Scratch ?? throw new InvalidOperationException("A locating builder needs the writer's scratch.");
            _chunkRuns.Add(_rows
                ? SortedRaw(scratch, chunk, firstBlock, blocks)
                : PostingsRaw(scratch, chunk, firstBlock, blocks));
        }
        finally
        {
            _table.Reclaim(chunk);
        }

        if (!_rows)
        {
            // The carried suffix was re-interned: its ids are new, and so are their last blocks.
            _lastBlock.Clear();
            foreach ((int key, long position) in _table.Log)
            {
                while (_lastBlock.Count <= key)
                {
                    _lastBlock.Add(-1);
                }

                _lastBlock[key] = (int)position;
            }
        }
    }

    /// <summary>
    /// Merges the chunk runs -- after the runs an append absorbed -- into the entry's run, and the
    /// last chunk's into a run of its own when an append would re-open it.
    /// </summary>
    internal override void EndOfData()
    {
        if (Abandoned is not null || (_chunkRuns.Count == 0 && Absorbed is not { Count: > 0 }))
        {
            return;
        }

        RunScratch scratch = Scratch ?? throw new InvalidOperationException("A locating builder needs the writer's scratch.");
        RawRun? tail = null;
        List<RawRun> mains = [.. _chunkRuns];
        if (BlockRows > 0 && _row % BlockRows != 0 && mains.Count > 0)
        {
            tail = mains[^1];
            mains.RemoveAt(mains.Count - 1);
        }

        if (mains.Count > 0 || Absorbed is { Count: > 0 })
        {
            Emit(MergeMains(scratch, mains));
        }

        if (tail is not null)
        {
            Emit(Final([Open(scratch, tail, 0)], tail.FirstBlock, tail.BlockCount));
            tail.Release(scratch);
        }

        _chunkRuns.Clear();
        Absorbed = null;
    }

    private void Emit(KeyRun run)
    {
        Runs.Add(run);
        foreach (PendingPayload payload in run.Payloads)
        {
            Enqueue(payload);
        }
    }

    /// <summary>
    /// The absorbed runs and the chunk runs, merged in passes of at most
    /// <see cref="RunMerger.MaxFanIn"/> runs, then once into payloads.
    /// </summary>
    private KeyRun MergeMains(RunScratch scratch, List<RawRun> mains)
    {
        List<RawRun> absorbed = Absorbed ?? [];
        int firstBlock = int.MaxValue;
        int endBlock = 0;
        foreach (RawRun run in (IEnumerable<RawRun>)[.. absorbed, .. mains])
        {
            firstBlock = Math.Min(firstBlock, run.FirstBlock);
            endBlock = Math.Max(endBlock, run.FirstBlock + run.BlockCount);
        }

        List<RawRun> level = mains;
        while (absorbed.Count + level.Count > RunMerger.MaxFanIn)
        {
            // A pass never mixes absorbed runs into a laid one: they are few and first in block
            // order, so the chunk runs alone are narrowed until they fit beside them.
            List<RawRun> next = [];
            for (int from = 0; from < level.Count; from += RunMerger.MaxFanIn)
            {
                List<RawRun> group = level.GetRange(from, Math.Min(RunMerger.MaxFanIn, level.Count - from));
                if (group.Count == 1)
                {
                    next.Add(group[0]);
                    continue;
                }

                RawRun first = group[0];
                RawRun last = group[^1];
                using RawRunWriter writer = new RawRunWriter(
                    scratch, _rows, _layout.Width, first.FirstBlock, last.FirstBlock + last.BlockCount - first.FirstBlock);
                List<RunCursor> cursors = [];
                for (int i = 0; i < group.Count; i++)
                {
                    cursors.Add(Open(scratch, group[i], i));
                }

                RunMerger.Merge(cursors, _layout, _rows, writer);
                next.Add(writer.Finish());
                foreach (RawRun input in group)
                {
                    input.Release(scratch);
                }
            }

            level = next;
        }

        List<RunCursor> all = [];
        foreach (RawRun run in absorbed)
        {
            all.Add(Open(AbsorbedScratch!, run, all.Count));
        }

        foreach (RawRun run in level)
        {
            all.Add(Open(scratch, run, all.Count));
        }

        KeyRun merged = Final(all, firstBlock, endBlock - firstBlock);
        foreach (RawRun run in level)
        {
            run.Release(scratch);
        }

        return merged;
    }

    private RunCursor Open(RunScratch scratch, RawRun run, int ordinal) =>
        new RunCursor(
            run.Held ? new HeldWindowSource(run) : new RawWindowSource(scratch, run), _layout, _rows, ordinal);

    private KeyRun Final(List<RunCursor> cursors, int firstBlock, int blockCount)
    {
        PayloadRunSink sink = new PayloadRunSink(
            _layout, _utf8, _rows, _segmentEntries, firstBlock, blockCount, BlockRows, WideRowsAbove);
        RunMerger.Merge(cursors, _layout, _rows, sink);
        return sink.Finish();
    }

    // Every array here is rented, and goes back once the chunk's run is laid in the scratch.
    private RawRun PostingsRaw(RunScratch scratch, ChunkKeys chunk, int firstBlock, int blocks)
    {
        int distinct = chunk.Ranked(_layout, out int[] ranked);
        int[] rank = RankOf(ranked, distinct, chunk);
        int entries = chunk.Log.Count;

        // Filling in log order is what makes each block list come out sorted.
        int[] starts = Counts(chunk, rank, distinct);
        uint[] lists = ArrayPool<uint>.Shared.Rent(Math.Max(entries, 1));
        int[] cursor = ArrayPool<int>.Shared.Rent(distinct + 1);
        starts.AsSpan(0, distinct + 1).CopyTo(cursor);
        foreach ((int key, long block) in chunk.Log)
        {
            lists[cursor[rank[key]]++] = checked((uint)block);
        }

        int keyBytes = KeyBytesOf(chunk, ranked, distinct);
        long held = RawRun.HeldBytes(distinct, keyBytes, hasRows: false, entries);
        RawRun run;
        if (scratch.Admit(held))
        {
            // Held: the arrays are the run, and the merge reads them in place.
            (byte[] keys, int[]? offsets) = Gather(chunk, ranked, distinct, keyBytes);
            run = new RawRun
            {
                HasRows = false,
                KeyWidth = _layout.Width,
                FirstBlock = firstBlock,
                BlockCount = blocks,
                Entries = distinct,
                HeldKeys = keys,
                HeldKeyOffsets = offsets,
                HeldBlockOffsets = starts,
                HeldBlocks = lists,
                Admitted = held,
            };
            ArrayPool<int>.Shared.Return(cursor);
            ArrayPool<int>.Shared.Return(rank);
            ArrayPool<int>.Shared.Return(ranked);
            return run;
        }

        using (RawRunWriter writer = new RawRunWriter(scratch, hasRows: false, _layout.Width, firstBlock, blocks))
        {
            for (int i = 0; i < distinct; i++)
            {
                writer.BeginKey(chunk.KeyBytes(ranked[i]));
                writer.AddBlocks(lists.AsSpan(starts[i], starts[i + 1] - starts[i]));
                writer.EndKey();
            }

            run = writer.Finish();
        }

        ArrayPool<uint>.Shared.Return(lists);
        ArrayPool<int>.Shared.Return(cursor);
        ArrayPool<int>.Shared.Return(starts);
        ArrayPool<int>.Shared.Return(rank);
        ArrayPool<int>.Shared.Return(ranked);
        return run;
    }

    private int KeyBytesOf(ChunkKeys chunk, int[] ids, int count)
    {
        if (_layout.Shape != KeyShape.Bytes)
        {
            return checked(count * _layout.Width);
        }

        long total = 0;
        for (int i = 0; i < count; i++)
        {
            total += chunk.KeyBytes(ids[i]).Length;
        }

        return checked((int)total);
    }

    /// <summary>The keys <paramref name="ids"/> names, end to end, and for byte keys their offsets; rented.</summary>
    private (byte[] Keys, int[]? Offsets) Gather(ChunkKeys chunk, int[] ids, int count, int keyBytes)
    {
        byte[] keys = ArrayPool<byte>.Shared.Rent(Math.Max(keyBytes, 1));
        int[]? offsets = _layout.Shape == KeyShape.Bytes ? ArrayPool<int>.Shared.Rent(count + 1) : null;
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> key = chunk.KeyBytes(ids[i]);
            if (offsets is not null)
            {
                offsets[i] = at;
            }

            key.CopyTo(keys.AsSpan(at));
            at += key.Length;
        }

        if (offsets is not null)
        {
            offsets[count] = at;
        }

        return (keys, offsets);
    }

    private RawRun SortedRaw(RunScratch scratch, ChunkKeys chunk, int firstBlock, int blocks)
    {
        int distinct = chunk.Ranked(_layout, out int[] ranked);
        int[] rank = RankOf(ranked, distinct, chunk);
        List<(int Key, long Position)> log = chunk.Log;
        int entries = log.Count;

        // A stable counting sort of the rows by their key's rank.
        int[] cursor = Counts(chunk, rank, distinct);
        int[] entryIds = ArrayPool<int>.Shared.Rent(Math.Max(entries, 1));
        long[] rows = ArrayPool<long>.Shared.Rent(Math.Max(entries, 1));
        foreach ((int key, long row) in log)
        {
            int slot = cursor[rank[key]]++;
            entryIds[slot] = key;
            rows[slot] = row;
        }

        int keyBytes = KeyBytesOf(chunk, entryIds, entries);
        long held = RawRun.HeldBytes(entries, keyBytes, hasRows: true, 0);
        RawRun run;
        if (scratch.Admit(held))
        {
            (byte[] keys, int[]? offsets) = Gather(chunk, entryIds, entries, keyBytes);
            run = new RawRun
            {
                HasRows = true,
                KeyWidth = _layout.Width,
                FirstBlock = firstBlock,
                BlockCount = blocks,
                Entries = entries,
                HeldKeys = keys,
                HeldKeyOffsets = offsets,
                HeldRows = rows,
                Admitted = held,
            };
        }
        else
        {
            using RawRunWriter writer = new RawRunWriter(scratch, hasRows: true, _layout.Width, firstBlock, blocks);
            for (int i = 0; i < entries; i++)
            {
                writer.Add(chunk.KeyBytes(entryIds[i]), rows[i]);
            }

            run = writer.Finish();
            ArrayPool<long>.Shared.Return(rows);
        }

        ArrayPool<int>.Shared.Return(entryIds);
        ArrayPool<int>.Shared.Return(cursor);
        ArrayPool<int>.Shared.Return(rank);
        ArrayPool<int>.Shared.Return(ranked);
        return run;
    }

    /// <summary>
    /// Where each rank's entries start: <c>[0]</c> is zero and <c>[distinct]</c> the log's length.
    /// </summary>
    private static int[] Counts(ChunkKeys chunk, int[] rank, int distinct)
    {
        int[] starts = ArrayPool<int>.Shared.Rent(distinct + 1);
        starts.AsSpan(0, distinct + 1).Clear();
        foreach ((int key, _) in chunk.Log)
        {
            starts[rank[key] + 1]++;
        }

        for (int i = 0; i < distinct; i++)
        {
            starts[i + 1] += starts[i];
        }

        return starts;
    }

    private static int[] RankOf(int[] ranked, int distinct, ChunkKeys chunk)
    {
        int maxId = 0;
        foreach ((int key, _) in chunk.Log)
        {
            maxId = Math.Max(maxId, key);
        }

        // Only the ids the log uses are read, and every one of them is ranked.
        int[] rank = ArrayPool<int>.Shared.Rent(maxId + 1);
        for (int i = 0; i < distinct; i++)
        {
            rank[ranked[i]] = i;
        }

        return rank;
    }

    public override void Dispose()
    {
        _table.Dispose();
        _spare.Dispose();
    }
}
