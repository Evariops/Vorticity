// The streaming builders of the two locating kinds (docs/10-indexes.md §6.1, §6.2).
//
// `vorticity.postings.blocks.v1`: per chunk, the chunk's distinct keys in key order, and for each
// the blocks of the chunk that hold it. A key is noted once per block, the first time the block
// sees it, so the list is built in the pass and sorted by construction.
//
// `vorticity.sorted.runs.v1`: per chunk, every (key, row) of the chunk in key order and, within a
// key, in row order (docs/12-index-reads.md §14's amendment to §6.2). A sort of one chunk's keys, in
// flux: a counting sort of the rows by the rank of their key, the ranks from one sort of the
// chunk's DISTINCT keys -- which is what makes a low-cardinality column cheap to order.
//
// BOTH ARE CUT INTO SEGMENTS of at most `segment_entries` entries (10 §4.2), each segment one keys
// array plus the kind's arrays beside it, and each described in the run's options by its first and
// last key, so that a probe reads the segments that can hold its key and no other. The arrays are
// ordinary columns written by the data's own compressor: sorted keys delta- or dictionary-encode,
// block lists and row offsets bit-pack.
//
// POSITIONS ARE RELATIVE TO THE RUN: a block id counts from the run's first block, a row from its
// first row, which is `first_block × block_len` because a chunk is a whole number of blocks. So a
// row fits `u32` whatever the file's length.
using System;
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
/// <param name="FirstBlock">The chunk's first block.</param>
/// <param name="BlockCount">Its blocks.</param>
/// <param name="Entries">Its entries, across segments.</param>
/// <param name="Segments">Its segment table.</param>
/// <param name="Payloads">Its arrays, `stride` per segment, in segment order.</param>
internal sealed record KeyRun(
    int FirstBlock, int BlockCount, long Entries, List<KeySegment> Segments, List<PendingPayload> Payloads);

/// <summary>Builds one column's postings or sorted runs, chunk by chunk.</summary>
internal sealed class KeyIndexBuilder : IndexBuilder
{
    private readonly bool _rows;
    private readonly KeyLayout _layout;
    private readonly bool _utf8;
    private readonly int _segmentEntries;
    private readonly bool _trigrams;
    private readonly bool _fold;
    private ChunkKeys _table = new ChunkKeys();
    private List<int> _lastBlock = [];
    private long _row;
    private int _block;

    /// <param name="rows">Sorted runs (every row) rather than postings (every block).</param>
    /// <param name="layout">The column's key layout.</param>
    /// <param name="utf8">Whether a bytes key is a string, so the keys array says so.</param>
    /// <param name="segmentEntries">The most entries a segment holds.</param>
    internal KeyIndexBuilder(bool rows, KeyLayout layout, bool utf8, int segmentEntries = KeyRunOptions.DefaultSegmentEntries)
    {
        _rows = rows;
        _layout = layout;
        _utf8 = utf8;
        _segmentEntries = segmentEntries;
    }

    /// <summary>
    /// A trigram postings builder (10 §6.4): the keys are every byte trigram of every value, typed
    /// <c>binary</c> because a trigram may cut a UTF-8 code point in two.
    /// </summary>
    /// <param name="fold">Whether trigrams are ASCII-lower-cased.</param>
    /// <param name="segmentEntries">The most entries a segment holds.</param>
    internal static KeyIndexBuilder ForTrigrams(bool fold, int segmentEntries) =>
        new KeyIndexBuilder(fold, segmentEntries);

    private KeyIndexBuilder(bool fold, int segmentEntries)
    {
        _rows = false;
        _layout = new KeyLayout(KeyShape.Bytes, 0, default);
        _utf8 = false;
        _segmentEntries = segmentEntries;
        _trigrams = true;
        _fold = fold;
    }

    /// <summary>The kind this builder writes.</summary>
    internal string Kind => _trigrams
        ? IndexKinds.PostingsNgram3
        : _rows ? IndexKinds.SortedRuns : IndexKinds.PostingsBlocks;

    /// <summary>Whether trigrams are ASCII-lower-cased.</summary>
    internal bool CaseInsensitive => _fold;

    /// <summary>Every run closed, in block order.</summary>
    internal List<KeyRun> Runs { get; } = [];

    /// <summary>The segment size the runs are cut at.</summary>
    internal int SegmentEntries => _segmentEntries;

    /// <summary>What the open table holds, for the budget.</summary>
    internal long OpenBytes => _table.Bytes;

    /// <summary>Whether the dtype can be keyed, and why not.</summary>
    /// <param name="dtype">The column's dtype.</param>
    /// <param name="layout">Its key layout.</param>
    /// <param name="utf8">Whether its keys are strings.</param>
    /// <param name="reason">Why not.</param>
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

    /// <inheritdoc/>
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
                // One key for every valid row; its id is looked up once.
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

    /// <inheritdoc/>
    internal override void CloseBlock() => _block++;

    /// <inheritdoc/>
    internal override void CloseChunk(int firstBlock, int blocks, long firstRow, long rows)
    {
        if (Abandoned is not null)
        {
            return;
        }

        long end = _rows ? firstRow + rows : (long)firstBlock + blocks;
        using ChunkKeys chunk = _table.Cut(end);
        if (!_rows)
        {
            // The carried suffix was re-interned: its ids are new, and so are their last blocks.
            List<int> last = [];
            foreach ((int key, long position) in _table.Log)
            {
                while (last.Count <= key)
                {
                    last.Add(-1);
                }

                last[key] = (int)position;
            }

            _lastBlock = last;
        }

        KeyRun run = _rows
            ? SortedRun(chunk, firstBlock, blocks, firstRow)
            : PostingsRun(chunk, firstBlock, blocks);
        Runs.Add(run);
        foreach (PendingPayload payload in run.Payloads)
        {
            Pending.Enqueue(payload);
        }
    }

    private KeyRun PostingsRun(ChunkKeys chunk, int firstBlock, int blocks)
    {
        int[] ranked = chunk.Ranked(_layout);
        int[] rank = RankOf(ranked, chunk);

        // The block lists, grouped by rank: count, prefix-sum, fill in log order (which is block
        // order, so each list comes out sorted).
        int[] starts = new int[ranked.Length + 1];
        foreach ((int key, _) in chunk.Log)
        {
            starts[rank[key] + 1]++;
        }

        for (int i = 0; i < ranked.Length; i++)
        {
            starts[i + 1] += starts[i];
        }

        uint[] lists = new uint[chunk.Log.Count];
        int[] cursor = (int[])starts.Clone();
        foreach ((int key, long block) in chunk.Log)
        {
            lists[cursor[rank[key]]++] = checked((uint)(block - firstBlock));
        }

        List<KeySegment> segments = [];
        List<PendingPayload> payloads = [];
        for (int from = 0; from < Math.Max(ranked.Length, 1); from += _segmentEntries)
        {
            int to = Math.Min(from + _segmentEntries, ranked.Length);
            uint[] offsets = new uint[to - from + 1];
            for (int i = from; i <= to; i++)
            {
                offsets[i - from] = (uint)(starts[i] - starts[from]);
            }

            uint[] segmentLists = lists.AsSpan(starts[from], starts[to] - starts[from]).ToArray();
            segments.Add(Segment(chunk, ranked, from, to, to - from));
            payloads.Add(Keys(chunk, ranked, from, to, entries: null));
            payloads.Add(PendingPayload.U32(offsets, compress: true));
            payloads.Add(PendingPayload.U32(segmentLists, compress: true));
        }

        return new KeyRun(firstBlock, blocks, ranked.Length, segments, payloads);
    }

    private KeyRun SortedRun(ChunkKeys chunk, int firstBlock, int blocks, long firstRow)
    {
        int[] ranked = chunk.Ranked(_layout);
        int[] rank = RankOf(ranked, chunk);
        List<(int Key, long Position)> log = chunk.Log;

        // A stable counting sort of the rows by their key's rank.
        int[] starts = new int[ranked.Length + 1];
        foreach ((int key, _) in log)
        {
            starts[rank[key] + 1]++;
        }

        for (int i = 0; i < ranked.Length; i++)
        {
            starts[i + 1] += starts[i];
        }

        int[] entryRank = new int[log.Count];
        uint[] rows = new uint[log.Count];
        int[] cursor = (int[])starts.Clone();
        foreach ((int key, long row) in log)
        {
            int slot = cursor[rank[key]]++;
            entryRank[slot] = rank[key];
            rows[slot] = checked((uint)(row - firstRow));
        }

        List<KeySegment> segments = [];
        List<PendingPayload> payloads = [];
        for (int from = 0; from < Math.Max(log.Count, 1); from += _segmentEntries)
        {
            int to = Math.Min(from + _segmentEntries, log.Count);
            int[] entries = entryRank.AsSpan(from, to - from).ToArray();
            segments.Add(to == from
                ? new KeySegment(0, [], [])
                : new KeySegment(
                    (ulong)(to - from),
                    chunk.KeyBytes(ranked[entries[0]]).ToArray(),
                    chunk.KeyBytes(ranked[entries[^1]]).ToArray()));
            payloads.Add(Keys(chunk, ranked, 0, 0, entries));
            payloads.Add(PendingPayload.U32(rows.AsSpan(from, to - from).ToArray(), compress: true));
        }

        return new KeyRun(firstBlock, blocks, log.Count, segments, payloads);
    }

    private static int[] RankOf(int[] ranked, ChunkKeys chunk)
    {
        int maxId = 0;
        foreach ((int key, _) in chunk.Log)
        {
            maxId = Math.Max(maxId, key);
        }

        int[] rank = new int[maxId + 1];
        for (int i = 0; i < ranked.Length; i++)
        {
            rank[ranked[i]] = i;
        }

        return rank;
    }

    private static KeySegment Segment(ChunkKeys chunk, int[] ranked, int from, int to, int entries) =>
        to == from
            ? new KeySegment(0, [], [])
            : new KeySegment((ulong)entries, chunk.KeyBytes(ranked[from]).ToArray(), chunk.KeyBytes(ranked[to - 1]).ToArray());

    /// <summary>
    /// The keys array of a segment, copied out of the chunk's table now: the table is gone by the
    /// time the payload is laid out. Either the distinct keys <c>ranked[from..to)</c>, or one key
    /// per entry, by rank.
    /// </summary>
    private PendingPayload Keys(ChunkKeys chunk, int[] ranked, int from, int to, int[]? entries)
    {
        int count = entries?.Length ?? to - from;
        int[] offsets = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            int id = entries is null ? ranked[from + i] : ranked[entries[i]];
            offsets[i + 1] = offsets[i] + chunk.KeyBytes(id).Length;
        }

        byte[] heap = new byte[offsets[count]];
        for (int i = 0; i < count; i++)
        {
            int id = entries is null ? ranked[from + i] : ranked[entries[i]];
            chunk.KeyBytes(id).CopyTo(heap.AsSpan(offsets[i]));
        }

        KeyLayout layout = _layout;
        bool utf8 = _utf8;
        return new PendingPayload(
            (arena, types) => layout.Shape == KeyShape.Bytes
                ? Views(arena, types, utf8, heap, offsets)
                : Fixed(arena, types, layout, heap, count),
            compress: true);
    }

    private static int Fixed(CanonicalArena arena, DTypeArena types, KeyLayout layout, byte[] heap, int count)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(heap.Length, layout.Width, out Span<byte> bytes);
        heap.CopyTo(bytes);
        return arena.AddPrimitive(
            types.Primitive(layout.PType, Nullability.NonNullable), count, Validity.NonNullable, layout.PType, buffer);
    }

    private static int Views(CanonicalArena arena, DTypeArena types, bool utf8, byte[] heap, int[] offsets)
    {
        int count = offsets.Length - 1;
        VortexBuffer data = arena.AllocateUninitialized(Math.Max(heap.Length, 1), 1, out Span<byte> dataBytes);
        heap.CopyTo(dataBytes);
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        for (int i = 0; i < count; i++)
        {
            int length = offsets[i + 1] - offsets[i];
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            MemoryMarshal.Write(view, in length);
            if (length <= 12)
            {
                heap.AsSpan(offsets[i], length).CopyTo(view[4..]);
                continue;
            }

            heap.AsSpan(offsets[i], 4).CopyTo(view[4..]);
            int buffer = 0;
            int offset = offsets[i];
            MemoryMarshal.Write(view[8..], in buffer);
            MemoryMarshal.Write(view[12..], in offset);
        }

        DType dtype = utf8 ? types.Utf8(Nullability.NonNullable) : types.Binary(Nullability.NonNullable);
        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
    }

    /// <inheritdoc/>
    public override void Dispose() => _table.Dispose();
}
