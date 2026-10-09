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
/// A source read as a stream of batches of all its rows, in its order, with nothing in hand that
/// prunes them: a query's result, a Parquet file. What a scan asks of it is applied to each batch as
/// it arrives — the filter evaluated on it, the rows kept by their position, compacted or selected,
/// the columns projected — and an order the batches do not arrive in is a sort of the rows kept.
/// </summary>
internal abstract class StreamScanSource : ScanSource
{
    /// <summary>
    /// The source's batches, every row in its order, each borrowed until the next is asked for: the
    /// root a struct of the schema's columns that <paramref name="columns"/> names, in the schema's
    /// order, or of every column when it is null or <see cref="ReadsColumns"/> is false.
    /// </summary>
    /// <param name="spec">
    /// The scan: a source may leave out the rows before and past its <see cref="ScanSpec.Rows"/>,
    /// and the rows it proves its <see cref="ScanSpec.Filter"/> does not select, and no other.
    /// </param>
    /// <param name="columns">The columns the scan reads, ascending: those it delivers and those its filter reads; null for every column.</param>
    /// <param name="metrics">The scan's counts, for what the source reads.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    private protected abstract IAsyncEnumerator<RecordBatch> Stream(ScanSpec spec, int[]? columns, ScanCounters metrics, CancellationToken cancellationToken);

    /// <summary>
    /// The source's batches of <paramref name="part"/> alone, a part of its rows the source described
    /// itself: what a count reads of the rows its statistics did not decide. The whole stream unless
    /// the source knows parts.
    /// </summary>
    private protected virtual IAsyncEnumerator<RecordBatch> Stream(ScanSpec spec, int[]? columns, ScanCounters metrics, object part, CancellationToken cancellationToken) =>
        Stream(spec, columns, metrics, cancellationToken);

    /// <summary>Whether <see cref="Stream(ScanSpec, int[], ScanCounters, CancellationToken)"/> delivers the columns it is asked for alone, rather than every column whatever it is asked.</summary>
    private protected virtual bool ReadsColumns => false;

    /// <summary>What the source is, as a refusal names it: "result", "Parquet file".</summary>
    private protected abstract string Kind { get; }

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) => new Batches(this, spec, metrics);

    internal override ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        CountAsync(spec, metrics, part: null, cancellationToken);

    /// <summary>The rows <paramref name="spec"/> selects of those the source streams: of <paramref name="part"/> alone, when it is given.</summary>
    private protected async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, object? part, CancellationToken cancellationToken)
    {
        long count = 0;
        await foreach (RecordBatch batch in SelectedAsync(spec, metrics, part).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            count += batch.SelectedRows;
        }

        return count;
    }

    internal override async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        await foreach (RecordBatch batch in SelectedAsync(spec, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (batch.SelectedRows > 0)
            {
                return true;
            }
        }

        return false;
    }

    internal override async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
    {
        FilterLiteral best = FilterLiteral.Null;
        int[] rows = [];
        await foreach (RecordBatch batch in SelectedAsync(spec, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
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

    internal override bool MayMatch(VortexExpr filter) => true;

    /// <summary>
    /// The keys of a column, read whole as the source streams and sorted in memory unless they arrive
    /// in order, reserved under the session's memory budget until the cursor is disposed.
    /// </summary>
    internal override async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken)
    {
        if (Unordered(path, out FilterLiteralKind kind, out int column) is { } reason)
        {
            throw new NotSupportedException($"'{path}' has no key cursor: {reason}.");
        }

        QueryMemory memory = new QueryMemory(Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        try
        {
            IAsyncEnumerator<RecordBatch> batches = Stream(new ScanSpec(), ReadsColumns ? [column] : null, new ScanCounters(), cancellationToken);
            MemoryKeySource source = await MemoryKeySource.ReadAsync(batches, new FieldExpr(path), kind, cancellationToken, memory).ConfigureAwait(false);
            return new KeyCursor(source, distinct);
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        new ValueTask<KeyPlan>(Unordered(path, out _, out _) is { } reason
            ? new KeyPlan(path, KeySourceKind.None, 0, null, HasRows: false, [new KeySourceRejection(KeySourceKind.InMemory, reason)])
            : new KeyPlan(path, KeySourceKind.InMemory, 1, null, HasRows: true, []));

    /// <summary>Why the column at <paramref name="path"/> has no key order, or null with its key domain and its column.</summary>
    private string? Unordered(string path, out FilterLiteralKind kind, out int top)
    {
        kind = FilterLiteralKind.Null;
        top = -1;
        DType column = VortexTypes.ToDType(Schema, new DTypeArena());
        foreach (string name in path.Split('.'))
        {
            while (column.Kind == DTypeKind.Extension)
            {
                column = column.StorageType;
            }

            int index = column.Kind == DTypeKind.Struct ? column.IndexOfField(name) : -1;
            if (index < 0)
            {
                return $"the {Kind} has no such column";
            }

            top = top < 0 ? index : top;
            column = column.GetField(index);
        }

        return SortedColumnSource.TryKeyKind(column, out kind) ? null : $"a {column.Kind} column has no key order a cursor can walk";
    }

    /// <summary>The batches of <paramref name="spec"/> left whole, with a selection of the rows kept: what a count or an extreme reads.</summary>
    private Batches SelectedAsync(ScanSpec spec, ScanCounters metrics, object? part = null) =>
        new Batches(this, spec with { Options = spec.Options with { Compact = false } }, metrics, part);

    /// <summary>
    /// The columns a spec reads, ascending: those it projects, and the top-level column of every path
    /// its filter reads; null for every column.
    /// </summary>
    private int[]? ColumnsOf(ScanSpec spec)
    {
        if (!ReadsColumns || spec.Projection is not { IsAll: false } mask)
        {
            return null;
        }

        bool[] read = new bool[Schema.Count];
        for (int i = 0; i < mask.NamedFieldCount; i++)
        {
            read[mask.GetNamedField(i)] = true;
        }

        if (spec.Filter is { } filter)
        {
            DType schema = VortexTypes.ToDType(Schema, new DTypeArena());
            List<FieldExpr> paths = [];
            ScanBuilder.FieldsOf(filter, paths);
            foreach (FieldExpr path in paths)
            {
                if (schema.IndexOfField(path.SegmentsUtf8[0]) is int field and >= 0)
                {
                    read[field] = true;
                }
            }
        }

        int count = 0;
        foreach (bool column in read)
        {
            count += column ? 1 : 0;
        }

        int[] columns = new int[count];
        for (int field = 0, at = 0; field < read.Length; field++)
        {
            if (read[field])
            {
                columns[at++] = field;
            }
        }

        return columns;
    }

    /// <summary>The batches a spec asks of the source: of a part of its rows, when one is given.</summary>
    private sealed class Batches(StreamScanSource source, ScanSpec spec, ScanCounters metrics, object? part = null) : IAsyncEnumerable<RecordBatch>
    {
        public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (spec.MatchesNothing)
            {
                return new Enumerator(source, spec, EmptyBatches.Instance, null);
            }

            int[]? columns = source.ColumnsOf(spec);
            IAsyncEnumerator<RecordBatch> stream = part is null
                ? source.Stream(spec, columns, metrics, cancellationToken)
                : source.Stream(spec, columns, metrics, part, cancellationToken);

            // An order the source does not arrive in is a sort of the rows kept, held whole.
            return spec.OrderPath is null
                ? new Enumerator(source, spec, stream, columns)
                : new SortedEnumerator(source, spec, stream, columns, cancellationToken);
        }
    }

    /// <summary>
    /// The rows a spec keeps of the source, in the order of one of its columns, nulls last, ties in
    /// the source's order: sorted under the session's memory budget, in memory while it holds them, by
    /// runs written to the scratch and merged back otherwise; delivered a batch at a time. A blocking
    /// stage.
    /// </summary>
    private sealed class SortedEnumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly StreamScanSource _source;
        private readonly ScanSpec _spec;
        private readonly Enumerator _kept;
        private readonly CancellationToken _cancellationToken;
        private QueryMemory? _memory;
        private ExternalSort? _sort;
        private IAsyncEnumerator<RecordBatch>? _sorted;

        internal SortedEnumerator(StreamScanSource source, ScanSpec spec, IAsyncEnumerator<RecordBatch> inner, int[]? columns, CancellationToken cancellationToken)
        {
            _source = source;
            _spec = spec;
            _cancellationToken = cancellationToken;
            _kept = new Enumerator(source, spec with { OrderPath = null, Descending = false, Options = spec.Options with { Compact = true } }, inner, columns);
        }

        public RecordBatch Current => _sorted?.Current ?? throw new InvalidOperationException("The stream has no current batch.");

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            if (_sorted is null)
            {
                VortexSchema schema = _spec.Projection is { IsAll: false } mask ? ToolPaths.Project(_source.Schema, mask) : _source.Schema;
                VortexSession session = _source.Session;
                _memory = new QueryMemory(session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
                VortexType type = schema.IndexOfName(_spec.OrderPath!) is int at and >= 0 ? schema[at].Type : VortexType.Null;
                int[] delivered = new int[schema.Count];
                for (int c = 0; c < delivered.Length; c++)
                {
                    delivered[c] = c;
                }

                _sort = new ExternalSort(session, schema, [new SortKey(new FieldExpr(_spec.OrderPath!), type, _spec.Descending)], delivered, _memory, GroupBatches.BatchRows);
                while (await _kept.MoveNextAsync().ConfigureAwait(false))
                {
                    await _sort.AddAsync(_kept.Current, _cancellationToken).ConfigureAwait(false);
                }

                _sorted = await _sort.SortedAsync(_cancellationToken).ConfigureAwait(false);
            }

            return await _sorted.MoveNextAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_sorted is not null)
            {
                await _sorted.DisposeAsync().ConfigureAwait(false);
            }

            await _kept.DisposeAsync().ConfigureAwait(false);
            if (_sort is not null)
            {
                await _sort.DisposeAsync().ConfigureAwait(false);
            }

            _memory?.Dispose();
        }
    }

    /// <summary>A source with no batch: a filter known to match nothing.</summary>
    private sealed class EmptyBatches : IAsyncEnumerator<RecordBatch>
    {
        internal static readonly EmptyBatches Instance = new EmptyBatches();

        public RecordBatch Current => throw new InvalidOperationException("The stream has no current batch.");

        public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The source's batches with the spec applied to each: the rows it keeps, by position and by
    /// filter, compacted or selected, and the columns it delivers.
    /// </summary>
    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly ScanSpec _spec;
        private readonly IAsyncEnumerator<RecordBatch> _inner;
        private readonly FilterEvaluator? _filter;

        /// <summary>Per column delivered, its field in the stream's struct; null when the stream's struct is delivered as it is.</summary>
        private readonly int[]? _fields;

        private readonly DType _projected;
        private byte[] _states = [];
        private int[] _kept = [];
        private RecordBatch? _current;
        private int _taken;

        internal Enumerator(StreamScanSource source, ScanSpec spec, IAsyncEnumerator<RecordBatch> inner, int[]? columns)
        {
            if (spec.OrderPath is not null)
            {
                throw new NotSupportedException("The source's batches arrive in its own order: order the query with OrderBy before the Select.");
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
                    int field = mask.GetNamedField(i);
                    _fields[i] = columns is null ? field : Array.BinarySearch(columns, field);
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
                // The positions asked for, sorted: those of this batch are a run of them, after those
                // of any rows the source left out, which its filter does not select.
                while (_taken < take.Length && take[_taken] < start)
                {
                    _taken++;
                }

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

        /// <summary>The columns the spec delivers, a struct of them over the same children.</summary>
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
