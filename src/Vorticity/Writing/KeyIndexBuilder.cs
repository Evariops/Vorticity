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
    private readonly ChunkKeys _spare = new ChunkKeys();
    private readonly List<int> _lastBlock = [];
    private ChunkKeys _table;
    private long _row;
    private int _block;

    /// <param name="rows">Sorted runs (every row) rather than postings (every block).</param>
    /// <param name="layout">The column's key layout.</param>
    /// <param name="utf8">Whether a bytes key is a string, so the keys array says so.</param>
    /// <param name="segmentEntries">The most entries a segment holds.</param>
    internal KeyIndexBuilder(bool rows, KeyLayout layout, bool utf8, int segmentEntries = KeyRunOptions.DefaultSegmentEntries)
    {
        _table = new ChunkKeys();
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
        _table = new ChunkKeys();
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

    /// <summary>
    /// Feeds encoded keys, one per row, for a composite key (10 §6.5): the rows
    /// <paramref name="include"/> leaves out -- a null in the tuple -- are counted and not entries.
    /// </summary>
    /// <param name="keys">The rows' keys.</param>
    /// <param name="include">Per row, whether it is an entry.</param>
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
    /// <param name="count">How many.</param>
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

    /// <inheritdoc/>
    internal override void CloseBlock() => _block++;

    /// <inheritdoc/>
    internal override void Start(int block, long row)
    {
        _block = block;
        _row = row;
    }

    /// <inheritdoc/>
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
            KeyRun run = _rows
                ? SortedRun(chunk, firstBlock, blocks, firstRow)
                : PostingsRun(chunk, firstBlock, blocks);
            Runs.Add(run);
            foreach (PendingPayload payload in run.Payloads)
            {
                Enqueue(payload);
            }
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

    // EVERY ARRAY BELOW IS RENTED. The work arrays go back when the run is built; the payload arrays
    // go back when the index writer has laid them (PendingPayload's release), and an array that is a
    // whole segment's payload as it stands is handed over rather than copied.
    private KeyRun PostingsRun(ChunkKeys chunk, int firstBlock, int blocks)
    {
        int distinct = chunk.Ranked(_layout, out int[] ranked);
        int[] rank = RankOf(ranked, distinct, chunk);
        int entries = chunk.Log.Count;

        // The block lists, grouped by rank: count, prefix-sum, fill in log order (which is block
        // order, so each list comes out sorted).
        int[] starts = Counts(chunk, rank, distinct);
        uint[] lists = ArrayPool<uint>.Shared.Rent(entries);
        int[] cursor = ArrayPool<int>.Shared.Rent(distinct + 1);
        starts.AsSpan(0, distinct + 1).CopyTo(cursor);
        foreach ((int key, long block) in chunk.Log)
        {
            lists[cursor[rank[key]]++] = checked((uint)(block - firstBlock));
        }

        List<KeySegment> segments = [];
        List<PendingPayload> payloads = [];
        bool listsHandedOver = false;
        for (int from = 0; from < Math.Max(distinct, 1); from += _segmentEntries)
        {
            int to = Math.Min(from + _segmentEntries, distinct);
            uint[] offsets = ArrayPool<uint>.Shared.Rent(to - from + 1);
            for (int i = from; i <= to; i++)
            {
                offsets[i - from] = (uint)(starts[i] - starts[from]);
            }

            ReadOnlySpan<int> ids = ranked.AsSpan(from, to - from);
            segments.Add(Segment(chunk, ids));
            payloads.Add(Keys(chunk, ids));
            payloads.Add(PendingPayload.RentedU32(offsets, to - from + 1, compress: true));
            payloads.Add(Rows(lists, starts[from], starts[to], entries, ref listsHandedOver));
        }

        Return(lists, listsHandedOver);
        ArrayPool<int>.Shared.Return(cursor);
        ArrayPool<int>.Shared.Return(starts);
        ArrayPool<int>.Shared.Return(rank);
        ArrayPool<int>.Shared.Return(ranked);
        return new KeyRun(firstBlock, blocks, distinct, segments, payloads);
    }

    private KeyRun SortedRun(ChunkKeys chunk, int firstBlock, int blocks, long firstRow)
    {
        int distinct = chunk.Ranked(_layout, out int[] ranked);
        int[] rank = RankOf(ranked, distinct, chunk);
        List<(int Key, long Position)> log = chunk.Log;
        int entries = log.Count;

        // A stable counting sort of the rows by their key's rank; each entry keeps its key's id.
        int[] cursor = Counts(chunk, rank, distinct);
        int[] entryIds = ArrayPool<int>.Shared.Rent(entries);
        uint[] rows = ArrayPool<uint>.Shared.Rent(entries);
        foreach ((int key, long row) in log)
        {
            int slot = cursor[rank[key]]++;
            entryIds[slot] = key;
            rows[slot] = checked((uint)(row - firstRow));
        }

        List<KeySegment> segments = [];
        List<PendingPayload> payloads = [];
        bool rowsHandedOver = false;
        for (int from = 0; from < Math.Max(entries, 1); from += _segmentEntries)
        {
            int to = Math.Min(from + _segmentEntries, entries);
            ReadOnlySpan<int> ids = entryIds.AsSpan(from, to - from);
            segments.Add(Segment(chunk, ids));
            payloads.Add(Keys(chunk, ids));
            payloads.Add(Rows(rows, from, to, entries, ref rowsHandedOver));
        }

        Return(rows, rowsHandedOver);
        ArrayPool<int>.Shared.Return(entryIds);
        ArrayPool<int>.Shared.Return(cursor);
        ArrayPool<int>.Shared.Return(rank);
        ArrayPool<int>.Shared.Return(ranked);
        return new KeyRun(firstBlock, blocks, entries, segments, payloads);
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

    /// <summary>
    /// The payload of words <c>[from, to)</c>: the array itself when that is all of it, a rented
    /// copy otherwise.
    /// </summary>
    private static PendingPayload Rows(uint[] words, int from, int to, int length, ref bool handedOver)
    {
        if (from == 0 && to == length)
        {
            handedOver = true;
            return PendingPayload.RentedU32(words, length, compress: true);
        }

        uint[] copy = ArrayPool<uint>.Shared.Rent(to - from);
        words.AsSpan(from, to - from).CopyTo(copy);
        return PendingPayload.RentedU32(copy, to - from, compress: true);
    }

    private static void Return(uint[] words, bool handedOver)
    {
        if (!handedOver)
        {
            ArrayPool<uint>.Shared.Return(words);
        }
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

    private static KeySegment Segment(ChunkKeys chunk, ReadOnlySpan<int> ids) =>
        ids.IsEmpty
            ? new KeySegment(0, [], [])
            : new KeySegment((ulong)ids.Length, chunk.KeyBytes(ids[0]).ToArray(), chunk.KeyBytes(ids[^1]).ToArray());

    /// <summary>
    /// The keys array of a segment, one key per id, copied out of the chunk's table now: the table
    /// is reset by the time the payload is laid out.
    /// </summary>
    private PendingPayload Keys(ChunkKeys chunk, ReadOnlySpan<int> ids)
    {
        int count = ids.Length;
        int[] offsets = ArrayPool<int>.Shared.Rent(count + 1);
        offsets[0] = 0;
        for (int i = 0; i < count; i++)
        {
            offsets[i + 1] = offsets[i] + chunk.KeyBytes(ids[i]).Length;
        }

        int length = offsets[count];
        byte[] heap = ArrayPool<byte>.Shared.Rent(length);
        for (int i = 0; i < count; i++)
        {
            chunk.KeyBytes(ids[i]).CopyTo(heap.AsSpan(offsets[i]));
        }

        KeyLayout layout = _layout;
        bool utf8 = _utf8;
        return new PendingPayload(
            (arena, types) => layout.Shape == KeyShape.Bytes
                ? Views(arena, types, utf8, heap.AsSpan(0, length), offsets.AsSpan(0, count + 1))
                : Fixed(arena, types, layout, heap.AsSpan(0, length), count),
            compress: true,
            estimate: length + (layout.Shape == KeyShape.Bytes ? 16L * count : 0),
            () =>
            {
                ArrayPool<byte>.Shared.Return(heap);
                ArrayPool<int>.Shared.Return(offsets);
            });
    }

    private static int Fixed(CanonicalArena arena, DTypeArena types, KeyLayout layout, ReadOnlySpan<byte> heap, int count)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(heap.Length, layout.Width, out Span<byte> bytes);
        heap.CopyTo(bytes);
        return arena.AddPrimitive(
            types.Primitive(layout.PType, Nullability.NonNullable), count, Validity.NonNullable, layout.PType, buffer);
    }

    private static int Views(
        CanonicalArena arena, DTypeArena types, bool utf8, ReadOnlySpan<byte> heap, ReadOnlySpan<int> offsets)
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
                heap.Slice(offsets[i], length).CopyTo(view[4..]);
                continue;
            }

            heap.Slice(offsets[i], 4).CopyTo(view[4..]);
            int buffer = 0;
            int offset = offsets[i];
            MemoryMarshal.Write(view[8..], in buffer);
            MemoryMarshal.Write(view[12..], in offset);
        }

        DType dtype = utf8 ? types.Utf8(Nullability.NonNullable) : types.Binary(Nullability.NonNullable);
        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _table.Dispose();
        _spare.Dispose();
    }
}
