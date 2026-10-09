using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;
using Vorticity.Parquet.Writing;
using Vorticity.Types;
using Vorticity.Writing;
using Vorticity.Zstd;

namespace Vorticity.Parquet;

/// <summary>
/// Writes a Parquet file: the rows it is given, from its builder, records, batches or a scan, cut
/// into pages of <see cref="ParquetWriteOptions.BlockRows"/> at the same rows in every column, the
/// pages into row groups of whole blocks, and the footer that describes them.
/// </summary>
/// <remarks>
/// Not thread-safe. Each write stages its rows as it takes them, so a builder is free again when the
/// write returns. A row group's pages wait in the writer until it closes; its column chunks then go
/// to the sink one after the other and the sink is flushed, the only I/O before
/// <see cref="CompleteAsync"/>. A write that fails part way leaves the file to be abandoned.
/// </remarks>
public sealed class ParquetFileWriter : IAsyncDisposable
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    private static readonly string CreatedBy = CreatedByOf(typeof(ParquetFileWriter).Assembly);

    private readonly VortexSession _session;
    private readonly DType _dtype;
    private readonly WriteSchema _map;
    private readonly ParquetWriteOptions _options;
    private readonly CompressionCodec _codec;
    private readonly ZstdCompressor? _zstd;
    private readonly bool _zstdRented;
    private readonly ColumnChunkWriter[] _columns;

    /// <summary>Per column, its node in the batch being taken.</summary>
    private readonly int[] _nodes;

    private readonly PipeSegmentSink _sink;
    private readonly FilePipeWriter? _filePipe;
    private readonly PipeWriter? _callerPipe;
    private readonly List<WrittenRowGroup> _rowGroups = [];

    /// <summary>The rows of the writer's builder, until a write takes them.</summary>
    private StructStore? _root;
    private ColumnsBuilder? _builder;
    private ColumnsBuilder? _typedBuilder;

    /// <summary>Where the builder's rows are laid out as a batch, over the builder's own buffers.</summary>
    private CanonicalArena? _arena;

    /// <summary>Where a record's columns are laid out in the file's order.</summary>
    private CanonicalArena? _scratch;

    private Type? _membersOf;
    private int[]? _members;
    private long _rowCount;

    /// <summary>The rows staged for the row group being written.</summary>
    private long _groupRows;

    private bool _started;
    private bool _completed;
    private bool _finished;
    private bool _abandoned;
    private bool _broken;
    private bool _disposed;

    private ParquetFileWriter(
        VortexSchema schema,
        WriteSchema map,
        ParquetWriteOptions options,
        VortexSession session,
        PipeSegmentSink sink,
        FilePipeWriter? filePipe,
        PipeWriter? callerPipe)
    {
        Schema = schema;
        _dtype = VortexTypes.ToDType(schema, new DTypeArena());
        _map = map;
        _options = options;
        _session = session;
        _sink = sink;
        _filePipe = filePipe;
        _callerPipe = callerPipe;
        _codec = (CompressionCodec)options.ResolvedCompression;
        int level = options.ResolvedLevel;
        if (_codec == CompressionCodec.Zstd)
        {
            // The pool's compressors are at the default level, which most files take.
            _zstdRented = level == ZstdCompressor.DefaultLevel;
            _zstd = _zstdRented ? ZstdEncoders.Rent() : new ZstdCompressor(level);
        }

        _columns = new ColumnChunkWriter[map.Columns.Length];
        for (int i = 0; i < _columns.Length; i++)
        {
            _columns[i] = new ColumnChunkWriter(map.Columns[i], _codec, level, _zstd, options.BlockRows, options.Dictionaries, session.Options.EnginePool);
        }

        _nodes = new int[_columns.Length];
    }

    /// <summary>The file's columns.</summary>
    public VortexSchema Schema { get; }

    /// <summary>The rows written so far, staged or in row groups the sink holds.</summary>
    public long RowCount => _rowCount;

    /// <summary>The row groups closed so far.</summary>
    public int RowGroupCount => _rowGroups.Count;

    /// <summary>A writer at <paramref name="path"/>, which replaces what is there once the file completes.</summary>
    /// <remarks>The schema and the options are checked before the file is opened, so a refused writer leaves the path as it was.</remarks>
    internal static ParquetFileWriter Create(string path, VortexSchema schema, ParquetWriteOptions options, VortexSession session)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(schema);
        options.Validate();
        WriteSchema map = WriteSchema.Map(schema);
        FilePipeWriter pipe = FilePipeWriter.Create(path, session.Options.MemoryPool, options.Durable);
        try
        {
            return new ParquetFileWriter(schema, map, options, session, new PipeSegmentSink(pipe), pipe, null);
        }
        catch
        {
            pipe.Abandon();
            throw;
        }
    }

    /// <summary>A writer over <paramref name="sink"/>, which it completes with the file.</summary>
    internal static ParquetFileWriter Create(PipeWriter sink, VortexSchema schema, ParquetWriteOptions options, VortexSession session)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(schema);
        options.Validate();
        return new ParquetFileWriter(schema, WriteSchema.Map(schema), options, session, new PipeSegmentSink(sink), null, sink);
    }

    /// <summary>The writer's builder, over buffers from the session's pool; reusable, and cleared by each write.</summary>
    /// <returns>The builder; every call returns the same one.</returns>
    /// <exception cref="ObjectDisposedException">The file is completed or abandoned.</exception>
    public ColumnsBuilder Builder()
    {
        ThrowIfDone();
        return _builder ??= new ColumnsBuilder(Root(), Schema, null, null);
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
        typed = new ColumnsBuilder<TRecord>(root, WriteBinding.Map(typeof(TRecord), TRecord.Schema, root.Type.FieldArray), null);
        _typedBuilder = typed;
        return typed;
    }

    /// <summary>Takes the rows of the writer's builder and clears it.</summary>
    /// <param name="builder">The builder this writer handed out.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken, and written when they close a row group.</returns>
    /// <exception cref="ArgumentException">The builder belongs to another writer, or is a nested struct's.</exception>
    /// <exception cref="VortexSchemaException">The builder's columns hold different row counts, or a list is left open.</exception>
    public ValueTask WriteAsync(ColumnsBuilder builder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ThrowIfDone();
        if (!ReferenceEquals(builder.Store, _root))
        {
            throw new ArgumentException(
                "A builder is written by the writer that handed it out; this one is another writer's, or a nested struct's.", nameof(builder));
        }

        // A cancelled call takes nothing: the rows stay in the builder, not in the file.
        cancellationToken.ThrowIfCancellationRequested();
        return AcceptAsync(cancellationToken);
    }

    /// <summary>Appends <paramref name="rows"/> through the writer's builder and takes them.</summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="rows">The rows, read before this returns.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <remarks>Rows appended to the builder and not yet written are written with these.</remarks>
    public ValueTask WriteAsync<TRecord>(ReadOnlySpan<TRecord> rows, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
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

    /// <summary>Writes a batch of a scan typed by <typeparamref name="TRecord"/>.</summary>
    /// <typeparam name="TRecord">The record the scan is typed by; its members cover every column of the file.</typeparam>
    /// <param name="columns">The batch, borrowed until the returned task completes.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <exception cref="VortexSchemaException">A column of the file has no member, or a member's column is not of the file's type.</exception>
    public ValueTask WriteAsync<TRecord>(Columns<TRecord> columns, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
        cancellationToken.ThrowIfCancellationRequested();
        int[] members = MembersOf<TRecord>();
        CanonicalArena scratch = _scratch ??= new CanonicalArena(64, _session.Options.EnginePool);
        try
        {
            return TakeScratchAsync(scratch, BatchIntake.Columns(scratch, columns, members, _dtype), cancellationToken);
        }
        catch
        {
            scratch.Reset();
            throw;
        }
    }

    /// <summary>Writes every batch of a scan as it comes.</summary>
    /// <typeparam name="TRecord">The record the scan is typed by; its members cover every column of the file.</typeparam>
    /// <param name="scan">The scan, whose one sink this is.</param>
    /// <param name="cancellationToken">Cancels the scan and the writes.</param>
    /// <returns>A task that completes when every row is taken.</returns>
    /// <exception cref="VortexSchemaException">A column of the file has no member, or a member's column is not of the file's type.</exception>
    public async ValueTask WriteAsync<TRecord>(Scan<TRecord> scan, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(scan);
        ThrowIfDone();
        Scan<TRecord>.AsyncEnumerator batches = scan.GetAsyncEnumerator(cancellationToken);
        await using (batches.ConfigureAwait(false))
        {
            while (await batches.MoveNextAsync().ConfigureAwait(false))
            {
                await WriteAsync(batches.Current, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Writes an owned batch.</summary>
    /// <param name="batch">The batch; the caller still owns and disposes it.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are taken.</returns>
    /// <exception cref="VortexSchemaException">The batch's columns are not of the file's types.</exception>
    public ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfDone();
        cancellationToken.ThrowIfCancellationRequested();
        BatchIntake.RequireFits(batch.DType, _dtype, "The batch");
        if (batch.SelectionWords.IsEmpty)
        {
            return IngestAsync(batch.Arena, batch.RootIndex, cancellationToken);
        }

        CanonicalArena scratch = _scratch ??= new CanonicalArena(64, _session.Options.EnginePool);
        try
        {
            int root = BatchIntake.Selected(scratch, scratch.ReferenceFrom(batch.Arena, batch.RootIndex), batch.SelectionWords);
            return TakeScratchAsync(scratch, root, cancellationToken);
        }
        catch
        {
            scratch.Reset();
            throw;
        }
    }

    /// <summary>
    /// Closes the row group being written at its last whole block and hands it to the sink; the rows
    /// past that block wait for the next one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the sink has accepted the row group.</returns>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDone();
        _broken = true;
        await CloseRowGroupAsync(partial: false, cancellationToken).ConfigureAwait(false);
        _broken = false;
    }

    /// <summary>
    /// Writes the last row group, the page index and the footer, and completes the sink. Rows still in
    /// the writer's builder are taken first, as a last write would.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="VortexSchemaException">The builder's columns hold different row counts, or a list is left open.</exception>
    /// <exception cref="ObjectDisposedException">The file is already completed or abandoned.</exception>
    public async ValueTask<ParquetWriteReport> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDone();
        if (_root is { } builder && builder.Rows > builder.Committed)
        {
            await AcceptAsync(cancellationToken).ConfigureAwait(false);
        }

        _completed = true;
        await CloseRowGroupAsync(partial: true, cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await WriteTailAsync(cancellationToken).ConfigureAwait(false);
        _finished = true;
        long bytes = _sink.Position;
        Release();
        if (_filePipe is { } file)
        {
            await file.CompleteAsync().ConfigureAwait(false);
        }
        else if (_callerPipe is { } pipe)
        {
            await pipe.CompleteAsync().ConfigureAwait(false);
        }

        return new ParquetWriteReport(_rowCount, _rowGroups.Count, bytes);
    }

    /// <summary>
    /// Gives the file up: a created file is deleted, and whatever was at its path stays as it was; a
    /// caller's pipe is completed with an error, so that whatever it feeds knows the bytes are not a
    /// file. Safe to call more than once, and a no-op on a completed file.
    /// </summary>
    public void Abandon()
    {
        if (_finished || _abandoned)
        {
            return;
        }

        _completed = true;
        _abandoned = true;
        Release();
        if (_filePipe is { } file)
        {
            file.Abandon();
        }
        else
        {
            _callerPipe?.Complete(new OperationCanceledException("The Parquet file was abandoned; the bytes written so far are not a file."));
        }
    }

    /// <summary>Releases what the writer holds; a file not completed is abandoned first.</summary>
    /// <returns>A task that completes when everything is released.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (!_finished)
        {
            Abandon();
        }

        foreach (ColumnChunkWriter column in _columns)
        {
            column.Dispose();
        }

        if (_zstdRented)
        {
            ZstdEncoders.Return(_zstd!);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary><c>Vorticity.Parquet version &lt;version&gt; (build &lt;commit&gt;)</c>, the form the standard asks a writer for.</summary>
    internal static string CreatedByOf(Assembly assembly)
    {
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        int build = version.IndexOf('+', StringComparison.Ordinal);
        return build < 0
            ? $"Vorticity.Parquet version {version}"
            : $"Vorticity.Parquet version {version[..build]} (build {version[(build + 1)..]})";
    }

    /// <summary>Takes the builder's appended rows: checks them, lays them out over its buffers, stages them, and drops them.</summary>
    private async ValueTask AcceptAsync(CancellationToken cancellationToken)
    {
        StructStore root = Root();
        if (root.Inconsistency() is { } why)
        {
            throw new VortexSchemaException($"The builder cannot be written: {why}. Complete every row before writing it.");
        }

        int rows = root.Rows - root.Committed;
        if (rows == 0)
        {
            return;
        }

        root.Commit();
        CanonicalArena arena = _arena ??= new CanonicalArena(64, _session.Options.EnginePool);
        try
        {
            await IngestAsync(arena, root.Build(arena, root.Committed), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            arena.Reset();
            root.Discard(root.Committed);
        }
    }

    /// <summary>Stages a batch laid out in the scratch arena, which is reset once the rows are taken.</summary>
    private async ValueTask TakeScratchAsync(CanonicalArena scratch, int root, CancellationToken cancellationToken)
    {
        try
        {
            await IngestAsync(scratch, root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scratch.Reset();
        }
    }

    /// <summary>
    /// Stages the rows of <paramref name="root"/>, a struct of the file's columns, a block at a time
    /// across the columns, so that a row group closes on the block that fills it.
    /// </summary>
    private async ValueTask IngestAsync(CanonicalArena arena, int root, CancellationToken cancellationToken)
    {
        // Rows delivered encoded are decoded here, once, so that the column writers only ever meet
        // canonical nodes.
        root = arena.DecodedTree(root);
        int rows = arena.GetNode(root).Length;
        if (rows == 0)
        {
            return;
        }

        for (int c = 0; c < _columns.Length; c++)
        {
            WriteColumn column = _columns[c].Column;
            int node = EncodedForms.Canonical(arena, arena.GetNode(root).GetFieldIndex(column.Field));
            if (column.ThroughStorage)
            {
                node = EncodedForms.Canonical(arena, arena.GetNode(node).StorageIndex);
            }

            _nodes[c] = node;
        }

        // Until every column holds the batch's rows, the columns disagree: a failure past here
        // leaves the file to be abandoned.
        _broken = true;
        int blockRows = _options.BlockRows;
        for (int start = 0; start < rows;)
        {
            int take = Math.Min(rows - start, blockRows - (int)(_groupRows % blockRows));
            for (int c = 0; c < _columns.Length; c++)
            {
                _columns[c].Append(arena, _nodes[c], start, take);
            }

            start += take;
            _groupRows += take;
            _rowCount += take;
            if (_groupRows % blockRows == 0 && (_groupRows >= _options.RowGroupRows || BufferedBytes() >= _options.RowGroupBytes))
            {
                await CloseRowGroupAsync(partial: false, cancellationToken).ConfigureAwait(false);
            }
        }

        _broken = false;
    }

    /// <summary>
    /// Closes the row group being written and hands its column chunks to the sink: every row staged
    /// when <paramref name="partial"/>, at the file's end, and its whole blocks otherwise.
    /// </summary>
    private async ValueTask CloseRowGroupAsync(bool partial, CancellationToken cancellationToken)
    {
        long rows = partial ? _groupRows : _groupRows - (_groupRows % _options.BlockRows);
        if (rows == 0)
        {
            return;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        WrittenChunk[] chunks = new WrittenChunk[_columns.Length];
        for (int c = 0; c < _columns.Length; c++)
        {
            ColumnChunkWriter column = _columns[c];
            ChunkResult chunk = column.Close(_sink.Position, partial);
            Debug.Assert(chunk.Rows == rows, "Every column closes the same rows.");
            await column.WriteChunkAsync(_sink, cancellationToken).ConfigureAwait(false);
            column.Reset();
            chunks[c] = new WrittenChunk { Column = column.Column, Chunk = chunk, Codec = _codec };
        }

        _rowGroups.Add(new WrittenRowGroup { Chunks = chunks, Rows = rows, Ordinal = _rowGroups.Count });
        _groupRows -= rows;
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the leading magic, once, before the first row group or the footer.</summary>
    private ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return ValueTask.CompletedTask;
        }

        _started = true;
        return _sink.WriteAsync(Magic, cancellationToken);
    }

    private async ValueTask WriteTailAsync(CancellationToken cancellationToken)
    {
        using PooledBytes tail = new(_session.Options.EnginePool);
        WriteTail(tail, _sink.Position);
        await _sink.WriteAsync(tail.Written, cancellationToken).ConfigureAwait(false);
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The file's end, starting at <paramref name="position"/>: every column chunk's offset index,
    /// then the footer, its length and the magic.
    /// </summary>
    private void WriteTail(PooledBytes tail, long position)
    {
        ThriftCompactWriter writer = new(tail);
        foreach (WrittenRowGroup rowGroup in _rowGroups)
        {
            foreach (WrittenChunk chunk in rowGroup.Chunks)
            {
                int start = tail.Length;
                OffsetIndex.Write(ref writer, chunk.Chunk.Pages);
                writer.Flush();
                chunk.OffsetIndexOffset = position + start;
                chunk.OffsetIndexLength = tail.Length - start;
            }
        }

        int footer = tail.Length;
        FooterWriter.WriteFileMetaData(ref writer, _map.Elements, _rowCount, _rowGroups, [], CreatedBy, _map.Columns);
        writer.Flush();
        int length = tail.Length - footer;
        Span<byte> end = tail.Reserve(8);
        BinaryPrimitives.WriteInt32LittleEndian(end, length);
        Magic.CopyTo(end[4..]);
    }

    private long BufferedBytes()
    {
        long bytes = 0;
        foreach (ColumnChunkWriter column in _columns)
        {
            bytes += column.BufferedBytes;
        }

        return bytes;
    }

    /// <summary>Per column of the file, the member of <typeparamref name="TRecord"/> that holds it.</summary>
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

    private StructStore Root() =>
        _root ??= (StructStore)ColumnStores.Create(_dtype, _session.Options.EnginePool, _session.Options.Extensions);

    /// <summary>Gives the builder's buffers back to the pool.</summary>
    private void Release()
    {
        _root?.Release();
        _arena?.Reset();
        _scratch?.Reset();
    }

    private void ThrowIfDone()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (_broken)
        {
            throw new InvalidOperationException("A write failed part way through; the file can only be abandoned.");
        }
    }
}
