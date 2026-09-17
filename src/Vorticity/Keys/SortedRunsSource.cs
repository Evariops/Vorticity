// The key source of a `vorticity.sorted.runs.v1` index - docs/12-index-reads.md §3, §4, §9.
//
// A K-WAY MERGE OVER RUNS THAT ARE EACH IN ORDER. A streaming writer sorts one chunk at a time
// (docs/10-indexes.md §6.2), so a key order exists inside each run and the union is a set; the
// cursor over the union is the merging iterator of every log-structured store: a heap of run
// positions, `O(log r)` per step, `O(r log n)` per seek. Forward it is a min-heap; backward a
// max-heap; a step against the heap's direction re-seeks at the current entry, which is what a
// merging iterator charges (§4.2).
//
// ONE PRIMITIVE. Every positioning is "the first entry at or after (key, row)" in each run, and the
// five operators, the steps across a flip, rank, select and the count of a key are all that
// primitive with a row of `long.MinValue` (before every row of the key), `long.MaxValue` (after
// every one) or a real row. Rows are unique across runs, so `(key, row)` is a total order on the
// entries and `rank` of an entry is exact.
//
// THE ORDER IS THE TOTAL ONE (§4.4): the writer ranks keys with `KeyOrder.Total`'s twin on bytes,
// so -0.0 and +0.0 are two keys here, where a sorted column holds them as one.
//
// ONLY THE SEGMENTS THAT CAN HOLD THE ANSWER ARE READ. A run's options give each segment's first and
// last key; a run whose range excludes the key costs nothing, and inside one only the segment the
// search lands in is decoded -- into the file's run cache, where the next seek finds it.
//
// THE SAME MERGE WALKS KEYS WITHOUT ROWS. A `postings.blocks` run is a chunk's distinct keys, sorted,
// and a dictionary is a chunk's values, sorted at open (SortedRunsSource.Dictionary.cs): each is a
// run whose keys are unique within it, so the run's ordinal stands in for the row and `(key,
// ordinal)` is still a total order on the entries. Such a source has no rows to give (§4.3), and
// its entries are not the column's distinct keys -- a key in two chunks is two entries -- so it
// counts none; a `Distinct()` cursor walks it by `NextKey`, which skips them.
//
// A RUN THAT DOES NOT MAKE SENSE REFUSES THE SOURCE WHOLE. The pruner can ignore one bad run and
// lose pruning; a cursor that skipped one would return a wrong walk. A payload that decodes to the
// wrong shape, or a row outside its run, is a `VortexFormatException` (Class I); keys out of order
// or bounds that lie give a wrong order and never a fault (Class II, docs/08-semantics.md §5).
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Keys;

/// <summary>One column's runs -- sorted runs, postings keys or dictionaries -- merged into one walk.</summary>
internal sealed partial class SortedRunsSource : KeySource
{
    private readonly VortexFile _file;
    private readonly KeyLayout _layout;
    private readonly DType _storage;
    private readonly Run[] _runs;
    private readonly int[] _heap;
    private readonly KeySourceKind _source;
    private string? _keyFormat;
    private int _heapSize;

    /// <summary>+1 while the heap is a min-heap, -1 while it is a max-heap.</summary>
    private int _direction = 1;

    private SortedRunsSource(
        VortexFile file, KeyLayout layout, DType storage, FilterLiteralKind kind, Run[] runs, KeySourceKind source)
    {
        _file = file;
        _layout = layout;
        _storage = storage;
        _runs = runs;
        _source = source;
        _heap = new int[runs.Length];
        KeyKind = kind;
        long entries = 0;
        foreach (Run run in runs)
        {
            entries += run.Count;
        }

        Entries = entries;
    }

    internal override FilterLiteralKind KeyKind { get; }

    internal override long? EntryCount => HasRows ? Entries : null;

    internal override int Runs => _runs.Length;

    internal override bool IsValid => _heapSize > 0;

    internal override bool HasRows => _source == KeySourceKind.SortedRuns;

    internal override string? KeyFormat => _keyFormat;

    /// <summary>Which kind of run this source merges.</summary>
    internal KeySourceKind Kind => _source;

    /// <summary>Every entry of every run: the column's non-null rows, for sorted runs.</summary>
    internal long Entries { get; }

    internal override FilterLiteral Key
    {
        get
        {
            Run run = _runs[_heap[0]];
            ReadOnlySpan<byte> key = CurrentKey(run);
            return _layout.Shape == KeyShape.Bytes ? FilterLiteral.From(key) : Literal(key);
        }
    }

    internal override ReadOnlySpan<byte> KeyBytes =>
        _layout.Shape == KeyShape.Bytes ? CurrentKey(_runs[_heap[0]]) : default;

    internal override long Row
    {
        get
        {
            Run run = _runs[_heap[0]];
            return RowAt(run, run.Current!, (int)(run.Position - run.CurrentStart));
        }
    }

    /// <summary>
    /// Opens a sorted-runs source over <paramref name="path"/>, or says why the file offers none.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the directory read.</param>
    /// <returns>The source, or null with the reason.</returns>
    internal static ValueTask<(SortedRunsSource? Source, string? Reason)> OpenAsync(
        VortexFile file, string path, CancellationToken cancellationToken) =>
        OpenAsync(file, path, KeySourceKind.SortedRuns, cancellationToken);

    /// <summary>
    /// Opens a source of <paramref name="source"/>'s kind over <paramref name="path"/>, or says why
    /// the file offers none.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="path">The column.</param>
    /// <param name="source">
    /// <see cref="KeySourceKind.SortedRuns"/>, <see cref="KeySourceKind.Postings"/> or
    /// <see cref="KeySourceKind.Dictionary"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The source, or null with the reason.</returns>
    internal static async ValueTask<(SortedRunsSource? Source, string? Reason)> OpenAsync(
        VortexFile file, string path, KeySourceKind source, CancellationToken cancellationToken)
    {
        string kind = source switch
        {
            KeySourceKind.SortedRuns => IndexKinds.SortedRuns,
            KeySourceKind.Postings => IndexKinds.PostingsBlocks,
            _ => IndexKinds.DictProbe,
        };

        if (!file.HasIndexDirectory)
        {
            return (null, "the file carries no index directory");
        }

        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return (null, $"the file's index directory was refused: {file.IndexDirectoryRefusal}");
        }

        string? reason = null;
        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != kind
                || !KeyIndexPruner.TryResolve(file.Schema, entry.ColumnPath, out string resolved, out DType dtype)
                || !string.Equals(resolved, path, StringComparison.Ordinal))
            {
                continue;
            }

            SortedRunsSource? opened = null;
            reason = Uncovered(file, entry);
            if (reason is null)
            {
                (opened, reason) = source == KeySourceKind.Dictionary
                    ? await OpenDictionaryAsync(file, entry, path, dtype, cancellationToken).ConfigureAwait(false)
                    : TryOpen(file, entry, dtype, source);
            }

            if (opened is not null)
            {
                return (opened, null);
            }
        }

        return (null, reason ?? $"the index directory has no {kind} entry for this column");
    }

    /// <summary>
    /// Why an entry's runs do not cover every block of the file, or null when they do.
    /// </summary>
    /// <remarks>
    /// A PRUNER MAY USE A PARTIAL INDEX, A SOURCE MAY NOT: a block no run covers is simply live
    /// (10 §4.1), but a walk that misses it misses its keys, and a count over the walk is wrong. An
    /// append whose builder was abandoned, and a dictionary probe over a column some of whose chunks
    /// are not dictionaries, both leave blocks uncovered.
    /// </remarks>
    internal static string? Uncovered(VortexFile file, IndexEntry entry)
    {
        ulong blocks = entry.BlockLength == 0
            ? 0
            : ((ulong)file.RowCount + entry.BlockLength - 1) / entry.BlockLength;
        ulong next = 0;
        foreach (IndexRun run in entry.Runs)
        {
            if (run.FirstBlock != next)
            {
                return $"its runs leave blocks {next} to {run.FirstBlock} uncovered, so a walk would miss their keys";
            }

            next = run.EndBlock;
        }

        return next < blocks
            ? $"its runs cover {next} of the file's {blocks} blocks, so a walk would miss the others' keys"
            : null;
    }

    /// <summary>The key domain and byte layout of a column, and its storage type.</summary>
    private static string? Shape(DType dtype, out FilterLiteralKind kind, out KeyLayout layout, out DType storage)
    {
        storage = dtype;
        if (!SortedColumnSource.TryKeyKind(dtype, out kind) || !KeyLayout.TryOf(dtype, out layout))
        {
            layout = default;
            return $"a {dtype.Kind} column has no key order a cursor can walk (docs/12-index-reads.md §4.4)";
        }

        while (storage.Kind == DTypeKind.Extension)
        {
            storage = storage.StorageType;
        }

        return null;
    }

    /// <summary>
    /// Opens the sorted runs of a composite key (docs/12-index-reads.md §4.6): keys that are the
    /// row encoding of the tuple, bytes ordered bytewise.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="paths">The key's top-level columns, in key order.</param>
    /// <param name="cancellationToken">Cancels the directory read.</param>
    /// <returns>The source, or null with the reason.</returns>
    internal static async ValueTask<(SortedRunsSource? Source, string? Reason)> OpenCompositeAsync(
        VortexFile file, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (!file.HasIndexDirectory)
        {
            return (null, "the file carries no index directory");
        }

        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return (null, $"the file's index directory was refused: {file.IndexDirectoryRefusal}");
        }

        DType schema = file.Schema;
        string? reason = null;
        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.SortedRuns || entry.ColumnPath.Count != 0
                || !KeyRunOptions.TryParseEntry(entry.Options, out _, out _, out List<uint[]> columns, out string? format)
                || !Names(schema, columns, paths))
            {
                continue;
            }

            DType binary = new DTypeArena().Binary(Nullability.NonNullable);
            SortedRunsSource? opened = null;
            reason = Uncovered(file, entry);
            if (reason is null)
            {
                (opened, reason) = TryOpen(file, entry, binary, KeySourceKind.SortedRuns, composite: true);
            }

            if (opened is not null)
            {
                opened._keyFormat = format;
                return (opened, null);
            }
        }

        return (null, reason ?? $"the index directory has no {IndexKinds.SortedRuns} entry keyed by ({string.Join(", ", paths)})");
    }

    /// <summary>Whether an entry's key columns are exactly <paramref name="paths"/>, in order.</summary>
    private static bool Names(DType schema, List<uint[]> columns, IReadOnlyList<string> paths)
    {
        if (columns.Count != paths.Count || schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            return false;
        }

        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Length != 1 || columns[i][0] >= (uint)schema.FieldCount
                || !string.Equals(schema.GetFieldName((int)columns[i][0]), paths[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static (SortedRunsSource? Source, string? Reason) TryOpen(
        VortexFile file, IndexEntry entry, DType dtype, KeySourceKind source, bool composite = false)
    {
        FilterLiteralKind kind = FilterLiteralKind.Bytes;
        KeyLayout layout = new KeyLayout(KeyShape.Bytes, 0, default);
        DType storage = dtype;
        if (!composite && Shape(dtype, out kind, out layout, out storage) is { } shape)
        {
            return (null, shape);
        }

        if (entry.BlockLength == 0 || !KeyRunOptions.TryParseEntry(entry.Options, out _, out bool folded))
        {
            return (null, "its entry's options or block length do not parse");
        }

        if (folded)
        {
            // Case-folded keys are not the column's values: a walk over them would lie.
            return (null, "its keys are case-folded, so they are not the column's values");
        }

        int stride = source == KeySourceKind.SortedRuns ? KeyRunOptions.SortedStride : KeyRunOptions.PostingsStride;
        Run[] runs = new Run[entry.Runs.Count];
        for (int i = 0; i < runs.Length; i++)
        {
            IndexRun meta = entry.Runs[i];
            if (!KeyRunOptions.TryParseRun(meta.OptionBytes, out List<KeySegment> segments)
                || meta.Payload.Count != stride * segments.Count)
            {
                return (null, $"run {i} has a segment table that does not match its payload");
            }

            long[] starts = new long[segments.Count + 1];
            for (int s = 0; s < segments.Count; s++)
            {
                KeySegment segment = segments[s];
                bool widths = layout.Shape == KeyShape.Bytes
                    || segment.Entries == 0
                    || (segment.Min.Length == layout.Width && segment.Max.Length == layout.Width);
                if (!widths || segment.Entries > int.MaxValue)
                {
                    return (null, $"run {i} has a segment whose bounds or size do not fit the column");
                }

                starts[s + 1] = starts[s] + (long)segment.Entries;
            }

            if (meta.EntryCount != 0 && meta.EntryCount != (ulong)starts[^1])
            {
                return (null, $"run {i} lists {meta.EntryCount} entries and its segments {starts[^1]}");
            }

            long firstRow = checked((long)(meta.FirstBlock * entry.BlockLength));
            long span = checked((long)(meta.BlockCount * entry.BlockLength));
            long limit = Math.Min(span, file.RowCount - firstRow);
            if (limit < 0 || starts[^1] > limit)
            {
                return (null, $"run {i} holds more entries than its blocks have rows");
            }

            runs[i] = new Run(meta, segments.ToArray(), starts, firstRow, limit, i);
        }

        return (new SortedRunsSource(file, layout, storage, kind, runs, source), null);
    }

    // ------------------------------------------------------------------------------ positioning

    internal override async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case SeekOp.After:
                return await ForwardAsync(key, long.MaxValue, cancellationToken).ConfigureAwait(false);
            case SeekOp.AtOrBefore:
                return await BackwardAsync(key, long.MaxValue, cancellationToken).ConfigureAwait(false);
            case SeekOp.Before:
                return await BackwardAsync(key, long.MinValue, cancellationToken).ConfigureAwait(false);
            case SeekOp.AtOrAfter:
                return await ForwardAsync(key, long.MinValue, cancellationToken).ConfigureAwait(false);
            default:
                if (!await ForwardAsync(key, long.MinValue, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                // The first entry at or after the key is the key's own when it is present.
                if (CompareKey(CurrentKey(_runs[_heap[0]]), key) != 0)
                {
                    Invalidate();
                    return false;
                }

                return true;
        }
    }

    internal override async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken)
    {
        Reset(1);
        for (int r = 0; r < _runs.Length; r++)
        {
            if (_runs[r].Count > 0)
            {
                await PlaceAsync(r, 0, cancellationToken).ConfigureAwait(false);
            }
        }

        return IsValid;
    }

    internal override async ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken)
    {
        Reset(-1);
        for (int r = 0; r < _runs.Length; r++)
        {
            if (_runs[r].Count > 0)
            {
                await PlaceAsync(r, _runs[r].Count - 1, cancellationToken).ConfigureAwait(false);
            }
        }

        return IsValid;
    }

    internal override ValueTask<bool> NextAsync(CancellationToken cancellationToken) =>
        _direction > 0 ? StepAsync(cancellationToken) : FlipAsync(forward: true, cancellationToken);

    internal override ValueTask<bool> PrevAsync(CancellationToken cancellationToken) =>
        _direction < 0 ? StepAsync(cancellationToken) : FlipAsync(forward: false, cancellationToken);

    internal override ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken) =>
        ForwardAsync(Key, long.MaxValue, cancellationToken);

    internal override ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken) =>
        BackwardAsync(Key, long.MinValue, cancellationToken);

    internal override ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        SumAsync(key, long.MinValue, cancellationToken);

    internal override ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        SumAsync(key, long.MaxValue, cancellationToken);

    private async ValueTask<long> SumAsync(FilterLiteral key, long row, CancellationToken cancellationToken)
    {
        long rank = 0;
        for (int r = 0; r < _runs.Length; r++)
        {
            rank += await FirstAtOrAfterAsync(_runs[r], key, row, cancellationToken).ConfigureAwait(false);
        }

        return rank;
    }

    /// <remarks>
    /// The entry of rank <c>i</c> lies in exactly one run. In each run in turn, the rank of its
    /// p-th entry is <c>p</c> plus the entries of the other runs before it, strictly increasing in
    /// <c>p</c>, so a bisection finds the p whose rank is <c>i</c> or proves the run does not hold
    /// it: <c>O(r² log² n)</c>, which docs/12 §4.5 allows until a measurement asks for better.
    /// </remarks>
    internal override async ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken)
    {
        if (rank < 0 || rank >= Entries)
        {
            Invalidate();
            return false;
        }

        for (int s = 0; s < _runs.Length; s++)
        {
            Run run = _runs[s];
            long low = 0;
            long high = run.Count;
            while (low < high)
            {
                long mid = low + ((high - low) >> 1);
                (FilterLiteral key, long row) = await EntryAsync(run, mid, cancellationToken).ConfigureAwait(false);
                long at = mid;
                for (int t = 0; t < _runs.Length; t++)
                {
                    if (t != s)
                    {
                        at += await FirstAtOrAfterAsync(_runs[t], key, row, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (at == rank)
                {
                    return await ForwardAsync(key, row, cancellationToken).ConfigureAwait(false);
                }

                if (at < rank)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
        }

        // Runs that list one row twice leave a rank no entry has.
        Invalidate();
        return false;
    }

    internal override async ValueTask<long> KeyCountAsync(CancellationToken cancellationToken)
    {
        FilterLiteral key = Key;
        long count = 0;
        for (int r = 0; r < _runs.Length; r++)
        {
            Run run = _runs[r];
            long high = await FirstAtOrAfterAsync(run, key, long.MaxValue, cancellationToken).ConfigureAwait(false);
            long low = await FirstAtOrAfterAsync(run, key, long.MinValue, cancellationToken).ConfigureAwait(false);
            count += high - low;
        }

        return count;
    }

    /// <remarks>
    /// Per slice, the keys at its two ends, then per run whether it holds a key between them: two
    /// selections and <c>2r</c> probes a slice, for <c>Explain</c> only.
    /// </remarks>
    internal override async ValueTask<int> RunsOverlappingAsync(
        List<(long Low, long High)> slices, CancellationToken cancellationToken)
    {
        bool[] hit = new bool[_runs.Length];
        foreach ((long low, long high) in slices)
        {
            if (low >= high || !await SeekRankAsync(low, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            FilterLiteral first = Key;
            if (!await SeekRankAsync(high - 1, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            FilterLiteral last = Key;
            for (int r = 0; r < _runs.Length; r++)
            {
                if (!hit[r]
                    && await FirstAtOrAfterAsync(_runs[r], first, long.MinValue, cancellationToken).ConfigureAwait(false)
                        < await FirstAtOrAfterAsync(_runs[r], last, long.MaxValue, cancellationToken).ConfigureAwait(false))
                {
                    hit[r] = true;
                }
            }
        }

        Invalidate();
        int count = 0;
        foreach (bool h in hit)
        {
            count += h ? 1 : 0;
        }

        return count;
    }

    internal override void Invalidate() => _heapSize = 0;

    public override ValueTask DisposeAsync()
    {
        Invalidate();
        foreach (Run run in _runs)
        {
            run.Current = null;
            run.Probe = null;
        }

        return default;
    }

    /// <summary>Every run at its first entry at or after <c>(key, row)</c>, as a min-heap.</summary>
    private async ValueTask<bool> ForwardAsync(FilterLiteral key, long row, CancellationToken cancellationToken)
    {
        Reset(1);
        for (int r = 0; r < _runs.Length; r++)
        {
            long at = await FirstAtOrAfterAsync(_runs[r], key, row, cancellationToken).ConfigureAwait(false);
            if (at < _runs[r].Count)
            {
                await PlaceAsync(r, at, cancellationToken).ConfigureAwait(false);
            }
        }

        return IsValid;
    }

    /// <summary>Every run at its last entry strictly before <c>(key, row)</c>, as a max-heap.</summary>
    private async ValueTask<bool> BackwardAsync(FilterLiteral key, long row, CancellationToken cancellationToken)
    {
        Reset(-1);
        for (int r = 0; r < _runs.Length; r++)
        {
            long at = await FirstAtOrAfterAsync(_runs[r], key, row, cancellationToken).ConfigureAwait(false) - 1;
            if (at >= 0)
            {
                await PlaceAsync(r, at, cancellationToken).ConfigureAwait(false);
            }
        }

        return IsValid;
    }

    /// <summary>
    /// Steps against the heap's direction: re-seeks just past the current entry, the other way.
    /// </summary>
    private async ValueTask<bool> FlipAsync(bool forward, CancellationToken cancellationToken)
    {
        FilterLiteral key = Key;
        long row = Row;
        return forward
            ? await ForwardAsync(key, row + 1, cancellationToken).ConfigureAwait(false)
            : await BackwardAsync(key, row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Advances the heap's top run in the heap's direction.</summary>
    private ValueTask<bool> StepAsync(CancellationToken cancellationToken)
    {
        int top = _heap[0];
        Run run = _runs[top];
        long next = run.Position + _direction;
        if (next < 0 || next >= run.Count)
        {
            _heap[0] = _heap[--_heapSize];
            SiftDown(0);
            return new ValueTask<bool>(IsValid);
        }

        if (run.Current is not null && next >= run.CurrentStart && next < run.CurrentStart + run.Current.Count)
        {
            run.Position = next;
            SiftDown(0);
            return new ValueTask<bool>(true);
        }

        return StepAcrossAsync(run, next, cancellationToken);
    }

    private async ValueTask<bool> StepAcrossAsync(Run run, long next, CancellationToken cancellationToken)
    {
        await MoveAsync(run, next, cancellationToken).ConfigureAwait(false);
        SiftDown(0);
        return true;
    }

    private void Reset(int direction)
    {
        _heapSize = 0;
        _direction = direction;
    }

    /// <summary>Puts run <paramref name="index"/> at <paramref name="position"/> into the heap.</summary>
    private async ValueTask PlaceAsync(int index, long position, CancellationToken cancellationToken)
    {
        await MoveAsync(_runs[index], position, cancellationToken).ConfigureAwait(false);
        int slot = _heapSize++;
        _heap[slot] = index;
        SiftUp(slot);
    }

    // ------------------------------------------------------------------------------ the heap

    /// <summary>Whether run <paramref name="a"/>'s entry comes before run <paramref name="b"/>'s in the heap's direction.</summary>
    private bool Before(int a, int b)
    {
        Run left = _runs[a];
        Run right = _runs[b];
        int order = _layout.Compare(CurrentKey(left), CurrentKey(right));
        if (order == 0)
        {
            order = CurrentRow(left).CompareTo(CurrentRow(right));
        }

        return order * _direction < 0;
    }

    private void SiftUp(int slot)
    {
        while (slot > 0)
        {
            int parent = (slot - 1) >> 1;
            if (!Before(_heap[slot], _heap[parent]))
            {
                return;
            }

            (_heap[slot], _heap[parent]) = (_heap[parent], _heap[slot]);
            slot = parent;
        }
    }

    private void SiftDown(int slot)
    {
        while (true)
        {
            int left = (2 * slot) + 1;
            if (left >= _heapSize)
            {
                return;
            }

            int child = left + 1 < _heapSize && Before(_heap[left + 1], _heap[left]) ? left + 1 : left;
            if (!Before(_heap[child], _heap[slot]))
            {
                return;
            }

            (_heap[slot], _heap[child]) = (_heap[child], _heap[slot]);
            slot = child;
        }
    }

    // ------------------------------------------------------------------------------ one run

    /// <summary>
    /// The first position of <paramref name="run"/> whose <c>(key, row)</c> is at or after the
    /// given pair; the run's count when there is none.
    /// </summary>
    private async ValueTask<long> FirstAtOrAfterAsync(
        Run run, FilterLiteral key, long row, CancellationToken cancellationToken)
    {
        KeySegment[] segments = run.Segments;
        if (run.Count == 0)
        {
            return 0;
        }

        // The run's own range, from its options alone: most runs are excluded or included here.
        if (CompareKey(LastMax(run), key) < 0)
        {
            return run.Count;
        }

        if (CompareKey(FirstMin(run), key) > 0)
        {
            return 0;
        }

        int low = 0;
        int high = segments.Length - 1;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (segments[mid].Entries == 0 || CompareKey(segments[mid].Max, key) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (int s = low; s < segments.Length; s++)
        {
            if (segments[s].Entries == 0)
            {
                continue;
            }

            if (s > low && CompareKey(segments[s].Min, key) > 0)
            {
                return run.Starts[s];
            }

            RunSegment segment = await SegmentAsync(run, s, cancellationToken).ConfigureAwait(false);
            int first = 0;
            int last = segment.Count;
            while (first < last)
            {
                int mid = (first + last) >>> 1;
                int order = CompareKey(KeyAt(segment, mid), key);
                if (order == 0)
                {
                    order = RowAt(run, segment, mid).CompareTo(row);
                }

                if (order < 0)
                {
                    first = mid + 1;
                }
                else
                {
                    last = mid;
                }
            }

            if (first < segment.Count)
            {
                return run.Starts[s] + first;
            }
        }

        return run.Count;
    }

    /// <summary>The key and row at a position of a run, for a selection probe.</summary>
    private async ValueTask<(FilterLiteral Key, long Row)> EntryAsync(
        Run run, long position, CancellationToken cancellationToken)
    {
        int s = SegmentOf(run, position);
        RunSegment segment = await SegmentAsync(run, s, cancellationToken).ConfigureAwait(false);
        int at = (int)(position - run.Starts[s]);
        ReadOnlySpan<byte> key = KeyAt(segment, at);
        FilterLiteral literal = _layout.Shape == KeyShape.Bytes ? FilterLiteral.From(key) : Literal(key);
        return (literal, RowAt(run, segment, at));
    }

    /// <summary>Makes <paramref name="position"/> the run's current entry, loading its segment.</summary>
    private async ValueTask MoveAsync(Run run, long position, CancellationToken cancellationToken)
    {
        if (run.Current is null || position < run.CurrentStart || position >= run.CurrentStart + run.Current.Count)
        {
            int s = SegmentOf(run, position);
            run.Current = await SegmentAsync(run, s, cancellationToken).ConfigureAwait(false);
            run.CurrentStart = run.Starts[s];
        }

        run.Position = position;
    }

    private static int SegmentOf(Run run, long position)
    {
        long[] starts = run.Starts;
        int low = 0;
        int high = starts.Length - 2;
        while (low < high)
        {
            int mid = (low + high + 1) >>> 1;
            if (starts[mid] <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        // Empty segments share a start with their successor; the entry is in the last of them.
        while (low + 1 < starts.Length - 1 && starts[low + 1] <= position)
        {
            low++;
        }

        return low;
    }

    /// <summary>A decoded segment of a run: the run's own last one, the file's cache, or a read.</summary>
    private ValueTask<RunSegment> SegmentAsync(Run run, int index, CancellationToken cancellationToken)
    {
        if (run.ProbeIndex == index && run.Probe is not null)
        {
            return new ValueTask<RunSegment>(run.Probe);
        }

        IndexSegment keys = run.Meta.Payload[index * Stride];
        if (_file.RunCache.TryGet(keys.Offset, out RunSegment cached))
        {
            run.Probe = cached;
            run.ProbeIndex = index;
            return new ValueTask<RunSegment>(cached);
        }

        return LoadAsync(run, index, cancellationToken);
    }

    private async ValueTask<RunSegment> LoadAsync(Run run, int index, CancellationToken cancellationToken)
    {
        IndexSegment keys = run.Meta.Payload[index * Stride];
        int entries = (int)run.Segments[index].Entries;
        RunSegment decoded;
        using (SegmentRequestSet requests = new SegmentRequestSet(2))
        {
            // A postings run's offsets and blocks are the pruner's; a cursor reads only its keys.
            int keySlot = requests.Add(new SegmentSpec(keys.Offset, keys.Length, keys.AlignmentExponent, 0, 0));
            int rowSlot = -1;
            if (HasRows)
            {
                IndexSegment rows = run.Meta.Payload[(index * Stride) + 1];
                rowSlot = requests.Add(new SegmentSpec(rows.Offset, rows.Length, rows.AlignmentExponent, 0, 0));
            }

            await _file.IndexSource.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            Diagnostics.VortexEventSource.RunsRead(requests.Count);
            using ScanContext context = _file.CreateIndexContext();
            decoded = Decode(
                context,
                requests.GetBuffer(keySlot),
                rowSlot < 0 ? default : requests.GetBuffer(rowSlot),
                entries,
                run.RowLimit);
        }

        if (_file.ReadOptions.VerifyStatistics)
        {
            Verify(decoded, run.Segments[index]);
        }

        decoded = _file.RunCache.Add(keys.Offset, decoded);
        run.Probe = decoded;
        run.ProbeIndex = index;
        return decoded;
    }

    private RunSegment Decode(ScanContext context, VortexBuffer keyBlob, VortexBuffer rowBlob, int entries, long rowLimit)
    {
        DTypeArena types = new DTypeArena();
        DType keyType = _layout.Shape != KeyShape.Bytes
            ? types.Primitive(_layout.PType, Nullability.NonNullable)
            : _storage.Kind == DTypeKind.Utf8
                ? types.Utf8(Nullability.NonNullable)
                : types.Binary(Nullability.NonNullable);
        DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);

        CanonicalNode keyNode = context.Canonical.GetNode(DecodeRoot(context, keyBlob, keyType, entries));
        byte[] keys;
        int[]? offsets = null;
        if (_layout.Shape == KeyShape.Bytes)
        {
            if (keyNode.Kind != CanonicalKind.VarBinView || keyNode.Length != entries)
            {
                throw Malformed("its keys do not decode to one string per entry");
            }

            offsets = new int[entries + 1];
            long total = 0;
            for (int i = 0; i < entries; i++)
            {
                total += LiteralReader.ViewAt(keyNode, i).Length;
                offsets[i + 1] = checked((int)total);
            }

            keys = new byte[total];
            for (int i = 0; i < entries; i++)
            {
                LiteralReader.ViewAt(keyNode, i).CopyTo(keys.AsSpan(offsets[i]));
            }
        }
        else
        {
            if (keyNode.Kind != CanonicalKind.Primitive || keyNode.PType != _layout.PType || keyNode.Length != entries)
            {
                throw Malformed("its keys do not decode to one value of the column's type per entry");
            }

            keys = keyNode.Values.Span[..(entries * _layout.Width)].ToArray();
        }

        if (!HasRows)
        {
            return new RunSegment(keys, offsets, null, entries);
        }

        context.ResetBatch();
        CanonicalNode rowNode = context.Canonical.GetNode(DecodeRoot(context, rowBlob, u32, entries));
        if (rowNode.Kind != CanonicalKind.Primitive || rowNode.PType != PType.U32 || rowNode.Length != entries)
        {
            throw Malformed("its rows do not decode to one u32 per entry");
        }

        uint[] rows = rowNode.Values.Cast<uint>()[..entries].ToArray();
        foreach (uint row in rows)
        {
            if (row >= rowLimit)
            {
                throw Malformed($"it names row {row} of a run of {rowLimit}");
            }
        }

        return new RunSegment(keys, offsets, rows, entries);
    }

    /// <summary>
    /// The Class II checks <see cref="VortexReadOptions.VerifyStatistics"/> asks for
    /// (docs/12-index-reads.md §13): a segment's entries in `(key, row)` order — keys strictly
    /// ascending in a run without rows, where a key is unique — and every key inside the bounds
    /// the directory states for the segment. Without the option a lie gives a wrong walk and never
    /// a fault; with it, the walk refuses to go on.
    /// </summary>
    private void Verify(RunSegment segment, KeySegment declared)
    {
        for (int i = 0; i < segment.Count; i++)
        {
            ReadOnlySpan<byte> key = KeyAt(segment, i);
            if (_layout.Compare(key, declared.Min) < 0 || _layout.Compare(key, declared.Max) > 0)
            {
                throw Lie($"entry {i} lies outside the bounds the directory states for its segment");
            }

            if (i == 0)
            {
                continue;
            }

            int order = _layout.Compare(KeyAt(segment, i - 1), key);
            bool ordered = order < 0
                || (order == 0 && segment.Rows is { } rows && rows[i - 1] < rows[i]);
            if (!ordered)
            {
                throw Lie($"entries {i - 1} and {i} are out of order");
            }
        }
    }

    private static VortexFormatException Lie(string what) =>
        new VortexFormatException($"A key index run segment does not hold what it claims: {what}.");

    /// <summary>Payload arrays per segment of this source's runs.</summary>
    private int Stride => HasRows ? KeyRunOptions.SortedStride : KeyRunOptions.PostingsStride;

    private static int DecodeRoot(ScanContext context, VortexBuffer blob, DType dtype, int length)
    {
        context.Decode.LoadBlob(blob);
        ArrayNode root = context.Nodes.Root;
        return context.Decode.DecodeRoot(in root, dtype, length);
    }

    private static VortexFormatException Malformed(string what) =>
        new VortexFormatException($"A key index run segment is malformed: {what}.");

    // ------------------------------------------------------------------------------ keys

    private ReadOnlySpan<byte> CurrentKey(Run run) =>
        KeyAt(run.Current!, (int)(run.Position - run.CurrentStart));

    private static long CurrentRow(Run run) =>
        RowAt(run, run.Current!, (int)(run.Position - run.CurrentStart));

    /// <summary>An entry's file row; for keys without rows, the run's ordinal, which orders them.</summary>
    private static long RowAt(Run run, RunSegment segment, int index) =>
        segment.Rows is { } rows ? run.FirstRow + rows[index] : run.Ordinal;

    private ReadOnlySpan<byte> KeyAt(RunSegment segment, int index) =>
        segment.Offsets is { } offsets
            ? segment.Keys.AsSpan(offsets[index], offsets[index + 1] - offsets[index])
            : segment.Keys.AsSpan(index * _layout.Width, _layout.Width);

    private static byte[] FirstMin(Run run)
    {
        foreach (KeySegment segment in run.Segments)
        {
            if (segment.Entries > 0)
            {
                return segment.Min;
            }
        }

        return [];
    }

    private static byte[] LastMax(Run run)
    {
        for (int s = run.Segments.Length - 1; s >= 0; s--)
        {
            if (run.Segments[s].Entries > 0)
            {
                return run.Segments[s].Max;
            }
        }

        return [];
    }

    /// <summary>Orders a key's bytes against a literal of the column's domain, in the total order.</summary>
    private int CompareKey(ReadOnlySpan<byte> entry, FilterLiteral key) =>
        _layout.Shape == KeyShape.Bytes
            ? Math.Sign(entry.SequenceCompareTo(key.BytesValue))
            : KeyOrder.Total(Literal(entry), key);

    /// <summary>A fixed-width key as a literal of the column's domain.</summary>
    private FilterLiteral Literal(ReadOnlySpan<byte> key) => LiteralOf(_layout, key);

    /// <summary>A fixed-width key of <paramref name="layout"/> as a literal of its domain.</summary>
    private static FilterLiteral LiteralOf(KeyLayout layout, ReadOnlySpan<byte> key) => layout.Shape switch
    {
        KeyShape.Signed => FilterLiteral.From(layout.Width switch
        {
            1 => (sbyte)key[0],
            2 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(key),
            4 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(key),
            _ => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(key),
        }),
        KeyShape.Unsigned => FilterLiteral.From(layout.Width switch
        {
            1 => key[0],
            2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(key),
            4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(key),
            _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(key),
        }),
        _ => FilterLiteral.From(layout.Width switch
        {
            2 => (double)System.Buffers.Binary.BinaryPrimitives.ReadHalfLittleEndian(key),
            4 => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(key),
            _ => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(key),
        }),
    };

    /// <summary>One run: its directory record, its segment table, and the merge's position in it.</summary>
    private sealed class Run(IndexRun meta, KeySegment[] segments, long[] starts, long firstRow, long rowLimit, int ordinal)
    {
        /// <summary>The run's place among the source's: the row a key without rows is ordered by.</summary>
        internal int Ordinal { get; } = ordinal;

        internal IndexRun Meta { get; } = meta;

        internal KeySegment[] Segments { get; } = segments;

        /// <summary>Each segment's first position, and the run's count last.</summary>
        internal long[] Starts { get; } = starts;

        internal long FirstRow { get; } = firstRow;

        /// <summary>One past the last row a relative row may name.</summary>
        internal long RowLimit { get; } = rowLimit;

        internal long Count => Starts[^1];

        internal long Position { get; set; }

        internal RunSegment? Current { get; set; }

        internal long CurrentStart { get; set; }

        internal RunSegment? Probe { get; set; }

        internal int ProbeIndex { get; set; } = -1;
    }
}
