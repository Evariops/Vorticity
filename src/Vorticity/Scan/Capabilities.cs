using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// The answer of a form of a question (<see cref="LogicalShape"/>) a source answers from its rows and what it knows of them: a
/// count, whether a row is, a column's extreme.
/// </summary>
/// <param name="Rows">The count; for whether a row is, 1 when one is and 0 otherwise.</param>
/// <param name="Value">A column's extreme, a null literal when no row holds one.</param>
internal readonly record struct FormAnswer(long Rows, FilterLiteral Value);

/// <summary>
/// What a source gives as rows (spec B, 8): the batches of a read, the forms of a question it answers from them, the order it
/// brings them in when asked, and what a read will do, before reading a data segment.
/// </summary>
internal interface IRowsCapability
{
    /// <summary>
    /// Whether the source settles the blocks of its files by their zone maps itself, as it reads each, when a pass names the
    /// aggregation (<see cref="StatesAsked.Settles"/>): a dataset, whose objects' marked rows only it knows, whether its
    /// objects keep counts or not.
    /// </summary>
    bool SettlesBlocks => false;

    /// <summary>The batches, borrowed: each is valid until the next is asked for.</summary>
    IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics);

    /// <summary>
    /// The answer of <paramref name="form"/> over the rows <paramref name="spec"/> keeps: <see cref="LogicalShape.Count"/>,
    /// <see cref="LogicalShape.Any"/>, or an <see cref="ExtremeShape"/>, each from the cheapest proof the source holds.
    /// </summary>
    ValueTask<FormAnswer> AnswerAsync(ScanSpec spec, LogicalShape form, ScanCounters metrics, CancellationToken cancellationToken);

    /// <summary>
    /// Tallies the paths of <paramref name="question"/>'s variant columns among the rows <paramref name="spec"/> keeps: by
    /// default a census of the rows a read of them brings, a source of any kind; a file and a dataset answer from the path
    /// directories of their objects first, as a count would plan them, reading no row where a directory answers.
    /// </summary>
    ValueTask PathsAsync(ScanSpec spec, PathsQuestion question, ScanCounters metrics, CancellationToken cancellationToken) =>
        PathQuestions.CountAsync(BatchesAsync(spec, metrics), question, question.Columns, cancellationToken);

    /// <summary>
    /// What a read of <paramref name="spec"/> will do (spec B, 7): from what the source holds in memory alone, reading
    /// nothing; with <paramref name="analyze"/>, the structures the read would consult read as it reads them, and no data
    /// segment.
    /// </summary>
    ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, bool analyze, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a scan that asks for the order of <paramref name="column"/> (<see cref="ScanSpec.OrderPath"/>) brings its rows
    /// in it at a cost a stream can bear: a Parquet file on a column it lies in; a dataset on the first column of its
    /// sort key when <paramref name="stopsEarly"/>, a take or a window stopping the read, whose objects a key-ordered
    /// read opens as the order reaches them. A file whose statistics say a column is sorted needs no asking.
    /// </summary>
    bool OrdersOnAsking(FieldExpr column, bool stopsEarly) => false;

    /// <summary>
    /// Whether a window of the rows in the order of <paramref name="column"/> reads cheaper late (spec G, 2): the column
    /// read alone, the places of the window's rows kept, then the other columns at those places, rather than walked in the
    /// order from a key source. A dataset on any column but the first of its sort key, in whose order its objects lie.
    /// </summary>
    bool ReadsTopLate(FieldExpr column) => false;

    /// <summary>
    /// The places of the best <paramref name="keep"/> rows <paramref name="spec"/> keeps in the order of
    /// <paramref name="path"/>, ascending, a null last and a tie to the earlier place (spec G, 2); null for the one read of
    /// the filter and the column that serves every source. A dataset reads its objects and granules best first (spec G, 3).
    /// </summary>
    ValueTask<long[]?> TopPositionsAsync(ScanSpec spec, string path, bool descending, long keep, ScanCounters metrics, CancellationToken cancellationToken) =>
        ValueTask.FromResult<long[]?>(null);
}

/// <summary>What a source knows of its rows before reading them (spec B, 8): their number, bounds, and what rules a filter out.</summary>
internal interface IStatisticsCapability
{
    /// <summary>The rows of the whole source, whatever a scan keeps of them; -1 when they are not known before it is read.</summary>
    long RowBound => -1;

    /// <summary>
    /// The smallest and the largest value of the column at <paramref name="path"/> over the whole source, whatever a scan
    /// keeps of it, when its statistics hold them.
    /// </summary>
    bool TryBounds(int[] path, out FilterLiteral min, out FilterLiteral max)
    {
        min = FilterLiteral.Null;
        max = FilterLiteral.Null;
        return false;
    }

    /// <summary>
    /// The smallest and the largest value of an integer column of a key, as the source's statistics bound them without a
    /// read: what bounds a table of its groups by value (<see cref="FixedKeys{TValue}"/>); null when nothing in hand bounds
    /// them.
    /// </summary>
    KeyBounds? Bounds(ColumnShape key) => null;

    /// <summary>
    /// <see cref="Bounds"/> once the source read what its scan reads first anyway: a dataset's roots, whose summaries bound
    /// its objects, and no request the scan would not make.
    /// </summary>
    ValueTask<KeyBounds?> BoundsAsync(ColumnShape key, CancellationToken cancellationToken) => ValueTask.FromResult(Bounds(key));
}

/// <summary>What a source gives as keys (spec B, 8): the walks of a column's keys in their order.</summary>
internal interface IKeysCapability
{
    /// <summary>A walker over the keys of the column at <paramref name="path"/>, from the source's key structures.</summary>
    ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken);

    /// <summary>Which key source <see cref="OpenKeysAsync"/> would walk.</summary>
    ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken);
}

/// <summary>
/// What a source gives as states of an aggregation in place of rows (spec B, 8): the alternatives that answer some of its
/// units from their synopses, and the window an aggregation may align on them.
/// </summary>
internal interface IStatesCapability
{
    /// <summary>
    /// Whether a pass of <paramref name="spec"/> asking <paramref name="states"/> would take states in place of some of its
    /// rows: a dataset one of whose units its synopses answer. Read from what the version's entries hold, before any data.
    /// </summary>
    ValueTask<bool> TakesStatesAsync(ScanSpec spec, StateRequest states, CancellationToken cancellationToken);

    /// <summary>
    /// The filter of <paramref name="spec"/> an aggregation asking <paramref name="states"/> answers under
    /// <see cref="ScanOptions.AlignWindow"/>, and the range it applies: its range on the column the source is ordered by,
    /// grown to the bounds of what it cuts that states would answer whole. Null when it grows nothing.
    /// </summary>
    ValueTask<(VortexExpr Filter, VortexExpr Window)?> AlignAsync(ScanSpec spec, StateRequest states, CancellationToken cancellationToken) =>
        ValueTask.FromResult<(VortexExpr, VortexExpr)?>(null);
}

/// <summary>What a source gives as tasks (spec B, 8): its units as ranges of rows read side by side, and the ranges an estimate draws from.</summary>
internal interface ITasksCapability
{
    /// <summary>
    /// The ranges of rows a pass of <paramref name="degree"/> lanes reads side by side, each a range of
    /// <see cref="ScanSpec.Rows"/> the source reads alone: the objects of a dataset, or parts of a large one. Null when the
    /// source has none of its own; a file's are cut by the engine, at its chunks.
    /// </summary>
    ValueTask<RowRange[]?> PiecesAsync(ScanSpec spec, int degree, CancellationToken cancellationToken) => default;

    /// <summary>
    /// The rows of <paramref name="pass"/> as the ranges an estimate draws from, a list for each file or object, in the order
    /// of their rows: each cut where the chunks of every column the pass reads end together, a range holding
    /// <paramref name="rangeBytes"/> of the narrowest of them at least (<see cref="EstimateStrata"/>). Null for a source with
    /// no cut of its own, which takes no estimate.
    /// </summary>
    ValueTask<RowRange[][]?> StrataAsync(ScanSpec pass, long rangeBytes, CancellationToken cancellationToken) => default;
}

/// <summary>What reading a source costs (spec B, 8): the profile its paths are priced at, the weight of a read, its reads ahead.</summary>
internal interface ICostCapability
{
    /// <summary>What reading the source costs, which its paths are priced by: a remote store's unless it says otherwise.</summary>
    CostProfile Profile => CostProfile.Remote;

    /// <summary>
    /// Whether a read of the source is a round trip, to an object store rather than to memory or a local file: a pass of one
    /// lane then reads its splits ahead as a wider pass's lanes do, its reads overlapping instead of waiting one after
    /// another.
    /// </summary>
    bool ReadsRoundTrips => false;

    /// <summary>
    /// The most bytes one split of <paramref name="spec"/> read ahead holds (<see cref="ScanSpec.ReadAhead"/>), over a source
    /// that copies what it reads; 0 over one that reads in place, where a read is a view, and for a source that reads nothing
    /// ahead.
    /// </summary>
    long ReadAheadBytes(ScanSpec spec) => 0;

    /// <summary>
    /// What reading <paramref name="spec"/> would cost, its requests and bytes as the source's planner prices its paths before
    /// reading anything (<see cref="Profile"/>); null for a source that cannot say.
    /// </summary>
    ValueTask<(long Requests, long Bytes)?> CostAsync(ScanSpec spec, CancellationToken cancellationToken) => default;
}
