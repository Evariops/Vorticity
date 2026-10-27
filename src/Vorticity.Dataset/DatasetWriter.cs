using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of a draft, taken through the calls a file's writer takes them by, and written as the dataset's objects: one a
/// partition, each sorted by the dataset's key, every time the rows held reach <see cref="DatasetOptions.AppendBufferBytes"/>
/// and once more for all of them when the draft is committed.
/// </summary>
/// <remarks>
/// <para>
/// A dataset with neither a partition nor a key writes the rows as they come into one object, as a file's writer would.
/// Otherwise each partition's rows wait in a store of their own, in memory reserved from the session's budget, and once
/// the stores pass the bytes an append holds, or the budget refuses them more, the largest partition's rows are written:
/// their keys row-encoded, their places sorted by them, rows of one key in the order they came, then the rows gathered in
/// that order a batch at a time. Each such object carries its table of contents, the sparse index of its key and the
/// geometry of its chunks, and an object dictionary for each column it costs less than a dictionary a chunk; the synopses
/// a job keeps of its columns wait for the first merge.
/// </para>
/// <para>
/// Not thread-safe. The objects it wrote before a draft is given up stay in the store, unreferenced, for vacuum.
/// </para>
/// </remarks>
public sealed class DatasetWriter
{
    // The rows of a batch the sorted rows are written in.
    private const int WrittenRows = ColumnBlocks.MostRows;

    private readonly VortexDataset _dataset;
    private readonly DatasetSchema _schema;
    private readonly IReadOnlyDictionary<string, VariantShredding>? _shredded;

    // The tree's key the rows are sorted by, the partition leading it, and the indexed prefix of the key past it, null to
    // keep their order; the partition's value alone, null for one partition; the column the key's first component reads as
    // it is, whose dictionary is priced as sorted.
    private readonly SortKey? _key;
    private readonly int _prefix;
    private readonly SortKey? _partition;
    private readonly int _sortedColumn = -1;
    private readonly bool _buffered;
    private readonly long _bufferBytes;
    private readonly int _mostPartitions;

    // The partitions the rows went to, the last one met first; the objects written; the one object of a dataset whose
    // rows keep their order, and what the sorted ones share.
    private readonly List<Part> _parts = [];
    private Part? _lastPart;
    private readonly List<WrittenObject> _written = [];
    private readonly List<byte[]> _writtenPartitions = [];
    private ObjectOutput? _direct;
    private SortedRows? _sorted;

    // The rows of the builders, and the arenas a batch is laid out and cut in.
    private StructStore? _intake;
    private ColumnsBuilder? _builder;
    private ColumnsBuilder? _typedBuilder;
    private CanonicalArena? _arena;
    private CanonicalArena? _scratch;
    private Type? _membersOf;
    private int[]? _members;

    // What the stores hold of the session's memory budget.
    private QueryMemory? _memory;
    private long _reserved;

    private long _rows;
    private bool _done;

    internal DatasetWriter(VortexDataset dataset, DatasetSchema schema, IReadOnlyDictionary<string, VariantShredding>? shredded)
    {
        _dataset = dataset;
        _schema = schema;
        _shredded = shredded;
        _bufferBytes = dataset.Options.AppendBufferBytes;
        _mostPartitions = dataset.Options.MaxPartitionsPerAppend;
        if (schema.Layout is { } plan)
        {
            if (plan.Components.Count > 0)
            {
                _key = schema.Tree;
                _prefix = Math.Min(plan.Layout.Prefix, plan.Components.Count);
                LayoutColumn first = plan.Components[0];
                _sortedColumn = first.Function is null && first.Read is null && !first.Value.SourcePath.Contains('.', StringComparison.Ordinal) ? first.Field : -1;
            }

            _partition = plan.Partition is null ? null : SortKey.Of(plan, partition: true, components: 0);
        }

        _buffered = _key is not null || _partition is not null;
    }

    /// <summary>The dataset's columns, which the rows are written in.</summary>
    public VortexSchema Schema => _schema.Columns;

    /// <summary>The rows taken so far.</summary>
    public long RowCount => _buffered ? _rows : _direct?.Writer.RowCount ?? 0;

    /// <summary>The builder over the writer's rows, reusable, and cleared by each write.</summary>
    /// <returns>The builder; every call returns the same one.</returns>
    /// <exception cref="ObjectDisposedException">The draft was committed or given up.</exception>
    public ColumnsBuilder Builder()
    {
        if (!_buffered)
        {
            return Direct().Writer.Builder();
        }

        ThrowIfDone();
        return _builder ??= new ColumnsBuilder(Intake(), Schema, null, this);
    }

    /// <summary>The builder over the writer's rows, seen as the columns of <typeparamref name="TRecord"/>.</summary>
    /// <typeparam name="TRecord">A record whose members name columns of the dataset.</typeparam>
    /// <returns>The builder, sharing its rows with <see cref="Builder()"/>.</returns>
    /// <exception cref="VortexSchemaException">A member has no column.</exception>
    public ColumnsBuilder<TRecord> Builder<TRecord>()
        where TRecord : IVortexRecord<TRecord>
    {
        if (!_buffered)
        {
            return Direct().Writer.Builder<TRecord>();
        }

        ThrowIfDone();
        if (_typedBuilder is ColumnsBuilder<TRecord> typed)
        {
            return typed;
        }

        StructStore intake = Intake();
        typed = new ColumnsBuilder<TRecord>(intake, WriteBinding.Map(typeof(TRecord), TRecord.Schema, intake.Type.FieldArray), this);
        _typedBuilder = typed;
        return typed;
    }

    /// <summary>Takes the rows of the writer's builder and clears it.</summary>
    /// <exception cref="ArgumentException">The builder belongs to another writer, or is a nested struct's.</exception>
    /// <exception cref="VortexSchemaException">The builder's columns hold different row counts, or a list is left open.</exception>
    public ValueTask WriteAsync(ColumnsBuilder builder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!_buffered)
        {
            return Direct().Writer.WriteAsync(builder, cancellationToken);
        }

        ThrowIfDone();
        if (!ReferenceEquals(builder.Owner, this))
        {
            throw new ArgumentException(
                "A builder is written by the writer that handed it out; this one is another writer's, or a nested struct's.", nameof(builder));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AcceptAsync(cancellationToken);
    }

    /// <summary>Takes <paramref name="rows"/>, read before this returns.</summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    public ValueTask WriteAsync<TRecord>(ReadOnlySpan<TRecord> rows, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        if (!_buffered)
        {
            return Direct().Writer.WriteAsync(rows, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ColumnsBuilder<TRecord> builder = Builder<TRecord>();
        int before = builder.Store.Rows;
        try
        {
            TRecord.WriteRows(builder, rows);
        }
        catch
        {
            builder.Store.Truncate(before);
            throw;
        }

        return AcceptAsync(cancellationToken);
    }

    /// <summary>Takes a stream of rows, a block of them at a time.</summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    public async ValueTask WriteAsync<TRecord>(IAsyncEnumerable<TRecord> rows, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (!_buffered)
        {
            await Direct().Writer.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
            return;
        }

        ThrowIfDone();
        int block = _dataset.Options.Write.BlockRows;
        TRecord[] buffer = ArrayPool<TRecord>.Shared.Rent(block);
        try
        {
            int count = 0;
            await foreach (TRecord row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                buffer[count++] = row;
                if (count == block)
                {
                    await WriteAsync<TRecord>(buffer.AsSpan(0, count), cancellationToken).ConfigureAwait(false);
                    count = 0;
                }
            }

            if (count > 0)
            {
                await WriteAsync<TRecord>(buffer.AsSpan(0, count), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<TRecord>.Shared.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<TRecord>());
        }
    }

    /// <summary>Takes a batch of a scan typed by <typeparamref name="TRecord"/>, whose members cover every column.</summary>
    /// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
    /// <exception cref="VortexSchemaException">A column has no member, or a member's column is not of the column's type.</exception>
    public ValueTask WriteAsync<TRecord>(Columns<TRecord> columns, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        if (!_buffered)
        {
            return Direct().Writer.WriteAsync(columns, cancellationToken);
        }

        ThrowIfDone();
        cancellationToken.ThrowIfCancellationRequested();
        CanonicalArena scratch = Scratch();
        try
        {
            Take(scratch, BatchIntake.Columns(scratch, columns, MembersOf<TRecord>(), _schema.DType));
        }
        finally
        {
            scratch.ResetKeepingBlocks();
        }

        return HeldAsync(cancellationToken);
    }

    /// <summary>Takes every batch of a scan as it comes.</summary>
    /// <typeparam name="TRecord">The record the scan is typed by; its members cover every column.</typeparam>
    public async ValueTask WriteAsync<TRecord>(Scan<TRecord> scan, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(scan);
        Scan<TRecord>.AsyncEnumerator batches = scan.GetAsyncEnumerator(cancellationToken);
        await using (batches.ConfigureAwait(false))
        {
            while (await batches.MoveNextAsync().ConfigureAwait(false))
            {
                await WriteAsync(batches.Current, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Takes every batch of a tool scan as it comes; its columns are the dataset's.</summary>
    public async ValueTask WriteAsync(Scan scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        Scan.AsyncEnumerator batches = scan.GetAsyncEnumerator(cancellationToken);
        await using (batches.ConfigureAwait(false))
        {
            while (await batches.MoveNextAsync().ConfigureAwait(false))
            {
                await WriteAsync(batches.Current, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Takes a batch of a tool scan, borrowed until the returned task completes; its columns are the dataset's.</summary>
    /// <exception cref="VortexSchemaException">The batch's columns are not of the dataset's types.</exception>
    public ValueTask WriteAsync(BatchView batch, CancellationToken cancellationToken = default)
    {
        if (!_buffered)
        {
            return Direct().Writer.WriteAsync(batch, cancellationToken);
        }

        ThrowIfDone();
        cancellationToken.ThrowIfCancellationRequested();
        BatchIntake.RequireFits(batch.Arena.GetNode(batch.Node).DType, _schema.DType, "The batch");
        TakeBorrowed(batch.Arena, batch.Node, batch.SelectionWords);
        return HeldAsync(cancellationToken);
    }

    /// <summary>Takes an owned batch, which the caller still disposes.</summary>
    /// <exception cref="VortexSchemaException">The batch's columns are not of the dataset's types.</exception>
    public ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        BatchIntake.RequireFits(batch.DType, _schema.DType, "The batch");
        return WriteBatchAsync(batch, cancellationToken);
    }

    /// <summary>Takes an engine batch whose schema the caller vouches for: the dataset's own appends and rewrites.</summary>
    internal ValueTask WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        if (!_buffered)
        {
            return Direct().Writer.WriteBatchAsync(batch, cancellationToken);
        }

        ThrowIfDone();
        cancellationToken.ThrowIfCancellationRequested();
        TakeBorrowed(batch.Arena, batch.RootIndex, batch.SelectionWords);
        return HeldAsync(cancellationToken);
    }

    /// <summary>
    /// Writes what the partitions hold and returns every object written, for the commit that takes them; the writer takes
    /// no row after. A dataset without a key places the objects' rows from <paramref name="firstRow"/> on, in the order
    /// they were written.
    /// </summary>
    internal async ValueTask<List<WrittenObject>> FinishAsync(long firstRow, CancellationToken cancellationToken)
    {
        ThrowIfDone();
        try
        {
            if (_direct is { } direct)
            {
                _direct = null;
                if (await _dataset.CompleteAsync(direct, firstRow, cancellationToken).ConfigureAwait(false) is { } written)
                {
                    _written.Add(written);
                }
            }

            if (_intake is { } intake && intake.Rows > intake.Committed)
            {
                await AcceptAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (Part part in _parts)
            {
                await WritePartAsync(part, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _done = true;
            Release();
        }

        // The objects of a dataset without a key, placed as they were written, which their keys in the tree say.
        if (_buffered && _key is null)
        {
            long at = firstRow;
            for (int i = 0; i < _written.Count; i++)
            {
                _written[i] = VortexDataset.Placed(_written[i], _writtenPartitions[i], at);
                at += _written[i].Entry.Rows;
            }
        }

        return _written;
    }

    /// <summary>Gives the rows up: what the partitions hold is dropped, and the objects written stay for vacuum.</summary>
    internal async ValueTask AbandonAsync()
    {
        _done = true;
        if (_direct is { } direct)
        {
            _direct = null;
            await direct.DisposeAsync().ConfigureAwait(false);
        }

        Release();
    }

    /// <summary>The one object of a dataset whose rows keep their order, started at the first row.</summary>
    private ObjectOutput Direct()
    {
        ThrowIfDone();
        return _direct ??= _dataset.StartObjectUnder(_schema, shredded: _shredded);
    }

    /// <summary>The store the builders fill: the one partition's own when the dataset has no partition.</summary>
    private StructStore Intake()
    {
        if (_intake is not null)
        {
            return _intake;
        }

        _intake = NewStore();
        if (_partition is null)
        {
            _parts.Add(_lastPart = new Part([], _intake));
        }

        return _intake;
    }

    /// <summary>Takes the rows the builders hold: each partition's copied to its store, unless the store is theirs.</summary>
    private ValueTask AcceptAsync(CancellationToken cancellationToken)
    {
        StructStore intake = Intake();
        if (intake.Inconsistency() is { } why)
        {
            throw new VortexSchemaException($"The builder cannot be written: {why}. Complete every row before writing it.");
        }

        int rows = intake.Rows - intake.Committed;
        if (rows == 0)
        {
            return ValueTask.CompletedTask;
        }

        intake.Commit();
        if (_partition is null)
        {
            _rows += rows;
        }
        else
        {
            CanonicalArena arena = Arena();
            try
            {
                Take(arena, intake.BuildRoot(arena, rows));
            }
            finally
            {
                arena.ResetKeepingBlocks();
                intake.Truncate(0);
                intake.Commit();
            }
        }

        return HeldAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the rows a borrowed batch selects, every row when it selects none: referenced from the scratch arena, so that
    /// what the partition's value adds lands there and not in the caller's.
    /// </summary>
    private void TakeBorrowed(CanonicalArena arena, int root, ReadOnlySpan<ulong> selection)
    {
        CanonicalArena scratch = Scratch();
        try
        {
            int node = scratch.ReferenceFrom(arena, root);
            Take(scratch, selection.IsEmpty ? node : BatchIntake.Selected(scratch, node, selection));
        }
        finally
        {
            scratch.ResetKeepingBlocks();
        }
    }

    /// <summary>
    /// Copies the rows of <paramref name="root"/>, a struct of the dataset's columns in <paramref name="arena"/>, to their
    /// partitions' stores: whole when they share one partition, as a batch's rows mostly do, gathered by partition otherwise.
    /// </summary>
    private void Take(CanonicalArena arena, int root)
    {
        int rows = arena.GetNode(root).Length;
        if (rows == 0)
        {
            return;
        }

        if (_partition is null)
        {
            // The builders fill the one partition's store: rows they hold and were not written yet would come apart.
            StructStore only = Intake();
            if (only.Rows > only.Committed)
            {
                throw new InvalidOperationException(
                    "The writer's builder holds rows not written yet; write them before rows that come another way.");
            }

            StoreRows.Append(only, arena, root);
            only.Commit();
        }
        else
        {
            RecordBatch view = RecordBatch.Over(arena, root, 0, null);
            RowKeys partitions;
            try
            {
                partitions = _partition.Encode(view);
            }
            finally
            {
                view.Dispose();
            }

            using (partitions)
            {
                int other = 1;
                while (other < rows && partitions.Same(0, other))
                {
                    other++;
                }

                if (other == rows)
                {
                    Part part = PartOf(partitions.Row(0));
                    StoreRows.Append(part.Store, arena, root);
                    part.Store.Commit();
                }
                else
                {
                    Split(arena, root, rows, partitions);
                }
            }
        }

        _rows += rows;
    }

    /// <summary>The rows of a batch of several partitions, each partition's gathered in their order and copied to its store.</summary>
    private void Split(CanonicalArena arena, int root, int rows, RowKeys partitions)
    {
        int[] order = ArrayPool<int>.Shared.Rent(rows);
        try
        {
            for (int i = 0; i < rows; i++)
            {
                order[i] = i;
            }

            partitions.SortStable(order.AsSpan(0, rows));
            for (int at = 0, end; at < rows; at = end)
            {
                end = at + 1;
                while (end < rows && partitions.Same(order[at], order[end]))
                {
                    end++;
                }

                Part part = PartOf(partitions.Row(order[at]));
                StoreRows.Append(part.Store, arena, CanonicalFilter.Apply(arena, root, order.AsSpan(at, end - at)));
                part.Store.Commit();
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>The partition of value <paramref name="partition"/>, row-encoded, met before or new.</summary>
    /// <exception cref="InvalidOperationException">The append would touch more partitions than the dataset allows one.</exception>
    private Part PartOf(ReadOnlySpan<byte> partition)
    {
        if (_lastPart is { } last && partition.SequenceEqual(last.Partition))
        {
            return last;
        }

        foreach (Part part in _parts)
        {
            if (partition.SequenceEqual(part.Partition))
            {
                return _lastPart = part;
            }
        }

        if (_parts.Count >= _mostPartitions)
        {
            throw new InvalidOperationException(
                $"The append touches more than {_mostPartitions} partitions (DatasetOptions.MaxPartitionsPerAppend), each an object at " +
                "least and merges after; nothing it wrote is committed. Split it by partition, or raise the bound for a load of history.");
        }

        Part made = new Part(partition.ToArray(), NewStore());
        _parts.Add(made);
        return _lastPart = made;
    }

    /// <summary>
    /// Reserves what the stores hold, twice their rows' bytes as stores grow by doubling, a megabyte ahead; past the bytes
    /// an append holds, or refused by the budget, the largest partition's rows are written, then the next, until they fit.
    /// </summary>
    private async ValueTask HeldAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            long held = 0;
            Part? largest = null;
            foreach (Part part in _parts)
            {
                long bytes = part.Store.CommittedBytes;
                held += bytes;
                if (bytes > 0 && (largest is null || bytes > largest.Store.CommittedBytes))
                {
                    largest = part;
                }
            }

            if (largest is null || (held < _bufferBytes && Reserve(2 * held)))
            {
                return;
            }

            await WritePartAsync(largest, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the session's budget holds <paramref name="bytes"/> for the stores, reserved a megabyte ahead.</summary>
    private bool Reserve(long bytes)
    {
        if (bytes <= _reserved)
        {
            return true;
        }

        _memory ??= new QueryMemory(_dataset.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        long more = (bytes - _reserved + QueryMemory.Chunk - 1) / QueryMemory.Chunk * QueryMemory.Chunk;
        if (!_memory.TryGrow(more))
        {
            return false;
        }

        _reserved += more;
        return true;
    }

    /// <summary>
    /// Writes the rows a partition holds as one object, sorted by the key when the dataset has one, and empties its store:
    /// the keys encoded over every row, their places sorted, then the rows gathered in that order a batch at a time.
    /// </summary>
    private async ValueTask WritePartAsync(Part part, CancellationToken cancellationToken)
    {
        StructStore store = part.Store;
        int rows = store.Committed;
        if (rows == 0)
        {
            return;
        }

        CanonicalArena arena = Arena();
        RowKeys? keys = null;
        int[]? order = null;
        ObjectOutput? output = null;
        try
        {
            int root = store.Build(arena, rows);
            if (_key is { } key)
            {
                RecordBatch all = RecordBatch.Over(arena, root, 0, null);
                try
                {
                    keys = key.Encode(all);
                }
                finally
                {
                    all.Dispose();
                }

                order = ArrayPool<int>.Shared.Rent(rows);
                for (int i = 0; i < rows; i++)
                {
                    order[i] = i;
                }

                keys.SortStable(order.AsSpan(0, rows));
            }

            output = _dataset.StartObjectUnder(_schema, inKeyOrder: keys is not null, rowBound: rows, contents: true, shredded: _shredded);
            VortexFileWriter writer = output.Writer;
            DeclareDictionaries(writer, arena, root, rows);
            SortedRows? sorted = keys is null ? null : _sorted ??= new SortedRows(_key!, _prefix, blocks: false, skip: _partition is null ? 0 : 1);
            RecordBatch? batch = null;
            try
            {
                for (int start = 0; start < rows; start += WrittenRows)
                {
                    // The store's rows laid out again over an arena emptied for each batch, so that the gathered rows
                    // are copied a batch at a time and not twice whole.
                    int count = Math.Min(WrittenRows, rows - start);
                    arena.ResetKeepingBlocks();
                    root = store.Build(arena, rows);
                    int node = order is null
                        ? (count == rows ? root : CanonicalSlice.SliceAcross(arena, arena, root, start, count))
                        : CanonicalFilter.Apply(arena, root, order.AsSpan(start, count));
                    batch = RecordBatch.Over(arena, node, 0, batch);
                    if (sorted is not null)
                    {
                        sorted.Take(new OrderedRows(keys!, order!, start), 0, count, writer.BlockRows);
                        sorted.DeclareEnds(writer);
                    }

                    await writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Dispose();
                }

                sorted?.Finish(writer);
            }
            catch
            {
                sorted?.Clear();
                batch?.Dispose();
                throw;
            }

            // Keyed by its first row's key and bounded by its last's; placed in its partition once the draft is committed,
            // when the dataset has no key (FinishAsync).
            ObjectOutput completed = output;
            output = null;
            completed.Keys = keys is null ? null : (keys.Row(order![0]).ToArray(), keys.Row(order[rows - 1]).ToArray());
            completed.Partition = keys is null && _partition is not null ? part.Partition : null;
            if (await _dataset.CompleteAsync(completed, 0, cancellationToken).ConfigureAwait(false) is { } written)
            {
                _written.Add(written);
                _writtenPartitions.Add(part.Partition);
            }
        }
        finally
        {
            if (output is not null)
            {
                await output.DisposeAsync().ConfigureAwait(false);
            }

            keys?.Dispose();
            if (order is not null)
            {
                ArrayPool<int>.Shared.Return(order);
            }

            arena.ResetKeepingBlocks();
        }

        store.Truncate(0);
        store.Commit();
    }

    /// <summary>
    /// Declares to <paramref name="writer"/> each column's object dictionary, of the values of the store's rows, where it
    /// costs less than a dictionary a chunk: the writer prices it; the key's first column, sorted, is priced as such.
    /// </summary>
    private void DeclareDictionaries(VortexFileWriter writer, CanonicalArena arena, int root, int rows)
    {
        CanonicalNode node = arena.GetNode(root);
        for (int field = 0; field < _schema.DType.FieldCount; field++)
        {
            DType type = _schema.DType.GetField(field);
            if (!ObjectDictionary.Holds(type))
            {
                continue;
            }

            int column = node.GetFieldIndex(field);
            if (ObjectDictionary.Of(arena, column) is { } dictionary)
            {
                writer.TryDeclareDictionary(field, dictionary, rows, RowBytes(arena, column, type, rows), sorted: field == _sortedColumn);
            }
        }
    }

    /// <summary>The bytes a column's values take over its rows, uncompressed: text's bytes, a number's width a row.</summary>
    private static long RowBytes(CanonicalArena arena, int column, DType type, int rows)
    {
        if (type.Kind == DTypeKind.Primitive)
        {
            return (long)rows * type.PType.ByteWidth();
        }

        CanonicalNode node = arena.GetNode(column);
        long bytes = 0;
        for (int row = 0; row < rows; row++)
        {
            bytes += LiteralReader.ViewAt(node, row).Length;
        }

        return bytes;
    }

    /// <summary>Per column of the dataset, the member of <typeparamref name="TRecord"/> that holds it.</summary>
    private int[] MembersOf<TRecord>()
        where TRecord : IVortexRecord<TRecord>
    {
        if (_membersOf != typeof(TRecord))
        {
            _members = BatchIntake.Members<TRecord>(Schema);
            _membersOf = typeof(TRecord);
        }

        return _members!;
    }

    private StructStore NewStore()
    {
        VortexSessionOptions options = _dataset.Session.Options;
        return ColumnStores.Root(_schema.DType, options.EnginePool, options.Extensions);
    }

    private CanonicalArena Arena() => _arena ??= new CanonicalArena(64, _dataset.Session.Options.EnginePool);

    private CanonicalArena Scratch() => _scratch ??= new CanonicalArena(64, _dataset.Session.Options.EnginePool);

    private void ThrowIfDone() => ObjectDisposedException.ThrowIf(_done, this);

    /// <summary>Gives the stores' buffers back to the pool and what they held back to the budget.</summary>
    private void Release()
    {
        foreach (Part part in _parts)
        {
            part.Store.Release();
        }

        _parts.Clear();
        _lastPart = null;
        if (!ReferenceEquals(_intake, null) && _partition is not null)
        {
            _intake.Release();
        }

        _intake = null;
        _builder = null;
        _typedBuilder = null;
        _arena?.Reset();
        _scratch?.Reset();
        _memory?.Dispose();
        _memory = null;
        _reserved = 0;
    }

    /// <summary>One partition's rows: its value, row-encoded, empty for the one partition, and the store that holds them.</summary>
    private sealed class Part(byte[] partition, StructStore store)
    {
        internal byte[] Partition { get; } = partition;

        internal StructStore Store { get; } = store;
    }
}
