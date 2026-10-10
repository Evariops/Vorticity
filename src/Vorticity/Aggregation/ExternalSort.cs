using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>A column of a sort's chain: where it lies among the rows' columns, its type, and whether the largest comes first.</summary>
internal readonly record struct SortKey(FieldExpr Field, VortexType Type, bool Descending);

/// <summary>
/// Rows sorted by a chain of their columns under a query's memory:
/// gathered into a store while the memory holds them, and sorted there when they all fit. Past that,
/// each store's worth is sorted and written to the scratch as a run, a Vortex file of its own in the
/// session's scratch directory, and the runs are merged back a batch at a time, a contiguous stretch
/// of one run at a time. Stable: rows of one key come in the order they came. Each column ranks as
/// <see cref="ColumnOrder"/> ranks it, a null last whichever the direction.
/// </summary>
/// <remarks>
/// It reserves its store's rows twice, a store growing by doubling, and a position and a bit a key
/// column for each row; the store goes to a run once that passes a quarter of the budget's ceiling,
/// the rest left to what feeds it, or once the budget refuses it. The merge holds a batch a run and
/// the batch it builds. The runs' files are written as the writer writes them fastest, without
/// statistics, and deleted once the sort is.
/// </remarks>
internal sealed class ExternalSort : IAsyncDisposable
{
    private static readonly VortexWriteOptions RunOptions = new VortexWriteOptions { Compression = CompressionProfile.Fastest, WriteStatistics = false };

    private readonly VortexSession _session;
    private readonly VortexSchema _schema;
    private readonly DType _dtype;
    private readonly SortKey[] _keys;
    private readonly int[] _delivered;
    private readonly DType _deliveredType;
    private readonly QueryMemory? _memory;
    private readonly long _runBytes;
    private readonly int _batchRows;
    private readonly List<string> _runs = [];
    private string? _directory;
    private StructStore? _store;
    private CanonicalArena? _arena;
    private long _held;
    private bool _disposed;

    // The bytes of the runs the sort holds of its session's scratch budget, when it has one.
    private long _scratch;

    /// <param name="session">The session whose pool, scratch directory and writer the sort uses.</param>
    /// <param name="schema">The rows' columns.</param>
    /// <param name="keys">The chain of columns the rows sort by, the first first.</param>
    /// <param name="delivered">The rows' columns the sorted batches hold, in order: the others serve the sort alone.</param>
    /// <param name="memory">What the sort reserves its rows under; null for none.</param>
    /// <param name="batchRows">The rows of a batch the sort delivers and writes its runs in.</param>
    internal ExternalSort(VortexSession session, VortexSchema schema, SortKey[] keys, int[] delivered, QueryMemory? memory, int batchRows)
    {
        _session = session;
        _schema = schema;
        _dtype = VortexTypes.ToDType(schema, new DTypeArena());
        _keys = keys;
        _delivered = delivered;
        VortexField[] fields = new VortexField[delivered.Length];
        for (int c = 0; c < fields.Length; c++)
        {
            fields[c] = schema[delivered[c]];
        }

        _deliveredType = VortexTypes.ToDType(VortexSchema.Create(fields), new DTypeArena());
        _memory = memory;
        _runBytes = memory is null ? long.MaxValue : Math.Max(64 * 1024, memory.Ceiling / 4);
        _batchRows = batchRows;
    }

    /// <summary>The runs the sort wrote to its scratch.</summary>
    internal int Runs => _runs.Count;

    /// <summary>
    /// The rows of <paramref name="batch"/>, a struct of the sort's columns, gathered; the rows gathered
    /// before written as a run first when the memory holds them no more.
    /// </summary>
    internal async ValueTask AddAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        StructStore store = Store();
        StoreRows.Append(store, batch.Arena, batch.RootIndex);
        store.Commit();
        long reserved = Reserved(store.CommittedBytes, store.Rows);
        if (reserved <= _held)
        {
            return;
        }

        if (reserved <= _runBytes && (_memory is null || _memory.TryGrow(reserved - _held)))
        {
            _held = reserved;
            return;
        }

        await WriteRunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The rows in order, a batch of the delivered columns at a time: sorted in memory when no run was
    /// written, merged from the runs otherwise, the rows gathered last written as one more.
    /// </summary>
    internal async ValueTask<IAsyncEnumerator<RecordBatch>> SortedAsync(CancellationToken cancellationToken)
    {
        if (_runs.Count == 0)
        {
            return new InMemory(this, cancellationToken);
        }

        if (_store is { Rows: > 0 })
        {
            await WriteRunAsync(cancellationToken).ConfigureAwait(false);
        }

        Merge merge = new Merge(this, cancellationToken);
        try
        {
            await merge.OpenAsync().ConfigureAwait(false);
        }
        catch
        {
            await merge.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return merge;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        LetStoreGo();
        _session.Options.ScratchBudget?.Release(_scratch);
        _scratch = 0;
        foreach (string run in _runs)
        {
            try
            {
                System.IO.File.Delete(run);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _runs.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>What a store of rows of <paramref name="bytes"/> holds at most: its buffers grown by doubling, and a position and a bit a key column for each row.</summary>
    private long Reserved(long bytes, int rows) => (2 * bytes) + ((long)rows * sizeof(int)) + ((long)rows * _keys.Length / 8);

    private StructStore Store()
    {
        if (_store is not null)
        {
            return _store;
        }

        VortexSessionOptions options = _session.Options;
        _store = (StructStore)ColumnStores.Create(_dtype, options.EnginePool, options.Extensions);
        _arena = new CanonicalArena(64, options.EnginePool);
        return _store;
    }

    /// <summary>The positions of the store's rows in the chain's order, ties in the order they came, over a root built in its arena.</summary>
    private int[] Ranked(int root, int rows, CancellationToken cancellationToken)
    {
        CanonicalArena arena = _arena!;
        ColumnOrder[] chain = new ColumnOrder[_keys.Length + 1];
        for (int k = 0; k < _keys.Length; k++)
        {
            int node = FilterEvaluator.Resolve(arena, root, _keys[k].Field, rows);
            chain[k] = ColumnOrder.For(arena, node, _keys[k].Type, _keys[k].Descending);
        }

        chain[^1] = ColumnOrder.Positions;
        int[] positions = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            positions[i] = i;
        }

        GroupSort.Sort(positions, rows, new ChainOrder(chain), long.MaxValue, memory: null, cancellationToken);
        return positions;
    }

    /// <summary>The store's rows sorted and written to the scratch as a run, the store emptied and what it held given back.</summary>
    private async ValueTask WriteRunAsync(CancellationToken cancellationToken)
    {
        StructStore store = _store!;
        CanonicalArena arena = _arena!;
        int rows = store.Rows;
        if (rows == 0)
        {
            return;
        }

        int[] positions = Ranked(store.Build(arena, rows), rows, cancellationToken);

        // Under the host's scratch budget, the run reserved at what its rows hold before it is written,
        // then at the bytes it took once it is; all of it given back when the sort is disposed.
        ScratchBudget? budget = _session.Options.ScratchBudget;
        long reserved = _held;
        if (budget is not null && !budget.TryReserve(reserved))
        {
            throw GroupCore.ScratchExceeded(budget, "sort of a result", reserved);
        }

        _scratch += budget is null ? 0 : reserved;
        string path = RunPath();
        _runs.Add(path);
        VortexFileWriter writer = _session.CreateWriter(path, _schema, RunOptions);
        await using (writer.ConfigureAwait(false))
        {
            RecordBatch? batch = null;
            try
            {
                for (int start = 0; start < rows; start += _batchRows)
                {
                    // The store's rows over an arena emptied for each batch: the run's rows are copied a
                    // batch at a time, not twice whole.
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(_batchRows, rows - start);
                    batch?.Dispose();
                    arena.ResetKeepingBlocks();
                    int gathered = CanonicalFilter.Apply(arena, store.Build(arena, rows), positions.AsSpan(start, count));
                    batch = RecordBatch.Over(arena, gathered, 0, batch);
                    await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                batch?.Dispose();
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (budget is not null)
        {
            long written = new System.IO.FileInfo(path).Length;
            if (written < reserved)
            {
                budget.Release(reserved - written);
            }
            else if (written > reserved && !budget.TryReserve(written - reserved))
            {
                throw GroupCore.ScratchExceeded(budget, "sort of a result", written - reserved);
            }

            _scratch += written - reserved;
        }

        store.Truncate(0);
        store.Commit();
        arena.ResetKeepingBlocks();
        _memory?.LetGo(_held);
        _held = 0;
    }

    /// <summary>A run's file in the session's scratch directory, never one in memory, keeping a tenth of its free space.</summary>
    private string RunPath()
    {
        if (_directory is null)
        {
            string directory = _session.Options.ScratchDirectory ?? Path.GetTempPath();
            if (GroupCore.InMemory(directory))
            {
                throw new VortexMemoryException(
                    $"The sort needs to write runs, and its scratch directory {directory} lies on a file system in memory, which would take the memory the runs give back. Give its session a ScratchDirectory on a disk.");
            }

            _directory = directory;
        }

        if (GroupCore.Free(_directory) is long free && free - _held < free / 10)
        {
            throw new VortexMemoryException($"The sort needs to write a run of {_held:N0} bytes, and its scratch directory {_directory} keeps too little free space.");
        }

        return Path.Combine(_directory, $"vorticity-sort-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
    }

    private void LetStoreGo()
    {
        _store?.Release();
        _arena?.Reset();
        _store = null;
        _arena = null;
        _memory?.LetGo(_held);
        _held = 0;
    }

    /// <summary>The delivered columns of <paramref name="root"/>'s rows [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>) appended to <paramref name="into"/>, cut through <paramref name="cut"/>.</summary>
    private void Append(StructStore into, CanonicalArena arena, int root, int start, int count, CanonicalArena cut)
    {
        cut.ResetKeepingBlocks();
        int window = CanonicalSlice.SliceAcross(arena, cut, root, start, count);
        CanonicalNode node = cut.GetNode(window);
        for (int c = 0; c < _delivered.Length; c++)
        {
            StoreRows.Append(into.Children[c], cut, node.GetFieldIndex(_delivered[c]));
        }
    }

    /// <summary>The rows of a sort that wrote no run: sorted in its store, delivered a batch at a time.</summary>
    private sealed class InMemory(ExternalSort sort, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
    {
        private StructStore? _out;
        private CanonicalArena? _outArena;
        private CanonicalArena? _cut;
        private RecordBatch? _current;
        private int[] _positions = [];
        private int _root;
        private int _rows;
        private int _next = -1;

        public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

        public ValueTask<bool> MoveNextAsync()
        {
            if (_next < 0)
            {
                StructStore store = sort.Store();
                _rows = store.Rows;
                _root = store.Build(sort._arena!, _rows);
                _positions = sort.Ranked(_root, _rows, cancellationToken);
                _next = 0;
                VortexSessionOptions options = sort._session.Options;
                _out = (StructStore)ColumnStores.Create(sort._deliveredType, options.EnginePool, options.Extensions);
                _outArena = new CanonicalArena(64, options.EnginePool);
                _cut = new CanonicalArena(64, options.EnginePool);
            }

            if (_next >= _rows)
            {
                _current?.Dispose();
                return new ValueTask<bool>(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(sort._batchRows, _rows - _next);
            CanonicalArena arena = sort._arena!;
            int gathered = CanonicalFilter.Apply(arena, _root, _positions.AsSpan(_next, count));
            _out!.Truncate(0);
            sort.Append(_out, arena, gathered, 0, count, _cut!);
            _current?.Dispose();
            _outArena!.ResetKeepingBlocks();
            _current = RecordBatch.Over(_outArena, _out.Build(_outArena, count), _next, _current);
            _next += count;

            // The gathered rows were copied into the batch's store: the sort's arena is built again.
            arena.ResetKeepingBlocks();
            _root = sort._store!.Build(arena, _rows);
            return new ValueTask<bool>(true);
        }

        public ValueTask DisposeAsync()
        {
            _current?.Dispose();
            _out?.Release();
            _outArena?.Reset();
            _cut?.Reset();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The runs merged: a cursor a run, a batch of it at a time, in a heap by the row each stands on,
    /// ties to the earlier run; the top's rows that come before the next cursor's appended at once.
    /// </summary>
    private sealed class Merge(ExternalSort sort, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
    {
        private readonly List<Cursor> _heap = [];
        private readonly List<Cursor> _cursors = [];
        private StructStore? _out;
        private CanonicalArena? _outArena;
        private CanonicalArena? _cut;
        private RecordBatch? _current;
        private long _delivered;

        public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

        /// <summary>Each run opened and on its first row.</summary>
        internal async ValueTask OpenAsync()
        {
            VortexSessionOptions options = sort._session.Options;
            _out = (StructStore)ColumnStores.Create(sort._deliveredType, options.EnginePool, options.Extensions);
            _outArena = new CanonicalArena(64, options.EnginePool);
            _cut = new CanonicalArena(64, options.EnginePool);
            for (int r = 0; r < sort._runs.Count; r++)
            {
                VortexFile file = await sort._session.OpenAsync(sort._runs[r], cancellationToken: cancellationToken).ConfigureAwait(false);
                Cursor cursor = new Cursor(sort, file, r, cancellationToken);
                _cursors.Add(cursor);
                if (await cursor.NextBatchAsync().ConfigureAwait(false))
                {
                    _heap.Add(cursor);
                    Up(_heap.Count - 1);
                }
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            StructStore output = _out!;
            output.Truncate(0);
            int rows = 0;
            while (rows < sort._batchRows && _heap.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cursor top = _heap[0];

                // The top's rows up to the next cursor's row, within the batch built and the top's own.
                int end = Math.Min(top.Rows, top.Row + (sort._batchRows - rows));
                if (_heap.Count > 1)
                {
                    Cursor next = Second();
                    int run = top.Row + 1;
                    while (run < end && Compare(top, run, next, next.Row) < 0)
                    {
                        run++;
                    }

                    end = run;
                }

                sort.Append(output, top.Batch!.Arena, top.Batch.RootIndex, top.Row, end - top.Row, _cut!);
                rows += end - top.Row;
                top.Row = end;
                if (top.Row == top.Rows && !await top.NextBatchAsync().ConfigureAwait(false))
                {
                    _heap[0] = _heap[^1];
                    _heap.RemoveAt(_heap.Count - 1);
                }

                if (_heap.Count > 0)
                {
                    Down(0);
                }
            }

            if (rows == 0)
            {
                _current?.Dispose();
                return false;
            }

            _current?.Dispose();
            _outArena!.ResetKeepingBlocks();
            _current = RecordBatch.Over(_outArena, output.Build(_outArena, rows), _delivered, _current);
            _delivered += rows;
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _current?.Dispose();
            foreach (Cursor cursor in _cursors)
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
            }

            _out?.Release();
            _outArena?.Reset();
            _cut?.Reset();
        }

        /// <summary>The cursor second in the heap: the smaller of the top's two children.</summary>
        private Cursor Second() =>
            _heap.Count > 2 && Compare(_heap[2], _heap[2].Row, _heap[1], _heap[1].Row) < 0 ? _heap[2] : _heap[1];

        /// <summary>Row <paramref name="a"/> of <paramref name="x"/>'s batch against row <paramref name="b"/> of <paramref name="y"/>'s, by the chain, then the earlier run.</summary>
        private static int Compare(Cursor x, int a, Cursor y, int b)
        {
            for (int k = 0; k < x.Orders.Length; k++)
            {
                int order = x.Orders[k].CompareAcross(a, y.Orders[k], b);
                if (order != 0)
                {
                    return order;
                }
            }

            return x.Ordinal.CompareTo(y.Ordinal);
        }

        private void Up(int at)
        {
            while (at > 0)
            {
                int parent = (at - 1) >> 1;
                if (Compare(_heap[at], _heap[at].Row, _heap[parent], _heap[parent].Row) >= 0)
                {
                    return;
                }

                (_heap[at], _heap[parent]) = (_heap[parent], _heap[at]);
                at = parent;
            }
        }

        private void Down(int at)
        {
            while (true)
            {
                int left = (2 * at) + 1;
                if (left >= _heap.Count)
                {
                    return;
                }

                int smallest = left + 1 < _heap.Count && Compare(_heap[left + 1], _heap[left + 1].Row, _heap[left], _heap[left].Row) < 0 ? left + 1 : left;
                if (Compare(_heap[smallest], _heap[smallest].Row, _heap[at], _heap[at].Row) >= 0)
                {
                    return;
                }

                (_heap[at], _heap[smallest]) = (_heap[smallest], _heap[at]);
                at = smallest;
            }
        }
    }

    /// <summary>A run read a batch at a time: the batch it stands in, its row there, and the orders of the chain's columns over it.</summary>
    private sealed class Cursor(ExternalSort sort, VortexFile file, int ordinal, CancellationToken cancellationToken) : IAsyncDisposable
    {
        private IAsyncEnumerator<RecordBatch>? _batches;

        internal int Ordinal { get; } = ordinal;

        internal RecordBatch? Batch { get; private set; }

        internal int Row { get; set; }

        internal int Rows { get; private set; }

        internal ColumnOrder[] Orders { get; } = new ColumnOrder[sort._keys.Length];

        /// <summary>The run's next batch, its orders made over it; false past its last.</summary>
        internal async ValueTask<bool> NextBatchAsync()
        {
            _batches ??= file.ScanSource
                .BatchesAsync(new ScanSpec { Options = ScanOptions.Default with { BatchRows = sort._batchRows } }, new ScanCounters())
                .GetAsyncEnumerator(cancellationToken);
            while (await _batches.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch batch = _batches.Current;
                if (batch.RowCount == 0)
                {
                    continue;
                }

                Batch = batch;
                Row = 0;
                Rows = batch.RowCount;
                for (int k = 0; k < Orders.Length; k++)
                {
                    int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, sort._keys[k].Field, Rows);
                    Orders[k] = ColumnOrder.For(batch.Arena, node, sort._keys[k].Type, sort._keys[k].Descending);
                }

                return true;
            }

            Batch = null;
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_batches is not null)
            {
                await _batches.DisposeAsync().ConfigureAwait(false);
            }

            await file.DisposeAsync().ConfigureAwait(false);
        }
    }
}
