using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
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

    /// <summary>Objects whose column sheets proved a value the predicate asks for absent, so nothing but the sheets was read.</summary>
    public long ObjectsRefutedBySheets { get; internal set; }

    /// <summary>Objects the scan went to read. <see cref="CacheHits"/> of them cost no open.</summary>
    public long ObjectsOpened { get; internal set; }

    /// <summary>Objects whose rows an identifier's index found, read with no open of the object.</summary>
    public long ObjectsLookedUp { get; internal set; }

    /// <summary>Child pages a node's summaries refuted, each a whole subtree never read.</summary>
    public long SubtreesSkipped { get; internal set; }

    /// <summary>Rents the object cache answered without opening anything.</summary>
    public long CacheHits { get; internal set; }

    /// <summary>The objects an answer took from their entries, without opening them.</summary>
    public long ObjectsCounted { get; internal set; }

    /// <summary>
    /// Objects whose counts answered an aggregation as states; <see cref="StateRows"/> rows of states stood for their rows.
    /// </summary>
    public long ObjectsAnswered { get; internal set; }

    /// <summary>The rows of states the answered objects gave, an entry of their counts each.</summary>
    public long StateRows { get; internal set; }

    /// <summary>Rollups whose totals answered an aggregation as states, each for the objects it covers, which <see cref="ObjectsAnswered"/> counts.</summary>
    public long RollupsAnswered { get; internal set; }

    /// <summary>Blocks of objects an aggregation took from their zone maps, their rows not read: blocks no delete marked a row of.</summary>
    public long BlocksSettled { get; internal set; }

    /// <summary>
    /// Blocks of objects whose columns' statistics over them answered an aggregation as states (<see cref="BlockAnswer"/>),
    /// their rows not read; <see cref="StateRows"/> counts the rows of states they gave.
    /// </summary>
    public long BlocksAnswered { get; internal set; }

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

    // The filter's lookup of an identifier, worked out the first time an object asks (Lookup).
    private VortexExpr? _lookup;
    private bool _lookupKnown;

    // What an aggregation asks as states in place of rows, which the objects' counts answer where they can (StatesAsync).
    private StateRequest? _states;

    // The aggregation whose blocks each object read as rows settles by its zone maps, where no delete marked a row of them.
    private AggregationPlan? _settles;

    // The proof, by an object's summaries, that the filter keeps the object whole, made once: a filter on the key's first
    // column alone; null for one that reads another column, under which no block settles (MaySettle).
    private SummaryPruner? _whole;
    private bool _wholeKnown;

    // How the objects' counts answer _states, worked out the first time a read asks (Answer); null when they answer none.
    private CountsAnswer? _answer;
    private bool _answerKnown;
    private BlockAnswer? _blockAnswer;
    private bool _blockAnswerKnown;

    // The ranges of each object's rows a selected row may lie in, by where its columns' values live, for the objects whose
    // indexes leave rows out (SheetPruning.KeptAsync); read by the lanes that read the objects. Made at the first such
    // object: a scan that meets none allocates nothing for them.
    private System.Collections.Concurrent.ConcurrentDictionary<UInt128, RowRange[]>? _narrowed;
    private Action<PositionedObject, RowRange[]>? _narrowing;

    // Whether the walk counts the objects it considers: not in a part of a scan whose own walk counted them (Part).
    private bool _considers = true;

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
    /// Answers what the objects' counts can of <paramref name="states"/> as batches of states, and the rest as rows;
    /// null for rows alone.
    /// </summary>
    internal DatasetScanBuilder WithStates(StateRequest? states)
    {
        _states = states;
        return this;
    }

    /// <summary>
    /// Settles, for <paramref name="plan"/>, the blocks of each object it reads as rows whose zone maps answer its keys and
    /// aggregates, and that hold no row a delete marked: each folded as a block of its zones' bounds, its rows not read.
    /// </summary>
    internal DatasetScanBuilder WithSettles(AggregationPlan? plan)
    {
        _settles = plan;
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
    /// The splits each object's scan of one lane reads ahead of the one it decodes, over a store whose read is a round
    /// trip (<see cref="ScanSpec.ReadAhead"/>): what an aggregation's lane is granted, which an object's scan reads as a
    /// file's would.
    /// </summary>
    internal DatasetScanBuilder WithReadAhead(int splits)
    {
        _readAhead = splits;
        _readAheadGranted = true;
        return this;
    }

    private int _readAhead;

    /// <summary>Whether an aggregation granted the scan its reads ahead, none among them, which its objects read as granted.</summary>
    private bool _readAheadGranted;

    /// <summary>
    /// Has the scan of one lane of an object read ahead, over a store whose read is a round trip, as many of its splits as
    /// a pass's lanes keep in flight together (<see cref="AggregationEngine.TotalAhead"/>), reserved in the session's
    /// budget as a pass reserves them: half as many at a time as the budget grants fewer, and none below two, the scan
    /// then decoding a batch ahead as before. The reservation, held while the scan reads; null when it reads none ahead:
    /// an aggregation's lane, granted its own, a scan of several lanes, of a take, or of a file that costs no round trip.
    /// </summary>
    private QueryMemory? ReadsAhead(ScanBuilder scan, VortexFile file)
    {
        int degree = _options.DegreeOfParallelism > 0 ? _options.DegreeOfParallelism : _dataset.Session.Options.MaxDegreeOfParallelism;
        if (_readAheadGranted || degree > 1 || _options.Prefetch > 1 || _take is not null || _orderPaths is not null
            || file.Source.CostsNoRoundTrip || scan.LargestSplitBytes() is not (> 0 and var split))
        {
            return null;
        }

        QueryMemory memory = new QueryMemory(_dataset.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        int ahead = AggregationEngine.TotalAhead(split);
        while (ahead >= BatchAsyncEnumerator.ReadAheadSplits && !memory.TryGrow(ahead * split))
        {
            ahead /= 2;
        }

        if (ahead < BatchAsyncEnumerator.ReadAheadSplits)
        {
            memory.Dispose();
            return null;
        }

        scan.WithReadAhead(ahead);
        return memory;
    }

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

        if (Answer() is { } answer && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in StatesAsync(answer, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        if (Lookup is { } lookup)
        {
            await foreach (RecordBatch batch in LookedUpAsync(lookup, cancellationToken).ConfigureAwait(false))
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

    /// <summary>One object's batches as the dataset's rows, its lease held until the last is read.</summary>
    private IAsyncEnumerable<RecordBatch> ObjectAsync(PositionedObject held, int open, CancellationToken cancellationToken) =>
        RowsAsync(held, open, cancellationToken);

    /// <summary>How the objects' counts answer the states the scan asks, worked out once; null when they answer none of them.</summary>
    private CountsAnswer? Answer()
    {
        if (!_answerKnown)
        {
            _answer = _states is { } request ? CountsAnswer.Of(request, _filter, _version) : null;
            _answerKnown = true;
        }

        return _answer;
    }

    /// <summary>How the statistics of the objects' blocks answer the states the scan asks, worked out once; null when they answer none.</summary>
    private BlockAnswer? BlockAnswer()
    {
        if (!_blockAnswerKnown)
        {
            _blockAnswer = _states is { } request && Answer() is { } counts ? Dataset.BlockAnswer.Of(request, _filter, _version, counts) : null;
            _blockAnswerKnown = true;
        }

        return _blockAnswer;
    }

    /// <summary>
    /// Whether the statistics of the blocks of the object <paramref name="held"/>, which the scan covers whole, may answer it;
    /// the answer worked out at the first object that keeps them.
    /// </summary>
    private bool BlocksAnswer(in PositionedObject held) =>
        held.Entry.Contents is { BlockStats.Count: > 0 } && Covered(held) == held.Entry.Rows && BlockAnswer() is { } blocks && blocks.MayAnswer(held);

    /// <summary>The column whose counts answer the object <paramref name="held"/> for <paramref name="answer"/>: one the scan covers whole; null for its rows.</summary>
    private int? AnsweredBy(CountsAnswer answer, in PositionedObject held) =>
        Covered(held) == held.Entry.Rows ? answer.FieldOf(held) : null;

    /// <summary>Whether the counts of an object the scan covers whole answer what it asks as states: read from the entries alone.</summary>
    internal async ValueTask<bool> AnswersAnyAsync(CancellationToken cancellationToken)
    {
        if (Answer() is not { } answer || !await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await foreach (PositionedObject held in _version.WalkAsync(Pruner(), _from, _to, null, cancellationToken).ConfigureAwait(false))
        {
            if (AnsweredBy(answer, held) is not null || BlocksAnswer(held))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the scan would cost, by the formula, before reading anything: each object it covers through the path its read
    /// takes, its column's counts, its blocks' statistics, or the rows of the columns it reads (<see cref="PathCosts"/>). An
    /// object a sheet or an index would pass over counts whole: the cost is a bound.
    /// </summary>
    internal async ValueTask<PathCosts.Cost> CostAsync(CancellationToken cancellationToken)
    {
        CountsAnswer? answer = Answer() is { } asked && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false) ? asked : null;
        int fieldCount = _version.Schema.DType.IsDefault ? 0 : _version.Schema.DType.FieldCount;
        int[] fields;
        if (_mask is { } mask)
        {
            fields = new int[mask.SelectedCount(fieldCount)];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = mask.SelectedField(i);
            }
        }
        else
        {
            fields = [.. System.Linq.Enumerable.Range(0, fieldCount)];
        }

        long requests = 0;
        long bytes = 0;
        await foreach (PositionedObject held in _version.WalkAsync(Pruner(), _from, _to, null, cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            PathCosts.Cost cost = answer is not null && AnsweredBy(answer, held) is int field
                ? PathCosts.Regions([held.Entry.Contents!.CountsOf(field), answer.AsksSketches ? held.Entry.Contents.SketchesOf(field) : null])
                : BlocksAnswer(held) ? BlockAnswer()!.Costs(held).Blocks : PathCosts.Rows(held.Entry, fields);
            requests += cost.Requests;
            bytes += cost.Bytes;
        }

        return new PathCosts.Cost((int)Math.Min(requests, int.MaxValue), bytes);
    }

    /// <summary>
    /// The scan as states where the objects' counts answer: each such object's entries as batches of states; the objects
    /// between them read as rows, those side by side a part of the scan, which
    /// reads them as it would (<see cref="Part"/>). The counts of the objects that answer are read side by side, as many at
    /// once as the session reads, and none of their sheets: their counts say which values they hold.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> StatesAsync(CountsAnswer answer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The rollups a scan of the whole version takes, each standing for the objects it covers, which the walk passes
        // over; then the objects the walk meets that no rollup covers. The rollups come first, as many as rolled holds.
        RollupChoice? rollups = await RollupsAsync(answer, cancellationToken).ConfigureAwait(false);
        List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)>? rolled = rollups is null ? null : Rolled(rollups);
        List<(PositionedObject Held, int? Field)> objects = [];
        if (rollups is not null)
        {
            foreach ((RollupEntry _, int field, ObjectSummaries?[]? _) in rollups.Chosen)
            {
                objects.Add((default, field));
            }
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken, answer, rollups).ConfigureAwait(false))
        {
            if (Covered(held) > 0 && rollups?.CoverOf(held) is not >= 0)
            {
                objects.Add((held, AnsweredBy(answer, held)));
            }
        }

        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[] reads = new Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[objects.Count];
        int width = Math.Max(1, _dataset.Session.Options.MaxConcurrentReads);
        CanonicalArena arena = new CanonicalArena(answer.Nodes);

        // The statistics over the blocks of the objects the counts do not answer and whose blocks may, by the place of the
        // object, read ahead as the counts are; made at the first such object.
        Dictionary<UInt128, Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]>>? blockReads = null;
        try
        {
            for (int o = 0; o < objects.Count; o++)
            {
                // The counts of the next objects and rollups read ahead, as many ahead as the session reads at once; a read
                // begun, or none to begin, is no longer null.
                for (int ahead = o; ahead < objects.Count && ahead < o + width; ahead++)
                {
                    reads[ahead] ??= objects[ahead].Field is not { } next ? NoCounts
                        : ahead < rolled?.Count
                            ? _dataset.ReadRollupCountsAsync(rolled[ahead].Rollup, next, answer.AsksSketches, answer.Values, rolled[ahead].Buckets is not null, stopped.Token).AsTask()
                        : objects[ahead].Held.Entry.HasDeletions
                            ? LiveCountsAsync(objects[ahead].Held.Entry, next, stopped.Token)
                        : answer.Exclusion is { } exclusion
                            ? KeptCountsAsync(answer, objects[ahead].Held.Entry, next, exclusion.Field, exclusion.Found, stopped.Token)
                        : _dataset.ReadCountsAsync(objects[ahead].Held.Entry, next, answer.AsksSketches, stopped.Token).AsTask();
                    if (objects[ahead].Field is null && ahead >= (rolled?.Count ?? 0) && (blockReads is null || !blockReads.ContainsKey(objects[ahead].Held.Entry.Uid))
                        && BlocksAnswer(objects[ahead].Held))
                    {
                        (blockReads ??= [])[objects[ahead].Held.Entry.Uid] = _dataset.ReadBlockColumnsAsync(objects[ahead].Held.Entry, BlockAnswer()!.Fields, stats: true, stopped.Token).AsTask();
                    }
                }

                // A rollup taken by bucket gives the states of each bucket the filter does not refute.
                PositionedObject held = objects[o].Held;
                RollupEntry? taken = o < rolled?.Count ? rolled[o].Rollup : null;
                ObjectSummaries?[]? buckets = o < rolled?.Count ? rolled[o].Buckets : null;
                StateRows? rows = null;
                List<StateRows>? byBucket = null;
                if (reads[o] is { } read && read != NoCounts)
                {
                    reads[o] = NoCounts;
                    if (await read.ConfigureAwait(false) is ({ } counts, var sketched))
                    {
                        if (buckets is null)
                        {
                            rows = answer.Rows(counts, sketched, taken is not null ? taken.Summaries : answer.SummariesOf(held));
                        }
                        else
                        {
                            byBucket = BucketRows(answer, counts, sketched, buckets);
                        }
                    }
                }

                if (rows is null && byBucket is null && taken is not null)
                {
                    // A rollup whose totals did not answer after all: its objects, one by one, after the others, the rollups
                    // left first; their reads begin as the loop meets them, the reads begun before them kept.
                    List<(PositionedObject Held, int? Field)> spread = await SpreadAsync(taken, answer, cancellationToken).ConfigureAwait(false);
                    objects.RemoveAt(o);
                    rolled!.RemoveAt(o);
                    objects.AddRange(spread);
                    Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[] grown = new Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[objects.Count];
                    Array.Copy(reads, grown, o);
                    Array.Copy(reads, o + 1, grown, o, reads.Length - o - 1);
                    reads = grown;
                    o--;
                    continue;
                }

                if (rows is null && byBucket is null && taken is null && blockReads is not null && blockReads.Remove(held.Entry.Uid, out Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]>? blockRead))
                {
                    // An object whose blocks answer where its counts do not: the states of the blocks its statistics answer,
                    // then the rows of the others, each run of them a part of the scan.
                    // The blocks that hold a row a delete marked are read as rows too.
                    (Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[] blockColumns = await blockRead.ConfigureAwait(false);
                    await held.Entry.ResolveAsync(_version.Pages, cancellationToken).ConfigureAwait(false);
                    (BlockStateRows? settled, List<RowRange> unsettled, int answered) = BlockAnswer()!.Settle(held, blockColumns, held.Entry.Deletions);
                    if (_metrics is { } counted)
                    {
                        counted.BlocksAnswered += answered;
                        counted.StateRows += settled?.Count ?? 0;
                    }

                    for (int at = 0; settled is not null && at < settled.Count; at += StateTable.BatchRows)
                    {
                        arena.Reset();
                        yield return settled.Batch(arena, at, Math.Min(StateTable.BatchRows, settled.Count - at), held.FirstRow);
                    }

                    foreach (RowRange range in unsettled)
                    {
                        DatasetScanBuilder part = Part(_filter, held.FirstRow + range.Start, held.FirstRow + range.End);
                        await foreach (RecordBatch batch in part.ExecuteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            yield return batch;
                        }
                    }

                    continue;
                }

                if (rows is null && byBucket is null)
                {
                    // This object's rows and those of the objects after it the counts do not answer, up to the next they may:
                    // a part of the scan, which reads them as it would, ahead and past their sheets. Under rollups, a part
                    // stops where the rows the walk passed over begin, which a rollup answers.
                    int last = o;
                    while (last + 1 < objects.Count && objects[last + 1].Field is null && !BlocksAnswer(objects[last + 1].Held)
                        && (rollups is null || objects[last + 1].Held.FirstRow == objects[last].Held.FirstRow + objects[last].Held.Entry.Rows))
                    {
                        last++;
                    }

                    PositionedObject end = objects[last].Held;
                    DatasetScanBuilder part = Part(_filter, Math.Max(_from, held.FirstRow), Math.Min(_to, end.FirstRow + end.Entry.Rows));
                    await foreach (RecordBatch batch in part.ExecuteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    o = last;
                    continue;
                }

                if (_metrics is { } metrics)
                {
                    metrics.ObjectsAnswered += taken?.Objects ?? 1;
                    metrics.RollupsAnswered += taken is null ? 0 : 1;
                    metrics.StateRows += rows?.Count ?? 0;
                    for (int b = 0; b < (byBucket?.Count ?? 0); b++)
                    {
                        metrics.StateRows += byBucket![b].Count;
                    }
                }

                if (rows is not null)
                {
                    for (int at = 0; at < rows.Count; at += StateRows.BatchRows)
                    {
                        arena.Reset();
                        yield return rows.Batch(arena, at, Math.Min(StateRows.BatchRows, rows.Count - at), held.FirstRow);
                    }

                    continue;
                }

                for (int b = 0; b < byBucket!.Count; b++)
                {
                    for (int at = 0; at < byBucket[b].Count; at += StateRows.BatchRows)
                    {
                        arena.Reset();
                        yield return byBucket[b].Batch(arena, at, Math.Min(StateRows.BatchRows, byBucket[b].Count - at), held.FirstRow);
                    }
                }
            }
        }
        finally
        {
            // The counts and statistics read ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach (Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]> left in (IEnumerable<Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]>>?)blockReads?.Values ?? [])
            {
                try
                {
                    await left.ConfigureAwait(false);
                }
                catch (Exception) when (left.IsFaulted || left.IsCanceled)
                {
                }
            }

            foreach (Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>? left in reads)
            {
                if (left is not null && left != NoCounts)
                {
                    try
                    {
                        await left.ConfigureAwait(false);
                    }
                    catch (Exception) when (left.IsFaulted || left.IsCanceled)
                    {
                    }
                }
            }

            arena.Reset();
        }
    }

    /// <summary>
    /// The bytes a reader of <paramref name="values"/> values reads of <paramref name="region"/>, about: its header of
    /// <paramref name="head"/> bytes, then a chunk a value at most.
    /// </summary>
    private static long InPart(Indexes.IndexSegment region, int head, int values) =>
        head + Math.Min(region.Length - (long)head, (long)values * Indexes.ColumnCounts.ChunkBytes);

    /// <summary>The rollups <paramref name="rollups"/> takes, in their order, each with its buckets' summaries when taken by bucket.</summary>
    private static List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)> Rolled(RollupChoice rollups)
    {
        List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)> rolled = new List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)>(rollups.Chosen.Count);
        foreach ((RollupEntry rollup, int _, ObjectSummaries?[]? buckets) in rollups.Chosen)
        {
            rolled.Add((rollup, buckets));
        }

        return rolled;
    }

    /// <summary>
    /// The states of each bucket of <paramref name="counts"/>, kept by bucket, with <paramref name="sketches"/> their
    /// sketches, each bucket with its summaries of <paramref name="buckets"/>, none for a bucket the filter refutes; null
    /// when a bucket's counts do not answer, or the counts are not of those buckets.
    /// </summary>
    private static List<StateRows>? BucketRows(CountsAnswer answer, Indexes.ColumnCounts counts, Indexes.ColumnSketches? sketches, ObjectSummaries?[] buckets)
    {
        if (counts.Buckets != buckets.Length)
        {
            return null;
        }

        List<StateRows> rows = [];
        for (int b = 0; b < buckets.Length; b++)
        {
            if (buckets[b] is not { } summaries || Indexes.CountsBuckets.Apart(counts, sketches, b) is not ({ } apart, var apartSketches))
            {
                continue;
            }

            if (answer.Rows(apart, apartSketches, summaries) is not { } held)
            {
                return null;
            }

            rows.Add(held);
        }

        return rows;
    }

    /// <summary>The objects <paramref name="rollup"/> covers, each with the column whose counts answer for it, in key order.</summary>
    private async Task<List<(PositionedObject Held, int? Field)>> SpreadAsync(RollupEntry rollup, CountsAnswer answer, CancellationToken cancellationToken)
    {
        List<(PositionedObject Held, int? Field)> spread = [];
        await foreach (PositionedObject under in _version.WalkAsync(Pruner(), 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            if (under.Level == rollup.Level && rollup.Covers(under.TreeKey.Span))
            {
                spread.Add((under, AnsweredBy(answer, under)));
            }
        }

        return spread;
    }

    /// <summary>
    /// The counts of column <paramref name="field"/> of an object a delete marked, less those of its deleted rows
    /// (<see cref="DeletedCounts.Less"/>), read beside them from the commit object they lie in; none, and the object read as
    /// rows, when they do not give the live rows' counts exactly.
    /// </summary>
    private async Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> LiveCountsAsync(ObjectEntry entry, int field, CancellationToken cancellationToken)
    {
        ValueTask<DeletedCounts?> negatives = entry.NegativesAsync(_version.Pages, cancellationToken);
        (Indexes.ColumnCounts? counts, _) = await _dataset.ReadCountsAsync(entry, field, sketches: false, cancellationToken).ConfigureAwait(false);
        return (await negatives.ConfigureAwait(false))?.CountsOf(field) is { } taken && counts is not null ? (DeletedCounts.Less(counts, taken), null) : (null, null);
    }

    /// <summary>
    /// The counts of column <paramref name="field"/> of an object under a value of identifier <paramref name="identifier"/>
    /// left out, less those of the rows that hold it (<see cref="CountsAnswer.Exclusion"/>): the rows its postings find for
    /// <paramref name="found"/>, read beside the counts, counted and taken out (<see cref="DeletedCounts.Less"/>); none, and the
    /// object read as rows, when the postings cannot say or the counts do not give the rows kept exactly.
    /// </summary>
    private async Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> KeptCountsAsync(
        CountsAnswer answer, ObjectEntry entry, int field, int identifier, VortexExpr found, CancellationToken cancellationToken)
    {
        (Indexes.ColumnCounts? counts, _) = await _dataset.ReadCountsAsync(entry, field, sketches: false, cancellationToken).ConfigureAwait(false);
        if (counts is null)
        {
            return (null, null);
        }

        // The columns the rows' counts read: the counted one, the measures the question reads, and the identifier the rows
        // are kept by; the counts' other measures, which the question does not read, stay every row's.
        List<int> measures = [];
        List<MeasureKind> kinds = [];
        foreach ((_, int measured, _, _) in answer.Measures)
        {
            int place = Array.IndexOf(counts.MeasureFields, measured);
            if (place >= 0 && !measures.Contains(measured))
            {
                measures.Add(measured);
                kinds.Add(counts.Kinds[place]);
            }
        }

        Indexes.ColumnCounts shape = new Indexes.ColumnCounts([.. measures], [.. kinds], [], []);
        SortedSet<int> columns = [field, identifier, .. measures];
        int[] read = [.. columns];
        List<RecordBatch>? rows = await _dataset.LookupAsync(entry, found, read, cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return (null, null);
        }

        try
        {
            DeletedCounts.Builder taken = new DeletedCounts.Builder(_version.Schema.DType, [(field, shape)], read);
            long held = 0;
            foreach (RecordBatch batch in rows)
            {
                SelectBy(batch, found);
                if (batch.SelectedRows > 0)
                {
                    taken.Add(batch);
                    held += batch.SelectedRows;
                }
            }

            return held == 0 ? (counts, null) : (DeletedCounts.Less(counts, taken.Finish().CountsOf(field)!), null);
        }
        finally
        {
            foreach (RecordBatch batch in rows)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>The read of an object's counts <see cref="StatesAsync"/> does not begin, or has taken: none.</summary>
    private static readonly Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> NoCounts = Task.FromResult<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>((null, null));

    /// <summary>
    /// The scan of rows <c>[from, to)</c> <paramref name="filter"/> keeps, as this scan reads rows, with no states asked and
    /// none of its objects counted again: an answered object's edges, or the objects between those its counts answer.
    /// </summary>
    private DatasetScanBuilder Part(VortexExpr? filter, long from, long to) =>
        new DatasetScanBuilder(_dataset, _version)
        {
            _filter = filter,
            _mask = _mask,
            _indexes = _indexes,
            _summaries = _summaries,
            _from = from,
            _to = to,
            _rowsSet = true,
            _considers = false,
            _metrics = _metrics,
            _counters = _counters,
            _options = _options,
            _keepEncodings = _keepEncodings,
            _sinkDecodes = _sinkDecodes,
            _positionsUnread = _positionsUnread,
            _variantKeys = _variantKeys,
            _readAhead = _readAhead,
            _readAheadGranted = _readAheadGranted,
            _settles = _settles,
        };

    /// <summary>
    /// The conjunct of <paramref name="filter"/> that looks one value of a declared identifier up, or a few,
    /// <c>id == v</c> or <c>id IN (…)</c> on a column of its own, never null: what the identifier's index answers object
    /// by object with no open of the object (<see cref="VortexDataset.LookupAsync"/>). Null for any other filter.
    /// </summary>
    internal static VortexExpr? LookupOf(VortexExpr? filter, IReadOnlyList<string> identifiers)
    {
        if (filter is null || identifiers.Count == 0)
        {
            return null;
        }

        List<VortexExpr> conjuncts = [];
        GroupPredicates.Conjuncts(filter, conjuncts);
        foreach (VortexExpr conjunct in conjuncts)
        {
            FieldExpr? field = conjunct switch
            {
                ComparisonExpr { Op: ComparisonOp.Equal } equal when equal.Value.Kind != FilterLiteralKind.Null => equal.Field,
                InExpr inList when inList.Values.Count > 0 && !HoldsNull(inList.Literals) => inList.Field,
                _ => null,
            };
            if (field is ColumnFieldExpr)
            {
                foreach (string identifier in identifiers)
                {
                    if (string.Equals(identifier, field.Path, StringComparison.Ordinal))
                    {
                        return conjunct;
                    }
                }
            }
        }

        return null;

        static bool HoldsNull(FilterLiteral[] values)
        {
            foreach (FilterLiteral value in values)
            {
                if (value.Kind == FilterLiteralKind.Null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The filter's lookup of an identifier (<see cref="LookupOf"/>), for a scan of rows that reads them in the tree's order
    /// with its indexes; null otherwise.
    /// </summary>
    private VortexExpr? Lookup
    {
        get
        {
            if (!_lookupKnown)
            {
                _lookup = _indexes && _orderPaths is null && _take is null && LookupColumns(out _)
                    ? LookupOf(_filter, _version.Header.Identifiers)
                    : null;
                _lookupKnown = true;
            }

            return _lookup;
        }
    }

    /// <summary>
    /// The columns a lookup reads its rows for: the projected ones, whole, which the filter's must be among; null in
    /// <paramref name="columns"/> for every one. False when the projection keeps part of a column, or leaves out a column
    /// the filter reads.
    /// </summary>
    private bool LookupColumns(out int[]? columns)
    {
        columns = null;
        if (_mask is not { IsAll: false } mask)
        {
            return true;
        }

        DType schema = _version.Schema.DType;
        int count = mask.SelectedCount(schema.FieldCount);
        int[] fields = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (!mask.SelectedMask(i).IsAll)
            {
                return false;
            }

            fields[i] = mask.SelectedField(i);
        }

        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        _filter?.CollectFields(named);
        foreach (string path in named)
        {
            int field = schema.IndexOfField(path);
            if (field < 0 || Array.IndexOf(fields, field) < 0)
            {
                return false;
            }
        }

        columns = fields;
        return true;
    }

    /// <summary>
    /// Whether an object's rows are found by its identifier's index rather than its open, when a scan looks a value up: one
    /// with no row marked, whose footer an open would read is large (<see cref="DatasetOptions.LookupFooterBytes"/>), and
    /// whose entry carries a table of contents that names its index directory.
    /// </summary>
    internal static bool LooksUp(ObjectEntry entry, DatasetOptions options) =>
        !entry.HasDeletions && entry.FooterBytes >= options.LookupFooterBytes && entry.Contents is { DirectoryRegion: not null };

    /// <summary>
    /// The filter's lookup when <paramref name="held"/> takes it: an object the scan covers whole, with no row marked, of the
    /// version's own columns, whose footer an open would read is large (<see cref="DatasetOptions.LookupFooterBytes"/>), and
    /// whose entry carries a table of contents that names its index directory; null otherwise.
    /// </summary>
    private VortexExpr? LookupFor(in PositionedObject held)
    {
        if (Lookup is not { } lookup || Covered(held) != held.Entry.Rows || !LooksUp(held.Entry, _dataset.Options)
            || held.Entry.Contents is not { } contents)
        {
            return null;
        }

        try
        {
            return _version.Schema.ColumnsOf(contents.SchemaType, held.Entry.Key) is null ? lookup : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The scan of a lookup of an identifier: every object the walk keeps that takes the lookup (<see cref="LookupFor"/>)
    /// looked up at once, as many reads in flight as the session lets be, and the objects' rows given in the tree's order;
    /// an object that does not take it, a small one whose footer an open reads in a request, read as the scan reads any,
    /// <see cref="LookupReads"/> of them at once, the rows they keep copied. A lookup takes four round trips whatever the
    /// object holds, and an open and a read of a small object two or three: thirty objects one after the other would wait
    /// for 120, or for 60 to 90.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> LookedUpAsync(VortexExpr lookup, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LookupColumns(out int[]? columns);
        List<(PositionedObject Held, Task<List<RecordBatch>?>? Found, bool Looked)> objects = [];
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SemaphoreSlim? reading = null;
        try
        {
            int read = 0;
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Covered(held) > 0)
                {
                    bool looked = LookupFor(held) is not null;
                    objects.Add((held, looked ? _dataset.LookupAsync(held.Entry, lookup, columns, stopped.Token).AsTask() : null, looked));
                    read += looked ? 0 : 1;
                }
            }

            // Objects read as rows, two or more, read ahead together; one alone is read when its turn comes, as a scan reads it.
            for (int i = 0; i < objects.Count && read > 1; i++)
            {
                if (!objects[i].Looked)
                {
                    reading ??= new SemaphoreSlim(LookupReads);
                    objects[i] = (objects[i].Held, ReadAheadAsync(objects[i].Held, reading, stopped.Token), false);
                }
            }

            foreach ((PositionedObject held, Task<List<RecordBatch>?>? found, bool looked) in objects)
            {
                if (looked)
                {
                    await foreach (RecordBatch batch in FoundAsync(held, await found!.ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    continue;
                }

                if (found is not null)
                {
                    foreach (RecordBatch batch in await found.ConfigureAwait(false) ?? [])
                    {
                        yield return batch;
                    }

                    continue;
                }

                await foreach (RecordBatch batch in RowsAsync(held, 1, cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            // A scan stopped or failed: the lookups and the reads still on their way are called off and end, and what they
            // found is let go.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach ((_, Task<List<RecordBatch>?>? found, _) in objects)
            {
                if (found is null)
                {
                    continue;
                }

                try
                {
                    foreach (RecordBatch batch in await found.ConfigureAwait(false) ?? [])
                    {
                        batch.Dispose();
                    }
                }
                catch (Exception) when (found.IsFaulted || found.IsCanceled)
                {
                }
            }

            reading?.Dispose();
        }
    }

    /// <summary>The objects a lookup's scan reads as rows at once, those that do not take the lookup.</summary>
    private const int LookupReads = 16;

    /// <summary>
    /// The rows of <paramref name="held"/> the scan keeps, read as the scan reads any object once <paramref name="reading"/>
    /// lets one more be read, each batch that keeps a row copied, the scan's own batches being let go as it moves on.
    /// </summary>
    private async Task<List<RecordBatch>?> ReadAheadAsync(PositionedObject held, SemaphoreSlim reading, CancellationToken cancellationToken)
    {
        await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<RecordBatch> kept = [];
        try
        {
            await foreach (RecordBatch batch in RowsAsync(held, 1, cancellationToken).ConfigureAwait(false))
            {
                if (batch.SelectedRows > 0)
                {
                    kept.Add(batch.View.ToOwned());
                }
            }

            return kept;
        }
        catch
        {
            foreach (RecordBatch batch in kept)
            {
                batch.Dispose();
            }

            throw;
        }
        finally
        {
            reading.Release();
        }
    }

    /// <summary>
    /// The rows an object's lookup found, the scan's filter kept, as the dataset's; the object's rows as any other's when its
    /// index could not say (<paramref name="found"/> null). The batches stay the lookup's, which the scan lets go.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> FoundAsync(
        PositionedObject held, List<RecordBatch>? found, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (found is null)
        {
            await foreach (RecordBatch batch in RowsAsync(held, 1, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        if (_metrics is { } metrics)
        {
            metrics.ObjectsLookedUp++;
        }

        RecordBatch? view = null;
        try
        {
            foreach (RecordBatch batch in found)
            {
                if (_filter is { } filter)
                {
                    SelectBy(batch, filter);
                }

                if (batch.SelectedRows > 0)
                {
                    yield return InDataset(batch, held.FirstRow, ref view);
                }
            }
        }
        finally
        {
            view?.Dispose();
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
            if (_settles is { } plan && columns is null && _take is null && MaySettle(held)
                && await SettlingOfAsync(lease.File, held, plan, cancellationToken).ConfigureAwait(false) is { } settling)
            {
                await foreach (RecordBatch batch in SettledAsync(lease.File, held, settling, cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }

                yield break;
            }

            if (columns is null && !held.Entry.HasDeletions && Volatile.Read(ref _narrowed) is { } narrowed && narrowed.TryGetValue(held.Entry.Uid, out RowRange[]? ranges))
            {
                // The ranges of the object's rows where the values its filter asks live, each its own scan, the rows
                // between them never read.
                RecordBatch? view = null;
                foreach (RowRange range in ranges)
                {
                    ScanBuilder scan = Of(lease.File, held, _filter, _mask, canonical: false, within: range);
                    using QueryMemory? ahead = ReadsAhead(scan, lease.File);
                    await foreach (RecordBatch batch in scan.ExecuteAsync()
                        .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        yield return InDataset(batch, held.FirstRow, ref view);
                    }
                }

                yield break;
            }

            if (columns is null && !held.Entry.HasDeletions && _take is null && _filter is { } asked
                && await TwoPhaseAsync(held, asked, cancellationToken).ConfigureAwait(false) is { } filtered)
            {
                // A filter its sheets say keeps so few rows that most chunks of the widest column read hold none: its own
                // columns first, then the rest for the rows it keeps alone, which read only the chunks that hold them.
                long[] kept = await KeptAsync(lease.File, held, asked, filtered, cancellationToken).ConfigureAwait(false);
                if (kept.Length > 0)
                {
                    RecordBatch? view = null;
                    ScanBuilder scan = Configure(lease.File.ScanBuilder(), null, _mask, canonical: false).Take(kept);
                    using QueryMemory? ahead = ReadsAhead(scan, lease.File);
                    await foreach (RecordBatch batch in scan.ExecuteAsync()
                        .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        yield return InDataset(batch, held.FirstRow, ref view);
                    }
                }

                yield break;
            }

            if (columns is null && !held.Entry.HasDeletions)
            {
                // The object's own scan, which every object of the version's schema with no row
                // marked is read by, rebased into one view for the whole object.
                RecordBatch? view = null;
                ScanBuilder scan = Of(lease.File, held);
                using QueryMemory? ahead = ReadsAhead(scan, lease.File);
                await foreach (RecordBatch batch in scan.ExecuteAsync()
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
    /// Whether the blocks of the object <paramref name="held"/> may settle under the scan's filter: there is none, or it reads
    /// the key's first column alone and the object's summaries prove it true over every row. Under a filter on another column
    /// a block is rarely kept whole, and the proof of each, the zone maps of the columns it reads, is not worth asking.
    /// </summary>
    private bool MaySettle(in PositionedObject held)
    {
        if (_filter is not { } filter)
        {
            return true;
        }

        if (!_wholeKnown)
        {
            SummaryPruner pruner = new SummaryPruner(filter);
            bool alone = _dataset.Key is { } key && pruner.ReadsColumns;
            foreach (string path in pruner.Paths)
            {
                alone &= string.Equals(path, _dataset.Key!.InstantPath, StringComparison.Ordinal);
            }

            _whole = alone ? pruner : null;
            _wholeKnown = true;
        }

        return _whole is { } whole && whole.MustMatch(held.Entry.Summaries.Columns, held.Entry.PhysicalRows);
    }

    /// <summary>
    /// The blocks of the object <paramref name="held"/>, within the rows the scan reads of it, that its zone maps settle for
    /// <paramref name="plan"/>, those no delete marked a row of; null when none does. The filter, which the object's summaries
    /// prove true over its rows (<see cref="MaySettle"/>), keeps every block whole.
    /// </summary>
    private ValueTask<ZoneSettling?> SettlingOfAsync(VortexFile file, PositionedObject held, AggregationPlan plan, CancellationToken cancellationToken)
    {
        (long lower, long upper) = PhysicalRange(held);
        return ZoneSettling.PlanObjectAsync(
            file, new RowRange(lower, upper), filter: null, _indexes, plan, held.Entry.HasDeletions ? held.Entry.Deletions : null, _counters, cancellationToken);
    }

    /// <summary>
    /// The rows of the object <paramref name="held"/>, the blocks <paramref name="settling"/> settles in place of theirs: the
    /// rows between them read as the object's own, its marked rows left out, and each settled block, which holds none, folded
    /// as a block of as many rows of its zones' bounds at its place among the dataset's rows.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> SettledAsync(
        VortexFile file, PositionedObject held, ZoneSettling settling, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (long lower, long upper) = PhysicalRange(held);
        ZoneSettling.Scratch scratch = settling.NewScratch();
        DeletionVector deletions = held.Entry.Deletions;
        long at = lower;
        for (int block = 0; block <= settling.Count; block++)
        {
            RowRange next = block < settling.Count ? settling.Range(block) : new RowRange(upper, upper);
            if (at < next.Start)
            {
                await foreach (RecordBatch batch in BetweenAsync(file, held, new RowRange(at, next.Start), cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }

            if (block < settling.Count)
            {
                if (_metrics is { } metrics)
                {
                    metrics.BlocksSettled++;
                }

                yield return settling.Batch(block, scratch, held.FirstRow + deletions.Logical(next.Start));
                at = next.End;
            }
        }
    }

    /// <summary>The rows of the object <paramref name="held"/> within <paramref name="within"/>, of its file, as the dataset's: its marked rows left out.</summary>
    private async IAsyncEnumerable<RecordBatch> BetweenAsync(
        VortexFile file, PositionedObject held, RowRange within, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (held.Entry.HasDeletions)
        {
            await foreach (RecordBatch batch in LiveAsync(file, held, _filter, _mask, held.FirstRow, _options.Compact, reshaped: false, cancellationToken, within)
                .ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        RecordBatch? view = null;
        ScanBuilder scan = Of(file, held, _filter, _mask, canonical: false, within);
        using QueryMemory? ahead = ReadsAhead(scan, file);
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return InDataset(batch, held.FirstRow, ref view);
        }
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
            Span<ulong> kept = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);

            // The states compared 64 at a time, each word of the selection written whole, and counted by its popcount; the
            // one word of a batch of no row, which no compare writes, cleared.
            kept[^1] = 0;
            int selected = Trilean.ToWords(states.AsSpan(0, rows), Trilean.True, equal: true, kept);
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
        CountsAnswer? answer = Answer() is { } asked && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false) ? asked : null;

        // A rollup the scan takes reads its column's totals, and their sketches beside them for a quantile, in a request,
        // for the objects it covers, which the walk passes over.
        RollupChoice? rollups = answer is null ? null : await RollupsAsync(answer, cancellationToken).ConfigureAwait(false);
        foreach ((RollupEntry rollup, int field, ObjectSummaries?[]? buckets) in rollups?.Chosen ?? [])
        {
            // Under values asked, large counts read their header, then the chunks of the values, a chunk each at most. A
            // rollup taken by bucket reads its counts by bucket, and answers for the rows of the buckets the filter keeps.
            RollupColumn kept = rollup.ColumnOf(field)!.Value;
            (Indexes.IndexSegment? counted, Indexes.IndexSegment? sketches, int countsHead, int sketchesHead) = kept.Regions(buckets is not null);
            Indexes.IndexSegment? sketched = answer!.AsksSketches ? sketches : null;
            string column = _version.Schema.Columns[field].Name;
            bool inPart = VortexDataset.ReadsInPart(kept, answer.AsksSketches, answer.Values, buckets is not null);
            int regions = (sketched is null ? 1 : 2) * (inPart ? 2 : 1);
            string what = buckets is null ? $"totals of {column}" : $"counts by bucket of {column}";
            string read = inPart ? $"the header of the {what} and the chunks of {answer.Values!.Length} values" : $"the {what}";
            long rows = buckets is null ? rollup.Rows : 0;
            int answered = 0;
            for (int b = 0; b < (buckets?.Length ?? 0); b++)
            {
                answered += buckets![b] is null ? 0 : 1;
                rows += buckets[b] is null ? 0 : rollup.Buckets[b].Rows;
            }

            states.Add(new StatesStep(
                rollup.Key,
                "rollup",
                $"{read}{(sketched is null ? string.Empty : " and their sketches")} over {rollup.Objects} objects, rank {rollup.Rank}"
                    + (buckets is null ? string.Empty : $", {answered} of its {buckets.Length} buckets"),
                regions));
            keptRows += rows;
            segments += regions;
            bytes += inPart
                ? InPart(counted!.Value, countsHead, answer.Values!.Length) + (sketched is { } some ? InPart(some, sketchesHead, answer.Values.Length) : 0)
                : counted!.Value.Length + (sketched?.Length ?? 0);
            mayMatch |= rows > 0;
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken, answer, rollups).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0 || rollups?.CoverOf(held) >= 0)
            {
                continue;
            }

            // An object whose counts answer reads its column's counts, and their sketches beside them for a quantile, in a
            // request.
            if (answer is not null && AnsweredBy(answer, held) is null && BlocksAnswer(held))
            {
                // An object whose blocks may answer reads its columns' statistics over them and their indexes, in as many
                // requests as runs of them lie apart; the blocks they do not answer are read as rows. The formula's costs of
                // both paths say why.
                (PathCosts.Cost statistics, PathCosts.Cost read) = BlockAnswer()!.Costs(held);
                states.Add(new StatesStep(
                    held.Entry.Key,
                    "blocks",
                    $"the statistics over the blocks of {BlockAnswer()!.Fields.Length} columns, {statistics.Bytes} bytes in {statistics.Requests} requests where the rows would take {read.Bytes} in {read.Requests}, and the rows of the blocks they do not answer",
                    statistics.Requests));
                keptRows += covered;
                segments += statistics.Requests;
                bytes += statistics.Bytes;
                mayMatch = true;
                continue;
            }

            if (answer is not null && AnsweredBy(answer, held) is int field)
            {
                // An object a delete marked reads the counts of its deleted rows too, from the commit object they lie in.
                Indexes.ObjectContents contents = held.Entry.Contents!;
                Indexes.IndexSegment? sketched = answer.AsksSketches ? contents.SketchesOf(field) : null;
                string column = _version.Schema.Columns[field].Name;
                // Under a value of an identifier left out, the rows its postings find, in three round trips cold: the index
                // entries and the chunk directories, the runs, the chunks.
                int less = (held.Entry.HasDeletions ? 1 : 0) + (answer.Exclusion is null ? 0 : 3);
                states.Add(new StatesStep(
                    held.Entry.Key,
                    "states",
                    (sketched is null ? $"the counts of {column}" : $"the counts of {column} and their sketches")
                        + (held.Entry.HasDeletions ? ", less those of its deleted rows" : string.Empty)
                        + (answer.Exclusion is { } exclusion ? $", less those of the rows of {exclusion.Path} its postings find" : string.Empty),
                    (sketched is null ? 1 : 2) + less));
                keptRows += covered;
                segments += (sketched is null ? 1 : 2) + less;
                bytes += contents.CountsOf(field)!.Value.Length + (sketched?.Length ?? 0) + (less > 0 ? held.Entry.NegativesAt.Length : 0);
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

    /// <summary>
    /// The objects the scan walks: those the summaries keep, and of them those the sheets do not refute, but the objects
    /// whose counts <paramref name="answered"/> answers, which read no sheet.
    /// </summary>
    private IAsyncEnumerable<PositionedObject> WalkAsync(CancellationToken cancellationToken, CountsAnswer? answered = null, RollupChoice? rollups = null)
    {
        IAsyncEnumerable<PositionedObject> walk = _version.WalkAsync(
            Pruner(), _from, _to, _considers ? _metrics : null, cancellationToken, rollups is null ? null : rollups.Covers);
        return _summaries && _filter is { } filter && SheetPruning.Asks(filter, _version.Schema.DType)
            ? SheetPrunedAsync(walk, filter, answered, rollups, cancellationToken)
            : walk;
    }

    /// <summary>
    /// The rollups the scan takes in place of the objects they cover (<see cref="RollupChoice"/>): a scan of the whole
    /// version's, for which the counts answer; null for another, which reads no rollup.
    /// </summary>
    private ValueTask<RollupChoice?> RollupsAsync(CountsAnswer answer, CancellationToken cancellationToken) =>
        _from == 0 && _to == long.MaxValue ? RollupChoice.OfAsync(_version, answer, Pruner(), cancellationToken) : default;

    /// <summary>The objects a sheet read at once may pass over, in the walk's order.</summary>
    private const int SheetBatch = 16;

    /// <summary>
    /// The objects of <paramref name="walk"/> their column sheets do not refute <paramref name="filter"/> by: an object
    /// whose sheets are to be read (<see cref="SheetPruning.Reads"/>) has them read with no open of it, the objects of a
    /// batch side by side, and one that lacks every value an equality or a set asks for there is passed over. An object
    /// with none passes at once while no batch waits, so that a walk of small objects holds and allocates nothing; and so
    /// does an object whose counts <paramref name="answered"/> answers, whose counts say which values it holds.
    /// </summary>
    private async IAsyncEnumerable<PositionedObject> SheetPrunedAsync(
        IAsyncEnumerable<PositionedObject> walk, VortexExpr filter, CountsAnswer? answered, RollupChoice? rollups,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DType schema = _version.Schema.DType;
        List<int>? columns = null;
        List<PositionedObject>? batch = null;
        Func<PositionedObject, bool>? unread = answered is null ? null : held => AnsweredBy(answered, held) is not null || rollups?.CoverOf(held) >= 0;
        DatasetScanCounters? metrics = _considers ? _metrics : null;
        await foreach (PositionedObject held in walk.ConfigureAwait(false))
        {
            if (batch is not { Count: > 0 } && (!SheetPruning.Reads(held.Entry, _dataset.Options, _version.Schema) || unread?.Invoke(held) == true))
            {
                yield return held;
                continue;
            }

            (batch ??= new List<PositionedObject>(SheetBatch)).Add(held);
            if (batch.Count == SheetBatch)
            {
                columns ??= SheetPruning.Columns(filter, schema);
                foreach (PositionedObject kept in await SheetPruning.KeptAsync(
                    _dataset, _version.Schema, batch, filter, columns, metrics, unread, cancellationToken, _narrowing ??= Narrow).ConfigureAwait(false))
                {
                    yield return kept;
                }

                batch.Clear();
            }
        }

        if (batch is { Count: > 0 })
        {
            columns ??= SheetPruning.Columns(filter, schema);
            foreach (PositionedObject kept in await SheetPruning.KeptAsync(
                _dataset, _version.Schema, batch, filter, columns, metrics, unread, cancellationToken, _narrowing ??= Narrow).ConfigureAwait(false))
            {
                yield return kept;
            }
        }
    }


    /// <summary>
    /// The columns of <paramref name="filter"/> when the object <paramref name="held"/> is read in two passes
    /// (<see cref="TwoPhaseReads"/>): a filter on top-level columns alone, whose sheets say it keeps rows fewer than half the
    /// chunks of the widest other column read, as the object's contents count them; null to read it in
    /// one. Its sheets are those the walk read, which the handle keeps.
    /// </summary>
    private async ValueTask<FieldMask?> TwoPhaseAsync(PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        DType schema = _version.Schema.DType;
        if (!_summaries || held.Entry.Contents is not { ColumnChunks.Count: > 0, Sheets.Count: > 0 } contents || schema.IsDefault
            || !SheetPruning.Asks(filter, schema) || WidestChunks(contents, null) < TwoPhaseReads.LeastChunks)
        {
            // Too few chunks in any column read to spare one, decided before the filter is looked into.
            return null;
        }

        List<int> fields = [];
        foreach (string path in new SummaryPruner(filter).Paths)
        {
            if (path.Contains('.', StringComparison.Ordinal) || schema.IndexOfField(path) is not (int field and >= 0))
            {
                return null;
            }

            fields.Add(field);
        }

        // The widest column the rows read besides the filter's, in chunks.
        long chunks = WidestChunks(contents, fields);
        if (chunks < TwoPhaseReads.LeastChunks)
        {
            return null;
        }

        List<int> asked = SheetPruning.Columns(filter, schema);
        Indexes.ColumnSheet?[] sheets = await _dataset.ReadSheetsAsync(held.Entry, asked, cancellationToken).ConfigureAwait(false);
        long? kept = TwoPhaseReads.ExpectedRows(filter, schema, field => asked.IndexOf(field) is int at and >= 0 ? sheets[at] : null);
        return kept is long rows && TwoPhaseReads.Pays(rows, chunks) ? FieldMasks.Of(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fields)) : null;
    }

    /// <summary>The chunks of the widest column the scan reads in an object of <paramref name="contents"/>, those of <paramref name="besides"/> aside.</summary>
    private long WidestChunks(Indexes.ObjectContents contents, List<int>? besides)
    {
        DType schema = _version.Schema.DType;
        long chunks = 0;
        int read = _mask is { } mask ? mask.SelectedCount(schema.FieldCount) : schema.FieldCount;
        for (int i = 0; i < read; i++)
        {
            int field = _mask is { } selected ? selected.SelectedField(i) : i;
            if (besides?.Contains(field) != true && field < contents.ColumnChunks.Count)
            {
                chunks = Math.Max(chunks, contents.ColumnChunks[field]);
            }
        }

        return chunks;
    }

    /// <summary>
    /// The rows of the object <paramref name="held"/> <paramref name="filter"/> keeps, by their place in its file, read from
    /// its columns <paramref name="filtered"/> alone, its batches whole with the rows kept marked.
    /// </summary>
    private async ValueTask<long[]> KeptAsync(VortexFile file, PositionedObject held, VortexExpr filter, FieldMask filtered, CancellationToken cancellationToken)
    {
        List<long> kept = [];
        ScanBuilder scan = Of(file, held, filter, filtered, canonical: false).WithCompaction(false);
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (batch)
            {
                ReadOnlySpan<ulong> words = batch.SelectionWords;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    if (words.IsEmpty || ((words[row >> 6] >> (row & 63)) & 1) != 0)
                    {
                        kept.Add(batch.StartRow + row);
                    }
                }
            }
        }

        return [.. kept];
    }

    /// <summary>Keeps the ranges of the object <paramref name="held"/>'s rows a selected row may lie in, which its read takes alone.</summary>
    private void Narrow(PositionedObject held, RowRange[] ranges)
    {
        System.Collections.Concurrent.ConcurrentDictionary<UInt128, RowRange[]> narrowed = Volatile.Read(ref _narrowed)
            ?? Interlocked.CompareExchange(ref _narrowed, new System.Collections.Concurrent.ConcurrentDictionary<UInt128, RowRange[]>(), null)
            ?? _narrowed!;
        narrowed[held.Entry.Uid] = ranges;
    }

    private void RecordOpen(ObjectLease lease, int open = 1)
    {
        if (_metrics is { } metrics)
        {
            // A lookup's scan opens the objects it reads as rows several at once.
            lock (metrics)
            {
                metrics.ObjectsOpened++;
                metrics.Cursors = Math.Max(metrics.Cursors, open);
                if (lease.WasCached)
                {
                    metrics.CacheHits++;
                }
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
    /// builder's options and the rows that fall in this object, within <paramref name="within"/> of
    /// the file's when it is given; with <paramref name="canonical"/>, every column decoded, which the
    /// reshaping of another schema's batches takes.
    /// </summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, bool canonical, RowRange? within = null)
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
            && held.Entry.Contents is { Slices.ByKey: false } contents && WindowRows(contents.Slices.Slices, low, high) is { } window)
        {
            lower = Math.Max(lower, window.Start);
            upper = Math.Min(upper, window.End);
        }

        // An object in slices bounded by its keys, the same of a range of the key's first column, of any type.
        if (held.Entry.Deletions.IsEmpty && filter is not null && _dataset.Key is { } keyed && held.Entry.Contents is { Slices.ByKey: true } sliced
            && KeyBounds(filter, keyed) is { } bounds)
        {
            RowRange range = KeyRows(sliced.Slices, bounds.Low, bounds.High, keyed.LeadingType);
            lower = Math.Max(lower, range.Start);
            upper = Math.Min(upper, range.End);
        }

        if (within is { } inside)
        {
            lower = Math.Max(lower, inside.Start);
            upper = Math.Min(upper, inside.End);
        }

        if (lower > 0 || upper < held.Entry.PhysicalRows)
        {
            scan.Rows(new RowRange(lower, Math.Max(upper, lower)));
        }

        return scan;
    }

    /// <summary>
    /// Narrows <paramref name="low"/> and <paramref name="high"/>, inclusive, by the conjuncts of <paramref name="filter"/>
    /// that compare <paramref name="instant"/> itself; whether one did. A walk of the conjunctions, which allocates nothing.
    /// </summary>
    internal static bool Bound(VortexExpr filter, string instant, ref long low, ref long high)
    {
        if (filter is LogicalExpr { IsAnd: true } both)
        {
            bool left = Bound(both.Left, instant, ref low, ref high);
            return Bound(both.Right, instant, ref low, ref high) | left;
        }

        return BoundBy(filter, instant, ref low, ref high);
    }

    /// <summary>
    /// Narrows the window by a comparison of the instant itself with an integer, in its units; false for
    /// any other predicate on it.
    /// </summary>
    private static bool BoundBy(VortexExpr conjunct, string instant, ref long low, ref long high)
    {
        if (conjunct is not ComparisonExpr { Value.Kind: FilterLiteralKind.Signed } comparison
            || comparison.Field is not ColumnFieldExpr || !string.Equals(comparison.Field.Path, instant, StringComparison.Ordinal))
        {
            return false;
        }

        long value = comparison.Value.SignedValue;
        switch (comparison.Op)
        {
            case ComparisonOp.Equal:
                low = Math.Max(low, value);
                high = Math.Min(high, value);
                return true;
            case ComparisonOp.Greater:
                low = value == long.MaxValue ? long.MaxValue : Math.Max(low, value + 1);
                high = value == long.MaxValue ? long.MinValue : high;
                return true;
            case ComparisonOp.GreaterOrEqual:
                low = Math.Max(low, value);
                return true;
            case ComparisonOp.Less:
                high = value == long.MinValue ? long.MinValue : Math.Min(high, value - 1);
                low = value == long.MinValue ? long.MaxValue : low;
                return true;
            case ComparisonOp.LessOrEqual:
                high = Math.Min(high, value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The bounds the conjuncts of <paramref name="filter"/> that compare the key's first column with a value put on it,
    /// each row-encoded as the key encodes the column, inclusive, an empty array for a side none bounds; null when none
    /// does. A strict comparison bounds as a loose one: the slices it reaches are among those, and the filter takes out
    /// the rest. A value the column's type does not take bounds nothing.
    /// </summary>
    internal static (byte[] Low, byte[] High)? KeyBounds(VortexExpr filter, ClusteringKey key)
    {
        byte[]? low = null;
        byte[]? high = null;
        Narrow(filter, key, ref low, ref high);
        return low is null && high is null ? null : (low ?? [], high ?? []);

        static void Narrow(VortexExpr conjunct, ClusteringKey key, ref byte[]? low, ref byte[]? high)
        {
            if (conjunct is LogicalExpr { IsAnd: true } both)
            {
                Narrow(both.Left, key, ref low, ref high);
                Narrow(both.Right, key, ref low, ref high);
                return;
            }

            if (conjunct is not ComparisonExpr { Field: ColumnFieldExpr field } comparison || comparison.Value.Kind == FilterLiteralKind.Null
                || !string.Equals(field.Path, key.InstantPath, StringComparison.Ordinal))
            {
                return;
            }

            byte[] bound;
            try
            {
                bound = key.Encode([comparison.Value]);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (comparison.Op is ComparisonOp.Equal or ComparisonOp.Greater or ComparisonOp.GreaterOrEqual
                && (low is null || bound.AsSpan().SequenceCompareTo(low) > 0))
            {
                low = bound;
            }

            if (comparison.Op is ComparisonOp.Equal or ComparisonOp.Less or ComparisonOp.LessOrEqual
                && (high is null || bound.AsSpan().SequenceCompareTo(high) < 0))
            {
                high = bound;
            }
        }
    }

    /// <summary>
    /// The rows of the slices of <paramref name="table"/>, bounded by keys, whose first column of <paramref name="column"/>
    /// reaches from <paramref name="low"/> to <paramref name="high"/>, inclusive, each row-encoded and empty for no bound:
    /// from the first's first row to the last's last, empty when it reaches none.
    /// </summary>
    internal static RowRange KeyRows(Indexes.SliceTable table, ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, Types.DType column)
    {
        IReadOnlyList<Indexes.TimeSlice> slices = table.Slices;

        // A slice reaches the range when the first value of the key above its keys is at its low or past, and the first
        // value of its first key at its high or before.
        int first = 0;
        while (first < slices.Count && !low.IsEmpty && FirstValue(table.CeilingOf(first), column).SequenceCompareTo(low) < 0)
        {
            first++;
        }

        int last = slices.Count - 1;
        while (last >= first && !high.IsEmpty && FirstValue(table.LowOf(last), column).SequenceCompareTo(high) > 0)
        {
            last--;
        }

        return last < first
            ? RowRange.Empty
            : new RowRange(slices[first].FirstRow, slices[last].FirstRow + slices[last].Rows);

        static ReadOnlySpan<byte> FirstValue(ReadOnlySpan<byte> key, Types.DType column) => key[..RowEncoding.RowEncoder.FirstValueLength(key, column)];
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
        VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, long baseRow, bool compact, bool reshaped, CancellationToken cancellationToken,
        RowRange? within = null)
    {
        bool deselect = !compact && _positionsUnread;
        IAsyncEnumerable<RecordBatch> rows = Of(file, held, filter, mask, canonical: reshaped || !deselect, within).WithCompaction(false).ExecuteAsync();
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

    /// <summary>
    /// Its entry's summaries, the first column of <paramref name="key"/> bounded by its keys where they do not bound it, a
    /// text's (<see cref="ClusteringKey.Bounded"/>): its smallest key, which its tree key leads with, and its largest, which
    /// its entry states. The entry's own when the dataset has no key, or the object states no largest.
    /// </summary>
    internal ObjectSummaries Summaries(ClusteringKey? key) =>
        key is null || Entry.LastKey.IsEmpty || TreeKey.Length <= VortexDataset.UidBytes
            ? Entry.Summaries
            : key.Bounded(Entry.Summaries, VortexDataset.OrderOf(TreeKey).Span, Entry.LastKey.Span, exact: !Entry.HasDeletions);
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
