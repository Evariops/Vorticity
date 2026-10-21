using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// What a dataset scan skipped, and how: counters rather than timings, so that a caller can check
/// how few objects were read instead of believing a speed.
/// </summary>
internal sealed class DatasetScanCounters
{
    /// <summary>Leaf entries the walk reached.</summary>
    public long ObjectsConsidered { get; internal set; }

    /// <summary>Objects whose own summaries refuted the predicate, so nothing was opened.</summary>
    public long ObjectsSkipped { get; internal set; }

    /// <summary>Objects the scan went to read. <see cref="CacheHits"/> of them cost no open.</summary>
    public long ObjectsOpened { get; internal set; }

    /// <summary>Child pages a node's summaries refuted, each a whole subtree never read.</summary>
    public long SubtreesSkipped { get; internal set; }

    /// <summary>Rents the object cache answered without opening anything.</summary>
    public long CacheHits { get; internal set; }

    /// <summary>The objects an answer took from their entries, without opening them.</summary>
    public long ObjectsCounted { get; internal set; }

    /// <summary>
    /// The most data objects the scan read at once: one in the tree's order, two once a prefetch
    /// opens the next object ahead, and under <see cref="DatasetScanBuilder.InKeyOrder(string, bool)"/>
    /// the inputs the merge held open.
    /// </summary>
    public int Cursors { get; internal set; }
}

/// <summary>
/// Builds a scan over every object of one version of a dataset: the engine a
/// <see cref="DatasetScanSource"/> compiles the core's scans to. Each object is read by the core's
/// own file scan, carrying the filter, the projection, the rows and the options, and its batches
/// are counted from the object's first row among the dataset's.
/// </summary>
internal sealed class DatasetScanBuilder
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;
    private VortexExpr? _filter;
    private FieldMask? _mask;
    private bool _indexes = true;
    private bool _summaries = true;
    private long _from;
    private long _to = long.MaxValue;
    private bool _rowsSet;
    private long[]? _take;
    private string[]? _orderPaths;
    private bool _descending;
    private DatasetScanCounters? _metrics;
    private ScanCounters? _counters;
    private ScanOptions _options = ScanOptions.Default;
    private bool _keepEncodings;
    private bool _sinkDecodes;
    private bool _positionsUnread;

    internal DatasetScanBuilder(VortexDataset dataset, DatasetSnapshot version)
    {
        _dataset = dataset;
        _version = version;
    }

    /// <summary>Keeps the rows the predicate selects.</summary>
    public DatasetScanBuilder Where(VortexExpr filter)
    {
        _filter = Compute.FunctionFieldExpr.Ranges(filter);
        return this;
    }

    /// <summary>Reads only the columns of <paramref name="mask"/>, which is resolved against the dataset's schema and so fits every object.</summary>
    public DatasetScanBuilder Project(in FieldMask mask)
    {
        _mask = mask;
        return this;
    }

    /// <summary>
    /// Reads only the dataset's rows <c>[from, to)</c>, in the tree's order. Answered through the
    /// tree's row sums, so an object outside the range is neither opened nor counted and a subtree
    /// outside it is never read.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scan is key-ordered, or selects rows by index.</exception>
    public DatasetScanBuilder Rows(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        if (_orderPaths is not null || _take is not null)
        {
            throw new InvalidOperationException(
                "A scan selects rows by range, by index or by key order, one of them at a time.");
        }

        _from = from;
        _to = to;
        _rowsSet = true;
        return this;
    }

    /// <summary>
    /// Reads only the dataset's rows at <paramref name="rows"/>, sorted and deduplicated. An object
    /// that holds none of them is neither opened nor counted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A row is negative, or past the version's rows.</exception>
    /// <exception cref="InvalidOperationException">The scan is key-ordered, or selects a range.</exception>
    public DatasetScanBuilder Take(ReadOnlySpan<long> rows)
    {
        if (_orderPaths is not null || _rowsSet)
        {
            throw new InvalidOperationException(
                "A scan selects rows by range, by index or by key order, one of them at a time.");
        }

        long[] sorted = rows.ToArray();
        Array.Sort(sorted);
        int distinct = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i == 0 || sorted[i] != sorted[distinct - 1])
            {
                sorted[distinct++] = sorted[i];
            }
        }

        if (distinct > 0 && (sorted[0] < 0 || sorted[distinct - 1] >= _version.RowCount))
        {
            long wrong = sorted[0] < 0 ? sorted[0] : sorted[distinct - 1];
            throw new ArgumentOutOfRangeException(
                nameof(rows), wrong, $"Version {_version.Version} of the dataset holds {_version.RowCount} rows.");
        }

        _take = sorted.AsSpan(0, distinct).ToArray();
        _from = distinct == 0 ? 0 : _take[0];
        _to = distinct == 0 ? 0 : _take[^1] + 1;
        return this;
    }

    /// <summary>
    /// Delivers the rows in the key order of a column, across every object the scan reads, equal
    /// keys in the dataset's order. A batch holds a run of one object's rows, so objects whose keys
    /// interleave give batches as short as one row.
    /// </summary>
    /// <remarks>
    /// On the clustering key an object is opened only once it could hold the next row, so a consumer
    /// that stops after <c>k</c> rows never opens an object whose minimum lies past the k-th key; on
    /// any other column every object the summaries keep may be open at once. A row whose key is null
    /// comes last in both directions, and an object with no source for the column is refused when
    /// the merge reaches it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The scan selects rows by range or by index.</exception>
    public DatasetScanBuilder InKeyOrder(string path, bool descending = false) => InKeyOrder([path], descending);

    /// <summary>
    /// Delivers the rows in the order of a composite key, the tuple of the given columns in key
    /// order. A row whose tuple holds a null is in no run and is not delivered.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scan selects rows by range or by index.</exception>
    public DatasetScanBuilder InKeyOrder(IReadOnlyList<string> paths, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("A key has at least one column.", nameof(paths));
        }

        foreach (string path in paths)
        {
            _ = ClusteringKey.Resolve(_version.Schema.DType, path);
        }

        if (_rowsSet || _take is not null)
        {
            throw new InvalidOperationException(
                "A key-ordered scan walks the objects' key sources; it cannot also select rows by range or by index.");
        }

        _orderPaths = [.. paths];
        _descending = descending;
        return this;
    }

    /// <summary>Turns the per-file index chain on or off; the answer is the same either way.</summary>
    public DatasetScanBuilder WithIndexes(bool indexes)
    {
        _indexes = indexes;
        return this;
    }

    /// <summary>
    /// Turns pruning by the node and object summaries on or off. Off, every object is opened and
    /// every subtree read, and the answer must not change.
    /// </summary>
    public DatasetScanBuilder WithSummaries(bool summaries)
    {
        _summaries = summaries;
        return this;
    }

    /// <summary>Records what the walk skipped.</summary>
    public DatasetScanBuilder WithMetrics(DatasetScanCounters metrics)
    {
        _metrics = metrics;
        return this;
    }

    /// <summary>Adds what every object's own scan requests, decodes and delivers to <paramref name="counters"/>.</summary>
    public DatasetScanBuilder WithCounters(ScanCounters counters)
    {
        _counters = counters;
        return this;
    }

    /// <summary>
    /// Says that the variant columns of <paramref name="keys"/>, by name, are read only through those
    /// top-level keys, which each object's path map lets its scan read alone.
    /// </summary>
    internal DatasetScanBuilder WithVariantKeys(IReadOnlyDictionary<string, string[]>? keys)
    {
        _variantKeys = keys;
        return this;
    }

    private IReadOnlyDictionary<string, string[]>? _variantKeys;

    /// <summary>
    /// The states an aggregation would take in place of rows (<see cref="StateRequest"/>): an object its
    /// synopsis answers is read as a batch of states, and the slices at the window's edges as rows.
    /// </summary>
    internal DatasetScanBuilder WithStates(StateRequest? states)
    {
        _states = states;
        return this;
    }

    private StateRequest? _states;

    /// <summary>What the answers of the objects to the states asked share, worked out at the first object's.</summary>
    private SynopsisQuestion? _question;

    /// <summary>The question of <paramref name="request"/> under the scan's filter, worked out once for every object it asks.</summary>
    private SynopsisQuestion QuestionOf(StateRequest request, string instant) =>
        _question is { } known && ReferenceEquals(known.Request, request) && ReferenceEquals(known.Filter, _filter)
            ? known
            : _question = new SynopsisQuestion(request, _filter, instant, _version.Header.Identifiers);

    /// <summary>The question the answers of the scan's objects share, once one was asked; null before.</summary>
    internal SynopsisQuestion? Question => _question;

    /// <summary>The objects a plan of pieces walked, in their order, which the scan of a piece takes as they are; null to walk the tree.</summary>
    private PositionedObject[]? _planned;

    /// <summary>
    /// Has the scan, a piece of a pass on several lanes, take the objects the plan of its pieces walked, the pass's every
    /// one in their order, those its rows cover among them, rather than walk the tree to its rows again; and the question
    /// their answers share, which the plan worked out (<see cref="QuestionOf"/> takes it when it is the scan's own).
    /// </summary>
    internal DatasetScanBuilder WithPlanned(PositionedObject[] objects, SynopsisQuestion? question)
    {
        _planned = objects;
        _question ??= question;
        return this;
    }

    /// <summary>
    /// The splits each object's scan of one lane reads ahead of the one it decodes, over a store whose read is a round
    /// trip (<see cref="ScanSpec.ReadAhead"/>): what an aggregation's lane is granted, which an object's scan reads as a
    /// file's would.
    /// </summary>
    internal DatasetScanBuilder WithReadAhead(int splits)
    {
        _readAhead = splits;
        return this;
    }

    private int _readAhead;

    /// <summary>
    /// A lease of <paramref name="held"/>: for a scan that reads keys of variant columns, an object of level 0
    /// through the node's memo of it shredded in memory, which parses its keys once; the object itself otherwise.
    /// </summary>
    private async ValueTask<ObjectLease> RentAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        // Once a scan, the memo lets go of the objects that have left level 0 since it last looked.
        if (!_memoChecked)
        {
            _memoChecked = true;
            await _dataset.LevelZero.ForgetMovedAsync(_version, cancellationToken).ConfigureAwait(false);
        }

        return held.Level == 0 && _variantKeys is { Count: > 0 } keys
            ? await _dataset.LevelZero.RentAsync(_dataset, _version, held.Entry, keys, cancellationToken).ConfigureAwait(false)
            : await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
    }

    private bool _memoChecked;

    /// <summary>
    /// Runs every object's scan under <paramref name="options"/>: the batch cap, zone-map pruning,
    /// the degree of parallelism, the prefetch and compaction; <paramref name="keepEncodings"/> lets
    /// the decoders deliver dictionary and run-end columns encoded, <paramref name="sinkDecodes"/>
    /// says the consumer reads those forms itself, so that only the blocks it decodes count as decoded,
    /// and <paramref name="positionsUnread"/> that it reads no row's place, so that the rows an object
    /// deleted may leave its batches, when they are not compacted, by their selection alone.
    /// </summary>
    public DatasetScanBuilder WithOptions(ScanOptions options, bool keepEncodings, bool sinkDecodes = false, bool positionsUnread = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keepEncodings = keepEncodings;
        _sinkDecodes = sinkDecodes;
        _positionsUnread = positionsUnread;
        return this;
    }

    /// <summary>The batches of every object the scan did not skip, in key order, borrowed: each is valid until the next is asked for.</summary>
    /// <remarks>
    /// With a prefetch, once the consumer reads past the scan's first batch, the next object is
    /// opened, and its first batch read and decoded, while the consumer reads the one before it: an
    /// object's open, its first request and its first decode then wait on nothing the consumer does.
    /// Two objects are open at once at most, and a consumer that stops at the first batch opens one.
    /// </remarks>
    public async IAsyncEnumerable<RecordBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_orderPaths is { } paths)
        {
            await foreach (RecordBatch run in MergedAsync(paths, cancellationToken).ConfigureAwait(false))
            {
                yield return run;
            }

            yield break;
        }

        if (_states is { } request)
        {
            await foreach (RecordBatch batch in StatesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        IAsyncEnumerator<PositionedObject> walk = WalkAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (walk.ConfigureAwait(false))
        {
            using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool prefetch = _options.Prefetch > 0;
            bool readOn = false;
            ObjectBatches? current = null;
            Task<ObjectBatches?>? ahead = null;
            try
            {
                current = await NextObjectAsync(walk, 1, stopped.Token).ConfigureAwait(false);
                while (current is { } reading)
                {
                    if (readOn && prefetch)
                    {
                        ahead ??= NextObjectAsync(walk, 2, stopped.Token);
                    }

                    for (bool more = reading.Any; more; more = await reading.Batches.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield return reading.Batches.Current;
                        if (!readOn)
                        {
                            // Past the scan's first batch, the consumer reads on: the next object opens ahead.
                            readOn = true;
                            if (prefetch)
                            {
                                ahead ??= NextObjectAsync(walk, 2, stopped.Token);
                            }
                        }
                    }

                    current = null;
                    await reading.Batches.DisposeAsync().ConfigureAwait(false);
                    if (ahead is { } next)
                    {
                        ahead = null;
                        current = await next.ConfigureAwait(false);
                    }
                    else
                    {
                        current = await NextObjectAsync(walk, 1, stopped.Token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (current is { } left)
                {
                    await left.Batches.DisposeAsync().ConfigureAwait(false);
                }

                if (ahead is { } late)
                {
                    // The consumer stopped, or a failure is on its way out: the object opened ahead
                    // holds rows nobody asked for, and whatever its open met is not this scan's answer.
                    await stopped.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        if (await late.ConfigureAwait(false) is { } unread)
                        {
                            await unread.Batches.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception) when (late.IsFaulted || late.IsCanceled)
                    {
                    }
                }
            }
        }
    }

    /// <summary>An object being read: its batches as the dataset's rows, positioned on the first one when <paramref name="Any"/>.</summary>
    private readonly record struct ObjectBatches(IAsyncEnumerator<RecordBatch> Batches, bool Any);

    /// <summary>
    /// The next object the scan covers, opened and positioned on its first batch, or none past the
    /// last; <paramref name="open"/> is how many objects are open with it.
    /// </summary>
    private async Task<ObjectBatches?> NextObjectAsync(IAsyncEnumerator<PositionedObject> walk, int open, CancellationToken cancellationToken)
    {
        while (await walk.MoveNextAsync().ConfigureAwait(false))
        {
            PositionedObject held = walk.Current;
            if (Covered(held) == 0)
            {
                continue;
            }

            IAsyncEnumerator<RecordBatch> batches = ObjectAsync(held, open, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                return new ObjectBatches(batches, await batches.MoveNextAsync().ConfigureAwait(false));
            }
            catch
            {
                await batches.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return null;
    }

    /// <summary>
    /// One object's batches as the dataset's rows, its lease held until the last is read; for an aggregation
    /// that takes states, the batch of states its synopsis answers with, then the rows of its edge slices.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> ObjectAsync(
        PositionedObject held, int open, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<RecordBatch> batches = _states is { } request && Answer(held, request) is ({ } answer, { } contents, string instant)
            ? AnsweredAsync(held, answer, contents, instant, open, cancellationToken)
            : RowsAsync(held, open, cancellationToken);
        await foreach (RecordBatch batch in batches.ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>One object's rows as the dataset's, its lease held until the last is read.</summary>
    private async IAsyncEnumerable<RecordBatch> RowsAsync(
        PositionedObject held, int open, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            RecordOpen(lease, open);
            ObjectColumns? columns = ColumnsOf(lease, held.Entry);
            if (columns is null && !held.Entry.HasDeletions)
            {
                // The object's own scan, which every object of the version's schema with no row
                // marked is read by, rebased into one view for the whole object.
                RecordBatch? view = null;
                await foreach (RecordBatch batch in Of(lease.File, held).ExecuteAsync()
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return InDataset(batch, held.FirstRow, ref view);
                }

                yield break;
            }

            if (BatchesOfAsync(lease.File, held, PartOf(columns, _mask), held.FirstRow, _options.Compact, cancellationToken) is { } batches)
            {
                await foreach (RecordBatch batch in batches.ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>
    /// What the synopsis of an object the scan covers, of the version's schema, answers of <paramref name="request"/>:
    /// the answer, the object's table of contents, and the dataset's instant; nothing when its rows are read. An object
    /// covered in part, from the first row of a slice to the last of one and with no row marked, as a lane covers a part
    /// of an object its pieces cut (<see cref="SynopsisAnswer.Cuts"/>), is answered under a window that ends where the
    /// part does; one covered in part otherwise is read.
    /// </summary>
    private (SynopsisAnswer? Answer, SynopsisContents? Contents, string? Instant) Answer(in PositionedObject held, StateRequest request)
    {
        long covered = Covered(held);
        if (covered == 0 || _dataset.Key is not { } key || held.Entry.Contents is not { } contents)
        {
            return default;
        }

        (long, long)? within = null;
        if (covered != held.Entry.Rows)
        {
            if (held.Entry.HasDeletions || _take is not null
                || Within(contents.Slices.Slices, Math.Max(_from, held.FirstRow) - held.FirstRow, covered) is not { } part)
            {
                return default;
            }

            within = part;
        }

        DType schema;
        try
        {
            schema = contents.SchemaType;
        }
        catch (VortexFormatException)
        {
            return default;
        }

        // The instants the object's rows hold, which bound its buckets however wide its slices' unit.
        (long, long)? instants = held.Entry.Summaries.TryGet(key.InstantPath, out ColumnSummary summary)
            && summary is { HasMin: true, HasMax: true, Min.Kind: FilterLiteralKind.Signed, Max.Kind: FilterLiteralKind.Signed }
            ? (summary.Min.SignedValue, summary.Max.SignedValue)
            : null;
        return _version.Schema.ColumnsOf(schema, held.Entry.Key) is null
            && SynopsisAnswer.Of(QuestionOf(request, key.InstantPath), contents, schema, instants, PartitionFilter.ValuesOf(held.Entry, key), within) is { } answer
            ? (answer, contents, key.InstantPath)
            : default;
    }

    /// <summary>
    /// The instants of the slices that hold rows <paramref name="first"/> to <paramref name="first"/> +
    /// <paramref name="count"/> of an object, the first's start to the last's end, inclusive; null when those rows do
    /// not begin and end with slices.
    /// </summary>
    private static (long Low, long High)? Within(IReadOnlyList<TimeSlice> slices, long first, long count)
    {
        // The first slice that begins at the first row, then the last that ends at the end.
        int low = 0;
        int high = slices.Count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (slices[middle].FirstRow < first)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low == slices.Count || slices[low].FirstRow != first)
        {
            return null;
        }

        int start = low;
        long end = first + count;
        high = slices.Count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (slices[middle].FirstRow + slices[middle].Rows <= end)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        int last = low - 1;
        return last >= start && slices[last].FirstRow + slices[last].Rows == end ? (slices[start].Start, slices[last].End - 1) : null;
    }


    /// <summary>
    /// Whether the synopsis of an object the scan covers answers the states it asks: what the version's
    /// entries hold, with no read of an object.
    /// </summary>
    internal async ValueTask<bool> AnswersAnyAsync(CancellationToken cancellationToken)
    {
        if (_states is not { } request || _orderPaths is not null || _take is not null)
        {
            return false;
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Answer(held, request).Answer is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the synopsis of <paramref name="held"/> answers the states the scan asks, as its scan would
    /// find: what its entry holds, with no read of it; and whether the answer is <paramref name="light"/>, its
    /// tables alone, no row read beside them at its edges, of an identifier left out or of a delete.
    /// </summary>
    internal bool Answers(in PositionedObject held, out bool light) => AnswerOf(held, out light, out _) is not null;

    /// <summary>
    /// The answer of <paramref name="held"/>'s synopsis to the states the scan asks, as its scan would find it, and the
    /// object's table of contents: what its entry holds, with no read of it; null when its rows are read. The answer is
    /// <paramref name="light"/> when it reads its tables alone, no row beside them at its edges, of an identifier left out
    /// or of a delete.
    /// </summary>
    internal SynopsisAnswer? AnswerOf(in PositionedObject held, out bool light, out SynopsisContents? contents)
    {
        (SynopsisAnswer? answer, contents, _) = _states is { } request && _orderPaths is null && _take is null ? Answer(held, request) : default;
        light = answer is { Edges.Count: 0, Excluded.Count: 0 } && !held.Entry.HasDeletions;
        return answer;
    }

    /// <summary>
    /// An object answered by its synopsis, with no open of it: the batches of states of its tables' kept
    /// buckets, the rows its deletion marked and the rows the filter leaves out by an identifier taken out
    /// of them, then the rows of the slices at the window's edges, through its chunk directories, the marked
    /// ones left out. When a row cannot come out of a table exactly, or an identifier's index cannot find
    /// the rows left out, the object's rows instead.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> AnsweredAsync(
        PositionedObject held, SynopsisAnswer answer, SynopsisContents contents, string instant, int open,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (await AnswerObjectAsync(held, answer, contents, instant, cancellationToken).ConfigureAwait(false) is { } answered)
        {
            foreach (RecordBatch batch in Delivered(held, answered))
            {
                yield return batch;
            }

            yield break;
        }

        await foreach (RecordBatch batch in RowsAsync(held, open, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The objects of an aggregation that takes states, newest first: the answers of those their synopses
    /// answer read ahead of the consumer, as many at once as the session lets reads be in flight, so that a
    /// question over a month of objects costs the round trips of one object, not one per object; an object
    /// read as rows is read in its turn, alone ahead of none.
    /// </summary>
    /// <remarks>
    /// An answer read ahead holds its tables' states and its edges' rows until its turn: a few tables' rows
    /// and a few slices an object, against the rows a scan of the object would hold.
    /// </remarks>
    private async IAsyncEnumerable<RecordBatch> StatesAsync(StateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The objects walked, in an array of the process's pool grown as the walk goes and given back at the end: a list
        // of them grew for each question, five arrays for 48 objects.
        PositionedObject[] objects = System.Buffers.ArrayPool<PositionedObject>.Shared.Rent(16);
        int count = 0;
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int width = _dataset.Session.Options.MaxConcurrentReads;
        bool prefetch = _options.Prefetch > 0;
        Queue<Turn>? ahead = null;
        int next = 0;
        try
        {
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Covered(held) > 0)
                {
                    if (count == objects.Length)
                    {
                        PositionedObject[] grown = System.Buffers.ArrayPool<PositionedObject>.Shared.Rent(2 * count);
                        objects.AsSpan(0, count).CopyTo(grown);
                        System.Buffers.ArrayPool<PositionedObject>.Shared.Return(objects, clearArray: true);
                        objects = grown;
                    }

                    objects[count++] = held;
                }
            }

            NewestFirst(objects.AsSpan(0, count));
            ahead = new Queue<Turn>(Math.Min(width, count));
            while (true)
            {
                while (ahead.Count < width && next < count)
                {
                    PositionedObject held = objects[next++];
                    ahead.Enqueue(Answer(held, request) is not ({ } answer, { } contents, string instant) ? new Turn(held)
                        : TryAnswerNow(held, answer, contents, instant, out AnsweredObject? done) ? new Turn(held, done)
                        : new Turn(held, AnswerObjectAsync(held, answer, contents, instant, stopped.Token)));
                }

                if (!ahead.TryDequeue(out Turn? turn))
                {
                    break;
                }

                // The answer was done, or started by this walk a few turns before. Its batches go as the dataset's,
                // each counted, then the rows its edges keep; the answer given back once the last is read, or the
                // consumer stops.
#pragma warning disable VSTHRD003
                if (turn.Answers && (turn.Done ?? await turn.Answer!.ConfigureAwait(false)) is { } answered)
#pragma warning restore VSTHRD003
                {
                    try
                    {
                        if (Covered(turn.Held) != turn.Held.Entry.Rows)
                        {
                            _counters?.AddAnsweredPart();
                        }

                        for (int b = 0; b < answered.States.Count; b++)
                        {
                            RecordBatch batch = answered.States[b];
                            _counters?.AddSynopsisRows(batch.SelectedRows);
                            yield return batch;
                        }

                        for (int e = 0; e < answered.Edges.Count; e++)
                        {
                            RecordBatch slice = answered.Edges[e];
                            using RecordBatch rows = slice.Rebased(turn.Held.FirstRow + slice.StartRow);
                            _counters?.AddEdgeRows(rows.SelectedRows);
                            if (rows.SelectedRows > 0)
                            {
                                yield return rows;
                            }
                        }
                    }
                    finally
                    {
                        answered.Dispose();
                    }

                    continue;
                }

                // An object read as rows, opened ahead when it could be; the next one read as rows opens ahead
                // while it is read, one at most, as a scan of rows opens its next object.
#pragma warning disable VSTHRD003
                ObjectBatches reading = await (turn.Rows ?? OpenRowsAsync(turn.Held, 1, stopped.Token)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                turn.Rows = null;
                try
                {
                    if (prefetch && Unanswered(ahead) is { Rows: null } following)
                    {
                        following.Rows = OpenRowsAsync(following.Held, 2, stopped.Token);
                    }

                    for (bool more = reading.Any; more; more = await reading.Batches.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield return reading.Batches.Current;
                    }
                }
                finally
                {
                    await reading.Batches.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // The answers and the rows read ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach (Turn left in ahead ?? [])
            {
                await left.ReleaseAsync().ConfigureAwait(false);
            }

            System.Buffers.ArrayPool<PositionedObject>.Shared.Return(objects, clearArray: true);
        }
    }

    /// <summary>The first object of <paramref name="ahead"/> its synopsis does not answer, which is read as rows; null for none.</summary>
    private static Turn? Unanswered(Queue<Turn> ahead)
    {
        foreach (Turn turn in ahead)
        {
            if (!turn.Answers)
            {
                return turn;
            }
        }

        return null;
    }

    /// <summary>An object's rows, opened and positioned on their first batch; <paramref name="open"/> is how many objects are open with it.</summary>
    private async Task<ObjectBatches> OpenRowsAsync(PositionedObject held, int open, CancellationToken cancellationToken)
    {
        IAsyncEnumerator<RecordBatch> batches = RowsAsync(held, open, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            return new ObjectBatches(batches, await batches.MoveNextAsync().ConfigureAwait(false));
        }
        catch
        {
            await batches.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// An object of a walk that takes states, in the queue of those read ahead: the answer of its synopsis, started
    /// when it has one, and its rows, once opened ahead.
    /// </summary>
    private sealed class Turn
    {
        /// <summary>An object read as rows.</summary>
        internal Turn(PositionedObject held) => Held = held;

        /// <summary>An object its synopsis answers, its answer started.</summary>
        internal Turn(PositionedObject held, Task<AnsweredObject?> answer)
        {
            Held = held;
            Answer = answer;
            Answers = true;
        }

        /// <summary>An object its synopsis answered on the spot: <paramref name="done"/>, or null when its rows are read after all.</summary>
        internal Turn(PositionedObject held, AnsweredObject? done)
        {
            Held = held;
            Done = done;
            Answers = true;
        }

        internal PositionedObject Held { get; }

        /// <summary>Whether its synopsis answers it: an answer done or started, which may still find its rows read.</summary>
        internal bool Answers { get; }

        /// <summary>The answer done on the spot, the walk's to deliver or to let go.</summary>
        internal AnsweredObject? Done { get; }

        internal Task<AnsweredObject?>? Answer { get; }

        internal Task<ObjectBatches>? Rows { get; set; }

        /// <summary>Lets go what was read ahead and never delivered: the answer's states, the rows' lease.</summary>
        internal async ValueTask ReleaseAsync()
        {
            Done?.Dispose();
            if (Answer is { } answer)
            {
                try
                {
#pragma warning disable VSTHRD003
                    (await answer.ConfigureAwait(false))?.Dispose();
#pragma warning restore VSTHRD003
                }
                catch (Exception) when (answer.IsFaulted || answer.IsCanceled)
                {
                }
            }

            if (Rows is { } rows)
            {
                try
                {
#pragma warning disable VSTHRD003
                    await (await rows.ConfigureAwait(false)).Batches.DisposeAsync().ConfigureAwait(false);
#pragma warning restore VSTHRD003
                }
                catch (Exception) when (rows.IsFaulted || rows.IsCanceled)
                {
                }
            }
        }
    }

    /// <summary>
    /// Orders <paramref name="objects"/> by the newest instant each holds, newest first, those whose
    /// summary does not say last, each kind in the walk's order.
    /// </summary>
    private void NewestFirst(Span<PositionedObject> objects)
    {
        if (_dataset.Key is not { } key)
        {
            return;
        }

        Span<(long Newest, int At)> order = objects.Length <= 512 ? stackalloc (long, int)[objects.Length] : new (long, int)[objects.Length];
        for (int i = 0; i < order.Length; i++)
        {
            long newest = objects[i].Entry.Summaries.TryGet(key.InstantPath, out ColumnSummary summary)
                && summary is { HasMax: true, Max.Kind: FilterLiteralKind.Signed }
                ? summary.Max.SignedValue
                : long.MinValue;
            order[i] = (newest, i);
        }

        order.Sort(static (a, b) => a.Newest != b.Newest ? b.Newest.CompareTo(a.Newest) : a.At.CompareTo(b.At));

        // Each place takes the object the order puts there, a cycle of places at a time, in place: a place done is
        // marked by its source made -1.
        for (int i = 0; i < order.Length; i++)
        {
            if (order[i].At < 0)
            {
                continue;
            }

            PositionedObject first = objects[i];
            for (int place = i; ;)
            {
                int from = order[place].At;
                order[place].At = -1;
                if (from == i)
                {
                    objects[place] = first;
                    break;
                }

                objects[place] = objects[from];
                place = from;
            }
        }
    }

    /// <summary>What an object's synopsis answered: its batches of states, the tables they lie in, and the rows of its edge slices.</summary>
    private sealed class AnsweredObject(List<RecordBatch> states, SynopsisTableSet? tables, List<RecordBatch> edges) : IDisposable
    {
        internal List<RecordBatch> States { get; } = states;

        internal List<RecordBatch> Edges { get; } = edges;

        public void Dispose()
        {
            foreach (RecordBatch batch in States)
            {
                batch.Dispose();
            }

            foreach (RecordBatch batch in Edges)
            {
                batch.Dispose();
            }

            tables?.Dispose();
        }
    }

    /// <summary>
    /// The batches of an object's answer as the dataset's: its states, then the rows its edges keep, each
    /// counted; the answer given back once the last is read, or the consumer stops.
    /// </summary>
    private IEnumerable<RecordBatch> Delivered(PositionedObject held, AnsweredObject answered)
    {
        try
        {
            foreach (RecordBatch batch in answered.States)
            {
                _counters?.AddSynopsisRows(batch.SelectedRows);
                yield return batch;
            }

            foreach (RecordBatch slice in answered.Edges)
            {
                using RecordBatch rows = slice.Rebased(held.FirstRow + slice.StartRow);
                _counters?.AddEdgeRows(rows.SelectedRows);
                if (rows.SelectedRows > 0)
                {
                    yield return rows;
                }
            }
        }
        finally
        {
            answered.Dispose();
        }
    }

    /// <summary>
    /// What an object's synopsis answers when its tables alone answer it, no row read beside them, and the session's
    /// synopsis cache holds every chunk of them: read, decoded and folded on the spot, with no task, null when a row
    /// cannot come out of a table exactly; false when the answer needs a read of its own, which
    /// <see cref="AnswerObjectAsync"/> makes. A walk of a warm question over many objects answers each so, where a task an
    /// object was the most it allocated for one.
    /// </summary>
    private bool TryAnswerNow(PositionedObject held, SynopsisAnswer answer, SynopsisContents contents, string instant, out AnsweredObject? answered)
    {
        answered = null;
        if (held.Entry.HasDeletions || answer.Edges.Count > 0 || answer.Excluded.Count > 0 || answer.Parts.Count == 0)
        {
            return false;
        }

        answer.Project(contents);
        if (_dataset.ReadSynopsisCached(held.Entry, contents, answer) is not { } tables)
        {
            return false;
        }

        DType schema = contents.SchemaType;
        List<RecordBatch> states = new List<RecordBatch>(tables.Tables.Count);
        bool exact = true;
        try
        {
            for (int p = 0; p < answer.Parts.Count && exact; p++)
            {
                exact = answer.TryBatches(tables, answer.Parts[p], contents, instant, held.FirstRow, states, null, schema, contents.Slices);
            }
        }
        catch
        {
            LetGo(states, tables);
            throw;
        }

        if (!exact)
        {
            LetGo(states, tables);
            return true;
        }

        answered = new AnsweredObject(states, tables, NoRows);
        return true;
    }

    /// <summary>The batches and tables of an answer given back.</summary>
    private static void LetGo(List<RecordBatch> states, SynopsisTableSet tables)
    {
        foreach (RecordBatch batch in states)
        {
            batch.Dispose();
        }

        tables.Dispose();
    }

    /// <summary>
    /// Reads what an object's synopsis answers, with no open of it, as <see cref="AnsweredAsync"/> delivers it;
    /// null, with nothing held, when a row cannot come out of a table exactly or an identifier's index cannot
    /// find the rows left out.
    /// </summary>
    private async Task<AnsweredObject?> AnswerObjectAsync(
        PositionedObject held, SynopsisAnswer answer, SynopsisContents contents, string instant, CancellationToken cancellationToken)
    {
        // A deletion vector kept out of line is read before the rows it marks are.
        if (held.Entry.HasDeletions && !held.Entry.IsResolved)
        {
            await held.Entry.ResolveAsync(_version.Pages, cancellationToken).ConfigureAwait(false);
        }

        // The slices cut and the rows marked are read beside the tables, not after them: their chunk
        // directories and their chunks take two round trips, the tables one, so the object costs two.
        DType schema = contents.SchemaType;
        Task<List<RecordBatch>>? edges = answer.Edges.Count > 0
            ? ReadSpansAsync(held, contents, answer.Edges, EdgeColumns(schema), select: true, cancellationToken)
            : null;
        Task<List<RecordBatch>>? marked = held.Entry.HasDeletions && answer.Parts.Count > 0
            ? ReadSpansAsync(held, contents, MarkedSpans(held.Entry.Deletions), MeasuredColumns(answer), select: false, cancellationToken)
            : null;
        Task<List<RecordBatch>>? excluded = answer.Excluded.Count > 0 && answer.Parts.Count > 0
            ? ExcludedAsync(held, answer.Excluded, ExcludedColumns(answer, schema), cancellationToken)
            : null;
        List<RecordBatch> states = [];
        SynopsisTableSet? tables = null;
        bool exact = true;
        try
        {
            if (answer.Parts.Count > 0)
            {
                // The fields the batches of states are read by, but every one where a row comes out of a table.
                if (!held.Entry.HasDeletions && answer.Excluded.Count == 0)
                {
                    answer.Project(contents);
                }

                tables = await _dataset.ReadSynopsisAsync(held.Entry, contents, answer, cancellationToken).ConfigureAwait(false);
                List<RecordBatch>? removed = marked is null ? null : await marked.ConfigureAwait(false);
                if (excluded is not null)
                {
                    List<RecordBatch> left = await excluded.ConfigureAwait(false);
                    exact = !ReferenceEquals(left, Unfound);
                    removed = removed is null ? left : [.. removed, .. left];
                }

                foreach (SynopsisPart part in answer.Parts)
                {
                    if (!exact || !answer.TryBatches(tables, part, contents, instant, held.FirstRow, states, removed, schema, contents.Slices))
                    {
                        exact = false;
                        break;
                    }
                }
            }
        }
        catch
        {
            await LetGoAsync(states, tables, marked, edges, excluded).ConfigureAwait(false);
            throw;
        }

        // The rows taken out are spent once the states are corrected.
        if (marked is not null || excluded is not null)
        {
            await LetGoAsync(NoRows, null, marked, excluded).ConfigureAwait(false);
        }

        if (!exact)
        {
            await LetGoAsync(states, tables, edges).ConfigureAwait(false);
            return null;
        }

        try
        {
            return new AnsweredObject(states, tables, edges is null ? NoRows : await edges.ConfigureAwait(false));
        }
        catch
        {
            await LetGoAsync(states, tables).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>What <see cref="ExcludedAsync"/> answers when an identifier's index cannot find the rows left out.</summary>
    private static readonly List<RecordBatch> Unfound = [];

    /// <summary>No batch: the edges of an answer that cuts none, the rows of none taken out. Never added to.</summary>
    private static readonly List<RecordBatch> NoRows = [];

    /// <summary>
    /// The rows the filter leaves out by an identifier's value, found by the identifier's index with no open
    /// of the object, three round trips cold, beside the tables: each part's rows, those an earlier part
    /// already left out and those a deletion marked left out too, since a row comes out of a table once.
    /// Batches counted from the object's first physical row, which the caller disposes; <see cref="Unfound"/>
    /// when an index cannot say.
    /// </summary>
    private async Task<List<RecordBatch>> ExcludedAsync(
        PositionedObject held, IReadOnlyList<(VortexExpr Part, VortexExpr Kept)> excluded, int[]? columns, CancellationToken cancellationToken)
    {
        Task<List<RecordBatch>?>[] lookups = new Task<List<RecordBatch>?>[excluded.Count];
        for (int i = 0; i < lookups.Length; i++)
        {
            lookups[i] = _dataset.LookupAsync(held.Entry, excluded[i].Part, columns, cancellationToken).AsTask();
        }

        try
        {
            await Task.WhenAll(lookups).ConfigureAwait(false);
        }
        catch
        {
            foreach (Task<List<RecordBatch>?> lookup in lookups)
            {
                if (lookup.IsCompletedSuccessfully)
                {
                    foreach (RecordBatch batch in await lookup.ConfigureAwait(false) ?? [])
                    {
                        batch.Dispose();
                    }
                }
            }

            throw;
        }

        List<RecordBatch> rows = [];
        bool found = true;
        VortexExpr? earlier = null;
        for (int i = 0; i < lookups.Length; i++)
        {
            List<RecordBatch>? batches = await lookups[i].ConfigureAwait(false);
            found &= batches is not null;
            VortexExpr part = earlier is null ? excluded[i].Part : Expr.And(excluded[i].Part, earlier);
            foreach (RecordBatch batch in batches ?? [])
            {
                rows.Add(batch);
                SelectBy(batch, part);
                if (held.Entry.HasDeletions)
                {
                    LeaveOutMarked(batch, held.Entry.Deletions);
                }
            }

            earlier = earlier is null ? excluded[i].Kept : Expr.And(earlier, excluded[i].Kept);
        }

        if (found)
        {
            return rows;
        }

        foreach (RecordBatch batch in rows)
        {
            batch.Dispose();
        }

        return Unfound;
    }

    /// <summary>The batches and tables of an answer given back, those read beside it once their reads end.</summary>
    private static async ValueTask LetGoAsync(List<RecordBatch> states, SynopsisTableSet? tables, params Task<List<RecordBatch>>?[] reads)
    {
        foreach (RecordBatch batch in states)
        {
            batch.Dispose();
        }

        if (!ReferenceEquals(states, NoRows))
        {
            states.Clear();
        }

        tables?.Dispose();
        foreach (Task<List<RecordBatch>>? read in reads)
        {
            if (read is null)
            {
                continue;
            }

            try
            {
                foreach (RecordBatch batch in await read.ConfigureAwait(false))
                {
                    batch.Dispose();
                }
            }
            catch (Exception) when (read.IsFaulted || read.IsCanceled)
            {
            }
        }
    }

    /// <summary>
    /// Rows of the object read with no open of it, through its chunk directories, a batch per span of
    /// <paramref name="columns"/>, the rows its deletion marked left out; with <paramref name="select"/>, the
    /// rows the filter holds for alone. Batches counted from the object's first physical row, which the
    /// caller disposes.
    /// </summary>
    private async Task<List<RecordBatch>> ReadSpansAsync(
        PositionedObject held, SynopsisContents contents, IReadOnlyList<TimeSlice> spans, int[]? columns, bool select, CancellationToken cancellationToken)
    {
        List<RecordBatch> batches = [.. await _dataset.ReadSlicesAsync(held.Entry, contents, spans, columns, cancellationToken).ConfigureAwait(false)];
        try
        {
            foreach (RecordBatch batch in batches)
            {
                if (select && _filter is { } filter)
                {
                    SelectBy(batch, filter);
                }

                if (select && held.Entry.HasDeletions)
                {
                    LeaveOutMarked(batch, held.Entry.Deletions);
                }
            }

            return batches;
        }
        catch
        {
            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }

            throw;
        }
    }

    /// <summary>The runs of rows a deletion marked, as spans of the object's physical rows.</summary>
    private static List<TimeSlice> MarkedSpans(DeletionVector deletions)
    {
        List<TimeSlice> spans = new List<TimeSlice>(deletions.Runs);
        for (int run = 0; run < deletions.Runs; run++)
        {
            spans.Add(new TimeSlice(0, 1, deletions.StartOf(run), deletions.EndOf(run) - deletions.StartOf(run)));
        }

        return spans;
    }

    /// <summary>The columns the answer's tables key and measure: what charges a row taken out to its table's row.</summary>
    /// <summary>
    /// The columns the rows an identifier's index finds are read for: the keys and the measures of the tables they come out
    /// of, and the columns of the filters that leave them out and keep the rest; null, every one, when a filter names a
    /// column that is none of the schema's. Every column's chunk directory was read and parsed for them, at every question.
    /// </summary>
    private static int[]? ExcludedColumns(SynopsisAnswer answer, DType schema)
    {
        SortedSet<int> fields = [.. MeasuredColumns(answer)];
        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        foreach ((VortexExpr part, VortexExpr kept) in answer.Excluded)
        {
            part.CollectFields(named);
            kept.CollectFields(named);
        }

        foreach (string name in named)
        {
            int field = schema.IndexOfField(name);
            if (field < 0)
            {
                return null;
            }

            fields.Add(field);
        }

        return [.. fields];
    }

    private static int[] MeasuredColumns(SynopsisAnswer answer)
    {
        SortedSet<int> fields = [];
        foreach (SynopsisPart part in answer.Parts)
        {
            foreach (int field in part.Table.Keys)
            {
                if (!SynopsisPath.Names(field))
                {
                    fields.Add(field);
                }
            }

            foreach (int field in part.Table.Measures)
            {
                if (!SynopsisPath.Names(field))
                {
                    fields.Add(field);
                }
            }
        }

        return [.. fields];
    }

    /// <summary>Leaves the rows a deletion marked out of a batch of an object's physical rows.</summary>
    private static void LeaveOutMarked(RecordBatch batch, DeletionVector deletions)
    {
        int rows = batch.RowCount;
        int words = Math.Max((rows + 63) >> 6, 1);
        Buffers.VortexBuffer selection = batch.Arena.Allocate(words * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
        Span<ulong> live = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);
        deletions.Live(batch.StartRow, rows, live);
        ReadOnlySpan<ulong> kept = batch.SelectionWords;
        int selected = 0;
        for (int w = 0; w < words; w++)
        {
            if (!kept.IsEmpty)
            {
                live[w] &= kept[w];
            }

            selected += System.Numerics.BitOperations.PopCount(live[w]);
        }

        batch.Select(selection, selected);
    }

    /// <summary>
    /// The top-level columns of <paramref name="schema"/> an edge reads: those the pass projects and those
    /// the filter names; null for every column, the pass projecting none.
    /// </summary>
    private int[]? EdgeColumns(DType schema)
    {
        if (_mask is not { } mask)
        {
            return null;
        }

        SortedSet<int> fields = [];
        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in PathsOf(mask, schema))
        {
            named.Add(path.Split('.')[0]);
        }

        _filter?.CollectFields(named);
        foreach (string name in named)
        {
            int field = schema.IndexOfField(name.Split('.')[0]);
            if (field >= 0)
            {
                fields.Add(field);
            }
        }

        return [.. fields];
    }

    /// <summary>Selects the rows of <paramref name="batch"/> <paramref name="filter"/> holds for.</summary>
    private static void SelectBy(RecordBatch batch, VortexExpr filter)
    {
        int rows = batch.RowCount;
        byte[] states = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            FilterEvaluator.Evaluate(filter, batch.Arena, batch.RootIndex, rows, states.AsSpan(0, rows));
            int words = Math.Max((rows + 63) >> 6, 1);
            Buffers.VortexBuffer selection = batch.Arena.Allocate(words * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
            bytes.Clear();
            Span<ulong> kept = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);
            int selected = 0;
            for (int r = 0; r < rows; r++)
            {
                if (states[r] == Trilean.True)
                {
                    kept[r >> 6] |= 1UL << (r & 63);
                    selected++;
                }
            }

            batch.Select(selection, selected);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(states);
        }
    }

    /// <summary>
    /// The rows the scan selects, without materialising them, cheapest proof first: an object whose
    /// covered rows are all kept is counted from its entry without being opened, which is every
    /// object when there is no filter and, under a range on the clustering key, an object whose
    /// summary bounds lie wholly inside it and whose key holds no null; any other object is counted
    /// by its own file's count, from statistics, zone maps and indexes before any decode.
    /// </summary>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        KeyRange? range = CountRange();
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0)
            {
                continue;
            }

            if (Settled(held, range))
            {
                rows += covered;
                RecordCounted();
                continue;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                rows += await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether the scan would return at least one row; the first object that holds one answers for
    /// the dataset, and one whose entry settles it answers without being opened.
    /// </summary>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        KeyRange? range = CountRange();
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (Settled(held, range))
            {
                RecordCounted();
                return true;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                if (await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: true, cancellationToken)
                    .ConfigureAwait(false) > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The smallest non-null value of a column among the rows the scan would return, or
    /// <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    public ValueTask<FilterLiteral> MinAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: true, cancellationToken);

    /// <summary>
    /// The largest non-null value of a column among the rows the scan would return, or
    /// <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    public ValueTask<FilterLiteral> MaxAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: false, cancellationToken);

    /// <summary>
    /// What the scan would read, without reading a data object. A level 0 that has outgrown its
    /// ceiling is reported as a lag rather than refused, since compaction is the caller's own
    /// background job and the answers stay correct meanwhile.
    /// </summary>
    public async ValueTask<DatasetPlan> ExplainAsync(CancellationToken cancellationToken = default)
    {
        DatasetScanCounters metrics = new DatasetScanCounters();
        long objects = 0;
        long rows = 0;
        List<long> keptByLevel = [];
        await foreach (PositionedObject held in _version
            .WalkAsync(Pruner(), _from, _to, metrics, cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            objects++;
            rows += held.Entry.Rows;
            while (keptByLevel.Count <= held.Level)
            {
                keptByLevel.Add(0);
            }

            keptByLevel[held.Level]++;
        }

        DatasetLevels levels = _version.Levels;
        long[] byLevel = new long[Math.Max(levels.Count, 1)];
        for (int level = 0; level < byLevel.Length; level++)
        {
            byLevel[level] = levels[level].Entries;
        }

        return new DatasetPlan(
            _version.Version,
            byLevel,
            levels.LagAtLevelZero(),
            _version.Header.ClusteringKey.Count > 0,
            objects,
            rows,
            metrics.ObjectsSkipped,
            metrics.SubtreesSkipped,
            _orderPaths is null ? null : _orderPaths.Length == 1 ? _orderPaths[0] : "(" + string.Join(", ", _orderPaths) + ")",
            CursorBound(objects, keptByLevel));
    }

    /// <summary>
    /// What the scan will do as the core's plan, summed over the objects it would open: each one's
    /// own plan, worked out from its statistics, zone maps and indexes, and before them the rows the
    /// summaries refute as one step. No data segment is read, but every kept object is opened.
    /// </summary>
    /// <remarks>
    /// An object the summaries refute is never opened, so its blocks are counted at the block size
    /// of the objects that were, or at the dataset's own when none was.
    /// </remarks>
    public async ValueTask<ScanPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        long keptRows = 0;
        long blockRows = 0;
        long blocks = 0;
        long live = 0;
        long segments = 0;
        long bytes = 0;
        bool mayMatch = false;
        List<PruningStep> steps = [];
        Dictionary<string, int> stepAt = new Dictionary<string, int>(StringComparer.Ordinal);
        bool exact = true;
        long exactRows = 0;
        long pruned = 0;
        long proven = 0;
        long decoded = 0;
        List<string> sources = [];
        long runs = 0;
        long entries = 0;
        List<StatesStep> states = [];
        KeyRange? range = CountRange();
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0)
            {
                continue;
            }

            // What an aggregation that takes states reads of the object: its tables, then its edges' rows,
            // or its rows and why. An object its synopsis answers is not opened, by the plan as by the pass.
            if (_states is { } request && StatesOf(held, request, states) is not null)
            {
                keptRows += covered;
                mayMatch = true;
                continue;
            }

            ScanExplanation plan;
            bool evaluated = false;
            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);

                // An object is planned as its own scan reads it: the filter it takes, or none when the
                // filter is evaluated on its reshaped batches.
                ObjectPart part = PartOf(ColumnsOf(lease, held.Entry), _mask);
                if (part.Fate == FilterFate.None)
                {
                    continue;
                }

                evaluated = part.Fate == FilterFate.Reshaped;
                ScanBuilder scan = _orderPaths is { } paths && part.Mapped is null
                    ? OrderedOf(lease.File, paths, withKey: false)
                    : ScanOf(lease.File, held, part);
                plan = await scan.ExplainAsync(cancellationToken).ConfigureAwait(false);
            }

            keptRows += covered;

            blockRows = blockRows == 0 ? plan.BlockRows : blockRows;
            blocks += plan.Blocks;
            live += plan.LiveBlocks;
            segments += plan.SegmentsToRead;
            bytes += plan.BytesToRead;
            mayMatch |= plan.FileMayMatch;
            foreach (PruningStep step in plan.Pruning)
            {
                if (stepAt.TryGetValue(step.Structure, out int at))
                {
                    PruningStep sum = steps[at];
                    steps[at] = sum with
                    {
                        BlocksPruned = sum.BlocksPruned + step.BlocksPruned,
                        SegmentsRead = sum.SegmentsRead + step.SegmentsRead,
                        BytesRead = sum.BytesRead + step.BytesRead,
                    };
                }
                else
                {
                    stepAt[step.Structure] = steps.Count;
                    steps.Add(step);
                }
            }

            if (evaluated || (held.Entry.HasDeletions && !Settled(held, range)))
            {
                // Its rows are counted by evaluating the filter on them, or its own plan counts rows
                // that are deleted: no plan settles the count.
                exact = false;
            }
            else if (Settled(held, range) || plan.Count is null)
            {
                exactRows += covered;
            }
            else
            {
                CountExplanation count = plan.Count;
                exact &= count.ExactCover;
                exactRows += count.ExactCount;
                pruned += count.SplitsPruned;
                proven += count.SplitsProven;
                decoded += count.SplitsDecoded;
            }

            if (plan.Order is { } order)
            {
                string source = order.Source.ToString();
                if (!sources.Contains(source))
                {
                    sources.Add(source);
                }

                runs += order.RunsInRange;
                entries += order.EntriesInRange;
            }
        }

        long coveredRows = _take is { } take ? take.Length : Math.Max(Math.Min(_to, _version.RowCount) - _from, 0);
        long refuted = Math.Max(coveredRows - keptRows, 0);
        long perBlock = blockRows > 0 ? blockRows : Math.Max(_dataset.Options.Write.BlockRows, 1);
        long refutedBlocks = (refuted + perBlock - 1) / perBlock;
        List<PruningStep> pruning = new List<PruningStep>(steps.Count + 1);
        if (refutedBlocks > 0)
        {
            pruning.Add(new PruningStep("object summaries", Narrow(refutedBlocks), 0, 0));
        }

        pruning.AddRange(steps);
        return new ScanPlan(
            coveredRows,
            Narrow(blocks + refutedBlocks),
            Narrow(live),
            Narrow(segments),
            bytes,
            mayMatch,
            [.. pruning],
            new CountPlan(exact, exactRows, Narrow(pruned), Narrow(proven), Narrow(decoded)),
            _orderPaths is null
                ? null
                : new OrderPlan(sources.Count == 0 ? nameof(KeySourceKind.None) : string.Join('+', sources), Narrow(runs), entries, _descending))
        {
            States = [.. states],
        };
    }

    /// <summary>
    /// Adds to <paramref name="into"/> what the object reads for an aggregation that takes states: each
    /// table of its answer with the buckets it folds and the slices read at its edges; or its rows, and
    /// why. The answer, or null when the rows are read whole.
    /// </summary>
    private SynopsisAnswer? StatesOf(in PositionedObject held, StateRequest request, List<StatesStep> into)
    {
        string key = held.Entry.Key.ToString();
        (SynopsisAnswer? answer, SynopsisContents? contents, _) = Answer(held, request);
        if (answer is null || contents is null)
        {
            string why = Covered(held) != held.Entry.Rows ? "a part of its rows asked"
                : held.Entry.Synopsis.IsEmpty ? "no synopsis"
                : "no table holds the keys, the measures and the window";
            into.Add(new StatesStep(key, "rows", why, held.Entry.Rows));
            return null;
        }

        if (held.Entry.HasDeletions && answer.Parts.Count > 0)
        {
            into.Add(new StatesStep(key, "marked", "rows a delete marked, taken out of the tables, or the object's rows if one cannot be", held.Entry.DeletedRows));
        }

        if (answer.Excluded.Count > 0 && answer.Parts.Count > 0)
        {
            into.Add(new StatesStep(
                key, "excluded", "rows the filter leaves out by an identifier, found by its index and taken out of the tables", answer.Excluded.Count));
        }

        DType schema = contents.SchemaType;
        foreach (SynopsisPart part in answer.Parts)
        {
            string[] keys = Array.ConvertAll(part.Table.Keys, field => SynopsisTableInfo.NameOf(field, schema, contents.Slices.Paths));
            into.Add(new StatesStep(key, "table", $"{part.Table.Kind} by {part.Table.Bucket} on ({string.Join(", ", keys)})", part.KeptCount));
        }

        if (answer.Edges.Count > 0)
        {
            into.Add(new StatesStep(key, "edges", "the slices the window or a key of time cuts", answer.Edges.Count));
        }

        return answer;
    }

    /// <summary>
    /// The objects this scan will read, in key order, each with its first row: the walk with its
    /// skips applied, and not one byte of any data object read.
    /// </summary>
    public IAsyncEnumerable<PositionedObject> ObjectsAsync(CancellationToken cancellationToken = default) =>
        WalkAsync(cancellationToken);

    /// <summary>
    /// The extreme over the objects, each one skipped when its own summary cannot beat the best so
    /// far, and taken from its summary without being opened when that summary is exact and covers
    /// every row the scan keeps in it. A summary's min is at or below the object's true minimum, so
    /// a summary min that already loses cannot hide a winner behind it.
    /// </summary>
    private async ValueTask<FilterLiteral> ExtremeAsync(
        string path, bool wantMin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        FieldMask projection = new FieldMaskBuilder().Include(ToolPaths.Resolve(_version.Schema.Columns, path)).Build();
        FilterLiteral best = FilterLiteral.Null;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (_summaries && Loses(held.Entry.Summaries, path, best, wantMin))
            {
                if (_metrics is { } skipped)
                {
                    skipped.ObjectsSkipped++;
                }

                continue;
            }

            if (_summaries && _filter is null && Whole(held) && Exact(held.Entry.Summaries, path, wantMin, out FilterLiteral bound))
            {
                if (Beats(bound, best, wantMin))
                {
                    best = bound;
                }

                RecordCounted();
                continue;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                FilterLiteral candidate = await ObjectExtremeAsync(lease.File, held, ColumnsOf(lease, held.Entry), path, projection, wantMin, cancellationToken)
                    .ConfigureAwait(false);
                if (candidate.Kind != FilterLiteralKind.Null && Beats(candidate, best, wantMin))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>Whether an object's summary proves it cannot hold a better value than the best.</summary>
    private static bool Loses(ObjectSummaries summaries, string path, FilterLiteral best, bool wantMin)
    {
        if (best.Kind == FilterLiteralKind.Null || !summaries.TryGet(path, out ColumnSummary column))
        {
            return false;
        }

        if (wantMin)
        {
            return column.HasMin && Comparable(column.Min, best) && KeyCursor.Compare(column.Min, best) >= 0;
        }

        return column.HasMax && Comparable(column.Max, best) && KeyCursor.Compare(column.Max, best) <= 0;
    }

    /// <summary>The true extreme an object's summary records, when it records one.</summary>
    private static bool Exact(ObjectSummaries summaries, string path, bool wantMin, out FilterLiteral bound)
    {
        bound = FilterLiteral.Null;
        if (!summaries.TryGet(path, out ColumnSummary column) || !column.IsExact)
        {
            return false;
        }

        bound = wantMin ? column.Min : column.Max;
        return (wantMin ? column.HasMin : column.HasMax) && bound.Kind != FilterLiteralKind.Null;
    }

    private static bool Beats(FilterLiteral candidate, FilterLiteral best, bool wantMin) =>
        best.Kind == FilterLiteralKind.Null
        || !Comparable(candidate, best)
        || (wantMin ? KeyCursor.Compare(candidate, best) < 0 : KeyCursor.Compare(candidate, best) > 0);

    /// <summary>Whether two literals are of one comparison kind, so that an order holds between them.</summary>
    private static bool Comparable(FilterLiteral left, FilterLiteral right) =>
        left.Kind == right.Kind && left.Kind != FilterLiteralKind.Null;

    /// <summary>The range on the clustering key the entries can count the filter by, or null.</summary>
    private KeyRange? CountRange() => _filter is not null && _summaries ? KeyRange.Of(_dataset, _filter) : null;

    /// <summary>
    /// Whether every row the scan covers in an object is kept, known without opening it: there is
    /// no filter, or the object lies wholly inside the scan and inside a range on the clustering key.
    /// </summary>
    private bool Settled(in PositionedObject held, KeyRange? range) =>
        _filter is null || (range is { } key && Whole(held) && key.Holds(held.Entry.Summaries));

    /// <summary>Whether the scan covers every row of an object.</summary>
    private bool Whole(in PositionedObject held) =>
        _take is null && _from <= held.FirstRow && held.FirstRow + held.Entry.Rows <= _to;

    /// <summary>The object's rows the scan covers: those it takes by index, or its part of the range.</summary>
    private long Covered(in PositionedObject held)
    {
        if (_take is not null)
        {
            return TakenBy(held).Count;
        }

        long lower = Math.Max(_from, held.FirstRow);
        long upper = Math.Min(_to, held.FirstRow + held.Entry.Rows);
        return Math.Max(upper - lower, 0);
    }

    /// <summary>Where the object's rows begin and how many there are among the rows the scan takes.</summary>
    private (int First, int Count) TakenBy(in PositionedObject held)
    {
        long[] take = _take!;
        int first = LowerBound(take, held.FirstRow);
        int end = LowerBound(take, held.FirstRow + held.Entry.Rows);
        return (first, end - first);
    }

    private static int LowerBound(long[] sorted, long value)
    {
        int found = Array.BinarySearch(sorted, value);
        return found >= 0 ? found : ~found;
    }

    private static int Narrow(long value) => (int)Math.Min(value, int.MaxValue);

    /// <summary>
    /// A file's batch seen as the dataset's rows: row 0 is the object's row <c>StartRow</c>, after
    /// the rows before the object. One view serves the whole stream, bound again per batch, since a
    /// batch is valid until the next one is asked for and a view per batch would be an allocation
    /// per batch.
    /// </summary>
    /// <param name="batch">The file's batch.</param>
    /// <param name="firstRow">The dataset row the object's row 0 is.</param>
    /// <param name="view">The stream's view, made at the first batch that needs one.</param>
    private static RecordBatch InDataset(RecordBatch batch, long firstRow, ref RecordBatch? view)
    {
        if (firstRow == 0)
        {
            return batch;
        }

        if (view is null)
        {
            view = batch.Rebased(firstRow + batch.StartRow);
            return view;
        }

        view.Dispose();
        view.RebindRebased(batch, firstRow + batch.StartRow);
        return view;
    }

    /// <summary>
    /// The most objects the scan will hold open at once, stated before any is read: one in the
    /// tree's order, two with a prefetch, which opens the next object ahead; key-ordered on the
    /// clustering key, level 0's objects, a late level's, and one per other level; on any other column
    /// every object, the summaries giving no disjointness to lean on.
    /// </summary>
    private long CursorBound(long objects, List<long> keptByLevel)
    {
        if (objects == 0)
        {
            return 0;
        }

        if (_orderPaths is not { } paths)
        {
            return objects > 1 && _options.Prefetch > 0 ? 2 : 1;
        }

        if (!_summaries || !RidesDisjointLevels(paths))
        {
            return objects;
        }

        // Level 0's objects, and a late level's, overlap: each may be open at once.
        long cursors = keptByLevel.Count > 0 ? keptByLevel[0] : 0;
        for (int level = 1; level < keptByLevel.Count; level++)
        {
            cursors += _version.Levels.IsDisjoint(level) ? Math.Min(keptByLevel[level], 1) : keptByLevel[level];
        }

        return cursors;
    }

    /// <summary>
    /// Whether a merge on those columns reads the key-disjoint objects of a level one after another:
    /// on the clustering key, upward by the tree's exact minima, downward by summary maxima that the
    /// key order respects, which a composite key has none of.
    /// </summary>
    private bool RidesDisjointLevels(string[] paths) =>
        IsClusteringKey(paths)
        && (!_descending || (paths.Length == 1 && HasKeyOrderBounds(ClusteringKey.Resolve(_version.Schema.DType, paths[0]))));

    private SummaryPruner? Pruner() => _summaries && _filter is { } filter ? new SummaryPruner(filter) : null;

    private IAsyncEnumerable<PositionedObject> WalkAsync(CancellationToken cancellationToken) =>
        _planned is { } planned ? new PlannedObjects(planned, _from, _to) : _version.WalkAsync(Pruner(), _from, _to, _metrics, cancellationToken);

    /// <summary>
    /// The objects of a plan of pieces the rows <c>[from, to)</c> overlap, in their order, from the first found by its
    /// place: what a walk of the tree over those rows gives, with no page read and no pruner made.
    /// </summary>
    private sealed class PlannedObjects(PositionedObject[] planned, long from, long to) : IAsyncEnumerable<PositionedObject>, IAsyncEnumerator<PositionedObject>
    {
        private int _next = First(planned, from);
        private bool _taken;

        public PositionedObject Current { get; private set; }

        public IAsyncEnumerator<PositionedObject> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            // An enumeration of its own each time, as a walk is: the first takes this one.
            if (_taken)
            {
                return new PlannedObjects(planned, from, to).GetAsyncEnumerator(cancellationToken);
            }

            _taken = true;
            return this;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            while (_next < planned.Length && planned[_next].FirstRow < to)
            {
                PositionedObject held = planned[_next++];
                if (held.FirstRow + held.Entry.Rows > from)
                {
                    Current = held;
                    return new ValueTask<bool>(true);
                }
            }

            return new ValueTask<bool>(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>The first of <paramref name="objects"/>, in the order of their rows, that ends past <paramref name="row"/>.</summary>
        private static int First(PositionedObject[] objects, long row)
        {
            int low = 0;
            int high = objects.Length;
            while (low < high)
            {
                int middle = (int)((uint)(low + high) >> 1);
                if (objects[middle].FirstRow + objects[middle].Entry.Rows <= row)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }

    private void RecordOpen(ObjectLease lease, int open = 1)
    {
        if (_metrics is { } metrics)
        {
            metrics.ObjectsOpened++;
            metrics.Cursors = Math.Max(metrics.Cursors, open);
            if (lease.WasCached)
            {
                metrics.CacheHits++;
            }
        }
    }

    private void RecordCounted()
    {
        if (_metrics is { } metrics)
        {
            metrics.ObjectsCounted++;
        }
    }

    /// <summary>The key-ordered read: every kept object's own key-ordered scan, merged.</summary>
    private async IAsyncEnumerable<RecordBatch> MergedAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The key is read whatever the projection says, and dropped before a batch goes out.
        FieldMask mask = _mask ?? FieldMask.All;
        bool drop = !mask.IsAll && !Array.TrueForAll(paths, path => Covers(mask, _version.Schema.DType, path));
        ScanProjection? kept = null;
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _version,
            MergeObjectsAsync(paths, cancellationToken),
            paths,
            _descending,
            rankedTies: true,
            (lease, entry) => OrderedBatchesAsync(lease, entry, paths, drop, cancellationToken),
            lease => RecordOpen(lease),
            cancellationToken);
        RecordBatch? view = null;
        await using (merge.ConfigureAwait(false))
        {
            try
            {
                while (await merge.MoveNextAsync().ConfigureAwait(false))
                {
                    RecordBatch run = merge.Current;
                    long firstRow = merge.CurrentFirstRow;
                    if (!drop)
                    {
                        yield return InDataset(run, firstRow, ref view);
                        continue;
                    }

                    kept ??= ScanProjection.Parse(run.DType, PathsOf(mask, _version.Schema.DType));
                    RecordBatch projected = run.Project(kept.Value);
                    try
                    {
                        yield return InDataset(projected, firstRow, ref view);
                    }
                    finally
                    {
                        projected.Dispose();
                    }
                }
            }
            finally
            {
                if (_metrics is { } metrics)
                {
                    metrics.Cursors = Math.Max(metrics.Cursors, merge.MostOpen);
                }
            }
        }
    }

    /// <summary>
    /// The objects the merge reads, in the order of a lower bound on their keys, each with the rank
    /// its ties go by. On the clustering key upward the tree already is that order, and the walk is
    /// lazy, so a consumer that stops early stops the walk; anywhere else the kept objects are
    /// collected and sorted by their summaries' bound, without being opened. A composite key is
    /// bounded upward by its first column, whose encoded minimum prefixes every tuple above it, and
    /// not downward.
    /// </summary>
    private async IAsyncEnumerable<MergeObject> MergeObjectsAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string path = paths[0];
        if (!_summaries || (!_descending && IsClusteringKey(paths)))
        {
            // Without summaries there is no bound to lean on: every object is opened before a row
            // goes out, and the lazy merge must answer the same as this eager one.
            long ordinal = 0;
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> bound = _summaries ? VortexDataset.OrderOf(held.TreeKey) : default;
                yield return new MergeObject(held.Entry, bound, _descending ? -ordinal : ordinal, held.FirstRow);
                ordinal++;
            }

            yield break;
        }

        DType dtype = ClusteringKey.Resolve(_version.Schema.DType, path);
        RowSortField field = KeyOrderedMerge.Field(_descending);
        List<MergeObject> objects = [];
        long position = 0;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> bound = paths.Length > 1 && _descending
                ? default
                : SummaryBound(held.Entry.Summaries, path, dtype, field, _descending);
            objects.Add(new MergeObject(held.Entry, bound, _descending ? -position : position, held.FirstRow));
            position++;
        }

        objects.Sort(static (left, right) =>
        {
            int order = left.Bound.Span.SequenceCompareTo(right.Bound.Span);
            return order != 0 ? order : left.Rank.CompareTo(right.Rank);
        });
        foreach (MergeObject held in objects)
        {
            yield return held;
        }
    }

    /// <summary>Whether the columns are the dataset's whole clustering key, in order.</summary>
    private bool IsClusteringKey(string[] paths) =>
        _version.Schema.Key is { } key && System.Linq.Enumerable.SequenceEqual(key.Paths, paths, StringComparer.Ordinal);

    /// <summary>
    /// A summary's bound on a column, encoded as the merge compares keys: its minimum upward, its
    /// maximum downward, and empty when the summary gives none that the key order respects. A float
    /// gives none, its zone bounds excluding NaN while the key order places NaN first or last, so an
    /// object holding one would have a key outside its own summary.
    /// </summary>
    private static ReadOnlyMemory<byte> SummaryBound(
        ObjectSummaries summaries, string path, DType dtype, RowSortField field, bool descending)
    {
        if (!HasKeyOrderBounds(dtype) || !summaries.TryGet(path, out ColumnSummary column))
        {
            return default;
        }

        bool known = descending ? column.HasMax : column.HasMin;
        FilterLiteral bound = descending ? column.Max : column.Min;
        if (!known || bound.Kind == FilterLiteralKind.Null)
        {
            return default;
        }

        return RowEncoder.EncodeKey([bound], [dtype], [field]);
    }

    /// <summary>
    /// Whether a column's summary bounds are bounds in the key order (see <see cref="SummaryBound"/>). An
    /// extension's, a timestamp's say, are its storage's, which the key order encodes it as.
    /// </summary>
    private static bool HasKeyOrderBounds(DType dtype) => dtype.Kind switch
    {
        DTypeKind.Primitive => !dtype.PType.IsFloat(),
        DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary => true,
        DTypeKind.Extension => HasKeyOrderBounds(dtype.StorageType),
        _ => false,
    };

    /// <summary>Whether a projection already reads a path, itself or through an ancestor.</summary>
    private static bool Covers(FieldMask mask, DType schema, string path)
    {
        FieldMask at = mask;
        DType type = schema;
        ReadOnlySpan<char> rest = path;
        foreach (Range segment in rest.Split('.'))
        {
            if (at.IsAll)
            {
                return true;
            }

            int field = ColumnPath.IndexOf(type, rest[segment]);
            if (field < 0 || !at.Includes(field))
            {
                return false;
            }

            at = at.Descend(field);
            type = type.GetField(field);
        }

        return true;
    }

    /// <summary>The <c>.</c>-separated paths a projection reads, by the schema's field names.</summary>
    private static string[] PathsOf(FieldMask mask, DType schema)
    {
        List<string> paths = [];
        Collect(mask, schema, string.Empty, paths);
        return [.. paths];

        static void Collect(FieldMask at, DType type, string prefix, List<string> into)
        {
            if (prefix.Length > 0 && (at.IsAll || at.IsEmpty || type.Kind != DTypeKind.Struct))
            {
                into.Add(prefix);
                return;
            }

            for (int i = 0; i < at.NamedFieldCount; i++)
            {
                int field = at.GetNamedField(i);
                string name = type.GetFieldName(field);
                Collect(at.Descend(field), type.GetField(field), prefix.Length == 0 ? name : prefix + "." + name, into);
            }
        }
    }

    /// <summary>One object's own key-ordered scan, carrying this builder's filter, projection and options.</summary>
    private ScanBuilder OrderedOf(VortexFile file, string[] paths, bool withKey)
    {
        ScanBuilder scan = Configure(file.ScanBuilder(), _filter, _mask, canonical: false);
        if (withKey)
        {
            scan.Project(paths);
        }

        return scan.InKeyOrder(paths, _descending);
    }

    /// <summary>
    /// One object's rows in key order, gathered: its own key-ordered scan, reshaped when it was
    /// written under another schema. An object that lacks the key's column holds only null keys,
    /// which come last in either direction, so its rows come in their own order; a tuple holding a
    /// null is in no run, so an object that lacks a column of a composite key delivers none.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> OrderedBatchesAsync(ObjectLease lease, ObjectEntry entry, string[] paths, bool withKey, CancellationToken cancellationToken)
    {
        if (ColumnsOf(lease, entry) is not { } columns)
        {
            return entry.HasDeletions
                ? LiveOrderedAsync(lease.File, entry, paths, withKey, cancellationToken)
                : OrderedOf(lease.File, paths, withKey).ExecuteAsync();
        }

        FieldMask projection = _mask ?? FieldMask.All;
        if (withKey)
        {
            FieldMaskBuilder keyed = new FieldMaskBuilder().Include(in projection);
            foreach (string path in paths)
            {
                keyed.Include(ToolPaths.Resolve(_version.Schema.Columns, path));
            }

            projection = keyed.Build();
        }

        ObjectReadPlan read = columns.Read(_filter, projection);
        string[]? source = columns.SourcePaths(paths);
        if (read.Fate == FilterFate.None || (source is null && paths.Length > 1))
        {
            return System.Linq.AsyncEnumerable.Empty<RecordBatch>();
        }

        ScanBuilder scan = Configure(lease.File.ScanBuilder(), read.Pushed, read.Shape.Source, canonical: true).WithCompaction(true);
        IAsyncEnumerable<RecordBatch> rows;
        if (source is not null)
        {
            scan = scan.InKeyOrder(source, _descending);
            rows = entry.HasDeletions
                ? LiveRows.OrderedAsync(
                    scan,
                    NullsOf(lease.File, source[0], paths.Length > 1, read.Pushed, read.Shape.Source),
                    entry.Deletions,
                    _descending,
                    cancellationToken)
                : scan.ExecuteAsync();
        }
        else
        {
            // Every key is null, and null keys come last in their own order either way.
            rows = entry.HasDeletions
                ? LiveRows.WithoutDeletedAsync(scan.WithCompaction(false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken)
                : scan.ExecuteAsync();
        }

        return ObjectColumns.ReshapeAsync(rows, read.Shape, read.Evaluated, 0, compact: true, cancellationToken);
    }

    /// <summary>
    /// How an object answers for the version's columns: null when it holds exactly them, the case of
    /// every object written since the schema last changed, whose own scan then serves unchanged.
    /// </summary>
    private ObjectColumns? ColumnsOf(ObjectLease lease, ObjectEntry entry) => _version.Schema.ColumnsOf(lease.File.DType, entry.Key);

    /// <summary>
    /// An object's part of this read of the columns <paramref name="mask"/> names: what the filter is
    /// for it, from <paramref name="columns"/> for an object of an earlier schema, and for one of the
    /// version's own the filter whole, which its scan takes as it is. What an object was written under
    /// is settled here, once, and every terminal goes by the filter's fate alone.
    /// </summary>
    private ObjectPart PartOf(ObjectColumns? columns, FieldMask? mask)
    {
        if (columns is null)
        {
            return new ObjectPart(_filter is null ? FilterFate.Every : FilterFate.Pushed, _filter, mask, null);
        }

        ObjectReadPlan read = columns.Read(_filter, mask ?? FieldMask.All);
        return new ObjectPart(read.Fate, read.Pushed, read.Shape.Source, read);
    }

    /// <summary>The object's own scan for its part of the read: the filter it takes and the fields it reads, decoded whole when its batches are to be reshaped.</summary>
    private ScanBuilder ScanOf(VortexFile file, PositionedObject held, in ObjectPart part) =>
        Of(file, held, part.Pushed, part.Source, canonical: part.Mapped is not null);

    /// <summary>
    /// The batches of an object that its own scan alone does not read as the version's rows, one of an
    /// earlier schema or one with rows marked, counted from <paramref name="baseRow"/>; null when the
    /// filter can keep no row of it. The one place where the steps compose, in this order whatever
    /// reads the batches: the object's scan, its marked rows taken out or deselected, the batches
    /// reshaped to the version's columns, and the filter evaluated on them where it could not go to
    /// the scan.
    /// </summary>
    private IAsyncEnumerable<RecordBatch>? BatchesOfAsync(
        VortexFile file, PositionedObject held, in ObjectPart part, long baseRow, bool compact, CancellationToken cancellationToken)
    {
        if (part.Fate == FilterFate.None)
        {
            return null;
        }

        return part.Mapped is { } read
            ? ReshapedAsync(file, held, read, baseRow, compact, cancellationToken)
            : LiveAsync(file, held, part.Pushed, part.Source, baseRow, compact, reshaped: false, cancellationToken);
    }

    /// <summary>An object of another schema read as the version's: its own scan, each batch reshaped and counted from <paramref name="baseRow"/>.</summary>
    private IAsyncEnumerable<RecordBatch> ReshapedAsync(
        VortexFile file, PositionedObject held, ObjectReadPlan read, long baseRow, bool compact, CancellationToken cancellationToken) =>
        ObjectColumns.ReshapeAsync(
            held.Entry.HasDeletions
                ? LiveAsync(file, held, read.Pushed, read.Shape.Source, 0, compact && read.Evaluated is null, reshaped: true, cancellationToken)
                : Of(file, held, read.Pushed, read.Shape.Source, canonical: true).ExecuteAsync(),
            read.Shape,
            read.Evaluated,
            baseRow,
            compact,
            cancellationToken);

    /// <summary>
    /// The rows of an object its part of the read keeps; with <paramref name="stopAtOne"/>, 1 at the
    /// first. None when the filter keeps none, every row the scan covers when it keeps all, the
    /// object's own count when the filter goes to its scan, which leaves its marked rows out in the
    /// same pass, and the rows selected on its reshaped batches otherwise.
    /// </summary>
    private async ValueTask<long> CountOfAsync(
        VortexFile file, PositionedObject held, ObjectPart part, bool stopAtOne, CancellationToken cancellationToken)
    {
        switch (part.Fate)
        {
            case FilterFate.None:
                return 0;
            case FilterFate.Every:
                return stopAtOne ? Math.Min(Covered(held), 1) : Covered(held);
            case FilterFate.Pushed:
                return stopAtOne
                    ? await LiveAnyAsync(file, held, part.Pushed!, cancellationToken).ConfigureAwait(false) ? 1 : 0
                    : await LiveCountAsync(file, held, part.Pushed!, cancellationToken).ConfigureAwait(false);
            default:
                long rows = 0;
                await foreach (RecordBatch batch in BatchesOfAsync(file, held, part, 0, compact: false, cancellationToken)!.ConfigureAwait(false))
                {
                    rows += batch.SelectedRows;
                    if (stopAtOne && rows > 0)
                    {
                        break;
                    }
                }

                return rows;
        }
    }

    /// <summary>
    /// The extreme of a column among the rows an object keeps: none when it lacks the column; the
    /// object's own extreme, under the column's name there, when the filter goes to its scan and no
    /// row is marked; its live extreme when rows are; and the extreme of its reshaped batches when
    /// the filter is evaluated on them.
    /// </summary>
    private async ValueTask<FilterLiteral> ObjectExtremeAsync(
        VortexFile file, PositionedObject held, ObjectColumns? columns, string path, FieldMask projection, bool wantMin, CancellationToken cancellationToken)
    {
        string? named = columns is null ? path : columns.SourcePath(path);
        ObjectPart part = PartOf(columns, projection);
        if (named is null || part.Fate == FilterFate.None)
        {
            return FilterLiteral.Null;
        }

        if (part.Fate == FilterFate.Reshaped)
        {
            return await ExtremeOfAsync(BatchesOfAsync(file, held, part, 0, compact: true, cancellationToken)!, path, wantMin).ConfigureAwait(false);
        }

        if (held.Entry.HasDeletions)
        {
            return await LiveExtremeAsync(file, held, part, named, path, wantMin, cancellationToken).ConfigureAwait(false);
        }

        ScanBuilder scan = Of(file, held, part.Pushed, null, canonical: false);
        return wantMin
            ? await scan.MinAsync(named, cancellationToken).ConfigureAwait(false)
            : await scan.MaxAsync(named, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file's own scan, carrying this builder's filter, projection, options and the rows that fall in this object.</summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held) => Of(file, held, _filter, _mask, canonical: false);

    /// <summary>
    /// An object's part of a read, whatever schema it was written under: what the filter is for it,
    /// the filter its own scan takes, the fields that scan reads, and how its batches become the
    /// version's rows; none for an object of the version's own columns, whose batches already are.
    /// </summary>
    private readonly record struct ObjectPart(FilterFate Fate, VortexExpr? Pushed, FieldMask? Source, ObjectReadPlan? Mapped);

    /// <summary>
    /// The file's own scan under <paramref name="filter"/> and <paramref name="mask"/>, with this
    /// builder's options and the rows that fall in this object; with <paramref name="canonical"/>,
    /// every column decoded, which the reshaping of another schema's batches takes.
    /// </summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, bool canonical)
    {
        ScanBuilder scan = Configure(file.ScanBuilder(), filter, mask, canonical);
        DeletionVector deletions = held.Entry.Deletions;
        if (_take is { } take)
        {
            // A row taken by its place among the dataset's live rows is at that place among the
            // object's live rows, which the deleted rows before it push further on.
            (int first, int count) = TakenBy(held);
            long[] local = new long[count];
            for (int i = 0; i < count; i++)
            {
                local[i] = deletions.Physical(take[first + i] - held.FirstRow);
            }

            return scan.Take(local);
        }

        // The range is the dataset's; the file's is the part of it that falls inside this object.
        (long lower, long upper) = PhysicalRange(held);

        // An object sliced by time holds its rows slice by slice, every row's instant within its slice's bounds, so the
        // filter's window on the instant is a range of its rows: the slices it reaches. The scan's plan, its zone maps and
        // its walk then cover those alone, where they covered every row of the object at every question, the window's
        // slices a day's object's 1 440 minutes for a window of an hour.
        long low = long.MinValue;
        long high = long.MaxValue;
        if (held.Entry.Deletions.IsEmpty && filter is not null && _dataset.Key is { } key && Bound(filter, key.InstantPath, ref low, ref high)
            && held.Entry.Contents is { } contents && WindowRows(contents.Slices.Slices, low, high) is { } window)
        {
            lower = Math.Max(lower, window.Start);
            upper = Math.Min(upper, window.End);
        }

        if (lower > 0 || upper < held.Entry.PhysicalRows)
        {
            scan.Rows(new RowRange(lower, Math.Max(upper, lower)));
        }

        return scan;
    }

    /// <summary>
    /// Narrows <paramref name="low"/> and <paramref name="high"/>, inclusive, by the conjuncts of <paramref name="filter"/>
    /// that compare <paramref name="instant"/> itself, as an answer reads its window (<see cref="SynopsisAnswer.Bound"/>);
    /// whether one did. A walk of the conjunctions, which allocates nothing.
    /// </summary>
    internal static bool Bound(VortexExpr filter, string instant, ref long low, ref long high)
    {
        if (filter is LogicalExpr { IsAnd: true } both)
        {
            bool left = Bound(both.Left, instant, ref low, ref high);
            return Bound(both.Right, instant, ref low, ref high) | left;
        }

        return SynopsisAnswer.Bound(filter, instant, ref low, ref high);
    }

    /// <summary>
    /// The rows of the slices of <paramref name="slices"/> the window from <paramref name="low"/> to <paramref name="high"/>,
    /// inclusive, reaches, from the first's first row to the last's last, empty when it reaches none; null when the object
    /// has no slice.
    /// </summary>
    internal static RowRange? WindowRows(IReadOnlyList<Indexes.TimeSlice> slices, long low, long high)
    {
        if (slices.Count == 0)
        {
            return null;
        }

        // A slice [Start, End) the window [low, high] reaches: one that ends past its low and starts at its high or before.
        int first = 0;
        while (first < slices.Count && slices[first].End <= low)
        {
            first++;
        }

        int last = slices.Count - 1;
        while (last >= first && slices[last].Start > high)
        {
            last--;
        }

        return last < first
            ? RowRange.Empty
            : new RowRange(slices[first].FirstRow, slices[last].FirstRow + slices[last].Rows);
    }

    /// <summary>
    /// The part of the scan's range inside an object, at the object's own positions: the range
    /// counts live rows, and the deleted rows before and inside it push it further on.
    /// </summary>
    private (long Lower, long Upper) PhysicalRange(in PositionedObject held)
    {
        long lower = Math.Max(_from - held.FirstRow, 0);
        long upper = _to == long.MaxValue ? held.Entry.Rows : Math.Min(_to - held.FirstRow, held.Entry.Rows);
        DeletionVector deletions = held.Entry.Deletions;
        if (deletions.IsEmpty)
        {
            return (lower, upper);
        }

        long first = deletions.Physical(lower);
        return (first, upper <= lower ? first : deletions.Physical(upper - 1) + 1);
    }

    /// <summary>
    /// An object's rows under <paramref name="filter"/> and <paramref name="mask"/> without the
    /// ones it deleted: its own scan run without compaction, so that the deleted rows can be found
    /// at their places, then taken out, which gathers decoded columns, and the rest numbered from
    /// <paramref name="baseRow"/>. Batches not compacted that nobody reads a place of keep their rows
    /// instead, the deleted ones deselected, and their columns encoded as an object without deletions
    /// delivers them; <paramref name="reshaped"/> decodes every column all the same, for the
    /// reshaping of another schema's batches.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> LiveAsync(
        VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, long baseRow, bool compact, bool reshaped, CancellationToken cancellationToken)
    {
        bool deselect = !compact && _positionsUnread;
        IAsyncEnumerable<RecordBatch> rows = Of(file, held, filter, mask, canonical: reshaped || !deselect).WithCompaction(false).ExecuteAsync();
        return deselect
            ? LiveRows.DeselectedAsync(rows, held.Entry.Deletions, baseRow, cancellationToken)
            : LiveRows.WithoutDeletedAsync(rows, held.Entry.Deletions, compact, baseRow, cancellationToken);
    }

    /// <summary>
    /// The rows of an object <paramref name="filter"/> keeps, the deleted ones not counted: its own
    /// count with them left out, cheapest proof first, in one pass. A take of the dataset's rows
    /// names live rows only.
    /// </summary>
    private async ValueTask<long> LiveCountAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false);
        return _take is null && held.Entry.HasDeletions
            ? await scan.CountExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="filter"/> keeps a live row of an object, the first one found answering.</summary>
    private async ValueTask<bool> LiveAnyAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false);
        return _take is null && held.Entry.HasDeletions
            ? await scan.AnyExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The extreme of a column among an object's live rows, under the filter its part of the read
    /// gives its scan, the column <paramref name="named"/> as the object names it. A column the
    /// object's statistics say is sorted and holds no null nor NaN has it at its first or its last
    /// live row, when every row is kept: the object's own extreme while that row is the column's end,
    /// and that row's value, one row read, once a delete took the end. Otherwise the object's own
    /// extreme is the answer when a live row holds it, which a count of the rows equal to it settles;
    /// when every row holding it is gone, or the column is a float whose equality a count cannot
    /// settle, its live rows are read for it, as the version's columns, under <paramref name="path"/>.
    /// </summary>
    private async ValueTask<FilterLiteral> LiveExtremeAsync(
        VortexFile file, PositionedObject held, ObjectPart part, string named, string path, bool wantMin, CancellationToken cancellationToken)
    {
        if (part.Fate == FilterFate.Every && Whole(held) && SortedWithoutGaps(file, named))
        {
            ObjectEntry entry = held.Entry;
            long end = wantMin ? 0 : entry.PhysicalRows - 1;
            long live = entry.Deletions.Physical(wantMin ? 0 : entry.Rows - 1);
            ScanBuilder ends = Configure(file.ScanBuilder(), null, null, canonical: false);
            if (live != end)
            {
                ends.Take([live]);
            }

            return wantMin
                ? await ends.MinAsync(named, cancellationToken).ConfigureAwait(false)
                : await ends.MaxAsync(named, cancellationToken).ConfigureAwait(false);
        }

        DType column = ClusteringKey.Resolve(file.DType, named);
        if (column.Kind is DTypeKind.Utf8 or DTypeKind.Binary or DTypeKind.Bool or DTypeKind.Decimal
            || (column.Kind == DTypeKind.Primitive && !column.PType.IsFloat()))
        {
            ScanBuilder scan = Of(file, held, part.Pushed, null, canonical: false);
            FilterLiteral found = wantMin
                ? await scan.MinAsync(named, cancellationToken).ConfigureAwait(false)
                : await scan.MaxAsync(named, cancellationToken).ConfigureAwait(false);
            if (found.Kind == FilterLiteralKind.Null)
            {
                return found;
            }

            VortexExpr equal = Expr.Eq(Expr.Field(named), Expr.Literal(found));
            if (await LiveCountAsync(file, held, part.Pushed is null ? equal : Expr.And(part.Pushed, equal), cancellationToken).ConfigureAwait(false) > 0)
            {
                return found;
            }
        }

        return await ExtremeOfAsync(BatchesOfAsync(file, held, part, 0, compact: true, cancellationToken)!, path, wantMin).ConfigureAwait(false);
    }

    /// <summary>Whether a file's statistics say a top-level column is sorted, and holds no null and no NaN.</summary>
    private static bool SortedWithoutGaps(VortexFile file, string path)
    {
        if (!KeyCursorBuilder.StatedSorted(file, path))
        {
            return false;
        }

        int field = file.DType.IndexOfField(path);
        FieldStatistics statistics = file.FileStatistics.GetField(field);
        bool floats = file.DType.GetField(field) is { Kind: DTypeKind.Primitive } column && column.PType.IsFloat();
        return statistics.TryGetStoredNullCount(out ulong nulls) && nulls == 0
            && (!floats || (statistics.TryGetNanCount(out ulong nans) && nans == 0));
    }

    /// <summary>The extreme of a column over a stream of batches that hold it.</summary>
    private static async ValueTask<FilterLiteral> ExtremeOfAsync(IAsyncEnumerable<RecordBatch> batches, string path, bool wantMin)
    {
        FilterLiteral best = FilterLiteral.Null;
        await foreach (RecordBatch batch in batches.ConfigureAwait(false))
        {
            int node = ColumnPath.NodeOf(batch, path);
            if (!Extremes.TryFind(batch.Arena, node, default, listed: false, wantMin, out int row))
            {
                continue;
            }

            bool numeric = batch.Arena.GetNode(ComparisonKernels.Unwrap(batch.Arena, node)).DType.Kind == DTypeKind.Decimal;
            bool read = numeric
                ? LiteralReader.TryReadDecimal(batch.Arena, node, row, out FilterLiteral value)
                : LiteralReader.TryRead(batch.Arena, node, row, out value);
            if (read && Beats(value, best, wantMin))
            {
                best = value;
            }
        }

        return best;
    }

    /// <summary>
    /// An object's rows in key order without the ones it deleted: its own key-ordered scan, which
    /// skips their entries as it walks, then the live rows whose key is null.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> LiveOrderedAsync(VortexFile file, ObjectEntry entry, string[] paths, bool withKey, CancellationToken cancellationToken)
    {
        FieldMask? projection = _mask;
        if (withKey && _mask is { } mask)
        {
            FieldMaskBuilder keyed = new FieldMaskBuilder().Include(in mask);
            foreach (string path in paths)
            {
                keyed.Include(ToolPaths.Resolve(_version.Schema.Columns, path));
            }

            projection = keyed.Build();
        }

        return LiveRows.OrderedAsync(
            OrderedOf(file, paths, withKey),
            NullsOf(file, paths[0], paths.Length > 1, _filter, projection),
            entry.Deletions,
            _descending,
            cancellationToken);
    }

    /// <summary>
    /// The scan of an object's rows whose key is null under a filter and a projection, decoded; null
    /// when the key cannot be null there, or is composite, whose run holds no tuple with a null.
    /// </summary>
    private ScanBuilder? NullsOf(VortexFile file, string path, bool composite, VortexExpr? filter, FieldMask? projection)
    {
        if (composite || !ColumnPath.MayBeNull(file.DType, path))
        {
            return null;
        }

        VortexExpr isNull = Expr.IsNull(Expr.Field(path));
        return Configure(file.ScanBuilder(), filter is null ? isNull : Expr.And(isNull, filter), projection, canonical: true);
    }

    /// <summary>A file's scan under a filter, a projection and this builder's options.</summary>
    private ScanBuilder Configure(ScanBuilder scan, VortexExpr? filter, FieldMask? projection, bool canonical)
    {
        if (filter is not null)
        {
            scan.Where(filter);
        }

        // An object read in the dataset's own columns reads its variants by keys where its path map
        // allows; one reshaped from an earlier schema reads them whole.
        if (!canonical)
        {
            scan.WithVariantKeys(_variantKeys);
        }

        if (projection is { } mask)
        {
            scan.ProjectMask(in mask);
        }

        if (_options.BatchRows > 0)
        {
            scan.WithMaxBatchRows(_options.BatchRows);
        }

        int degree = _options.DegreeOfParallelism > 0
            ? _options.DegreeOfParallelism
            : _dataset.Session.Options.MaxDegreeOfParallelism;
        scan.WithPruning(_options.UseStatistics)
            .WithIndexes(_indexes)
            .WithDegreeOfParallelism(Math.Max(degree, 1))
            .WithPrefetch(_options.Prefetch)
            .WithCompaction(_options.Compact)
            .WithEncodings(!canonical && _keepEncodings, !canonical && _sinkDecodes);
        if (_readAhead > 0)
        {
            scan.WithReadAhead(_readAhead);
        }

        return _counters is { } counters ? scan.WithMetrics(counters) : scan;
    }
}

/// <summary>One data object and where its rows start in the dataset.</summary>
/// <param name="Entry">Its leaf entry, summaries included.</param>
/// <param name="FirstRow">Its first row among the dataset's, in the tree's order.</param>
internal readonly record struct PositionedObject(ObjectEntry Entry, long FirstRow)
{
    /// <summary>The level whose tree holds it; level 0 is the one an append lands in.</summary>
    public int Level { get; init; }

    /// <summary>Its key in that tree: what orders it, then its uid.</summary>
    internal ReadOnlyMemory<byte> TreeKey { get; init; }
}

/// <summary>What a dataset scan would read, and what it costs.</summary>
/// <param name="Version">The version it would read.</param>
/// <param name="ObjectsByLevel">How many objects each level holds, level 0 first.</param>
/// <param name="Lag">
/// The objects level 0 holds above its ceiling: the extra objects every key lookup has to touch
/// until compaction catches up, and zero when it has.
/// </param>
/// <param name="IsClustered">
/// Whether a clustering key is declared. Without one, levels are size tiers and a lookup touches
/// every object the summaries cannot refute, which is output-sensitive and not bounded.
/// </param>
/// <param name="Objects">The objects the scan would open.</param>
/// <param name="Rows">Their rows, before the filter.</param>
/// <param name="ObjectsSkipped">Objects whose own summaries refuted the predicate.</param>
/// <param name="SubtreesSkipped">Child pages a node's summaries refuted, unread.</param>
/// <param name="Order">The column a key-ordered scan delivers its rows by, or null for the tree's order.</param>
/// <param name="Cursors">
/// The most objects the scan will hold open at once: one in the tree's order, two with a prefetch,
/// which opens the next object ahead; key-ordered on the clustering key, level 0's objects and one
/// per level above it; on any other column, every object it would open.
/// </param>
internal sealed record DatasetPlan(
    ulong Version,
    IReadOnlyList<long> ObjectsByLevel,
    long Lag,
    bool IsClustered,
    long Objects,
    long Rows,
    long ObjectsSkipped,
    long SubtreesSkipped,
    string? Order,
    long Cursors);
