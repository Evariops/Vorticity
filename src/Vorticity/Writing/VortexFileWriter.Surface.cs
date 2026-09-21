using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity;

public sealed partial class VortexFileWriter
{
    /// <summary>The bytes a streamed write lets the sink hold before it flushes on its own.</summary>
    private const long StreamingFlushBytes = 8L << 20;

    private VortexSchema? _publicSchema;

    /// <summary>The rows of the writer's builder: those accepted and not yet sealed, then those being appended.</summary>
    private StructStore? _root;
    private ColumnsBuilder? _builder;
    private ColumnsBuilder? _typedBuilder;

    /// <summary>Where the builder's rows are laid out as a batch, over the builder's own buffers.</summary>
    private CanonicalArena? _builderArena;

    /// <summary>Where a pass-through lays out what it reshapes: a record's columns in the file's order, a selection.</summary>
    private CanonicalArena? _scratch;
    private long _acceptedRows;
    private Type? _membersOf;
    private int[]? _members;

    /// <summary>The file's columns.</summary>
    public VortexSchema Schema => _publicSchema ??= VortexTypes.SchemaOf(_schema);

    /// <summary>Rows accepted so far; right after an append opens, the row the append resumes from.</summary>
    /// <remarks>
    /// An append whose file did not end on a block rewrites its last chunk; the rows of that chunk
    /// are the file's already, and this count starts where they start.
    /// </remarks>
    public long RowCount => _acceptedRows;

    /// <summary>Rows per block, <see cref="VortexWriteOptions.BlockRows"/>, or the file's own for an append.</summary>
    public int BlockRows => _blockRows;

    /// <summary>
    /// Bytes accepted and not yet handed to the sink by a flush: encoded chunks the sink holds, and
    /// rows waiting for their chunk.
    /// </summary>
    public long UnflushedBytes =>
        (_sink is PipeSegmentSink pipe ? pipe.UnflushedBytes : 0) + _pendingBytes + (_root?.CommittedBytes ?? 0);

    /// <summary>Keeps the schema instance the writer was created with.</summary>
    internal void Declare(VortexSchema schema) => _publicSchema = schema;

    /// <summary>The writer's builder, over buffers from the session's pool; reusable, and cleared by each write.</summary>
    /// <returns>The builder; every call returns the same one.</returns>
    /// <exception cref="ObjectDisposedException">The file is completed or abandoned.</exception>
    public ColumnsBuilder Builder()
    {
        ThrowIfDone();
        return _builder ??= new ColumnsBuilder(Root(), Schema, null, this);
    }

    /// <summary>The writer's builder, seen as the columns of <typeparamref name="TRecord"/>.</summary>
    /// <typeparam name="TRecord">A record whose members name columns of the file.</typeparam>
    /// <returns>The builder, sharing its rows with <see cref="Builder()"/>.</returns>
    /// <exception cref="VortexSchemaException">A member has no column.</exception>
    public ColumnsBuilder<TRecord> Builder<TRecord>()
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
        if (_typedBuilder is ColumnsBuilder<TRecord> typed)
        {
            return typed;
        }

        StructStore root = Root();
        typed = new ColumnsBuilder<TRecord>(root, WriteBinding.Map(typeof(TRecord), TRecord.Schema, root.Type.Fields), this);
        _typedBuilder = typed;
        return typed;
    }

    /// <summary>Takes the rows of the writer's builder and clears it; whole blocks are sealed and encoded here once they fill a chunk.</summary>
    /// <param name="builder">The builder this writer handed out.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken; the sink sees them at the next flush.</returns>
    /// <exception cref="ArgumentException">The builder belongs to another writer, or is a nested struct's.</exception>
    /// <exception cref="VortexSchemaException">The builder's columns hold different row counts, or a list is left open.</exception>
    public ValueTask WriteAsync(ColumnsBuilder builder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ThrowIfDone();
        if (!ReferenceEquals(builder.Writer, this))
        {
            throw new ArgumentException(
                "A builder is written by the writer that handed it out; this one is another writer's, or a nested struct's.", nameof(builder));
        }

        return AcceptAsync(cancellationToken);
    }

    /// <summary>Appends <paramref name="rows"/> through the writer's builder, one column at a time, and takes them.</summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="rows">The rows, read before this returns.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <remarks>Rows appended to the builder and not yet written are written with these.</remarks>
    public ValueTask WriteAsync<TRecord>(ReadOnlySpan<TRecord> rows, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
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

    /// <summary>Writes a stream of rows, grouped by <see cref="BlockRows"/>, flushing as the sink fills.</summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="rows">The rows.</param>
    /// <param name="cancellationToken">Cancels the enumeration and the writes.</param>
    /// <returns>A task that completes when every row is taken.</returns>
    public async ValueTask WriteAsync<TRecord>(IAsyncEnumerable<TRecord> rows, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(rows);
        ThrowIfDone();
        int block = _blockRows > 0 ? _blockRows : VortexWriteOptions.Default.BlockRows;
        TRecord[] buffer = ArrayPool<TRecord>.Shared.Rent(block);
        long flushAt = Math.Max(StreamingFlushBytes, 4L * _blockBytes);
        try
        {
            int count = 0;
            await foreach (TRecord row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                buffer[count++] = row;
                if (count < block)
                {
                    continue;
                }

                await WriteAsync<TRecord>(buffer.AsSpan(0, count), cancellationToken).ConfigureAwait(false);
                count = 0;
                if (UnflushedBytes >= flushAt)
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Writes a batch of a scan typed by <typeparamref name="TRecord"/>: the columns decode once and encode once.</summary>
    /// <typeparam name="TRecord">The record the scan is typed by; its members cover every column of the file.</typeparam>
    /// <param name="columns">The batch, borrowed until the returned task completes.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <exception cref="VortexSchemaException">A column of the file has no member, or a member's column is not of the file's type.</exception>
    public ValueTask WriteAsync<TRecord>(Columns<TRecord> columns, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
        if (!_isTabular)
        {
            throw new VortexSchemaException("A record's columns are written to a file whose root is a struct.");
        }

        int[] members = MembersOf<TRecord>();
        CanonicalArena scratch = Scratch();
        int[] fields = ArrayPool<int>.Shared.Rent(members.Length);
        try
        {
            for (int column = 0; column < members.Length; column++)
            {
                int node = columns.ColumnNode(members[column]);
                RequireFits(columns.Arena.GetNode(node).DType, _schema.GetField(column), $"Member '{TRecord.Schema[members[column]].Name}' of {typeof(TRecord).Name}");
                fields[column] = scratch.ReferenceFrom(columns.Arena, node);
            }

            int root = scratch.AddStruct(_schema, columns.RowCount, Validity.NonNullable, fields.AsSpan(0, members.Length));
            if (!columns.SelectionWords.IsEmpty)
            {
                root = Selected(scratch, root, columns.SelectionWords);
            }

            return PassAsync(scratch, root, cancellationToken);
        }
        catch
        {
            scratch.Reset();
            throw;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(fields);
        }
    }

    /// <summary>Writes a batch of a tool scan: the columns decode once and encode once.</summary>
    /// <param name="batch">The batch, borrowed until the returned task completes; its columns are the file's.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <exception cref="VortexSchemaException">The batch's columns are not of the file's types.</exception>
    public ValueTask WriteAsync(BatchView batch, CancellationToken cancellationToken = default)
    {
        ThrowIfDone();
        RequireFits(batch.Arena.GetNode(batch.Node).DType, _schema, "The batch");
        return batch.SelectionWords.IsEmpty
            ? PassThroughAsync(batch.Arena, batch.Node, cancellationToken)
            : PassSelectedAsync(batch.Arena, batch.Node, batch.SelectionWords, cancellationToken);
    }

    /// <summary>Writes an owned batch: the columns decode once and encode once.</summary>
    /// <param name="batch">The batch; the caller still owns and disposes it.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <exception cref="VortexSchemaException">The batch's columns are not of the file's types.</exception>
    public ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfDone();
        RequireFits(batch.DType, _schema, "The batch");
        return batch.SelectionWords.IsEmpty
            ? PassThroughAsync(batch.Arena, batch.RootIndex, cancellationToken)
            : PassSelectedAsync(batch.Arena, batch.RootIndex, batch.SelectionWords, cancellationToken);
    }

    /// <summary>Takes an engine batch whose schema the caller vouches for: the dataset's objects, the tests.</summary>
    internal async ValueTask WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfDone();
        await DrainAsync(cancellationToken).ConfigureAwait(false);
        _acceptedRows += batch.RowCount;
        await WriteCoreAsync(batch.Arena, batch.RootIndex, seal: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seals every whole block accepted so far into a chunk and hands the encoded bytes to the sink.</summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the sink has accepted the bytes.</returns>
    /// <remarks>The only call that does I/O before <see cref="CompleteAsync"/>; a partial block stays pending.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDone();
        if (_root is { Committed: > 0 } root)
        {
            if (_rowBlock == 0)
            {
                await HandOffAsync(root.Committed, seal: false, cancellationToken).ConfigureAwait(false);
            }
            else if (root.Committed >= _rowBlock)
            {
                await HandOffAsync(root.Committed / _rowBlock * _rowBlock, seal: true, cancellationToken).ConfigureAwait(false);
            }
        }

        await SealAsync(cancellationToken).ConfigureAwait(false);
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the pending rows as the tail, then the statistics, zone maps, indexes and footer, and
    /// hands everything to the sink.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>What was written: bytes by kind, each column's encodings, and every index asked for, built or abandoned with its reason.</returns>
    /// <exception cref="VortexException">An index the policy marked required was not built; the file is not completed.</exception>
    /// <exception cref="ObjectDisposedException">The file is already completed or abandoned.</exception>
    public async ValueTask<WriteReport> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDone();
        _completed = true;
        if (_root is { Committed: > 0 } whole && _rowBlock > 0 && whole.Committed >= _rowBlock)
        {
            await HandOffAsync(whole.Committed / _rowBlock * _rowBlock, seal: true, cancellationToken).ConfigureAwait(false);
        }

        CanonicalArena? tail = null;
        int tailRoot = -1;
        if (_root is { Committed: > 0 } rest)
        {
            tail = BuilderArena();
            tailRoot = BuildRoot(tail, rest.Committed);
        }

        try
        {
            await CompleteCoreAsync(tail, tailRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tail?.Reset();
        }

        _finished = true;
        ReleaseBuilders();
        if (_filePipe is { } file)
        {
            _sinkClosed = true;
            await file.CompleteAsync().ConfigureAwait(false);
        }
        else if (_callerPipe is { } pipe)
        {
            _sinkClosed = true;
            await pipe.CompleteAsync().ConfigureAwait(false);
        }

        return BuildReport();
    }

    /// <summary>Gives the file up: a created file is deleted, an appended one is truncated back to what it was.</summary>
    /// <remarks>
    /// The caller's pipe is completed with an error, so that whatever it feeds knows the bytes are
    /// not a file. Nothing is written after this; safe to call more than once, and a no-op on a
    /// completed file.
    /// </remarks>
    public void Abandon()
    {
        if (_finished || _abandoned)
        {
            return;
        }

        _completed = true;
        _abandoned = true;
        ReleaseBuilders();
        if (_filePipe is { } file)
        {
            _sinkClosed = true;
            file.Abandon();
            DeleteCreated();
        }
        else if (_callerPipe is { } pipe)
        {
            _sinkClosed = true;
            pipe.Complete(new OperationCanceledException("The Vortex file was abandoned; the bytes written so far are not a file."));
        }
    }

    /// <summary>Releases what the writer holds; a file not completed is abandoned first.</summary>
    /// <returns>A task that completes when everything is released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            Abandon();
        }

        // The transit contexts own pooled native blocks: dropped rather than disposed, each block
        // is freed on the finalizer thread instead of returning to the pool.
        if (_transit is not null)
        {
            for (int i = 0; i < _transit.Length; i++)
            {
                _transit[i]?.Dispose();
                _transit[i] = null;
            }
        }

        // The index builders hold pooled hash sets and a payload arena; their results survive.
        _indexes?.Dispose();
        ReleaseBuilders();

        if (!_sinkClosed)
        {
            _sinkClosed = true;
            if (_sink is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }

            // After the sink, which holds the file with FileShare.None.
            if (_abandoned)
            {
                DeleteCreated();
                TruncateAppended();
            }
        }
    }

    /// <summary>Takes the rows appended to the writer's builder, and seals them once they fill a chunk.</summary>
    private ValueTask AcceptAsync(CancellationToken cancellationToken)
    {
        StructStore root = Root();
        if (root.Inconsistency() is { } why)
        {
            throw new VortexSchemaException($"The builder cannot be written: {why}. Complete every row before writing it.");
        }

        int rows = root.Rows - root.Committed;
        if (rows == 0)
        {
            return ValueTask.CompletedTask;
        }

        root.Commit();
        _acceptedRows += rows;
        int committed = root.Committed;
        if (_rowBlock == 0)
        {
            return HandOffAsync(committed, seal: false, cancellationToken);
        }

        return committed >= _rowBlock && root.CommittedBytes >= _blockBytes
            ? HandOffAsync(committed / _rowBlock * _rowBlock, seal: true, cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hands the first <paramref name="rows"/> accepted rows of the builder to the file as batches
    /// over the builder's own buffers, then drops them from the builder.
    /// </summary>
    /// <param name="rows">The rows: whole blocks when <paramref name="seal"/> is set.</param>
    /// <param name="seal">Whether they go out now as chunks of about the target bytes, rather than wait in transit.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    private async ValueTask HandOffAsync(int rows, bool seal, CancellationToken cancellationToken)
    {
        StructStore root = _root!;
        CanonicalArena arena = BuilderArena();
        try
        {
            int node = BuildRoot(arena, rows);
            long chunk = seal ? ChunkRows(root) : rows;
            for (long start = 0; start < rows; start += chunk)
            {
                int length = (int)Math.Min(chunk, rows - start);
                int window = length == rows ? node : CanonicalSlice.SliceAcross(arena, arena, node, (int)start, length);
                await WriteCoreAsync(arena, window, seal, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            arena.Reset();
        }

        root.Discard(rows);
    }

    /// <summary>Rows per chunk, whole blocks, for the chunk target in bytes at the rows' present width.</summary>
    private long ChunkRows(StructStore root)
    {
        if (_blockBytes <= 0 || root.Committed == 0)
        {
            return int.MaxValue;
        }

        long perRow = Math.Max(1, root.CommittedBytes / root.Committed);
        return Math.Max(_rowBlock, _blockBytes / perRow / _rowBlock * _rowBlock);
    }

    /// <summary>Hands the builder's accepted rows to the file before rows that arrive another way.</summary>
    private ValueTask DrainAsync(CancellationToken cancellationToken) =>
        _root is { Committed: > 0 } root ? HandOffAsync(root.Committed, seal: false, cancellationToken) : ValueTask.CompletedTask;

    private async ValueTask PassThroughAsync(CanonicalArena arena, int root, CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken).ConfigureAwait(false);
        _acceptedRows += arena.GetNode(root).Length;
        await WriteCoreAsync(arena, root, seal: false, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask PassSelectedAsync(CanonicalArena arena, int root, ReadOnlySpan<ulong> selection, CancellationToken cancellationToken)
    {
        CanonicalArena scratch = Scratch();
        try
        {
            return PassAsync(scratch, Selected(scratch, scratch.ReferenceFrom(arena, root), selection), cancellationToken);
        }
        catch
        {
            scratch.Reset();
            throw;
        }
    }

    /// <summary>Writes a batch laid out in the scratch arena, which is reset once the rows are taken.</summary>
    private async ValueTask PassAsync(CanonicalArena scratch, int root, CancellationToken cancellationToken)
    {
        try
        {
            await PassThroughAsync(scratch, root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scratch.Reset();
        }
    }

    /// <summary>The rows of <paramref name="node"/> a selection keeps, gathered into <paramref name="scratch"/>.</summary>
    private static int Selected(CanonicalArena scratch, int node, ReadOnlySpan<ulong> words)
    {
        int count = 0;
        foreach (ulong word in words)
        {
            count += BitOperations.PopCount(word);
        }

        int[] indices = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            int n = 0;
            for (int w = 0; w < words.Length; w++)
            {
                ulong word = words[w];
                while (word != 0)
                {
                    indices[n++] = (w << 6) + BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                }
            }

            return CanonicalFilter.Apply(scratch, node, indices.AsSpan(0, n));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
        }
    }

    /// <summary>Per column of the file, the member of <typeparamref name="TRecord"/> that holds it.</summary>
    private int[] MembersOf<TRecord>()
        where TRecord : IVortexRecord<TRecord>
    {
        if (_membersOf == typeof(TRecord))
        {
            return _members!;
        }

        VortexSchema record = TRecord.Schema;
        int[]? map = WriteBinding.Map(typeof(TRecord), record, Schema.FieldArray);
        int[] members = new int[_fieldCount];
        Array.Fill(members, -1);
        for (int member = 0; member < record.Count; member++)
        {
            members[map is null ? member : map[member]] = member;
        }

        int missing = Array.IndexOf(members, -1);
        if (missing >= 0)
        {
            throw new VortexSchemaException(
                $"Column '{Schema[missing].Name}' of the file has no member of {typeof(TRecord).Name}; a record written as columns covers every column.");
        }

        _membersOf = typeof(TRecord);
        _members = members;
        return members;
    }

    /// <summary>Refuses a column that is not of the file's type: a pass-through writes the file's own types, a non-nullable column into a nullable one aside.</summary>
    private static void RequireFits(DType value, DType column, string what)
    {
        if (!Fits(value, column))
        {
            throw new VortexSchemaException(
                $"{what} is {VortexTypes.FromDType(value)}, and the file holds {VortexTypes.FromDType(column)} there.");
        }
    }

    private static bool Fits(DType value, DType column)
    {
        if (value.Kind != column.Kind || (value.IsNullable && !column.IsNullable))
        {
            return false;
        }

        switch (column.Kind)
        {
            case DTypeKind.Primitive:
                return value.PType == column.PType;
            case DTypeKind.Decimal:
                return value.Precision == column.Precision && value.Scale == column.Scale;
            case DTypeKind.List:
                return Fits(value.ElementType, column.ElementType);
            case DTypeKind.FixedSizeList:
                return value.FixedSize == column.FixedSize && Fits(value.ElementType, column.ElementType);
            case DTypeKind.Extension:
                return value.ExtensionIdUtf8.SequenceEqual(column.ExtensionIdUtf8)
                    && value.ExtensionMetadata.SequenceEqual(column.ExtensionMetadata)
                    && Fits(value.StorageType, column.StorageType);
            case DTypeKind.Struct:
            case DTypeKind.Union:
                if (value.FieldCount != column.FieldCount)
                {
                    return false;
                }

                for (int i = 0; i < column.FieldCount; i++)
                {
                    if (!Fits(value.GetField(i), column.GetField(i)))
                    {
                        return false;
                    }
                }

                return true;
            case DTypeKind.Map:
                return Fits(value.KeyType, column.KeyType) && Fits(value.ValueType, column.ValueType);
            default:
                return true;
        }
    }

    private StructStore Root()
    {
        if (_root is { } root)
        {
            return root;
        }

        VortexSessionOptions options = Session.Options;
        ColumnStore store = ColumnStores.Create(_schema, options.EnginePool, options.Extensions);
        _root = _isTabular
            ? (StructStore)store
            : new StructStore(_schema, VortexType.Struct([new VortexField(string.Empty, store.Type)]), [store], options.EnginePool);
        return _root;
    }

    /// <summary>The node of the builder's first <paramref name="rows"/> rows, in the shape of the file's root.</summary>
    private int BuildRoot(CanonicalArena arena, int rows) =>
        _isTabular ? _root!.Build(arena, rows) : _root!.Children[0].Build(arena, rows);

    private CanonicalArena BuilderArena() => _builderArena ??= new CanonicalArena(64, Session.Options.EnginePool);

    private CanonicalArena Scratch() => _scratch ??= new CanonicalArena(64, Session.Options.EnginePool);

    /// <summary>Gives the builder's buffers back to the pool.</summary>
    private void ReleaseBuilders()
    {
        _root?.Release();
        _builderArena?.Reset();
        _scratch?.Reset();
    }

    private void ThrowIfDone() => ObjectDisposedException.ThrowIf(_completed, this);

    private void DeleteCreated()
    {
        if (_createdPath is null)
        {
            return;
        }

        try
        {
            System.IO.File.Delete(_createdPath);
        }
        catch (IOException)
        {
            // A file already gone is the outcome asked for, and one the caller has since replaced
            // is not this writer's to judge.
        }
        catch (UnauthorizedAccessException)
        {
            // The same answer for a read-only file or a path that has become a directory.
        }
    }

    /// <summary>Truncates an append made over another sink back to the file's length before it.</summary>
    private void TruncateAppended()
    {
        if (_appendedPath is null || _filePipe is not null)
        {
            return;
        }

        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = System.IO.File.OpenHandle(
            _appendedPath, FileMode.Open, FileAccess.Write, FileShare.None);
        RandomAccess.SetLength(handle, _appendOrigin);
    }
}
