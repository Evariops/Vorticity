using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
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
/// the rest left to what feeds it, or once the budget refuses it. The merge holds a block a run and
/// the batch it builds, under the query's account: each run is written in blocks of a
/// <see cref="RunBlocks"/>th of its rows, a chunk a block, and read back a block at a time, so that
/// the runs' cursors hold a <see cref="RunBlocks"/>th of a run each, and the batch it builds a quarter
/// of the ceiling at most. Read whole, as they were written, forty runs of a sort ten times its budget
/// held ten times its budget. The runs' files are written as the writer writes them fastest, without
/// statistics, and deleted once the sort is.
/// </remarks>
internal sealed class ExternalSort : IAsyncDisposable
{
    private static readonly VortexWriteOptions RunOptions = new VortexWriteOptions { Compression = CompressionProfile.Fastest, WriteStatistics = false };

    /// <summary>The blocks a run is written in, each read back alone by the merge.</summary>
    private const int RunBlocks = 32;

    /// <summary>The fewest rows of a run's block.</summary>
    private const int LeastRunBlockRows = 256;

    // The rows of each run's blocks, and the most bytes a row of the store held: what the merge reads and builds by. Each
    // run's rows and its file's bytes, which a level of the merge sums for the run it writes of several.
    private readonly List<int> _runBlockRows = [];
    private readonly List<long> _runRows = [];
    private readonly List<long> _runFileBytes = [];
    private long _rowBytes;

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

    /// <summary>Whom the sort's rows and the batches it delivers are counted for: its query, until the query's memory is given back.</summary>
    private MemoryAccount Account => QueryMemory.AccountOf(_memory);

    /// <summary>The rows of a batch the merge builds: what a quarter of the ceiling holds at the store's widest rows, a thousand at the fewest.</summary>
    private int MergeRows => _memory is null || _rowBytes == 0
        ? _batchRows
        : (int)Math.Clamp(_memory.Ceiling / 4 / _rowBytes, 1_024, _batchRows);

    /// <summary>
    /// The rows of <paramref name="batch"/>, a struct of the sort's columns, gathered; the rows gathered
    /// before written as a run first when the memory holds them no more.
    /// </summary>
    internal async ValueTask AddAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        StructStore store = Store();
        StoreRows.Append(store, batch.Arena, batch.RootIndex, validated: true);
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

        await CascadeAsync(cancellationToken).ConfigureAwait(false);
        Merge merge = new Merge(this, 0, _runs.Count, MergeRows, whole: false, cancellationToken);
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
        _runBlockRows.Clear();
        _runRows.Clear();
        _runFileBytes.Clear();
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
        _store = (StructStore)ColumnStores.Create(_dtype, Account, options.Extensions);
        _arena = new CanonicalArena(64, Account);
        return _store;
    }

    /// <summary>The positions of the store's rows in the chain's order, ties in the order they came, over a root built in its arena.</summary>
    private Block<int> Ranked(int root, int rows, CancellationToken cancellationToken)
    {
        CanonicalArena arena = _arena!;
        ColumnOrder[] chain = new ColumnOrder[_keys.Length + 1];
        for (int k = 0; k < _keys.Length; k++)
        {
            int node = FilterEvaluator.Resolve(arena, root, _keys[k].Field, rows);
            chain[k] = ColumnOrder.For(arena, node, _keys[k].Type, _keys[k].Descending);
        }

        chain[^1] = ColumnOrder.Positions;
        Block<int> positions = Blocks.Rent<int>(rows);
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

        // The positions in a block of the pool, given back once the run is written.
        Block<int> positions = Ranked(store.Build(arena, rows), rows, cancellationToken);

        // Under the host's scratch budget, the run reserved at what its rows hold before it is written,
        // then at the bytes it took once it is; all of it given back when the sort is disposed.
        ScratchBudget? budget = _session.Options.ScratchBudget;
        long reserved = _held;
        if (budget is not null && !budget.TryReserve(reserved))
        {
            Blocks.Return(positions);
            throw GroupCore.ScratchExceeded(budget, "sort of a result", reserved);
        }

        _scratch += budget is null ? 0 : reserved;
        string path = RunPath(_held);
        _runs.Add(path);

        // Blocks of a RunBlocks-th of the run, a chunk each, which the merge reads back one at a time.
        int blockRows = Math.Clamp(rows / RunBlocks, LeastRunBlockRows, RunOptions.BlockRows);
        _runBlockRows.Add(blockRows);
        _runRows.Add(rows);
        _runFileBytes.Add(0);
        _rowBytes = Math.Max(_rowBytes, (store.CommittedBytes / rows) + 1);
        VortexFileWriter writer = _session.CreateWriter(path, _schema, RunOptions with { BlockRows = blockRows, ChunkTargetBytes = 1 });
        await using (writer.ConfigureAwait(false))
        {
            RecordBatch? batch = null;
            try
            {
                for (int start = 0; start < rows; start += blockRows)
                {
                    // The store's rows over an arena emptied for each batch: the run's rows are copied a
                    // block at a time, not twice whole, each block a chunk of its own, which a batch of
                    // many blocks written where it lies would not be.
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(blockRows, rows - start);
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
                Blocks.Return(positions);
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        _runFileBytes[^1] = new System.IO.FileInfo(path).Length;
        if (budget is not null)
        {
            long written = _runFileBytes[^1];
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

    /// <summary>
    /// The runs merged by levels until one merge reads them all at once (<see cref="FanIn"/>): each level merges consecutive
    /// groups of runs into one each, in order, so that rows of one key keep the order they came in, and writes the rows
    /// once; the runs it read are deleted as it ends, a run left alone at a level's end carried to the next. One level held
    /// a cursor, its block and its open file, for every run: past the hundreds, more than the budget, and more files than
    /// the process may open.
    /// </summary>
    private async ValueTask CascadeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int fanIn = FanIn();
            int count = _runs.Count;
            if (count <= fanIn)
            {
                return;
            }

            bool[] carried = new bool[count];
            for (int first = 0; first < count; first += fanIn)
            {
                int group = Math.Min(fanIn, count - first);
                if (group == 1)
                {
                    carried[first] = true;
                    _runs.Add(_runs[first]);
                    _runBlockRows.Add(_runBlockRows[first]);
                    _runRows.Add(_runRows[first]);
                    _runFileBytes.Add(_runFileBytes[first]);
                    continue;
                }

                await MergeRunsAsync(first, group, cancellationToken).ConfigureAwait(false);
            }

            // The level's runs read go, the new ones, appended after them, take their places.
            ScratchBudget? budget = _session.Options.ScratchBudget;
            for (int r = 0; r < count; r++)
            {
                if (carried[r])
                {
                    continue;
                }

                Delete(_runs[r]);
                budget?.Release(_runFileBytes[r]);
                _scratch -= budget is null ? 0 : _runFileBytes[r];
            }

            _runs.RemoveRange(0, count);
            _runBlockRows.RemoveRange(0, count);
            _runRows.RemoveRange(0, count);
            _runFileBytes.RemoveRange(0, count);
        }
    }

    /// <summary>
    /// The runs a merge reads at once: as many cursors as half of the ceiling holds blocks of, the largest run's block each,
    /// beside the batch it builds, a quarter, and its reads; two at least. Unbounded with no memory, whose sort writes no run.
    /// </summary>
    private int FanIn()
    {
        if (_memory is null)
        {
            return int.MaxValue;
        }

        long block = 1;
        for (int r = 0; r < _runBlockRows.Count; r++)
        {
            block = Math.Max(block, _runBlockRows[r] * _rowBytes);
        }

        return (int)Math.Clamp(_memory.Ceiling / 2 / block, 2, int.MaxValue);
    }

    /// <summary>
    /// Runs [<paramref name="first"/>, <paramref name="first"/> + <paramref name="count"/>) merged into one run appended to
    /// the runs, in blocks of the largest of theirs, so that a cursor over it holds no more than over them.
    /// </summary>
    private async ValueTask MergeRunsAsync(int first, int count, CancellationToken cancellationToken)
    {
        long rows = 0;
        long bytes = 0;
        int blockRows = 0;
        for (int r = first; r < first + count; r++)
        {
            rows += _runRows[r];
            bytes += _runFileBytes[r];
            blockRows = Math.Max(blockRows, _runBlockRows[r]);
        }

        // Under the host's scratch budget, the run reserved at what the runs it merges took, then at what it took.
        ScratchBudget? budget = _session.Options.ScratchBudget;
        if (budget is not null && !budget.TryReserve(bytes))
        {
            throw GroupCore.ScratchExceeded(budget, "sort of a result", bytes);
        }

        _scratch += budget is null ? 0 : bytes;
        string path = RunPath(bytes);
        _runs.Add(path);
        _runBlockRows.Add(blockRows);
        _runRows.Add(rows);
        _runFileBytes.Add(bytes);
        Merge merge = new Merge(this, first, count, blockRows, whole: true, cancellationToken);
        try
        {
            await merge.OpenAsync().ConfigureAwait(false);
            VortexFileWriter writer = _session.CreateWriter(path, _schema, RunOptions with { BlockRows = blockRows, ChunkTargetBytes = 1 });
            await using (writer.ConfigureAwait(false))
            {
                // A block a batch, a chunk each, as the runs it reads were written.
                while (await merge.MoveNextAsync().ConfigureAwait(false))
                {
                    await writer.WriteAsync(merge.Current, cancellationToken).ConfigureAwait(false);
                }

                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await merge.DisposeAsync().ConfigureAwait(false);
        }

        long written = new System.IO.FileInfo(path).Length;
        if (budget is not null)
        {
            if (written < bytes)
            {
                budget.Release(bytes - written);
            }
            else if (written > bytes && !budget.TryReserve(written - bytes))
            {
                throw GroupCore.ScratchExceeded(budget, "sort of a result", written - bytes);
            }

            _scratch += written - bytes;
        }

        _runFileBytes[^1] = written;
    }

    /// <summary>Deletes a run's file, whatever is left of it; one already gone, or held by another, is left.</summary>
    private static void Delete(string run)
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

    /// <summary>A run's file of about <paramref name="bytes"/> in the session's scratch directory, never one in memory, keeping a tenth of its free space.</summary>
    private string RunPath(long bytes)
    {
        if (_directory is null)
        {
            string directory = ScratchDirectories.Of(_session.Options.ScratchDirectory);
            if (GroupCore.InMemory(directory))
            {
                throw new VortexMemoryException(
                    $"The sort needs to write runs, and its scratch directory {directory} lies on a file system in memory, which would take the memory the runs give back. Give its session a ScratchDirectory on a disk.");
            }

            _directory = directory;
        }

        if (GroupCore.Free(_directory) is long free && free - bytes < free / 10)
        {
            throw new VortexMemoryException($"The sort needs to write a run of {bytes:N0} bytes, and its scratch directory {_directory} keeps too little free space.");
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

    /// <summary>
    /// The delivered columns of <paramref name="root"/>'s rows [<paramref name="start"/>, <paramref name="start"/> +
    /// <paramref name="count"/>) appended to <paramref name="into"/>, cut through <paramref name="cut"/>; every column with
    /// <paramref name="whole"/>, into a store of the sort's own.
    /// </summary>
    private void Append(StructStore into, CanonicalArena arena, int root, int start, int count, CanonicalArena cut, bool whole = false)
    {
        cut.ResetKeepingBlocks();
        int window = CanonicalSlice.SliceAcross(arena, cut, root, start, count);
        if (whole)
        {
            StoreRows.Append(into, cut, window, validated: true);
            return;
        }

        CanonicalNode node = cut.GetNode(window);
        for (int c = 0; c < _delivered.Length; c++)
        {
            StoreRows.Append(into.Children[c], cut, node.GetFieldIndex(_delivered[c]), validated: true);
        }
    }

    /// <summary>The rows of a sort that wrote no run: sorted in its store, delivered a batch at a time.</summary>
    private sealed class InMemory(ExternalSort sort, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
    {
        private StructStore? _out;
        private CanonicalArena? _outArena;
        private CanonicalArena? _cut;
        private RecordBatch? _current;
        private Block<int> _positions;
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
                _out = (StructStore)ColumnStores.Create(sort._deliveredType, sort.Account, options.Extensions);
                _outArena = new CanonicalArena(64, sort.Account);
                _cut = new CanonicalArena(64, sort.Account);
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
            Blocks.Return(_positions);
            _positions = default;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The runs [<paramref name="first"/>, <paramref name="first"/> + <paramref name="count"/>) merged: a cursor a run, a
    /// batch of it at a time, in a heap by the row each stands on, ties to the earlier run; the top's rows that come before
    /// the next cursor's appended at once, <paramref name="most"/> rows a batch. The delivered columns, or with
    /// <paramref name="whole"/> every column, for a run of a level of the merge.
    /// </summary>
    private sealed class Merge(ExternalSort sort, int first, int count, int most, bool whole, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
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
            _out = (StructStore)ColumnStores.Create(whole ? sort._dtype : sort._deliveredType, sort.Account, options.Extensions);
            _outArena = new CanonicalArena(64, sort.Account);
            _cut = new CanonicalArena(64, sort.Account);
            for (int r = first; r < first + count; r++)
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
            while (rows < most && _heap.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cursor top = _heap[0];

                // The top's rows up to the next cursor's row, within the batch built and the top's own.
                int end = Math.Min(top.Rows, top.Row + (most - rows));
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

                sort.Append(output, top.Batch!.Arena, top.Batch.RootIndex, top.Row, end - top.Row, _cut!, whole);
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
            // A block of the run at a time, decoded on demand on one lane, under the sort's question: a cursor that decoded
            // ahead held several of the run's splits, and the runs together ten times the sort's budget.
            if (_batches is null)
            {
                ScanSpec spec = new ScanSpec
                {
                    Options = ScanOptions.Default with { BatchRows = sort._runBlockRows[Ordinal], DegreeOfParallelism = 1, Prefetch = 0 },
                };
                _batches = file.ScanSource.Rows.BatchesAsync(sort._memory is { } memory ? spec.Under(memory) : spec, new ScanCounters())
                    .GetAsyncEnumerator(cancellationToken);
            }

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
