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
/// <remarks>
/// Each run is in key order on its own, so the union is walked by a heap of run positions: a
/// min-heap forward, a max-heap backward, and a step against the heap's direction re-seeks from the
/// current entry. Every positioning reduces to one primitive -- the first entry at or after a
/// <c>(key, row)</c> pair -- with a row before or after every row of the key standing for the open
/// ends; rows are unique across runs, so that pair orders the entries totally and a rank is exact.
/// Keys are ordered the way the writer ranked them, so <c>-0.0</c> and <c>+0.0</c> are two keys
/// here where a sorted column holds them as one. Only the segments that can hold the answer are
/// read: a run whose stated range excludes the key costs nothing. A run of keys without rows -- a
/// chunk's distinct keys, or a dictionary's values -- merges the same way, the run's ordinal
/// standing in for the row; such a source gives no rows, and its entries are not the column's
/// distinct keys, since a key present in two chunks is two entries. One run that does not make
/// sense refuses the source whole, because a cursor that skipped it would return a wrong walk.
/// </remarks>
internal sealed partial class SortedRunsSource : KeySource
{
    /// <summary>What <see cref="Gallop"/> answers when the key's entries run on past the decoded segment.</summary>
    private const long Unknown = long.MinValue;

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
                || !KeyIndexPruner.TryResolve(file.DType, entry.ColumnPath, out string resolved, out DType dtype)
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
    /// A pruner may use a partial index, a source may not: a block no run covers is simply left
    /// live for the pruner, but a walk that misses it misses its keys, and a count over the walk is
    /// wrong. An append whose builder was abandoned, and a dictionary probe over a column some of
    /// whose chunks are not dictionaries, both leave blocks uncovered.
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
            return $"a {dtype.Kind} column has no key order a cursor can walk";
        }

        while (storage.Kind == DTypeKind.Extension)
        {
            storage = storage.StorageType;
        }

        return null;
    }

    /// <summary>
    /// Opens the sorted runs of a composite key: keys that are the row encoding of the tuple, bytes
    /// ordered bytewise.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="paths">The key's columns, in key order.</param>
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

        DType schema = file.DType;
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
            if (!PathOf(schema, columns[i], paths[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a field-index path spells <paramref name="path"/>: one top-level name, dots and all,
    /// or the names of the structs it descends through, joined by dots.
    /// </summary>
    private static bool PathOf(DType schema, uint[] fields, string path)
    {
        if (fields.Length == 1)
        {
            return fields[0] < (uint)schema.FieldCount
                && string.Equals(schema.GetFieldName((int)fields[0]), path, StringComparison.Ordinal);
        }

        DType dtype = schema;
        int at = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            while (dtype.Kind == DTypeKind.Extension)
            {
                dtype = dtype.StorageType;
            }

            if (dtype.Kind != DTypeKind.Struct || fields[i] >= (uint)dtype.FieldCount)
            {
                return false;
            }

            string name = dtype.GetFieldName((int)fields[i]);
            int end = i + 1 < fields.Length ? path.IndexOf('.', at) : path.Length;
            if (end < 0 || !path.AsSpan(at, end - at).SequenceEqual(name))
            {
                return false;
            }

            at = end + 1;
            dtype = dtype.GetField((int)fields[i]);
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
            if (!FenceTable.TryOpen(meta, stride, layout, out FenceTable? table, out string? why))
            {
                return (null, $"run {i}: {why}");
            }

            long firstRow = checked((long)(meta.FirstBlock * entry.BlockLength));
            long span = checked((long)(meta.BlockCount * entry.BlockLength));
            long limit = Math.Min(span, file.RowCount - firstRow);
            if (limit < 0 || table!.Entries > limit)
            {
                return (null, $"run {i} holds more entries than its blocks have rows");
            }

            runs[i] = new Run(meta, table, firstRow, limit, i);
        }

        return (new SortedRunsSource(file, layout, storage, kind, runs, source), null);
    }

    // ------------------------------------------------------------------------------ positioning

    internal override async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekMode op, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case SeekMode.After:
                return await ForwardAsync(key, long.MaxValue, cancellationToken).ConfigureAwait(false);
            case SeekMode.AtOrBefore:
                return await BackwardAsync(key, long.MaxValue, cancellationToken).ConfigureAwait(false);
            case SeekMode.Before:
                return await BackwardAsync(key, long.MinValue, cancellationToken).ConfigureAwait(false);
            case SeekMode.AtOrAfter:
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

    internal override ValueTask<bool> PreviousAsync(CancellationToken cancellationToken) =>
        _direction < 0 ? StepAsync(cancellationToken) : FlipAsync(forward: false, cancellationToken);

    internal override ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken) =>
        _direction > 0 ? PastKeyAsync(forward: true, cancellationToken) : ForwardAsync(Key, long.MaxValue, cancellationToken);

    internal override ValueTask<bool> PreviousKeyAsync(CancellationToken cancellationToken) =>
        _direction < 0 ? PastKeyAsync(forward: false, cancellationToken) : BackwardAsync(Key, long.MinValue, cancellationToken);

    /// <summary>
    /// Moves every run past the current key in the heap's direction, the top first: a run that
    /// holds the key gallops over its entries of it in the segment it has decoded, and is sought
    /// past them the long way only when they run on into another segment.
    /// </summary>
    /// <remarks>
    /// A key held by k entries of a run costs O(log k) comparisons there, and a run that does not
    /// hold it is not touched, where seeking every run costs a bisection in each whatever k is.
    /// The key is read from the segment it was found in, held here: a segment owns its arrays, so
    /// moving the run off it leaves the key's bytes whole.
    /// </remarks>
    private async ValueTask<bool> PastKeyAsync(bool forward, CancellationToken cancellationToken)
    {
        Run top = _runs[_heap[0]];
        RunSegment keySegment = top.Current!;
        int keyIndex = (int)(top.Position - top.CurrentStart);
        while (PastKeyInSegments(keySegment, keyIndex, forward) is { } run)
        {
            // The entries reach the segment's edge: the run goes on to the next segment, which it
            // would load anyway, and gallops there; a key that fills that one too is sought the
            // long way, which is the only step that copies it.
            long edge = forward ? run.CurrentStart + run.Current!.Count : run.CurrentStart - 1;
            long past = edge;
            if (edge >= 0 && edge < run.Count)
            {
                await MoveAsync(run, edge, cancellationToken).ConfigureAwait(false);
                past = CurrentKey(run).SequenceEqual(KeyAt(keySegment, keyIndex))
                    ? Gallop(run, KeyAt(keySegment, keyIndex), forward)
                    : edge;
            }

            if (past == Unknown)
            {
                FilterLiteral key = LiteralAt(keySegment, keyIndex);
                past = forward
                    ? await FirstAtOrAfterAsync(run, key, long.MaxValue, cancellationToken).ConfigureAwait(false)
                    : await FirstAtOrAfterAsync(run, key, long.MinValue, cancellationToken).ConfigureAwait(false) - 1;
            }

            if (past < 0 || past >= run.Count)
            {
                _heap[0] = _heap[--_heapSize];
            }
            else
            {
                await MoveAsync(run, past, cancellationToken).ConfigureAwait(false);
            }

            SiftDown(0);
        }

        return _heapSize > 0;
    }

    /// <summary>
    /// Moves the heap's runs past the key of entry <paramref name="keyIndex"/> of
    /// <paramref name="keySegment"/> while their entries of it end in the segment each has decoded.
    /// </summary>
    /// <returns>
    /// The top run when its entries of the key reach its segment's edge, which takes a read; null
    /// once the top is past the key or no run is left.
    /// </returns>
    /// <remarks>
    /// Two keys are one when their bytes are: the total order is one-to-one on them, which is what
    /// lets the comparison be an equality.
    /// </remarks>
    private Run? PastKeyInSegments(RunSegment keySegment, int keyIndex, bool forward)
    {
        ReadOnlySpan<byte> key = KeyAt(keySegment, keyIndex);
        while (_heapSize > 0)
        {
            Run run = _runs[_heap[0]];
            if (!CurrentKey(run).SequenceEqual(key))
            {
                return null;
            }

            long past = Gallop(run, key, forward);
            if (past == Unknown)
            {
                return run;
            }

            // A gallop lands within the decoded segment, so the move is the position alone.
            run.Position = past;
            SiftDown(0);
        }

        return null;
    }

    private FilterLiteral LiteralAt(RunSegment segment, int index)
    {
        ReadOnlySpan<byte> key = KeyAt(segment, index);
        return _layout.Shape == KeyShape.Bytes ? FilterLiteral.From(key) : Literal(key);
    }

    /// <summary>
    /// The first position of <paramref name="run"/> past its entries of <paramref name="key"/>,
    /// going forward or back, from those of its decoded segment; <see cref="Unknown"/> when they
    /// reach that segment's end in the direction gone.
    /// </summary>
    private long Gallop(Run run, ReadOnlySpan<byte> key, bool forward)
    {
        RunSegment segment = run.Current!;
        int at = (int)(run.Position - run.CurrentStart);
        int edge = forward ? segment.Count - 1 : 0;
        int past = segment.Offsets is { } offsets
            ? KeyGallop.Past(new ByteKeys(segment.Keys, offsets, key), at, edge, forward)
            : KeyGallop.Past(new FixedKeys(segment.Keys, _layout.Width, key), at, edge, forward);
        return past < 0 ? Unknown : run.CurrentStart + past;
    }

    internal override ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        SumAsync(new LiteralKey(this, key), long.MinValue, cancellationToken);

    internal override ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        SumAsync(new LiteralKey(this, key), long.MaxValue, cancellationToken);

    internal override ValueTask<long> RankOfAsync(KeySource other, bool upper, CancellationToken cancellationToken)
    {
        long row = upper ? long.MaxValue : long.MinValue;
        return _layout.Shape == KeyShape.Bytes
            ? SumAsync(new LentKey(other), row, cancellationToken)
            : SumAsync(new LiteralKey(this, other.Key), row, cancellationToken);
    }

    private async ValueTask<long> SumAsync<TKey>(TKey key, long row, CancellationToken cancellationToken)
        where TKey : struct, IKeyOrder
    {
        long rank = 0;
        for (int r = 0; r < _runs.Length; r++)
        {
            rank += await FirstAtOrAfterAsync(_runs[r], key, row, cancellationToken).ConfigureAwait(false);
        }

        return rank;
    }

    /// <remarks>
    /// A selection over every run at once, each run keeping the positions still candidates. A
    /// round takes a pivot among them and bounds it in every run: its rank rules out the candidates
    /// on its wrong side. The pivot is taken two ways in turn.
    /// <list type="bullet">
    /// <item>Where the rank would fall in the widest run, were its candidates spread like all the
    /// others: over runs that interleave, a few rounds close in on the entry.</item>
    /// <item>The middle below which half the candidates lie, each run standing on its own middle
    /// and counting for its candidates: a quarter of them at least are ruled out, whatever the
    /// keys, so the rounds are O(log N).</item>
    /// </list>
    /// Each round costs a bound per run, where a bisection in each run over bounds in all the others
    /// costs R times as many; late rounds bound in the segment each run stands in. The round that
    /// finds the entry leaves the runs where its bounds stand.
    /// </remarks>
    internal override async ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken)
    {
        if (rank < 0 || rank >= Entries)
        {
            Invalidate();
            return false;
        }

        foreach (Run run in _runs)
        {
            run.Low = 0;
            run.High = run.Count;
        }

        for (int round = 0; ; round++)
        {
            Reset(1);
            long ruledOut = 0;
            long candidates = 0;
            Run? widest = null;
            foreach (Run run in _runs)
            {
                long width = run.High - run.Low;
                ruledOut += run.Low;
                candidates += width;
                widest = width > 0 && (widest is null || width > widest.High - widest.Low) ? run : widest;
            }

            if (widest is null)
            {
                // Runs that list one row twice leave a rank no entry has.
                return false;
            }

            Run pivot = widest;
            if ((round & 1) == 0)
            {
                long width = widest.High - widest.Low;
                long offset = (long)((double)(rank - ruledOut) * width / candidates);
                await MoveAsync(widest, widest.Low + Math.Clamp(offset, 0, width - 1), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The heap orders the middles, and gives them back smallest first until half the
                // candidates are counted.
                for (int r = 0; r < _runs.Length; r++)
                {
                    Run run = _runs[r];
                    if (run.Low < run.High)
                    {
                        await PlaceAsync(r, run.Low + ((run.High - run.Low) >> 1), cancellationToken).ConfigureAwait(false);
                    }
                }

                long counted = 0;
                do
                {
                    pivot = _runs[_heap[0]];
                    counted += pivot.High - pivot.Low;
                    _heap[0] = _heap[--_heapSize];
                    SiftDown(0);
                }
                while (2 * counted < candidates);
            }

            // The pivot's bound in every run: in the segment the run stands in when its candidates
            // all lie there, which the late rounds come to, and the long way otherwise.
            RunSegment keySegment = pivot.Current!;
            int keyIndex = (int)(pivot.Position - pivot.CurrentStart);
            long row = CurrentRow(pivot);
            long before = 0;
            foreach (Run run in _runs)
            {
                if (run == pivot)
                {
                    run.Cut = run.Position;
                }
                else if (run.Low == run.High)
                {
                    run.Cut = run.Low;
                }
                else if (run.Current is { } segment && run.Low >= run.CurrentStart && run.High <= run.CurrentStart + segment.Count)
                {
                    run.Cut = CutInSegment(run, KeyAt(keySegment, keyIndex), row);
                }
                else
                {
                    run.Cut = Math.Clamp(
                        await FirstAtOrAfterAsync(run, new EntryKey(this, keySegment, keyIndex), row, cancellationToken).ConfigureAwait(false),
                        run.Low,
                        run.High);
                }

                before += run.Cut;
            }

            if (before == rank)
            {
                Reset(1);
                for (int r = 0; r < _runs.Length; r++)
                {
                    if (_runs[r].Cut < _runs[r].Count)
                    {
                        await PlaceAsync(r, _runs[r].Cut, cancellationToken).ConfigureAwait(false);
                    }
                }

                return true;
            }

            foreach (Run run in _runs)
            {
                if (before < rank)
                {
                    run.Low = Math.Max(run.Low, run.Cut);
                }
                else
                {
                    run.High = Math.Min(run.High, run.Cut);
                }
            }

            if (before < rank)
            {
                pivot.Low = pivot.Cut + 1;
            }
        }
    }

    /// <summary>
    /// The first position of <paramref name="run"/> at or after <c>(key, row)</c>, found among
    /// its candidates, which all lie in the segment it stands in.
    /// </summary>
    private long CutInSegment(Run run, ReadOnlySpan<byte> key, long row)
    {
        RunSegment segment = run.Current!;
        int first = (int)(run.Low - run.CurrentStart);
        int last = (int)(run.High - run.CurrentStart);
        while (first < last)
        {
            int mid = (first + last) >>> 1;
            int order = _layout.Compare(KeyAt(segment, mid), key);
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

        return run.CurrentStart + first;
    }

    internal override async ValueTask<long> CountAtKeyAsync(CancellationToken cancellationToken)
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
    private ValueTask<long> FirstAtOrAfterAsync(
        Run run, FilterLiteral key, long row, CancellationToken cancellationToken) =>
        FirstAtOrAfterAsync(run, new LiteralKey(this, key), row, cancellationToken);

    /// <summary>
    /// The first position of <paramref name="run"/> whose <c>(key, row)</c> is at or after the
    /// given pair, the key however it is held; the run's count when there is none.
    /// </summary>
    private async ValueTask<long> FirstAtOrAfterAsync<TKey>(
        Run run, TKey key, long row, CancellationToken cancellationToken)
        where TKey : struct, IKeyOrder
    {
        FenceTable table = run.Table;
        if (run.Count == 0)
        {
            return 0;
        }

        // The run's own range, from its options alone: most runs are excluded or included here.
        if (key.Order(table.LastMax) < 0)
        {
            return run.Count;
        }

        if (key.Order(table.FirstMin) > 0)
        {
            return 0;
        }

        // The first segment whose last key does not come before the key: a binary search over the
        // bounds in memory, or a descent through the fence pages when they are paged out.
        ISegmentReader source = _file.IndexSourceOf(run.Meta);
        long low = await table.LowerBoundAsync(source, new MaxProbe<TKey>(key), cancellationToken).ConfigureAwait(false);
        for (long s = low; s < table.SegmentCount; s++)
        {
            Fence fence = await table.GetAsync(source, s, cancellationToken).ConfigureAwait(false);
            if (fence.Bounds.Entries == 0)
            {
                continue;
            }

            if (s > low && key.Order(fence.Bounds.Min) > 0)
            {
                return fence.Start;
            }

            RunSegment segment = await SegmentAsync(run, fence, cancellationToken).ConfigureAwait(false);
            int first = 0;
            int last = segment.Count;
            while (first < last)
            {
                int mid = (first + last) >>> 1;
                int order = key.Order(KeyAt(segment, mid));
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
                return fence.Start + first;
            }
        }

        return run.Count;
    }

    /// <summary>Makes <paramref name="position"/> the run's current entry, loading its segment.</summary>
    private async ValueTask MoveAsync(Run run, long position, CancellationToken cancellationToken)
    {
        if (run.Current is null || position < run.CurrentStart || position >= run.CurrentStart + run.Current.Count)
        {
            Fence fence = await run.Table.OfPositionAsync(_file.IndexSourceOf(run.Meta), position, cancellationToken).ConfigureAwait(false);
            run.Current = await SegmentAsync(run, fence, cancellationToken).ConfigureAwait(false);
            run.CurrentStart = fence.Start;
        }

        run.Position = position;
    }

    /// <summary>A decoded segment of a run: the run's own last one, the file's cache, or a read.</summary>
    private ValueTask<RunSegment> SegmentAsync(Run run, Fence fence, CancellationToken cancellationToken)
    {
        if (run.ProbeIndex == fence.Index && run.Probe is not null)
        {
            return new ValueTask<RunSegment>(run.Probe);
        }

        IndexSegment keys = fence.Regions[0];
        if (_file.RunCache.TryGet(new RunSegmentKey(run.Meta.Origin, keys.Offset), out RunSegment cached))
        {
            run.Probe = cached;
            run.ProbeIndex = fence.Index;
            return new ValueTask<RunSegment>(cached);
        }

        return LoadAsync(run, fence, cancellationToken);
    }

    private async ValueTask<RunSegment> LoadAsync(Run run, Fence fence, CancellationToken cancellationToken)
    {
        IndexSegment keys = fence.Regions[0];
        if (fence.Bounds.Entries > int.MaxValue)
        {
            throw Malformed("a segment holds more entries than one array can");
        }

        int entries = (int)fence.Bounds.Entries;
        RunSegment decoded;
        using (SegmentRequestSet requests = new SegmentRequestSet(2))
        {
            // A postings run's offsets and blocks are the pruner's; a cursor reads only its keys.
            int keySlot = requests.Add(new SegmentSpec(keys.Offset, keys.Length, keys.AlignmentExponent, 0, 0));
            int rowSlot = -1;
            if (HasRows)
            {
                IndexSegment rows = fence.Regions[1];
                rowSlot = requests.Add(new SegmentSpec(rows.Offset, rows.Length, rows.AlignmentExponent, 0, 0));
            }

            await _file.IndexSourceOf(run.Meta).ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            Diagnostics.VortexEventSource.RunsRead(requests.Count);

            // A walk over torn bytes would be wrong and not merely slow, so the source refuses, as
            // it does for a run that decodes to the wrong shape.
            if (!keys.Holds(requests.GetBuffer(keySlot).Span)
                || (rowSlot >= 0 && !fence.Regions[1].Holds(requests.GetBuffer(rowSlot).Span)))
            {
                throw Malformed("a segment's bytes do not match its checksum: it is torn or corrupt");
            }

            using ScanContext context = _file.CreateIndexContext(run.Meta);
            decoded = Decode(
                context,
                requests.GetBuffer(keySlot),
                rowSlot < 0 ? default : requests.GetBuffer(rowSlot),
                entries,
                run.RowLimit,
                rowSlot >= 0 && run.Table.WideRows);
        }

        if (_file.ReadOptions.VerifyStatistics)
        {
            Verify(decoded, fence.Bounds);
        }

        decoded = _file.RunCache.Add(new RunSegmentKey(run.Meta.Origin, keys.Offset), decoded);
        run.Probe = decoded;
        run.ProbeIndex = fence.Index;
        return decoded;
    }

    private RunSegment Decode(
        ScanContext context, VortexBuffer keyBlob, VortexBuffer rowBlob, int entries, long rowLimit, bool wideRows)
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
        if (wideRows)
        {
            // A run spanning more rows than a u32 can name writes its rows at 64 bits.
            DType u64 = types.Primitive(PType.U64, Nullability.NonNullable);
            CanonicalNode wideNode = context.Canonical.GetNode(DecodeRoot(context, rowBlob, u64, entries));
            if (wideNode.Kind != CanonicalKind.Primitive || wideNode.PType != PType.U64 || wideNode.Length != entries)
            {
                throw Malformed("its rows do not decode to one u64 per entry");
            }

            ulong[] wide = wideNode.Values.Cast<ulong>()[..entries].ToArray();
            foreach (ulong row in wide)
            {
                if (row >= (ulong)rowLimit)
                {
                    throw Malformed($"it names row {row} of a run of {rowLimit}");
                }
            }

            return new RunSegment(keys, offsets, null, entries, wide);
        }

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
    /// The checks <see cref="VortexReadOptions.VerifyStatistics"/> asks for: a segment's entries in
    /// <c>(key, row)</c> order — keys strictly ascending in a run without rows, where a key is
    /// unique — and every key inside the bounds the directory states for the segment. Without the
    /// option a lie gives a wrong walk and never a fault; with it, the walk refuses to go on.
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
                || (order == 0 && segment.HasRows && segment.RowAt(i - 1) < segment.RowAt(i));
            if (!ordered)
            {
                throw Lie($"entries {i - 1} and {i} are out of order");
            }
        }
    }

    private static VortexFormatException Lie(string what) =>
        new VortexFormatException($"A key index run segment does not hold what it claims: {what}.");

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
        segment.HasRows ? run.FirstRow + segment.RowAt(index) : run.Ordinal;

    private ReadOnlySpan<byte> KeyAt(RunSegment segment, int index) =>
        segment.Offsets is { } offsets
            ? segment.Keys.AsSpan(offsets[index], offsets[index + 1] - offsets[index])
            : segment.Keys.AsSpan(index * _layout.Width, _layout.Width);

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
    private sealed class Run(IndexRun meta, FenceTable table, long firstRow, long rowLimit, int ordinal)
    {
        /// <summary>The run's place among the source's: the row a key without rows is ordered by.</summary>
        internal int Ordinal { get; } = ordinal;

        internal IndexRun Meta { get; } = meta;

        /// <summary>The run's segments, held in memory or reached through fence pages.</summary>
        internal FenceTable Table { get; } = table;

        internal long FirstRow { get; } = firstRow;

        /// <summary>One past the last row a relative row may name.</summary>
        internal long RowLimit { get; } = rowLimit;

        internal long Count => Table.Entries;

        internal long Position { get; set; }

        internal RunSegment? Current { get; set; }

        internal long CurrentStart { get; set; }

        internal RunSegment? Probe { get; set; }

        internal long ProbeIndex { get; set; } = -1;

        /// <summary>A selection's first candidate.</summary>
        internal long Low { get; set; }

        /// <summary>One past a selection's last candidate.</summary>
        internal long High { get; set; }

        /// <summary>A selection's bound for its pivot: the entries before it.</summary>
        internal long Cut { get; set; }
    }

    /// <summary>A descent's comparison: a fence whose last key comes before the key sought.</summary>
    private readonly struct MaxProbe<TKey>(TKey key) : IFenceProbe
        where TKey : struct, IKeyOrder
    {
        public bool Below(ReadOnlySpan<byte> max) => key.Order(max) < 0;
    }

    /// <summary>A key sought, ordered against the keys of a run.</summary>
    private interface IKeyOrder
    {
        /// <summary>The sign of <paramref name="entry"/> less the key sought.</summary>
        int Order(ReadOnlySpan<byte> entry);
    }

    /// <summary>A key sought as a literal of the column's domain, as a seek names it.</summary>
    private readonly struct LiteralKey(SortedRunsSource source, FilterLiteral key) : IKeyOrder
    {
        public int Order(ReadOnlySpan<byte> entry) => source.CompareKey(entry, key);
    }

    /// <summary>A key sought as an entry of a decoded segment, compared where it lies.</summary>
    private readonly struct EntryKey(SortedRunsSource source, RunSegment segment, int index) : IKeyOrder
    {
        public int Order(ReadOnlySpan<byte> entry) => source._layout.Compare(entry, source.KeyAt(segment, index));
    }

    /// <summary>A byte key sought where another source lends it, on its current entry.</summary>
    private readonly struct LentKey(KeySource lender) : IKeyOrder
    {
        public int Order(ReadOnlySpan<byte> entry) => Math.Sign(entry.SequenceCompareTo(lender.KeyBytes));
    }

    /// <summary>A segment's fixed-width keys laid end to end, against one key's bytes.</summary>
    private readonly ref struct FixedKeys : KeyGallop.IKeyed
    {
        private readonly ReadOnlySpan<byte> _keys;
        private readonly ReadOnlySpan<byte> _key;
        private readonly int _width;

        internal FixedKeys(ReadOnlySpan<byte> keys, int width, ReadOnlySpan<byte> key)
        {
            _keys = keys;
            _width = width;
            _key = key;
        }

        public bool Holds(int index) => _keys.Slice(index * _width, _width).SequenceEqual(_key);
    }

    /// <summary>A segment's byte keys, delimited by their offsets, against one key's bytes.</summary>
    private readonly ref struct ByteKeys : KeyGallop.IKeyed
    {
        private readonly ReadOnlySpan<byte> _keys;
        private readonly ReadOnlySpan<int> _offsets;
        private readonly ReadOnlySpan<byte> _key;

        internal ByteKeys(ReadOnlySpan<byte> keys, ReadOnlySpan<int> offsets, ReadOnlySpan<byte> key)
        {
            _keys = keys;
            _offsets = offsets;
            _key = key;
        }

        public bool Holds(int index) => _keys[_offsets[index].._offsets[index + 1]].SequenceEqual(_key);
    }
}
