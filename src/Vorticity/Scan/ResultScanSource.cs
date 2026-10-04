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
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// A query whose result is a stream of batches: what a result's scan reads, whatever produced it.
/// Its batches are borrowed, each valid until the next is asked for.
/// </summary>
internal abstract class ResultQuery
{
    /// <summary>The result's columns.</summary>
    internal abstract VortexSchema Schema { get; }

    /// <summary>The session whose pool and options the query runs with.</summary>
    internal abstract VortexSession Session { get; }

    /// <summary>What the query's own scan did, which a result's scan reports as its own.</summary>
    internal abstract ScanMetrics Metrics { get; }

    /// <summary>The result's batches; the query runs as they are asked for.</summary>
    internal abstract IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken);

    /// <summary>The plan of the query's scan.</summary>
    internal abstract ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken);

    /// <summary>The same query, its result's columns named and typed by the members of a record, in order.</summary>
    /// <param name="record">The record's schema.</param>
    /// <param name="type">The record's type, for a message.</param>
    /// <exception cref="VortexSchemaException">The record has another number of members, or a member does not take its element.</exception>
    internal abstract ResultQuery As(VortexSchema record, Type type);

    /// <summary>The same query, its rows past the first <paramref name="skip"/> of those it delivers, <paramref name="take"/> of them at most.</summary>
    internal abstract ResultQuery Limit(long skip, long take);

    /// <summary>How the values of column <paramref name="column"/> are records: a custom aggregate's state; null for a column of values.</summary>
    internal virtual IVortexRecord? RecordOf(int column) => null;

    /// <summary>The window [<paramref name="skip"/>, <paramref name="skip"/> + <paramref name="take"/>) of a window [<paramref name="skipped"/>, <paramref name="skipped"/> + <paramref name="taken"/>): the operators in the order written.</summary>
    internal static (long Skip, long Take) Within(long skipped, long taken, long skip, long take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        long left = taken == long.MaxValue ? long.MaxValue : Math.Max(0, taken - skip);
        return (skipped + skip, Math.Min(left, take));
    }
}

/// <summary>
/// The source of a result's scan: the batches of a query's result, with what a scan asks of its
/// source applied to each — the filter evaluated on it, the rows by their position in the order
/// the result is delivered, the columns projected. A result has no statistics, zone maps or
/// indexes: nothing is pruned, and every batch is evaluated.
/// </summary>
internal sealed class ResultScanSource : ScanSource
{
    private readonly ResultQuery _query;

    internal ResultScanSource(ResultQuery query) => _query = query;

    internal override VortexSchema Schema => _query.Schema;

    internal override VortexSession Session => _query.Session;

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanMetrics metrics) => new Stream(this, spec);

    internal override async ValueTask<long> CountAsync(ScanSpec spec, ScanMetrics metrics, CancellationToken cancellationToken)
    {
        long count = 0;
        await foreach (RecordBatch batch in SelectedAsync(spec).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            count += batch.SelectedRows;
        }

        return count;
    }

    internal override async ValueTask<bool> AnyAsync(ScanSpec spec, ScanMetrics metrics, CancellationToken cancellationToken)
    {
        await foreach (RecordBatch batch in SelectedAsync(spec).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (batch.SelectedRows > 0)
            {
                return true;
            }
        }

        return false;
    }

    internal override async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanMetrics metrics, CancellationToken cancellationToken)
    {
        FilterLiteral best = FilterLiteral.Null;
        int[] rows = [];
        await foreach (RecordBatch batch in SelectedAsync(spec).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            CanonicalArena arena = batch.Arena;
            int count = batch.RowCount;
            int node = FilterEvaluator.Resolve(arena, batch.RootIndex, column, count);
            ReadOnlySpan<ulong> selection = batch.SelectionWords;
            int listed = 0;
            if (!selection.IsEmpty)
            {
                Scratch.Grow(ref rows, count);
                foreach (int row in new Selection(selection, count, batch.SelectedRows))
                {
                    rows[listed++] = row;
                }
            }

            if (!Extremes.TryFind(arena, node, rows.AsSpan(0, listed), !selection.IsEmpty, min, out int bestRow))
            {
                continue;
            }

            bool numeric = arena.GetNode(ComparisonKernels.Unwrap(arena, node)).DType.Kind == DTypeKind.Decimal;
            if (numeric ? LiteralReader.TryReadDecimal(arena, node, bestRow, out FilterLiteral literal) : LiteralReader.TryRead(arena, node, bestRow, out literal))
            {
                TerminalScan.Keep(ref best, literal, min, numeric);
            }
        }

        return best;
    }

    internal override ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken) => _query.ExplainAsync(cancellationToken);

    internal override bool MayMatch(VortexExpr filter) => true;

    internal override ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A result's scan has no key source yet: order its query with OrderBy before the Select.");

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A result's scan has no key source yet: order its query with OrderBy before the Select.");

    /// <summary>The batches of <paramref name="spec"/> left whole, with a selection of the rows kept: what a count or an extreme reads.</summary>
    private Stream SelectedAsync(ScanSpec spec) => new Stream(this, spec with { Options = spec.Options with { Compact = false } });

    /// <summary>The batches a spec asks of a result.</summary>
    private sealed class Stream(ResultScanSource source, ScanSpec spec) : IAsyncEnumerable<RecordBatch>
    {
        public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (spec.MatchesNothing)
            {
                return new Enumerator(source, spec, EmptyBatches.Instance);
            }

            // An order the result does not arrive in is a sort of the rows kept, held whole.
            return spec.OrderPath is null
                ? new Enumerator(source, spec, source._query.Batches(cancellationToken))
                : new SortedEnumerator(source, spec, source._query.Batches(cancellationToken), cancellationToken);
        }
    }

    /// <summary>
    /// The rows a spec keeps of a result, in the order of one of its columns: gathered into one
    /// batch, sorted by position, nulls last, ties in the result's order, then delivered a batch at
    /// a time. A blocking stage, holding the rows kept.
    /// </summary>
    private sealed class SortedEnumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly ResultScanSource _source;
        private readonly ScanSpec _spec;
        private readonly Enumerator _kept;
        private readonly CancellationToken _cancellationToken;
        private StructStore? _store;
        private CanonicalArena? _arena;
        private RecordBatch? _current;
        private int[] _positions = [];
        private int _root;
        private int _count;
        private int _next;

        internal SortedEnumerator(ResultScanSource source, ScanSpec spec, IAsyncEnumerator<RecordBatch> inner, CancellationToken cancellationToken)
        {
            _source = source;
            _spec = spec;
            _cancellationToken = cancellationToken;
            _kept = new Enumerator(source, spec with { OrderPath = null, Descending = false, Options = spec.Options with { Compact = true } }, inner);
        }

        public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            if (_store is null)
            {
                VortexSchema schema = _spec.Projection is { IsAll: false } mask ? ToolPaths.Project(_source.Schema, mask) : _source.Schema;
                VortexSessionOptions options = _source.Session.Options;
                _store = (StructStore)ColumnStores.Create(VortexTypes.ToDType(schema, new DTypeArena()), options.EnginePool, options.Extensions);
                _arena = new CanonicalArena(64, options.EnginePool);
                while (await _kept.MoveNextAsync().ConfigureAwait(false))
                {
                    RecordBatch batch = _kept.Current;
                    StoreRows.Append(_store, batch.Arena, batch.RootIndex);
                }

                _count = _store.Rows;
                _root = _store.Build(_arena, _count);
                int key = FilterEvaluator.Resolve(_arena, _root, new FieldExpr(_spec.OrderPath!), _count);
                ColumnOrder order = ColumnOrder.For(_arena, key, schema.IndexOfName(_spec.OrderPath!) is int at and >= 0 ? schema[at].Type : VortexType.Null, _spec.Descending);
                _positions = new int[_count];
                for (int i = 0; i < _count; i++)
                {
                    _positions[i] = i;
                }

                GroupSort.Sort(_positions, _count, new ChainOrder([order, ColumnOrder.Positions]), long.MaxValue, _cancellationToken);
            }

            if (_next >= _count)
            {
                _current?.Dispose();
                return false;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            int rows = Math.Min(GroupBatches.BatchRows, _count - _next);
            int gathered = CanonicalFilter.Apply(_arena!, _root, _positions.AsSpan(_next, rows));
            _current?.Dispose();
            _current = RecordBatch.Over(_arena!, gathered, _next, _current);
            _next += rows;
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _current?.Dispose();
            await _kept.DisposeAsync().ConfigureAwait(false);
            _store?.Release();
            _arena?.Reset();
        }
    }

    /// <summary>A result with no batch: a filter known to match nothing.</summary>
    private sealed class EmptyBatches : IAsyncEnumerator<RecordBatch>
    {
        internal static readonly EmptyBatches Instance = new EmptyBatches();

        public RecordBatch Current => throw new InvalidOperationException("The stream has no current batch.");

        public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The result's batches with the spec applied to each: the rows it keeps, by position and by
    /// filter, compacted or selected, and the columns it reads.
    /// </summary>
    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly ScanSpec _spec;
        private readonly IAsyncEnumerator<RecordBatch> _inner;
        private readonly FilterEvaluator? _filter;
        private readonly int[]? _fields;
        private readonly DType _projected;
        private byte[] _states = [];
        private int[] _kept = [];
        private RecordBatch? _current;
        private int _taken;

        internal Enumerator(ResultScanSource source, ScanSpec spec, IAsyncEnumerator<RecordBatch> inner)
        {
            if (spec.OrderPath is not null)
            {
                throw new NotSupportedException("A result's scan is delivered in the order of its query: order the query with OrderBy before the Select.");
            }

            _spec = spec;
            _inner = inner;
            _filter = spec.Filter is { } filter ? new FilterEvaluator(filter) : null;
            if (spec.Projection is { IsAll: false } mask)
            {
                VortexSchema projected = ToolPaths.Project(source.Schema, mask);
                _fields = new int[mask.NamedFieldCount];
                for (int i = 0; i < _fields.Length; i++)
                {
                    _fields[i] = mask.GetNamedField(i);
                }

                _projected = VortexTypes.ToDType(projected, new DTypeArena());
            }
        }

        public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            while (await _inner.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch batch = _inner.Current;
                if (Past(batch.StartRow))
                {
                    break;
                }

                if (Keep(batch))
                {
                    return true;
                }
            }

            _current?.Dispose();
            return false;
        }

        public ValueTask DisposeAsync()
        {
            _current?.Dispose();
            return _inner.DisposeAsync();
        }

        /// <summary>Whether no row from <paramref name="start"/> on is one the spec asks for, so that the read stops.</summary>
        private bool Past(long start) =>
            (_spec.Rows is { } range && start >= range.End) || (_spec.Take is { } take && _taken >= take.Length);

        /// <summary>The rows of <paramref name="batch"/> the spec keeps, as the current batch; false when it keeps none.</summary>
        private bool Keep(RecordBatch batch)
        {
            int rows = batch.RowCount;
            long start = batch.StartRow;
            CanonicalArena arena = batch.Arena;
            int root = batch.RootIndex;
            Scratch.Grow(ref _states, rows);
            Span<byte> states = _states.AsSpan(0, rows);
            if (_filter is null)
            {
                states.Fill(Trilean.True);
            }
            else
            {
                _filter.Evaluate(arena, root, rows, states);
            }

            if (_spec.Rows is { } range)
            {
                // The rows before the range and past it.
                states[..(int)Math.Clamp(range.Start - start, 0, rows)].Fill(Trilean.False);
                states[(int)Math.Clamp(range.End - start, 0, rows)..].Fill(Trilean.False);
            }

            if (_spec.Take is { } take)
            {
                // The positions asked for, sorted: those of this batch are a run of them.
                int first = _taken;
                while (_taken < take.Length && take[_taken] < start + rows)
                {
                    _taken++;
                }

                for (int row = 0, next = first; row < rows; row++)
                {
                    if (next < _taken && take[next] == start + row)
                    {
                        next++;
                    }
                    else
                    {
                        states[row] = Trilean.False;
                    }
                }
            }

            int selected = Trilean.CountTrue(states);
            if (selected == 0)
            {
                return false;
            }

            Buffers.VortexBuffer words = default;
            if (selected < rows)
            {
                if (_spec.Options.Compact)
                {
                    Scratch.Grow(ref _kept, rows);
                    int count = CanonicalFilter.Select(states, _kept);
                    root = CanonicalFilter.Apply(arena, root, _kept.AsSpan(0, count));
                }
                else
                {
                    words = arena.AllocateUninitialized(Math.Max((rows + 63) >> 6, 1) * sizeof(ulong), 64, out Span<byte> raw);
                    Span<ulong> bits = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(raw);
                    bits[0] = 0;
                    Trilean.ToWords(states, Trilean.True, equal: true, bits);
                }
            }

            _current?.Dispose();
            _current = RecordBatch.Over(arena, Project(arena, root), start, _current);
            if (!words.IsEmpty)
            {
                _current.Select(words, selected);
            }

            return true;
        }

        /// <summary>The columns the spec reads, a struct of them over the same children.</summary>
        private int Project(CanonicalArena arena, int root)
        {
            if (_fields is null)
            {
                return root;
            }

            CanonicalNode node = arena.GetNode(root);
            Span<int> children = _fields.Length <= 64 ? stackalloc int[_fields.Length] : new int[_fields.Length];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = node.GetFieldIndex(_fields[i]);
            }

            return arena.AddStruct(_projected, node.Length, node.Validity, children);
        }
    }
}
