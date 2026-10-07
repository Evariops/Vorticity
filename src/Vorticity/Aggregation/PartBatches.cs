using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The batches of the parts of a core's result, each built where its part is applied: the operators on
/// its groups applied, its chosen rows fetched, its
/// groups written into stores of the result's columns while they are in the cache of the core that
/// applied them, then the part let go. The result's reader hands the batches out, in the order the
/// parts come, and gives each store back once its batch is done with.
/// </summary>
/// <remarks>
/// A result's columns keep what they read a batch with: each worker builds with a set of its own,
/// taken from a pool as it starts a part. The stores are pooled too, a batch each: the parts built
/// ahead of the reader hold as many as the bound on them allows.
/// </remarks>
internal sealed class PartBuilder
{
    private readonly AggregationQuery _query;
    private readonly int _batchRows;
    private readonly ScanSpec _spec;
    private readonly ConcurrentBag<ResultColumn[]> _columns = [];
    private readonly Lock _gate = new Lock();
    private readonly Stack<PartSlate> _slates = [];
    private DType _dtype;
    private bool _released;

    // A result sorted in runs: the operators before its order alone, and
    // the sort's columns, whose rows each part is built into.
    private int? _until;
    private Func<ResultColumn[]>? _sortColumns;

    internal PartBuilder(AggregationQuery query, int batchRows)
    {
        _query = query;
        _batchRows = batchRows;
        _spec = query.Host.Spec(query.RowFilter);
    }

    /// <summary>
    /// Each part built into the rows of a sort of the result, of
    /// <paramref name="schema"/>, by <paramref name="columns"/>, after the operators before
    /// <paramref name="order"/>, the order's own, alone. Asked before any part is built.
    /// </summary>
    internal void SortRows(int order, VortexSchema schema, Func<ResultColumn[]> columns)
    {
        lock (_gate)
        {
            _until = order;
            _sortColumns = columns;
            _dtype = VortexTypes.ToDType(schema, new DTypeArena());
        }

        _columns.Clear();
    }

    /// <summary>
    /// The batches of the groups of <paramref name="outcome"/>, a part's, its operators keep, each of
    /// the result's batch at most; the groups the part found, before its operators, counted.
    /// </summary>
    internal async ValueTask<PartResult> BuildAsync(AggregationOutcome outcome, CancellationToken cancellationToken)
    {
        // The result's window is the reader's, over every part: each part's chosen rows are read for
        // every group its operators keep.
        (int[] groups, int count) = await GroupSelection.ApplyAsync(_query, outcome, _spec, cancellationToken, windowed: false, until: _until).ConfigureAwait(false);
        PartResult result = new PartResult(outcome.Keys?.Count ?? 0);
        if (!_columns.TryTake(out ResultColumn[]? columns))
        {
            columns = _sortColumns is { } sort ? sort() : _query.NewColumns();
        }

        try
        {
            for (int from = 0; from < count; from += _batchRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PartSlate slate = Take();
                result.Slates.Add(slate);
                slate.Build(columns, outcome, groups.AsSpan(from, Math.Min(_batchRows, count - from)));
            }
        }
        catch
        {
            Give(result);
            throw;
        }
        finally
        {
            _columns.Add(columns);
            outcome.LetChosen();
        }

        return result;
    }

    /// <summary>The stores of a part's batches back in the pool: delivered, or never to be.</summary>
    internal void Give(PartResult result)
    {
        foreach (PartSlate slate in result.Slates)
        {
            Give(slate);
        }

        result.Slates.Clear();
    }

    /// <summary>A store back in the pool once its batch is done with; let go once the result is.</summary>
    internal void Give(PartSlate slate)
    {
        lock (_gate)
        {
            if (!_released)
            {
                _slates.Push(slate);
                return;
            }
        }

        slate.Release();
    }

    /// <summary>Every store in the pool let go, their buffers back in the session's pool: the result is done.</summary>
    internal void Release()
    {
        lock (_gate)
        {
            _released = true;
        }

        // Past the flag, no store comes back to the pool: the ones on it are the last.
        foreach (PartSlate slate in _slates)
        {
            slate.Release();
        }

        _slates.Clear();
    }

    /// <summary>A store of the pool, or a new one.</summary>
    private PartSlate Take()
    {
        DType dtype;
        lock (_gate)
        {
            if (_slates.TryPop(out PartSlate? slate))
            {
                return slate;
            }

            if (_dtype.IsDefault)
            {
                _dtype = VortexTypes.ToDType(_query.Schema, new DTypeArena());
            }

            dtype = _dtype;
        }

        VortexSessionOptions options = _query.Session.Options;
        return new PartSlate((StructStore)ColumnStores.Create(dtype, options.EnginePool, options.Extensions), new CanonicalArena(64, options.EnginePool));
    }
}

/// <summary>A part's batches, built, and the groups the part found before its operators.</summary>
internal sealed class PartResult(long groups)
{
    /// <summary>The part's batches, in order.</summary>
    internal List<PartSlate> Slates { get; } = [];

    internal long Groups { get; } = groups;
}

/// <summary>A store of the result's columns and the arena its batch is built in: a batch of a part, pooled.</summary>
internal sealed class PartSlate(StructStore store, CanonicalArena arena)
{
    internal StructStore Store { get; } = store;

    internal CanonicalArena Arena { get; } = arena;

    /// <summary>The batch's root in <see cref="Arena"/>.</summary>
    internal int Root { get; private set; }

    /// <summary>The batch's groups.</summary>
    internal int Rows { get; private set; }

    /// <summary>The values of <paramref name="groups"/> of <paramref name="outcome"/> written in the store, its batch built over them.</summary>
    internal void Build(ResultColumn[] columns, AggregationOutcome outcome, ReadOnlySpan<int> groups)
    {
        Begin();
        Append(columns, outcome, groups);
        Seal();
    }

    /// <summary>The store emptied for a batch written a few groups at a time (<see cref="Append"/>, then <see cref="Seal"/>).</summary>
    internal void Begin()
    {
        Store.Truncate(0);
        Rows = 0;
    }

    /// <summary>The values of <paramref name="groups"/> of <paramref name="outcome"/> written after those before.</summary>
    internal void Append(ResultColumn[] columns, AggregationOutcome outcome, ReadOnlySpan<int> groups)
    {
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c].Append(outcome, Store.Children[c], groups);
        }

        Rows += groups.Length;
    }

    /// <summary>The batch built over the values written.</summary>
    internal void Seal()
    {
        Arena.ResetKeepingBlocks();
        Root = Store.Build(Arena, Rows);
    }

    internal void Release()
    {
        Store.Release();
        Arena.Reset();
    }
}
