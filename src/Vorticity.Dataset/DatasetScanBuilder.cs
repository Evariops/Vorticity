using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
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

    /// <summary>
    /// The objects whose key range the filter refutes, passed over unopened, and the granules of the objects read that the
    /// key's pruning passed over by their sparse indexes (<see cref="KeyPruning"/>).
    /// </summary>
    public long ObjectsPrunedByKey { get; internal set; }

    /// <summary>The objects a lookup passed over unread, the index of their partition locating none of its values in them (D13).</summary>
    public long ObjectsPassedByPartitionIndex { get; internal set; }

    /// <summary>The granules of the objects read that the key's pruning passed over.</summary>
    public long GranulesPrunedByKey { get; internal set; }

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

    /// <summary>Granules of objects an aggregation took from their zone maps, their rows not read: granules no delete marked a row of.</summary>
    public long GranulesSettled { get; internal set; }

    /// <summary>
    /// Granules of objects whose columns' statistics over them answered an aggregation as states (<see cref="GranuleAnswer"/>),
    /// their rows not read; <see cref="StateRows"/> counts the rows of states they gave.
    /// </summary>
    public long GranulesAnswered { get; internal set; }

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
internal sealed partial class DatasetScanBuilder
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
    private GranuleAnswer? _granuleAnswer;
    private bool _granuleAnswerKnown;

    // The granules of each object a selected row may lie in, a bit a granule, by where its columns' values lie, for the
    // objects whose indexes leave granules out (SheetPruning.KeptAsync); read by the lanes that read the objects. Made at the
    // first such object: a scan that meets none allocates nothing for them.
    private System.Collections.Concurrent.ConcurrentDictionary<UInt128, ulong[]>? _narrowed;
    private Action<PositionedObject, ulong[]>? _narrowing;

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
    /// Says the shape of the question the scan answers (<see cref="LogicalQuery.Shape"/>), which the entry alternative
    /// answers some objects of from their entries alone: a count, whether a row is, an extreme.
    /// </summary>
    internal DatasetScanBuilder WithShape(LogicalShape? shape)
    {
        _shape = shape;
        return this;
    }

    // The shape of the question the scan answers, from its logical plan; null for a read of rows the engine asks itself.
    private LogicalShape? _shape;

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
    /// Learns, for <paramref name="family"/>, the partial of each object it would read as rows that may learn one, run over
    /// <paramref name="source"/>, or answers the object from the one it learned (<see cref="LearnedPartials"/>).
    /// </summary>
    internal DatasetScanBuilder WithFamily(LearnedFamily family, ScanSource source)
    {
        _family = family;
        _familySource = source;
        return this;
    }

    // The family of the aggregation the scan asks states for, whose partials it learns, and the source its runs read.
    private LearnedFamily? _family;
    private ScanSource? _familySource;

    /// <summary>The top-level columns the scan's filter reads but the key's first, by name, which a clustering by would narrow; found once.</summary>
    private List<string> FilterColumns()
    {
        if (_filterColumns is null)
        {
            string? leading = _version.Schema.KeyColumns is { Count: > 0 } key ? key[0] : null;
            List<string> columns = [];
            foreach (string path in FilterPaths())
            {
                int dot = path.IndexOf('.', StringComparison.Ordinal);
                string top = dot < 0 ? path : path[..dot];
                if (!string.Equals(top, leading, StringComparison.Ordinal) && _version.Schema.DType.IndexOfField(top) >= 0 && !columns.Contains(top))
                {
                    columns.Add(top);
                }
            }

            // A variant key's read is a value of its own, which a component may read as well.
            if (_filter is { } filter)
            {
                VariantReads reads = new VariantReads(columns);
                ScanBuilder.VisitFields(filter, ref reads);
            }

            columns.Sort(StringComparer.Ordinal);
            _filterColumns = columns;
        }

        return _filterColumns;
    }

    /// <summary>A walk over a filter's columns that keeps the text of each variant key's read it meets.</summary>
    private readonly struct VariantReads(List<string> into) : ScanBuilder.IFieldVisitor
    {
        public bool Visit(FieldExpr field)
        {
            if (field is Compute.VariantFieldExpr variant && !into.Contains(variant.Text))
            {
                into.Add(variant.Text);
            }

            return true;
        }
    }

    private List<string>? _filterColumns;

    /// <summary>The paths the scan's filter reads, none without one; found once.</summary>
    private HashSet<string> FilterPaths()
    {
        if (_filterPaths is null)
        {
            HashSet<string> paths = [];
            _filter?.CollectFields(paths);
            _filterPaths = paths;
        }

        return _filterPaths;
    }

    private HashSet<string>? _filterPaths;

    /// <summary>The top-level columns the scan reads, by their places: those its mask selects and those its filter reads, or every one; found once.</summary>
    private int[] FieldsRead()
    {
        if (_fieldsRead is null)
        {
            int count = _version.Schema.DType.IsDefault ? 0 : _version.Schema.DType.FieldCount;
            if (_mask is not { } mask)
            {
                _fieldsRead = [.. System.Linq.Enumerable.Range(0, count)];
                return _fieldsRead;
            }

            Span<bool> read = count <= 256 ? stackalloc bool[count] : new bool[count];
            int total = 0;
            for (int i = 0; i < mask.SelectedCount(count); i++)
            {
                total += Read(read, mask.SelectedField(i));
            }

            foreach (string path in _filter is null ? [] : FilterPaths())
            {
                int dot = path.IndexOf('.', StringComparison.Ordinal);
                total += _version.Schema.DType.IndexOfField(dot < 0 ? path : path[..dot]) is int field and >= 0 ? Read(read, field) : 0;
            }

            int[] fields = new int[total];
            for (int field = 0, at = 0; field < count; field++)
            {
                if (read[field])
                {
                    fields[at++] = field;
                }
            }

            _fieldsRead = fields;
        }

        return _fieldsRead;

        // Marks a column read, 1 the first time.
        static int Read(Span<bool> read, int field)
        {
            int first = read[field] ? 0 : 1;
            read[field] = true;
            return first;
        }
    }

    private int[]? _fieldsRead;

    /// <summary>What keys the scan filter's pages (<see cref="PredicatePages"/>): its canonical text and the version's columns.</summary>
    private UInt128 PredicateKey => _predicateKey ??=
        System.IO.Hashing.XxHash128.HashToUInt128(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ExprText.Format(_filter!).AsSpan()))
        ^ System.IO.Hashing.XxHash128.HashToUInt128(_version.Schema.Bytes.Span);

    private UInt128? _predicateKey;

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

        IAsyncEnumerator<PositionedObject> walk = WalkAsync(cancellationToken, granules: true).GetAsyncEnumerator(cancellationToken);
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
            _geometries = LazyInitializer.EnsureInitialized(ref _geometries),
        };

    /// <summary>
    /// The objects this scan will read, in key order, each with its first row: the walk with its
    /// skips applied, and not one byte of any data object read.
    /// </summary>
    public IAsyncEnumerable<PositionedObject> ObjectsAsync(CancellationToken cancellationToken = default) =>
        WalkAsync(cancellationToken);

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
        if (held.Granules is { } part)
        {
            lower = Math.Max(lower, held.FirstRow + part.Start);
            upper = Math.Min(upper, held.FirstRow + part.End);
        }

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

    private SummaryPruner? Pruner() => _summaries && _filter is { } filter ? new SummaryPruner(filter) : null;

    /// <summary>
    /// The objects the scan walks: those the summaries keep, and of them those the sheets do not refute; with
    /// <paramref name="granules"/>, those the key's pruning keeps, by the ranges of their granules it keeps.
    /// </summary>
    private IAsyncEnumerable<PositionedObject> WalkAsync(CancellationToken cancellationToken, bool granules = false)
    {
        IAsyncEnumerable<PositionedObject> walk = _version.WalkAsync(Pruner(), _from, _to, _considers ? _metrics : null, cancellationToken);
        if (_summaries && _filter is { } filter && (SheetPruning.Asks(filter, _version.Schema.DType) || SheetPaths() is { Count: > 0 }))
        {
            walk = SheetPrunedAsync(walk, filter, cancellationToken);
        }

        return granules && _summaries && _indexes && _take is null && KeyPruning.Of(_version.Schema, _filter) is { } pruning
            ? KeyPrunedAsync(walk, pruning, cancellationToken)
            : walk;
    }

    /// <summary>
    /// The objects of <paramref name="walk"/> as the key's pruning keeps them (<see cref="KeyPruning"/>): an object whose key
    /// range the filter refutes is passed over unopened, and one that keeps a sparse index is read by the ranges of its
    /// granules the filter may keep a row of, a part of it each. An object with marked rows, whose index counts its rows
    /// before the marks, is read whole past its range.
    /// </summary>
    private async IAsyncEnumerable<PositionedObject> KeyPrunedAsync(
        IAsyncEnumerable<PositionedObject> walk, KeyPruning pruning, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DatasetSchema schema = _version.Schema;
        await foreach (PositionedObject held in walk.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> first = held.TreeKey.Length > VortexDataset.UidBytes ? VortexDataset.OrderOf(held.TreeKey) : default;
            if (!first.IsEmpty && !pruning.MayHold(first.Span, held.Entry.LastKey.Span))
            {
                if (_considers && _metrics is { } skipped)
                {
                    skipped.ObjectsPrunedByKey++;
                }

                continue;
            }

            if (!held.Entry.Deletions.IsEmpty || held.Entry.Contents is not { KeyIndex: not null } contents || !ReadsKeyIndex(held)
                || await _dataset.KeyIndexAsync(held.Entry, contents, cancellationToken).ConfigureAwait(false) is not { } keys
                || keys.Rows != held.Entry.PhysicalRows)
            {
                yield return held;
                continue;
            }

            int prefix = schema.Partitioned && !first.IsEmpty ? schema.Tree!.PrefixLength(first.Span, 1) : 0;
            List<(int First, int End)> ranges = pruning.Granules(keys, prefix > 0 ? first.Span[..prefix] : default);
            int kept = 0;
            foreach ((int from, int end) in ranges)
            {
                kept += end - from;
            }

            if (_considers && _metrics is { } metrics)
            {
                metrics.GranulesPrunedByKey += keys.Granules - kept;
                metrics.ObjectsPrunedByKey += ranges.Count == 0 ? 1 : 0;
            }

            if (ranges.Count == 1 && ranges[0] == (0, keys.Granules))
            {
                yield return held;
                continue;
            }

            foreach ((int from, int end) in ranges)
            {
                yield return held with
                {
                    Granules = new RowRange((long)from * keys.GranuleRows, Math.Min((long)end * keys.GranuleRows, keys.Rows)),
                };
            }
        }
    }

    // The rule that says whose sheets the walk reads, as a delegate made once.
    private Func<PositionedObject, bool>? _consulted;

    /// <summary>The objects a sheet read at once may pass over, in the walk's order.</summary>
    private const int SheetBatch = 16;

    /// <summary>
    /// The objects of <paramref name="walk"/> their column sheets do not refute <paramref name="filter"/> by: an object
    /// whose sheets are to be read (<see cref="ReadsSheets"/>) has them read with no open of it, the objects of a
    /// batch side by side, and one that lacks every value an equality or a set asks for there is passed over. An object
    /// with none passes at once while no batch waits, so that a walk of small objects holds and allocates nothing.
    /// </summary>
    private async IAsyncEnumerable<PositionedObject> SheetPrunedAsync(
        IAsyncEnumerable<PositionedObject> walk, VortexExpr filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DType schema = _version.Schema.DType;
        List<int>? columns = null;
        PartitionLocates? partitions = null;
        List<PositionedObject>? batch = null;
        DatasetScanCounters? metrics = _considers ? _metrics : null;
        await foreach (PositionedObject held in walk.ConfigureAwait(false))
        {
            if (batch is not { Count: > 0 } && !ReadsSheets(held))
            {
                yield return held;
                continue;
            }

            (batch ??= new List<PositionedObject>(SheetBatch)).Add(held);
            if (batch.Count == SheetBatch)
            {
                if (columns is null)
                {
                    columns = SheetPruning.Columns(filter, schema);
                    partitions = PartitionLocates.Of(_dataset, _version, filter, columns);
                }

                foreach (PositionedObject kept in await SheetPruning.KeptAsync(
                    _dataset, _version.Schema, batch, filter, columns, metrics, _consulted ??= held => ReadsSheets(held), cancellationToken, _narrowing ??= Narrow,
                    partitions, SheetPaths()).ConfigureAwait(false))
                {
                    yield return kept;
                }

                batch.Clear();
            }
        }

        if (batch is { Count: > 0 })
        {
            if (columns is null)
            {
                columns = SheetPruning.Columns(filter, schema);
                partitions = PartitionLocates.Of(_dataset, _version, filter, columns);
            }

            foreach (PositionedObject kept in await SheetPruning.KeptAsync(
                _dataset, _version.Schema, batch, filter, columns, metrics, _consulted ??= held => ReadsSheets(held), cancellationToken, _narrowing ??= Narrow,
                partitions, SheetPaths()).ConfigureAwait(false))
            {
                yield return kept;
            }
        }
    }

    /// <summary>Keeps the granules of the object <paramref name="held"/> a selected row may lie in, which its read takes alone.</summary>
    private void Narrow(PositionedObject held, ulong[] granules)
    {
        System.Collections.Concurrent.ConcurrentDictionary<UInt128, ulong[]> narrowed = Volatile.Read(ref _narrowed)
            ?? Interlocked.CompareExchange(ref _narrowed, new System.Collections.Concurrent.ConcurrentDictionary<UInt128, ulong[]>(), null)
            ?? _narrowed!;
        narrowed[held.Entry.Uid] = granules;
    }
}

/// <summary>One data object and where its rows start in the dataset.</summary>
/// <param name="Entry">Its leaf entry, summaries included.</param>
/// <param name="FirstRow">Its first row among the dataset's, in the tree's order.</param>
internal readonly record struct PositionedObject(ObjectEntry Entry, long FirstRow)
{
    /// <summary>The level whose tree holds it; level 0 is the one an append lands in.</summary>
    public int Level { get; init; }

    /// <summary>
    /// The rows of the object, its own, that a read takes of it: the granules the key's pruning kept, one range a part
    /// (<see cref="KeyPruning"/>); null for every row.
    /// </summary>
    internal RowRange? Granules { get; init; }

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
