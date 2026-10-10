using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Parquet.Thrift;
using Vorticity.Parquet.Writing;
using Vorticity.Types;
using Vorticity.Types.Serialization;
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
/// write returns. A row group's pages wait in the writer until it closes; its column chunks are then
/// lent to the sink one after the other and the sink is flushed, the only I/O before
/// <see cref="CompleteAsync"/>: over a path, one gathered write of the buffers the pages lie in, which
/// copies none of them. A write that fails part way leaves the file to be abandoned.
/// </remarks>
public sealed class ParquetFileWriter : IAsyncDisposable
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    /// <summary>The magic of a file whose footer is encrypted.</summary>
    private static readonly byte[] EncryptedMagic = "PARE"u8.ToArray();

    private static readonly string CreatedBy = CreatedByOf(typeof(ParquetFileWriter).Assembly);

    private readonly VortexSession _session;
    private readonly DType _dtype;

    /// <summary>Whether the root is a struct of the columns: one whose root is not is its one column, with no struct level above it.</summary>
    private readonly bool _isTabular;

    /// <summary>The key-value metadata the footer carries.</summary>
    private readonly KeyValuePair<string, string>[] _keyValues;
    private readonly WriteSchema _map;
    private readonly ParquetWriteOptions _options;

    /// <summary>Per ZSTD level a column compresses at, the compressors its columns share.</summary>
    private readonly Dictionary<int, Compressors> _compressors = [];

    /// <summary>
    /// The cells, rows times columns, from which a block's rows are staged side by side on the
    /// writer's threads: below, waking the threads costs more than the staging they would share.
    /// </summary>
    private const int AcrossCells = 16_384;

    /// <summary>The threads the columns stage their rows and close their pages on, the writing one included.</summary>
    private readonly int _lanes;
    private WorkFan? _fan;
    private BlockWork? _blockWork;
    private readonly ColumnChunkWriter[] _columns;

    /// <summary>Per column, its node in the batch being taken.</summary>
    private readonly int[] _nodes;

    private readonly PipeSegmentSink _sink;
    private readonly FilePipeWriter? _filePipe;
    private readonly PipeWriter? _callerPipe;
    private readonly List<WrittenRowGroup> _rowGroups = [];

    /// <summary>What encrypts the file, or null for a file in plaintext.</summary>
    private readonly Encryption.FileEncryptor? _encryptor;

    /// <summary>What holds the rows to the order the file declares, or null for rows in no order.</summary>
    private readonly SortedRows? _sorted;

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
        _isTabular = _dtype.Kind == DTypeKind.Struct;
        _keyValues = KeyValues(_dtype, options);
        _map = map;
        _options = options;
        _session = session;
        _sink = sink;
        _filePipe = filePipe;
        _callerPipe = callerPipe;
        _lanes = options.DegreeOfParallelism > 0 ? options.DegreeOfParallelism : session.Options.MaxDegreeOfParallelism;

        _columns = new ColumnChunkWriter[map.Columns.Length];

        // The lanes every column's pages compress on, while the columns encode the pages after them.
        PageLanes? lanes = _lanes > 1 ? new PageLanes(_lanes) : null;
        HashSet<string> blooms = options.BloomFilters is { } named ? new(named.Keys, StringComparer.Ordinal) : [];
        HashSet<string> codecs = options.ColumnCompression is { } own ? new(own.Keys, StringComparer.Ordinal) : [];
        HashSet<string> hints = options.Hints is { } pinned ? new(pinned.Keys, StringComparer.Ordinal) : [];
        HashSet<string> keys = options.Encryption?.ColumnKeys is { } columnKeys ? new(columnKeys.Keys, StringComparer.Ordinal) : [];
        try
        {
            _encryptor = options.Encryption is { } encryption ? new Encryption.FileEncryptor(encryption) : null;
            for (int i = 0; i < _columns.Length; i++)
            {
                // A column's options by its dotted path, which a top-level column's name is.
                WriteColumn column = map.Columns[i];
                string path = string.Join('.', column.Path);
                double rate = options.BloomFilters is { } rates && rates.TryGetValue(path, out double asked) ? asked : 0;
                ParquetEncodingHint hint = options.Hints is { } given && given.TryGetValue(path, out ParquetEncodingHint wanted) ? wanted : ParquetEncodingHint.Auto;
                RequireTakes(column, path, hint);
                ParquetCodec codec = options.CodecOf(path);
                blooms.Remove(path);
                codecs.Remove(path);
                hints.Remove(path);
                keys.Remove(path);
                _columns[i] = new ColumnChunkWriter(
                    column, (CompressionCodec)codec.Compression, codec.Level, CompressorsOf(codec), options.BlockRows, options.Profile, session.Options.EnginePool, rate, options.RowGroupRows)
                {
                    WriteChecksums = options.WriteChecksums,
                    PageBytes = options.PageBytes,
                    DefersPages = _lanes > 1 && map.Columns.Length > 1,
                    DataPages = options.DataPageVersion,
                    AlignUncompressedPages = options.AlignUncompressedPages,
                    AllowAlp = options.Alp,
                    Hint = hint,
                    Encryptor = _encryptor?.Column(path, column.Path, i),
                    Lanes = lanes,
                };
            }

            _sorted = SortedRows.For(options.SortingColumns, map, schema);
            RequireColumns(keys, "column keys");
            RequireColumns(blooms, "Bloom filters");
            RequireColumns(codecs, "column codecs");
            RequireColumns(hints, "hints");
        }
        catch
        {
            ReleaseColumns();
            _encryptor?.Dispose();
            throw;
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
        if (!_isTabular)
        {
            throw new VortexSchemaException("A record's columns are written to a file whose root is a struct.");
        }

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

        ReleaseColumns();
        _encryptor?.Dispose();
        if (_fan is { } fan)
        {
            WorkFan.Return(fan);
            _fan = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Throws unless the column at <paramref name="path"/> takes <paramref name="hint"/>: the encodings
    /// the standard gives its physical type, and a dictionary where this writer builds one.
    /// </summary>
    private static void RequireTakes(WriteColumn column, string path, ParquetEncodingHint hint)
    {
        bool takes = hint switch
        {
            ParquetEncodingHint.Auto or ParquetEncodingHint.Plain => true,
            ParquetEncodingHint.Dictionary => column.Conversion is not (ValueConversion.Bool or ValueConversion.Null) && !column.FixedElements,
            ParquetEncodingHint.DeltaBinaryPacked => column.Physical is PhysicalType.Int32 or PhysicalType.Int64,
            ParquetEncodingHint.DeltaLengthByteArray => column.Physical == PhysicalType.ByteArray,
            ParquetEncodingHint.DeltaByteArray => column.Physical is PhysicalType.ByteArray or PhysicalType.FixedLenByteArray,
            ParquetEncodingHint.ByteStreamSplit => column.Physical is PhysicalType.Int32 or PhysicalType.Int64 or PhysicalType.Float or PhysicalType.Double or PhysicalType.FixedLenByteArray,
            ParquetEncodingHint.Alp => column.Physical is PhysicalType.Float or PhysicalType.Double,
            _ => column.Physical == PhysicalType.Boolean,
        };
        if (!takes)
        {
            throw new ArgumentException(
                $"The column '{path}' is {ParquetMetadata.Physical(column.Physical)}, which is not written as {ParquetMetadata.EncodingName(ColumnChunkWriter.EncodingOf(hint))}.",
                "options");
        }
    }

    /// <summary>Throws when an option names columns the schema does not have.</summary>
    private static void RequireColumns(HashSet<string> unknown, string what)
    {
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"The {what} name '{string.Join("', '", unknown)}', which no column of the schema is.", "options");
        }
    }

    /// <summary>The compressors of a column's codec: one pool for the columns that share a ZSTD level, none for another codec.</summary>
    private Compressors? CompressorsOf(ParquetCodec codec)
    {
        if (codec.Compression != ParquetCompression.Zstd)
        {
            return null;
        }

        if (!_compressors.TryGetValue(codec.Level, out Compressors? compressors))
        {
            compressors = new Compressors(codec.Level);
            _compressors.Add(codec.Level, compressors);
        }

        return compressors;
    }

    /// <summary>Gives back what the column writers and their compressors hold.</summary>
    private void ReleaseColumns()
    {
        foreach (ColumnChunkWriter? column in _columns)
        {
            column?.Dispose();
        }

        foreach (Compressors compressors in _compressors.Values)
        {
            compressors.Release();
        }

        _compressors.Clear();
    }

    /// <summary>
    /// Stages rows <c>[start, start + count)</c> of the batch in every column and, when they end the
    /// block, closes the columns' pages: on one lane, a column after the other, each closing its own;
    /// on more, side by side on the writer's threads, each column staging its rows and closing its
    /// page on the thread that claims it.
    /// </summary>
    /// <remarks>
    /// Rows too few to pay for the threads are staged on this one, and only the pages they end are
    /// closed side by side. A nested column always stages here, before the others: its shredding
    /// may lay out nodes in the batch's arena, which the columns staging side by side only read.
    /// </remarks>
    private void Stage(CanonicalArena arena, int start, int count, bool closes)
    {
        if (_lanes == 1 || _columns.Length == 1)
        {
            for (int c = 0; c < _columns.Length; c++)
            {
                _columns[c].Append(arena, _nodes[c], start, count);
            }

            return;
        }

        bool across = (long)count * _columns.Length >= AcrossCells;
        for (int c = 0; c < _columns.Length; c++)
        {
            if (!across || _columns[c].Column.Nested)
            {
                _columns[c].Append(arena, _nodes[c], start, count);
            }
        }

        if (across || closes)
        {
            WorkFan fan = _fan ??= WorkFan.Rent(_lanes);
            BlockWork work = _blockWork ??= new BlockWork(this);
            work.Start = start;
            work.Stages = across;
            work.Closes = closes;
            work.Order(fan.Items(_columns.Length));
            fan.Run(work, _columns.Length, arena, count);
        }
    }

    /// <summary>
    /// A block's work for the work fan, a column an item, on whichever of the writer's threads claims
    /// it: the column's rows staged, but a nested column's, and its page closed when the rows end the
    /// block.
    /// </summary>
    /// <remarks>
    /// The items go longest first, by what each column's took the block before: the writing thread
    /// claims the first while the others wake, and a column that holds every block is not the one
    /// that waits for a thread.
    /// </remarks>
    private sealed class BlockWork(ParquetFileWriter writer) : IFanWork
    {
        /// <summary>Per column, the ticks its item took the last time it ran.</summary>
        private readonly long[] _took = new long[writer._columns.Length];

        /// <summary>The batch's first row the block takes.</summary>
        internal int Start { get; set; }

        /// <summary>Whether the columns stage the rows here, the fan's state the batch's arena and its value their count.</summary>
        internal bool Stages { get; set; }

        /// <summary>Whether the rows end the block, whose pages then close.</summary>
        internal bool Closes { get; set; }

        /// <summary>Lays the columns out in <paramref name="order"/>, longest first.</summary>
        internal void Order(Span<int> order)
        {
            for (int c = 0; c < order.Length; c++)
            {
                int at = c;
                while (at > 0 && _took[order[at - 1]] < _took[c])
                {
                    order[at] = order[at - 1];
                    at--;
                }

                order[at] = c;
            }
        }

        public void Run(WorkFan fan, int item)
        {
            int index = fan.Item(item);
            long began = Stopwatch.GetTimestamp();
            RunColumn(fan, index);
            _took[index] = Stopwatch.GetTimestamp() - began;
        }

        private void RunColumn(WorkFan fan, int item)
        {
            ColumnChunkWriter column = writer._columns[item];
            if (Stages && !column.Column.Nested)
            {
                column.Append((CanonicalArena)fan.State!, writer._nodes[item], Start, fan.Value);
            }

            if (Closes)
            {
                column.ClosePage();
            }
        }
    }

    /// <summary>
    /// The file's key-value metadata: the Vortex schema it is written from, from which a reader of this
    /// package restores what Parquet cannot say, then the caller's pairs, by key.
    /// </summary>
    private static KeyValuePair<string, string>[] KeyValues(DType dtype, ParquetWriteOptions options)
    {
        List<KeyValuePair<string, string>> pairs = [new(ParquetSchema.VortexSchemaKey, Convert.ToBase64String(DTypeFlatBuffers.Serialize(dtype)))];
        if (options.KeyValueMetadata is { } caller)
        {
            string[] keys = [.. caller.Keys];
            Array.Sort(keys, StringComparer.Ordinal);
            foreach (string key in keys)
            {
                pairs.Add(new(key, caller[key]));
            }
        }

        return [.. pairs];
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
            await IngestAsync(arena, root.BuildRoot(arena, root.Committed), cancellationToken).ConfigureAwait(false);
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
    /// Stages the rows of <paramref name="root"/>, a struct of the file's columns or the one column of
    /// a file whose root is not a struct, a block at a time across the columns, so that a row group
    /// closes on the block that fills it.
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
            // A nested column is given its top-level field, which it shreds down to its leaf.
            int node = EncodedForms.Canonical(arena, _isTabular ? arena.GetNode(root).GetFieldIndex(column.Field) : root);
            if (column.ThroughStorage && !column.Nested)
            {
                node = EncodedForms.Canonical(arena, arena.GetNode(node).StorageIndex);
            }

            if (column.FixedElements && !column.Nested)
            {
                // Its elements laid out here, once, for the column to find them so when it stages
                // beside the others, which only read the arena.
                EncodedForms.Canonical(arena, arena.GetNode(node).ElementsIndex);
            }

            _nodes[c] = node;
        }

        // Rows out of the declared order are refused before any column holds them.
        _sorted?.Check(arena, _nodes, rows);

        // Until every column holds the batch's rows, the columns disagree: a failure past here
        // leaves the file to be abandoned.
        _broken = true;
        int blockRows = _options.BlockRows;
        for (int start = 0; start < rows;)
        {
            int take = Math.Min(rows - start, blockRows - (int)(_groupRows % blockRows));
            bool closes = (_groupRows + take) % blockRows == 0;
            Stage(arena, start, take, closes);
            start += take;
            _groupRows += take;
            _rowCount += take;
            if (!closes)
            {
                continue;
            }

            // The block is whole in every column, its pages closed.
            if (_groupRows >= _options.RowGroupRows || Filled())
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
            await column.LendChunkAsync(_sink, cancellationToken).ConfigureAwait(false);
            chunks[c] = new WrittenChunk { Column = column.Column, Chunk = chunk, Codec = column.Codec, Encryptor = column.Encryptor };
        }

        // The row group's Bloom filters after its chunks, the standard's other place for them: a
        // writer holds none past its row group.
        for (int c = 0; c < _columns.Length; c++)
        {
            if (_columns[c].HasBloom)
            {
                chunks[c].BloomFilterOffset = _sink.Position;
                chunks[c].BloomFilterLength = await _columns[c].WriteBloomAsync(_sink, cancellationToken).ConfigureAwait(false);
            }
        }

        // The chunks were lent where they lie: over a file, this flush writes them in one gathered write,
        // and only then do the columns take their buffers back.
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        foreach (ColumnChunkWriter column in _columns)
        {
            column.Reset();
        }

        _rowGroups.Add(new WrittenRowGroup { Chunks = chunks, Rows = rows, Ordinal = _rowGroups.Count });

        // The next row group's pages name its ordinal in their modules' AAD.
        foreach (ColumnChunkWriter column in _columns)
        {
            column.RowGroup = _rowGroups.Count;
        }
        _groupRows -= rows;
    }

    /// <summary>Writes the leading magic, once, before the first row group or the footer.</summary>
    private ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return ValueTask.CompletedTask;
        }

        _started = true;
        return _sink.WriteAsync(_encryptor is { PlaintextFooter: false } ? EncryptedMagic : Magic, cancellationToken);
    }

    private async ValueTask WriteTailAsync(CancellationToken cancellationToken)
    {
        using PooledBytes tail = new(_session.Options.EnginePool);
        WriteTail(tail, _sink.Position);
        await _sink.WriteAsync(tail.Written, cancellationToken).ConfigureAwait(false);
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The file's end, starting at <paramref name="position"/>: every column chunk's column index,
    /// then every offset index, then the footer, its length and the magic.
    /// </summary>
    private void WriteTail(PooledBytes tail, long position)
    {
        // Every column index, then every offset index, by row group then column, so that a reader's
        // reads of each coalesce.
        ThriftCompactWriter writer = new(tail);
        using PooledBytes plain = new(_session.Options.EnginePool);
        foreach (WrittenRowGroup rowGroup in _rowGroups)
        {
            foreach (WrittenChunk chunk in rowGroup.Chunks)
            {
                WrittenStatistics statistics = chunk.Chunk.Statistics;
                if (!statistics.Indexed)
                {
                    continue;
                }

                int start = tail.Length;
                if (chunk.Encryptor is { } encryptor)
                {
                    // An encrypted column's index is a module of its own.
                    plain.Clear();
                    ThriftCompactWriter inner = new(plain);
                    ColumnIndex.Write(ref inner, statistics.Pages, statistics.Order, chunk.Chunk.Sizes);
                    inner.Flush();
                    encryptor.Encrypt(plain.Written.Span, Encryption.ModuleType.ColumnIndex, rowGroup.Ordinal, -1, tail);
                }
                else
                {
                    ColumnIndex.Write(ref writer, statistics.Pages, statistics.Order, chunk.Chunk.Sizes);
                    writer.Flush();
                }

                chunk.ColumnIndexOffset = position + start;
                chunk.ColumnIndexLength = tail.Length - start;
            }
        }

        foreach (WrittenRowGroup rowGroup in _rowGroups)
        {
            foreach (WrittenChunk chunk in rowGroup.Chunks)
            {
                int start = tail.Length;
                if (chunk.Encryptor is { } encryptor)
                {
                    plain.Clear();
                    ThriftCompactWriter inner = new(plain);
                    OffsetIndex.Write(ref inner, chunk.Chunk.Pages, chunk.Chunk.Sizes);
                    inner.Flush();
                    encryptor.Encrypt(plain.Written.Span, Encryption.ModuleType.OffsetIndex, rowGroup.Ordinal, -1, tail);
                }
                else
                {
                    OffsetIndex.Write(ref writer, chunk.Chunk.Pages, chunk.Chunk.Sizes);
                    writer.Flush();
                }

                chunk.OffsetIndexOffset = position + start;
                chunk.OffsetIndexLength = tail.Length - start;
            }
        }

        int footer = tail.Length;
        if (_encryptor is { PlaintextFooter: false } sealedFooter)
        {
            // The footer encrypted: its crypto metadata, then its module, both counted by the length.
            plain.Clear();
            ThriftCompactWriter inner = new(plain);
            using PooledBytes scratch = new(_session.Options.EnginePool);
            FooterWriter.WriteFileMetaData(ref inner, _map.Elements, _rowCount, _rowGroups, _keyValues, CreatedBy, _map.Columns, sealedFooter, scratch, _sorted?.Declared);
            inner.Flush();
            sealedFooter.WriteEncryptedFooter(tail, plain.Written.Span);
        }
        else
        {
            using PooledBytes scratch = new(_session.Options.EnginePool);
            FooterWriter.WriteFileMetaData(ref writer, _map.Elements, _rowCount, _rowGroups, _keyValues, CreatedBy, _map.Columns, _encryptor, scratch, _sorted?.Declared);
            writer.Flush();
            if (_encryptor is { } signer)
            {
                // A plaintext footer of an encrypted file: its signature right after it.
                Span<byte> signature = tail.Reserve(Encryption.ModuleCipher.NonceLength + Encryption.ModuleCipher.TagLength);
                signer.Sign(tail.Written.Span[footer..^signature.Length], signature);
            }
        }

        int length = tail.Length - footer;
        Span<byte> end = tail.Reserve(8);
        BinaryPrimitives.WriteInt32LittleEndian(end, length);
        (_encryptor is { PlaintextFooter: false } ? EncryptedMagic : Magic).CopyTo(end[4..]);
    }

    /// <summary>
    /// Whether the row group's pages reach <see cref="ParquetWriteOptions.RowGroupBytes"/>: never while
    /// the most their pages compressing ahead can store keeps them under it, and otherwise as they
    /// are once stored, so that a row group closes on the same block at every degree.
    /// </summary>
    private bool Filled()
    {
        long bound = 0;
        foreach (ColumnChunkWriter column in _columns)
        {
            bound += column.BufferedBound;
        }

        if (bound < _options.RowGroupBytes)
        {
            return false;
        }

        long bytes = 0;
        foreach (ColumnChunkWriter column in _columns)
        {
            column.Settle();
            bytes += column.BufferedBytes;
        }

        return bytes >= _options.RowGroupBytes;
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
        _root ??= ColumnStores.Root(_dtype, _session.Options.EnginePool, _session.Options.Extensions);

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
