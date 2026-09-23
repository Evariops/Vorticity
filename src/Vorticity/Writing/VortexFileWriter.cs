using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using Vorticity.Editions;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// Writes a Vortex file in a single forward pass. Rows arrive through the writer's builder, as
/// records, or as batches; whole blocks are sealed into chunks and encoded on the calling thread,
/// and <see cref="FlushAsync"/> hands the encoded bytes to the sink. The layout, footer and
/// postscript can only be written once every segment's offset is known, so they belong to
/// <see cref="CompleteAsync"/> and the sink never has to seek.
/// </summary>
/// <remarks>
/// A writer is used by one thread at a time. Disposing it without <see cref="CompleteAsync"/>
/// abandons the file, as <see cref="Abandon"/> does.
/// </remarks>
public sealed partial class VortexFileWriter : IAsyncDisposable, IChunkLedger
{
    private readonly ISegmentSink _sink;
    private readonly DType _schema;
    private readonly int _fieldCount;
    private readonly EncodingDictionary _arrayEncodings;
    private readonly EncodingDictionary _layoutEncodings;
    private readonly List<SegmentSpec> _segments = [];

    /// <summary>Per root field, the segment index of each batch's column.</summary>
    private readonly List<int>[] _columnSegments;

    /// <summary>Per root field, what each chunk's values were written as, for the report.</summary>
    private readonly List<string>[] _written;

    /// <summary>Per batch, its row count; every field's chunk list has the same shape.</summary>
    private readonly List<long> _chunkRows = [];

    /// <summary>Per root field, the state machine that summarizes it block by block.</summary>
    private readonly ColumnWriter[] _columns;

    /// <summary>Scratch for one batch's field nodes, so the ingest loop allocates none.</summary>
    private readonly int[] _fieldNodes;

    private readonly bool _isTabular;
    private readonly bool _compress;
    private readonly VortexEdition _target;

    /// <summary>Rows a chunk is a multiple of, or 0 when one call is one chunk.</summary>
    private readonly int _rowBlock;

    /// <summary>
    /// Rows per block, which is the zone length. A block is counted from row 0 of the file and owes
    /// nothing to the caller's batching. Zero means the batch is the block, and the zone map then
    /// needs the uniformity check <see cref="TryZoneLength"/> makes.
    /// </summary>
    private readonly int _blockRows;

    /// <summary>Rows already folded into the open block, below <see cref="_blockRows"/>.</summary>
    private int _blockFilled;

    /// <summary>Blocks the chunks emitted so far have consumed; the next chunk starts here.</summary>
    private int _emittedBlocks;

    /// <summary>
    /// (Column chunk, field) pairs the chooser had to measure itself because the block range handed
    /// to it did not cover them. The fallback is silent, so only this counter can catch it.
    /// </summary>
    internal long ChunksWithoutStatistics => _chunksWithoutStatistics;

    private long _chunksWithoutStatistics;

    /// <summary>
    /// List chunks whose elements the chooser counted itself, because their blocks did not name
    /// exactly the elements the chunk writes — the elements' counterpart of
    /// <see cref="ChunksWithoutStatistics"/>, and silent for the same reason.
    /// </summary>
    internal long ElementChunksWithoutStatistics => _elementChunksWithoutStatistics;

    private long _elementChunksWithoutStatistics;

    private readonly bool _elementStatistics;

    /// <summary>Column <paramref name="field"/>'s ingest state.</summary>
    internal ColumnWriter ColumnState(int field) => _columns[field];

    bool IChunkLedger.ElementsServe => _elementStatistics;

    void IChunkLedger.ElementsUnserved() => _elementChunksWithoutStatistics++;

    /// <summary>Whether the chooser prices each column by its bytes alone, as <see cref="CompressionProfile.Smallest"/> asks.</summary>
    private bool _sizeFirst;

    bool IChunkLedger.SizeFirst => _sizeFirst;

    /// <summary>
    /// (Column chunk, field) pairs of a comparable kind whose distinct table could not answer, so the
    /// chooser walked the chunk to build the dictionary it would otherwise have read off the table.
    /// The walk is byte-identical to the table, so a table that stopped serving would cost a second
    /// pass without changing a byte.
    /// </summary>
    internal long ChunksWithoutTable => _chunksWithoutTable;

    private long _chunksWithoutTable;

    /// <summary>
    /// (Column chunk, field) pairs whose distinct table answered: the dictionary was read off the
    /// table rather than walked for. The positive half of <see cref="ChunksWithoutTable"/>, since a
    /// table that never runs leaves that counter at zero too.
    /// </summary>
    internal long ChunksFromTable => _chunksFromTable;

    private long _chunksFromTable;

    /// <summary>
    /// (Column chunk, field) pairs whose bit-packing was priced from the ingested width histograms
    /// rather than from a walk of the chunk, the columns' children included. The width counterpart
    /// of <see cref="ChunksFromTable"/>.
    /// </summary>
    internal long ChunksFromWidths
    {
        get
        {
            long served = 0;
            for (int column = 0; column < _fieldCount; column++)
            {
                served += _columns[column].WidthsServed;
            }

            return served;
        }
    }

    /// <summary>Canonical bytes to accumulate before emitting, or 0 for no byte threshold.</summary>
    private readonly long _blockBytes;

    /// <summary>Whether the schema holds a list or a map, so a chunk may share a batch's children.</summary>
    private readonly bool _mayShareChildren;

    /// <summary>
    /// The two arenas the accumulation ping-pongs between: a block's remainder is copied into the
    /// other one before the first is reset, since one arena would mean resetting storage the
    /// remainder still views.
    /// </summary>
    private ScanContext?[]? _transit;
    private int _current;
    private readonly List<int> _pending = [];

    /// <summary>Scratch for the nodes a block carries forward; reused so a block allocates none.</summary>
    private readonly List<int> _carry = [];
    private long _pendingRows;
    private long _pendingBytes;
    private int[]? _zoneSegments;
    private byte[][]? _zoneMetadata;
    private long _rowCount;
    private bool _started;

    /// <summary>Whether the writer takes no more rows: completed, completing, or abandoned.</summary>
    private bool _completed;

    /// <summary>Whether the footer went out and the sink accepted it: the file is whole.</summary>
    private bool _finished;
    private bool _abandoned;

    /// <summary>
    /// The path this writer created, or null when the caller brought the sink: what
    /// <see cref="Abandon"/> deletes, since a created file holds nothing but a half-written attempt.
    /// </summary>
    private string? _createdPath;

    /// <summary>The file an append continues, which an abandon truncates back to <see cref="_appendOrigin"/>.</summary>
    private string? _appendedPath;
    private long _appendOrigin;

    /// <summary>The pipe over the file this writer opened, which it completes or abandons.</summary>
    private FilePipeWriter? _filePipe;

    /// <summary>The caller's pipe, which the writer completes when the file is whole and fails when it is abandoned.</summary>
    private System.IO.Pipelines.PipeWriter? _callerPipe;

    /// <summary>Whether the sink has been closed, one way or the other.</summary>
    private bool _sinkClosed;

    /// <summary>The user metadata the postscript carries, in the order it is written.</summary>
    private KeyValuePair<string, ReadOnlyMemory<byte>>[] _metadata = [];
    // Shared, because nothing writes to them and the sink takes memory rather than a span.
    private static readonly byte[] Padding = new byte[VortexLimits.MaxAlignment];
    private static readonly byte[] Magic = VortexFileFormat.MagicBytes.ToArray();
    private readonly bool _fileStatistics;

    /// <summary>The identity the options pinned, or null for a fresh one.</summary>
    private readonly Guid? _identity;
    // Null when the policy asks for nothing, so such a write allocates none of the index machinery.
    private readonly IndexWriter? _indexes;

    /// <summary>What every blob of the file is assembled in, created with the first one.</summary>
    private ArrayBlobWriter.Workspace? _blobs;
    private WriteBytes _reportBytes;

    /// <summary>The sink position the write meter has counted to; an append starts it at the file's length.</summary>
    private long _meteredBytes;

    /// <summary>The write's activity, or null when nothing listens.</summary>
    private System.Diagnostics.Activity? _activity;

    private VortexFileWriter(
        ISegmentSink sink, DType schema, bool compress, VortexEdition target, int rowBlock,
        long blockBytes, bool fileStatistics, WritePolicy indexes, int indexBudgetPerMille, IKeyEncoder? keyEncoder,
        int stringBoundBytes, Guid? identity, string? scratchDirectory, long scratchMemoryBytes, long wideRowsAbove,
        FenceShape fences, bool elementStatistics, IReadOnlyDictionary<string, EncodingHint>? hints)
    {
        VortexRuntimeChecks.Require();
        _sink = sink;
        _meteredBytes = sink.Position;
        _activity = VortexTelemetry.StartWrite();
        _elementStatistics = elementStatistics;
        _schema = schema;
        _identity = identity;
        _compress = compress;
        _fileStatistics = fileStatistics;
        _target = target;
        _rowBlock = rowBlock;
        _blockBytes = blockBytes;
        _mayShareChildren = ChunkCompactor.MayShareChildren(schema);
        _arrayEncodings = new EncodingDictionary(ComponentKind.Array, target);
        _layoutEncodings = new EncodingDictionary(ComponentKind.Layout, target);
        _isTabular = schema.Kind == DTypeKind.Struct;
        _blockRows = rowBlock;

        // A non-struct root is one column whose layout IS the root, with no struct level above it.
        _fieldCount = _isTabular ? schema.FieldCount : 1;
        _columnSegments = new List<int>[Math.Max(_fieldCount, 1)];
        _written = new List<string>[Math.Max(_fieldCount, 1)];
        _columns = new ColumnWriter[Math.Max(_fieldCount, 1)];
        _fieldNodes = new int[Math.Max(_fieldCount, 1)];
        for (int i = 0; i < _columnSegments.Length; i++)
        {
            _columnSegments[i] = [];
            _written[i] = [];
            // The distinct table has a consumer only if the edition can write a dictionary.
            _columns[i] = new ColumnWriter
            {
                EditionAllowsDictionary = ColumnCompressor.Allows(target, "vortex.dict"),
                StringBoundBytes = HasStringBounds(schema, _isTabular, i) ? stringBoundBytes : 0,
            };
        }

        if (hints is not null)
        {
            Pin(hints);
        }

        _indexes = IndexWriter.Asks(indexes)
            ? new IndexWriter(
                indexes, schema, _isTabular, _fieldCount, indexBudgetPerMille, _blockRows, keyEncoder,
                scratchDirectory, scratchMemoryBytes, wideRowsAbove)
            {
                Fences = fences,
            }
            : null;
    }

    /// <summary>
    /// Whether column <paramref name="field"/> is one whose zone map carries string bounds: a utf8 or
    /// binary column, and no other, so that no other column keeps any.
    /// </summary>
    private static bool HasStringBounds(DType schema, bool tabular, int field)
    {
        if (tabular && field >= schema.FieldCount)
        {
            return false;
        }

        return (tabular ? schema.GetField(field) : schema).Kind is DTypeKind.Utf8 or DTypeKind.Binary;
    }

    /// <summary>
    /// The row count a caller should batch in multiples of: such a batch, carrying at least
    /// <see cref="VortexWriteOptions.DataBlockTargetBytes"/> bytes, is written where it lies and
    /// pays no transit copy. Any other batch waits in transit for the rows completing its last
    /// block.
    /// </summary>
    internal int PreferredBatchRows => _rowBlock > 0 ? _rowBlock : 1;

    /// <summary>
    /// Whether any batch has waited in the transit arena, which is what
    /// <see cref="PreferredBatchRows"/> exists to avoid.
    /// </summary>
    internal bool Buffered => _transit is not null;

    /// <summary>
    /// Pins the scheme of every column the caller named, before the first batch, since the ingest
    /// state a scheme reads starts with it.
    /// </summary>
    private void Pin(IReadOnlyDictionary<string, EncodingHint> hints)
    {
        foreach ((string path, EncodingHint hint) in hints)
        {
            if (hint == EncodingHint.Auto)
            {
                continue;
            }

            if (!TryDescend(path, out ColumnWriter? column))
            {
                throw new ArgumentException(
                    $"The encoding hint '{path}' names no column of this schema.", nameof(hints));
            }

            column!.Pin(hint switch
            {
                EncodingHint.Canonical => ColumnScheme.None,
                EncodingHint.RunEnd => ColumnScheme.RunEnd,
                EncodingHint.Dictionary => ColumnScheme.Dict,
                EncodingHint.BitPacked => ColumnScheme.BitPacked,
                EncodingHint.Fsst => ColumnScheme.Fsst,
                EncodingHint.Alp => ColumnScheme.Alp,
                EncodingHint.Sequence => ColumnScheme.Sequence,
                EncodingHint.Zstd => ColumnScheme.Zstd,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(hints), hint, "not a defined encoding hint"),
            });
        }
    }

    /// <summary>
    /// The column state a path names, creating the nodes under it: a top-level column, a
    /// <c>.</c>-separated path through structs and the extensions around them, or the empty path
    /// for the one column of a file whose root is not a struct.
    /// </summary>
    private bool TryDescend(string path, out ColumnWriter? column)
    {
        column = null;
        if (!_isTabular)
        {
            if (path.Length > 0)
            {
                return false;
            }

            column = _columns[0];
            return true;
        }

        // A top-level name that holds a dot is that column, as the scan reads it.
        int top = _schema.IndexOfField(path);
        if (top >= 0)
        {
            column = _columns[top];
            return true;
        }

        string[] names = path.Split('.');
        int first = _schema.IndexOfField(names[0]);
        if (first < 0)
        {
            return false;
        }

        ColumnWriter writer = _columns[first];
        DType dtype = _schema.GetField(first);
        for (int i = 1; i < names.Length; i++)
        {
            // An extension is a node of its own in the tree, with the storage as its only child.
            while (dtype.Kind == DTypeKind.Extension)
            {
                writer = writer.Descend(0, 1);
                dtype = dtype.StorageType;
            }

            if (dtype.Kind != DTypeKind.Struct)
            {
                return false;
            }

            int field = dtype.IndexOfField(names[i]);
            if (field < 0)
            {
                return false;
            }

            writer = writer.Descend(field, dtype.FieldCount);
            dtype = dtype.GetField(field);
        }

        column = writer;
        return true;
    }

    /// <summary>The schema every batch must match.</summary>
    internal DType DType => _schema;

    /// <summary>Starts a file over <paramref name="sink"/>.</summary>
    /// <param name="sink">Where the bytes go.</param>
    /// <param name="schema">The file's dtype. A struct makes it tabular; anything else is one column.</param>
    /// <returns>The writer. The caller completes and disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> has not been set.</exception>
    internal static VortexFileWriter Create(ISegmentSink sink, DType schema) =>
        Create(sink, schema, VortexWriteOptions.Default);

    /// <summary>Starts a file over <paramref name="sink"/> with explicit options.</summary>
    /// <param name="sink">Where the bytes go.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="options">Write-time policy.</param>
    /// <param name="session">The session whose registered extensions the schema may name beyond the target edition; none when null.</param>
    /// <returns>The writer. The caller completes and disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> has not been set.</exception>
    internal static VortexFileWriter Create(ISegmentSink sink, DType schema, VortexWriteOptions options, VortexSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Validate(schema, options, session?.Options.Extensions);

        int rowBlock = options.RowBlockSize ?? 0;
        if (rowBlock < 0)
        {
            throw new ArgumentException(
                "RowBlockSize must be positive, or null for one chunk per WriteAsync.",
                nameof(options));
        }

        long blockBytes = options.DataBlockTargetBytes ?? 0;
        if (blockBytes < 0)
        {
            throw new ArgumentException(
                "DataBlockTargetBytes must be positive, or null to disable byte coalescing.",
                nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.WritePolicy, nameof(options));
        WritePolicy indexes = options.Profile == WriteProfile.Fastest ? WritePolicy.None : options.WritePolicy;
        ArgumentOutOfRangeException.ThrowIfNegative(options.IndexBudgetPerMille, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.StringBoundBytes, nameof(options));
        return new VortexFileWriter(
            sink, schema, options.Compress, options.TargetEdition, rowBlock, blockBytes,
            options.FileStatistics, indexes, options.IndexBudgetPerMille, options.KeyEncoder,
            options.StringBoundBytes, options.Identity, options.ScratchDirectory, options.ScratchMemoryBytes,
            options.WideRowsAbove, options.Fences, options.ElementStatistics, options.EncodingHints)
        {
            _sizeFirst = options.Compression == CompressionProfile.Smallest,
            _metadata = UserMetadata.Ordered(options.Metadata),
        };
    }

    /// <summary>
    /// Refuses options that cannot describe a file of <paramref name="schema"/>, before anything is
    /// opened or written: a component the target edition lacks, a hint or an index naming no
    /// column, metadata the postscript cannot carry.
    /// </summary>
    internal static void Validate(DType schema, VortexWriteOptions options, VortexExtensionRegistry? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (schema.IsDefault)
        {
            throw new ArgumentException("The schema has not been set.", nameof(schema));
        }

        RequireSchemaInTarget(schema, options.TargetEdition, extensions);
        bool tabular = schema.Kind == DTypeKind.Struct;
        // The entries rather than Keys, whose enumerator is an allocated iterator.
        foreach (KeyValuePair<string, EncodingHint> hint in options.Hints)
        {
            if (!Names(schema, tabular, hint.Key))
            {
                throw new ArgumentException($"The encoding hint '{hint.Key}' names no column of the schema {schema}.", nameof(options));
            }
        }

        foreach (string path in options.Indexes.Paths)
        {
            if (!Names(schema, tabular, path))
            {
                throw new ArgumentException($"The index policy names '{path}', which is no column of the schema {schema}.", nameof(options));
            }
        }

        UserMetadata.Validate(options.Metadata);
    }

    /// <summary>Whether <paramref name="path"/> is a column: a top-level name, a dotted path through structs, or the empty path of a file whose root is not a struct.</summary>
    internal static bool Names(DType schema, bool tabular, string path) =>
        tabular ? IndexWriter.TryResolve(schema, tabular, path, out _, out _, out _) : path.Length == 0;

    /// <summary>
    /// Rejects a schema naming an extension dtype the target edition does not carry, unless the
    /// writer's session registers it: a registered extension lies outside every edition by design,
    /// and a reader needs the same registration to read it as more than its storage.
    /// </summary>
    private static void RequireSchemaInTarget(DType dtype, VortexEdition target, VortexExtensionRegistry? extensions)
    {
        if (dtype.Kind == DTypeKind.Extension)
        {
            string id = dtype.ExtensionId;
            if (!EditionRegistry.Contains(target, ComponentKind.DType, id) && extensions?.IsRegistered(dtype.ExtensionIdUtf8) != true)
            {
                VortexEdition? introduced = EditionRegistry.IntroducedIn(ComponentKind.DType, id);
                throw new VortexUnsupportedException(
                    id,
                    VortexComponentKind.DType,
                    introduced is null
                        ? "No core edition contains it, so no target can emit it."
                        : $"The write targets edition {EditionRegistry.Name(target)}, which does not " +
                          $"contain it; it was introduced in {EditionRegistry.Name(introduced.Value)}.");
            }
        }

        for (int i = 0; i < dtype.FieldCount; i++)
        {
            RequireSchemaInTarget(dtype.GetField(i), target, extensions);
        }

        if (dtype.Kind is DTypeKind.List or DTypeKind.FixedSizeList)
        {
            RequireSchemaInTarget(dtype.ElementType, target, extensions);
        }
        else if (dtype.Kind == DTypeKind.Extension)
        {
            RequireSchemaInTarget(dtype.StorageType, target, extensions);
        }
    }

    /// <summary>Creates a file at <paramref name="path"/>.</summary>
    /// <param name="path">The destination path; truncated if it exists.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <returns>The writer, which owns the underlying stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal static VortexFileWriter Create(string path, DType schema) =>
        Create(path, schema, VortexWriteOptions.Default);

    /// <summary>Creates a file at <paramref name="path"/> with explicit options.</summary>
    /// <param name="path">The destination path; truncated if it exists.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="options">Write-time policy.</param>
    /// <returns>The writer, which owns the underlying stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal static VortexFileWriter Create(string path, DType schema, VortexWriteOptions options) =>
        Create(path, schema, options, VortexSession.Default);

    /// <summary>A writer at <paramref name="path"/>, in <paramref name="session"/>: a pipe over the file's handle, whose flush is the only I/O.</summary>
    /// <remarks>The options are checked before the file is opened, so a refused writer leaves whatever was at the path.</remarks>
    internal static VortexFileWriter Create(string path, DType schema, VortexWriteOptions options, VortexSession session)
    {
        ArgumentNullException.ThrowIfNull(path);
        Validate(schema, options, session.Options.Extensions);
        FilePipeWriter pipe = FilePipeWriter.Create(path, session.Options.MemoryPool);
        try
        {
            VortexFileWriter writer = Create(new PipeSegmentSink(pipe), schema, options, session);
            writer._createdPath = path;
            writer._filePipe = pipe;
            writer.Session = session;
            return writer;
        }
        catch
        {
            pipe.Abandon();
            System.IO.File.Delete(path);
            throw;
        }
    }

    /// <summary>A writer over <paramref name="sink"/>, in <paramref name="session"/>; the writer completes the pipe.</summary>
    internal static VortexFileWriter Create(System.IO.Pipelines.PipeWriter sink, DType schema, VortexWriteOptions options, VortexSession session)
    {
        VortexFileWriter writer = Create(new PipeSegmentSink(sink), schema, options, session);
        writer._callerPipe = sink;
        writer.Session = session;
        return writer;
    }

    /// <summary>The session the writer belongs to.</summary>
    internal VortexSession Session { get; private set; } = VortexSession.Default;

    /// <summary>An append to <paramref name="path"/>, in <paramref name="session"/>.</summary>
    internal static async ValueTask<VortexFileWriter> AppendInSessionAsync(
        string path, VortexWriteOptions? options, VortexSession session, CancellationToken cancellationToken)
    {
        VortexFileWriter writer = await AppendAsync(path, options, null, session, cancellationToken).ConfigureAwait(false);
        writer.Session = session;
        return writer;
    }

    /// <summary>
    /// Takes the rows of node <paramref name="root"/> into the file: whole blocks are written where
    /// they lie when nothing is pending, anything else waits in the transit arena for the rows that
    /// complete its last block.
    /// </summary>
    /// <param name="arena">The arena holding the rows; the caller keeps it, and may reset it once this returns.</param>
    /// <param name="root">The rows' node. Its dtype must be the file's.</param>
    /// <param name="seal">
    /// Whether whole blocks go out now whatever their bytes: a flush, or the writer's own builder,
    /// whose buffers are only borrowed for the call.
    /// </param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the rows' segments are with the sink.</returns>
    private async ValueTask WriteCoreAsync(CanonicalArena arena, int root, bool seal, CancellationToken cancellationToken)
    {
        int rows = arena.GetNode(root).Length;
        if (rows == 0)
        {
            // An empty chunk is legal but pointless, and it would make the chunk lists of the
            // columns disagree with a reader's expectation of non-degenerate children.
            return;
        }

        RequireMatchingSchema(arena.GetNode(root).DType);

        // Rows delivered encoded are decoded here, once, so that the statistics, the distinct
        // tables, the indexes and the blob writer behind this line only ever meet canonical nodes.
        root = arena.DecodedTree(root);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        // The statistics pass runs before any copy or emission: the decode that produced the batch
        // has just touched every byte, and a row's block does not depend on where the row ends up.
        Ingest(arena, root);

        if (_rowBlock == 0)
        {
            // One call, one chunk.
            await EmitChunkAsync(arena, root, rows, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Nothing pending and already big enough: write it where it lies, with no transit copy.
        if (_pending.Count == 0 &&
            rows >= _rowBlock &&
            (seal || arena.ByteSize(root) >= _blockBytes) &&
            rows % _rowBlock == 0)
        {
            await EmitChunkAsync(arena, root, rows, cancellationToken).ConfigureAwait(false);
            return;
        }

        Hold(arena, root);
        while (_pendingRows >= _rowBlock && (seal || _pendingBytes >= _blockBytes))
        {
            await EmitBlockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies the rows of node <paramref name="root"/> into the transit arena, where they wait for their chunk.</summary>
    private void Hold(CanonicalArena arena, int root)
    {
        // Materialized, not borrowed: the batch's arena is the scan's and is reset as soon as the
        // caller asks for the next batch, so held rows must own their bytes. Narrowed before it is
        // owned, since a batch cut from a large list chunk shares that chunk's elements whole and
        // copying it as it comes would materialize the whole child; referencing then compacting
        // works on views, so the copy pays for the window alone. Narrowing here also keeps the
        // concatenation tight, because rebasing uses each chunk's whole child length.
        CanonicalArena transit = Transit();
        _pending.Add(_mayShareChildren
            ? transit.CopyFrom(transit, ChunkCompactor.Compact(transit, transit.ReferenceFrom(arena, root)))
            : transit.CopyFrom(arena, root));
        _pendingRows += arena.GetNode(root).Length;
        _pendingBytes += transit.ByteSize(_pending[^1]);
    }

    /// <summary>Seals the whole blocks waiting in transit into a chunk, whatever their bytes.</summary>
    private async ValueTask SealAsync(CancellationToken cancellationToken)
    {
        if (_rowBlock > 0 && _pendingRows >= _rowBlock)
        {
            await EmitBlockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Folds one batch into the open block of every column, closing blocks as it crosses them: a
    /// batch that straddles a boundary is cut there and its leftover continues the next block.
    /// </summary>
    private void Ingest(CanonicalArena arena, int rootIndex)
    {
        CanonicalNode root = arena.GetNode(rootIndex);
        if (_isTabular)
        {
            for (int field = 0; field < _fieldCount; field++)
            {
                _fieldNodes[field] = root.GetFieldIndex(field);
            }
        }
        else
        {
            _fieldNodes[0] = rootIndex;
        }

        int rows = root.Length;
        if (_blockRows == 0)
        {
            // The batch is the block: repartitioning is off, so batches are the pruning unit.
            for (int field = 0; field < _fieldCount; field++)
            {
                _columns[field].Accumulate(arena, _fieldNodes[field], 0, rows);
            }

            FeedIndexes(arena, 0, rows);
            CloseBlock();
            return;
        }

        int offset = 0;
        while (offset < rows)
        {
            int take = Math.Min(_blockRows - _blockFilled, rows - offset);
            for (int field = 0; field < _fieldCount; field++)
            {
                _columns[field].Accumulate(arena, _fieldNodes[field], offset, take);
            }

            FeedIndexes(arena, offset, take);
            offset += take;
            _blockFilled += take;
            if (_blockFilled == _blockRows)
            {
                CloseBlock();
            }
        }
    }

    /// <summary>
    /// Feeds the rows of <paramref name="rootIndex"/> to every column's distinct table only, with
    /// the field resolution <see cref="Ingest"/> makes.
    /// </summary>
    private void Reprobe(CanonicalArena arena, int rootIndex)
    {
        CanonicalNode root = arena.GetNode(rootIndex);
        int rows = root.Length;
        for (int field = 0; field < _fieldCount; field++)
        {
            int node = _isTabular ? root.GetFieldIndex(field) : rootIndex;
            _columns[field].Reprobe(arena, node, 0, rows);
        }
    }

    /// <summary>Hands the same rows to the index builders, right after the statistics pass.</summary>
    private void FeedIndexes(CanonicalArena arena, int start, int count)
    {
        if (_indexes is not { Streams: true } indexes)
        {
            return;
        }

        for (int field = 0; field < _fieldCount; field++)
        {
            indexes.Accumulate(field, arena, _fieldNodes[field], start, count);
        }

        indexes.AccumulateKeys(arena, _fieldNodes, start, count);
        indexes.AccumulateNested(arena, _fieldNodes, start, count);
    }

    /// <summary>Seals the open block of every column.</summary>
    private void CloseBlock()
    {
        for (int field = 0; field < _fieldCount; field++)
        {
            _columns[field].CloseBlock();
        }

        _indexes?.CloseBlock(_columns);
        _blockFilled = 0;
    }

    /// <summary>
    /// Writes every index payload the builders have closed, between data chunks, as regions no
    /// layout references and no footer lists.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="judged">
    /// Whether the budget has already ruled on these payloads. True only on the completion path,
    /// which judges over the whole file before it flushes; between chunks the budget is asked here.
    /// </param>
    private async ValueTask FlushIndexesAsync(CancellationToken cancellationToken, bool judged = false)
    {
        if (_indexes is not { HasPending: true } indexes)
        {
            return;
        }

        // Between chunks the budget is asked first, and holds the payloads back when it cannot yet
        // answer. The completion path has already judged by the time it flushes, so it says so.
        if (!judged && !indexes.TryOpenFlush(_sink.Position))
        {
            return;
        }

        while (indexes.TryTakePayload(Blobs, _arrayEncodings, out ArrayBlobWriter.BlobLease blob, out PendingPayload? payload))
        {
            using (blob)
            {
                long before = _sink.Position;
                long aligned = await PadAsync(cancellationToken).ConfigureAwait(false);
                await _sink.WriteAsync(blob.Memory, cancellationToken).ConfigureAwait(false);
                IndexSegment segment = IndexSegment.Of(
                    aligned, blob.Memory.Span, (byte)VortexLimits.MaxAlignmentExponent);
                indexes.Placed(payload!, segment, _sink.Position - before, _sink.Position);
            }
        }
    }

    /// <summary>
    /// The workspace every blob is assembled in, one after the other: a column's chunk, a zone map,
    /// an index payload.
    /// </summary>
    private ArrayBlobWriter.Workspace Blobs => _blobs ??= new ArrayBlobWriter.Workspace { FrameRows = _rowBlock };

    /// <summary>The arena the pending rows live in, created on first use.</summary>
    private CanonicalArena Transit()
    {
        // A detached scan context: the writer needs the arena and decode plumbing CanonicalConcat
        // and CanonicalSlice take, and has no file to scan. The second one is taken only when a
        // block actually splits.
        _transit ??= new ScanContext?[2];
        return (_transit[_current] ??= TransitContexts.Rent()).Canonical;
    }

    /// <summary>
    /// Emits whole multiples of <see cref="_rowBlock"/> rows and carries the remainder forward.
    /// </summary>
    private async ValueTask EmitBlockAsync(CancellationToken cancellationToken)
    {
        ScanContext from = _transit![_current]!;
        long blocks = _pendingRows / _rowBlock;
        int emit = checked((int)(blocks * _rowBlock));
        int total = checked((int)_pendingRows);

        if (emit == total)
        {
            int all = _pending.Count == 1
                ? _pending[0]
                : CanonicalConcat.Concat(
                    from.Decode, _schema, total,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending));
            await EmitChunkAsync(from.Canonical, all, emit, cancellationToken).ConfigureAwait(false);
            ResetTransit();
            return;
        }

        // What is carried is cut from one batch, never from the concatenation: a slice keeps the
        // whole of what it was cut from for the forms that share storage, so carrying a slice of
        // the concatenated block would drag every byte already emitted and grow without bound. The
        // boundary falls inside at most one pending batch; everything before it goes out whole,
        // that one is cut in two, and the carry is its tail plus the whole batches that follow.
        int consumed = 0;
        int split = 0;
        while (split < _pending.Count)
        {
            int rows = from.Canonical.GetNode(_pending[split]).Length;
            if (consumed + rows > emit)
            {
                break;
            }

            consumed += rows;
            split++;
        }

        int straddleTail = -1;
        int going = split;
        if (consumed < emit)
        {
            int straddle = _pending[split];
            int take = emit - consumed;
            int rows = from.Canonical.GetNode(straddle).Length;
            _pending[split] = CanonicalSlice.Slice(from.Decode, straddle, 0, take);
            straddleTail = CanonicalSlice.Slice(from.Decode, straddle, take, rows - take);
            going = split + 1;
        }

        int head = going == 1
            ? _pending[0]
            : CanonicalConcat.Concat(
                from.Decode, _schema, emit,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending)[..going]);
        await EmitChunkAsync(from.Canonical, head, emit, cancellationToken).ConfigureAwait(false);

        // The remainder moves to the other arena before this one is reset, because a slice is a
        // view onto the storage the reset would hand back.
        ScanContext to = _transit[_current ^ 1] ??= TransitContexts.Rent();
        _carry.Clear();
        if (straddleTail >= 0)
        {
            _carry.Add(to.Canonical.CopyFrom(from.Canonical, straddleTail));
        }

        for (int i = going; i < _pending.Count; i++)
        {
            _carry.Add(to.Canonical.CopyFrom(from.Canonical, _pending[i]));
        }

        long carriedBytes = 0;
        for (int i = 0; i < _carry.Count; i++)
        {
            carriedBytes += to.Canonical.ByteSize(_carry[i]);
        }

        // The tail is probed again, into the tables the emission just reset: these rows were probed
        // under the chunk that has gone out, and in the chunk they open they are new. Only the
        // tables see them again; their statistics belong to the still-open block.
        for (int i = 0; i < _carry.Count; i++)
        {
            Reprobe(to.Canonical, _carry[i]);
        }

        from.Canonical.Reset();
        _pending.Clear();
        _pending.AddRange(_carry);
        _pendingRows = total - emit;
        _pendingBytes = carriedBytes;
        _current ^= 1;
    }

    /// <summary>Drops every pending row and frees the transit arena's storage.</summary>
    private void ResetTransit()
    {
        _transit![_current]!.Canonical.Reset();
        _pending.Clear();
        _pendingRows = 0;
        _pendingBytes = 0;
    }

    /// <summary>
    /// How many blocks a chunk of <paramref name="rows"/> rows covers, rounding up because the
    /// file's last chunk may end inside a block. The index builders replay chunk sizes through this
    /// same rule.
    /// </summary>
    /// <param name="rows">The chunk's rows.</param>
    /// <param name="blockRows">Rows per block, or 0 with repartitioning off.</param>
    internal static int ChunkBlocks(long rows, int blockRows) =>
        blockRows > 0 ? checked((int)((rows + blockRows - 1) / blockRows)) : 1;

    /// <summary>Writes one chunk of every column from <paramref name="arena"/>.</summary>
    private async ValueTask EmitChunkAsync(
        CanonicalArena arena, int rootIndex, long rows, CancellationToken cancellationToken)
    {
        int blocks = ChunkBlocks(rows, _blockRows);

        for (int field = 0; field < _fieldCount; field++)
        {
            int node = _isTabular ? arena.GetNode(rootIndex).GetFieldIndex(field) : rootIndex;


            // A chunk cut from a batch shares that batch's list elements whole, and what the blob
            // writer is handed is what lands in the file: the calls below must see the narrowed
            // node, or the zone map would summarise rows the segment has dropped.
            if (_mayShareChildren)
            {
                node = ChunkCompactor.Compact(arena, node);
            }

            // The chunk's statistics, computed at ingest over exactly these rows. The chooser
            // checks the row count against the node it is given and measures the column again when
            // they disagree, so a mismatch costs a pass and never a wrong plan.
            ChunkStats stats = new ChunkStats(_columns[field], _emittedBlocks, blocks, this);
            if (stats.Stats.Rows != rows)
            {
                _chunksWithoutStatistics++;
            }

            // A table plan memory turned off was never expected to serve, and is not a fallback;
            // a table that was running and cannot answer is.
            if (DistinctTable.Serves(arena.GetNode(node).Kind) && stats.TableExpected)
            {
                if (stats.TableServes(checked((int)rows)))
                {
                    _chunksFromTable++;
                }
                else
                {
                    _chunksWithoutTable++;
                }
            }

            using ArrayBlobWriter.BlobLease blob =
                ArrayBlobWriter.Write(Blobs, arena, node, _arrayEncodings, _compress, stats);
            _written[field].Add(WrittenAs.Of(blob.Memory.Span, _arrayEncodings.Ids, _isTabular ? _schema.GetField(field) : _schema));
            int segment = await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false);
            _columnSegments[field].Add(segment);
            _indexes?.AddColumnBytes(field, _segments[segment].Length);

            // The compact summaries stay -- the zone map wants them at completion -- and only the
            // sized buffers go back to the pool.
            _columns[field].ReleaseChunk(_emittedBlocks, blocks);
        }

        // The chunk's locating runs close with it and go out right behind it, and the builders are
        // judged before anything is written, so one `Auto` gives up on leaves no dead weight.
        _indexes?.CloseChunk(_emittedBlocks, blocks, _rowCount, rows);
        _indexes?.Judge();
        _emittedBlocks += blocks;
        _chunkRows.Add(rows);
        _rowCount += rows;
        await FlushIndexesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the last chunk, then the zone maps, statistics, indexes, layout, footer, postscript
    /// and EOF marker, and finishes the file.
    /// </summary>
    /// <param name="tailArena">The arena of the last rows, which the caller keeps; null when there are none.</param>
    /// <param name="tailRoot">
    /// The last rows' node: written where they lie when nothing waits in transit, behind the
    /// waiting rows otherwise.
    /// </param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="VortexException">An index the policy marked required was not built.</exception>
    private async ValueTask CompleteCoreAsync(CanonicalArena? tailArena, int tailRoot, CancellationToken cancellationToken)
    {
        // A file with no batches still has to start with the magic.
        await StartAsync(cancellationToken).ConfigureAwait(false);

        bool direct = false;
        int tailRows = tailArena is null ? 0 : tailArena.GetNode(tailRoot).Length;
        if (tailRows > 0)
        {
            RequireMatchingSchema(tailArena!.GetNode(tailRoot).DType);
            Ingest(tailArena, tailRoot);
            direct = _pending.Count == 0;
            if (!direct)
            {
                Hold(tailArena, tailRoot);
            }
        }

        // The trailing block is closed before the last chunk goes out: that chunk covers this
        // block, and emission reads the closed blocks for the chooser's statistics. Closing it here
        // rather than at ingest is also what makes a short last zone mean the file ended, never the
        // batch ended.
        if (_blockFilled > 0)
        {
            CloseBlock();
        }

        // The last chunk goes out whatever its size: the row-block multiple and the byte target are
        // conditions on the chunks before the last one.
        if (direct)
        {
            await EmitChunkAsync(tailArena!, tailRoot, tailRows, cancellationToken).ConfigureAwait(false);
        }
        else if (_pendingRows > 0)
        {
            ScanContext from = _transit![_current]!;
            int total = checked((int)_pendingRows);
            int whole = _pending.Count == 1
                ? _pending[0]
                : CanonicalConcat.Concat(
                    from.Decode, _schema, total,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending));
            await EmitChunkAsync(from.Canonical, whole, total, cancellationToken).ConfigureAwait(false);
            ResetTransit();
        }

        // The last runs, after the last data segment and before the zone maps: the generation the
        // end of the data left open, and the file-level filter.
        long dataEnd = _sink.Position;
        long interleaved = _indexes?.FileBytes ?? 0;
        _indexes?.EndOfData();
        _indexes?.Judge();
        _indexes?.SettleBudget(dataEnd - interleaved);
        await FlushIndexesAsync(cancellationToken, judged: true).ConfigureAwait(false);
        _indexes?.Close(_columns, _chunkRows, _blockRows, dataEnd - interleaved);

        // Before any byte of the tail: a file missing an index its policy required is not completed.
        if (_indexes?.MissingRequired() is { } missing)
        {
            throw new VortexException(
                $"The {missing.Kind} index on '{missing.Column}' is required and was not built: {missing.Reason}. The file is not completed.");
        }

        // A long run's fence pages, which name the regions just written.
        if (_indexes is { } closed)
        {
            await closed.WriteFencePagesAsync(_sink, cancellationToken).ConfigureAwait(false);
        }

        long indexStart = _sink.Position;

        // The zones arrays are segments like any other, so they precede the footer recording them.
        await WriteZoneMapsAsync(cancellationToken).ConfigureAwait(false);
        long zoneMapsEnd = _sink.Position;

        // The file statistics: the merge of every closed block per top-level field.
        long statisticsOffset = 0;
        int statisticsLength = 0;
        if (_fileStatistics)
        {
            statisticsOffset = _sink.Position;
            byte[] statistics = BuildFileStatistics();
            statisticsLength = statistics.Length;
            await _sink.WriteAsync(statistics, cancellationToken).ConfigureAwait(false);
        }

        // The index directory, last before the footer: one postscript metadata entry, the only
        // carrier a strict reference reader tolerates without being told about it.
        long directoryOffset = _sink.Position;
        byte[]? directory = _indexes?.Directory(_rowCount);
        if (directory is not null)
        {
            await _sink.WriteAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        long dtypeOffset = _sink.Position;
        byte[] dtype = DTypeFlatBuffers.Serialize(_schema);
        await _sink.WriteAsync(dtype, cancellationToken).ConfigureAwait(false);

        long layoutOffset = _sink.Position;
        byte[] layout = BuildLayout();
        await _sink.WriteAsync(layout, cancellationToken).ConfigureAwait(false);

        long footerOffset = _sink.Position;
        byte[] footer = BuildFooter();
        await _sink.WriteAsync(footer, cancellationToken).ConfigureAwait(false);

        await WriteEndAsync(
            _sink,
            new PostscriptPlacement(
                dtypeOffset, dtype.Length, layoutOffset, layout.Length, footerOffset, footer.Length,
                statisticsOffset, statisticsLength, directoryOffset, directory?.Length ?? 0),
            _identity,
            _metadata,
            cancellationToken).ConfigureAwait(false);

        await FlushSinkAsync(cancellationToken).ConfigureAwait(false);

        // Runs written between chunks sit inside `dataEnd`, and are moved to the index count.
        long data = dataEnd - interleaved;
        long zoneMaps = zoneMapsEnd - indexStart;
        long indexBytes = interleaved + (indexStart - dataEnd) + (directory?.Length ?? 0);
        long footerBytes = _sink.Position - dtypeOffset;
        _reportBytes = new WriteBytes(
            Total: data + zoneMaps + statisticsLength + indexBytes + footerBytes,
            Data: data,
            Statistics: statisticsLength,
            ZoneMaps: zoneMaps,
            Indexes: indexBytes,
            Footer: footerBytes);
    }

    /// <summary>What the completed file holds, from the writer's own accounts.</summary>
    /// <remarks>
    /// Each array is filled whole and nothing else holds it, so the report takes it as it is: an
    /// immutable array over it costs no copy and no builder.
    /// </remarks>
    private WriteReport BuildReport()
    {
        int[] chunks = new int[_chunkRows.Count];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = checked((int)_chunkRows[i]);
        }

        ImmutableArray<IndexWriteReport> indexes = _indexes is { } writer
            ? [.. writer.Reports]
            : ImmutableArray<IndexWriteReport>.Empty;
        return new WriteReport(
            _rowCount, _blockRows, ImmutableCollectionsMarshal.AsImmutableArray(chunks), _reportBytes, ReportColumns(), indexes);
    }

    private ImmutableArray<ColumnWriteReport> ReportColumns()
    {
        ColumnWriteReport[] reports = new ColumnWriteReport[_fieldCount];
        for (int field = 0; field < _fieldCount; field++)
        {
            ColumnWriter column = _columns[field];
            reports[field] = new ColumnWriteReport(_isTabular ? _schema.GetFieldName(field) : string.Empty, [.. _written[field]])
            {
                PlansPriced = column.PlansPriced,
                PlansHeld = column.PlansHeld,
            };
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(reports);
    }

    /// <summary>
    /// Builds and writes one zones segment per column, when the chunking allows a zone map at all.
    /// </summary>
    /// <remarks>
    /// A zone map declares one zone length, zone z covering [z * length, (z + 1) * length) with
    /// only the last zone short. Blocks honour that by construction, since they are counted from
    /// row 0 of the file whatever the chunks did; zones therefore need not line up with segments,
    /// and pruning a zone inside a live segment saves decode rather than bytes read.
    /// </remarks>
    private async ValueTask WriteZoneMapsAsync(CancellationToken cancellationToken)
    {
        // An edition without `vortex.zoned` has nowhere to put a zone map. Omitted rather than
        // approximated with the legacy `vortex.stats`, which no reference release emits: pruning is
        // an optimization, so dropping it costs correctness nothing.
        if (!EditionRegistry.Contains(_target, ComponentKind.Layout, "vortex.zoned"))
        {
            return;
        }

        if (!TryZoneLength(out uint zoneLength))
        {
            return;
        }

        _zoneSegments = new int[_fieldCount];
        _zoneMetadata = new byte[_fieldCount][];
        for (int field = 0; field < _fieldCount; field++)
        {
            _zoneSegments[field] = -1;
            DType column = _isTabular ? _schema.GetField(field) : _schema;
            if (_append is { } append && append.NoZoneMap[field])
            {
                // An appended column whose old part had no zones: a map over the new part alone
                // would describe the old rows with invented counts.
                continue;
            }

            if (!ZoneMapWriter.TryBuild(
                    Blobs, column, _columns[field].Blocks, _arrayEncodings, zoneLength,
                    out byte[] metadata, out ArrayBlobWriter.BlobLease blob,
                    _columns[field].StringZones, _columns[field].StringBoundBytes))
            {
                continue;
            }

            using (blob)
            {
                _zoneMetadata[field] = metadata;
                _zoneSegments[field] =
                    await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The one zone length the closed blocks can be described by, if there is one. It can say no
    /// only with repartitioning off, where ragged batches have no single length and a zone map
    /// declaring one anyway would attach every bound to the wrong rows; such a file gets a plain
    /// chunked layout instead.
    /// </summary>
    private bool TryZoneLength(out uint zoneLength)
    {
        zoneLength = 0;
        IReadOnlyList<BlockStats> blocks = _columns[0].Blocks;
        if (blocks.Count == 0)
        {
            return false;
        }

        long first = blocks[0].Rows;
        if (first <= 0 || first > uint.MaxValue)
        {
            return false;
        }

        // Every block but the last must be exactly the zone length; the last may be short.
        for (int i = 0; i < blocks.Count - 1; i++)
        {
            if (blocks[i].Rows != first)
            {
                return false;
            }
        }

        if (blocks[^1].Rows > first)
        {
            return false;
        }

        zoneLength = (uint)first;
        return true;
    }

    /// <summary>Writes the leading <c>VTXF</c> magic, once, before anything else.</summary>
    private async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await _sink.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pads to the format's widest alignment, writes <paramref name="blob"/>, and records where it
    /// landed. The blob's buffers are placed at their own width relative to its start, so the blob
    /// has to start somewhere satisfying the widest of them.
    /// </summary>
    private async ValueTask<int> WriteSegmentAsync(
        ArrayBlobWriter.BlobLease blob, CancellationToken cancellationToken)
    {
        long aligned = await PadAsync(cancellationToken).ConfigureAwait(false);
        await _sink.WriteAsync(blob.Memory, cancellationToken).ConfigureAwait(false);

        // blob.Length, never the rented array's: the rental is longer than the blob.
        _segments.Add(new SegmentSpec(
            (ulong)aligned, (uint)blob.Length, (byte)VortexLimits.MaxAlignmentExponent, 0, 0));
        return _segments.Count - 1;
    }

    /// <summary>Pads the sink to the format's widest alignment and returns where the next byte lands.</summary>
    private async ValueTask<long> PadAsync(CancellationToken cancellationToken)
    {
        long position = _sink.Position;
        long aligned = (position + VortexLimits.MaxAlignment - 1) & ~((long)VortexLimits.MaxAlignment - 1);
        int padding = (int)(aligned - position);
        if (padding > 0)
        {
            await _sink.WriteAsync(Padding.AsMemory(0, padding), cancellationToken).ConfigureAwait(false);
        }

        return aligned;
    }

    /// <summary>struct -> per field chunked -> per batch flat.</summary>
    private byte[] BuildLayout()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();

        ushort flat = _layoutEncodings.Intern("vortex.flat");
        ushort chunked = _layoutEncodings.Intern("vortex.chunked");
        ushort zoned = 0;
        ushort structural = _isTabular ? _layoutEncodings.Intern("vortex.struct") : (ushort)0;

        // `has_stats_table = false`: the first child is a chunk, not a statistics table.
        Span<byte> chunkedMetadata = stackalloc byte[1];
        int metadataLength = Arrays.Metadata.ChunkedLayoutMetadata.Write(
            new Arrays.Metadata.ChunkedLayoutMetadata(false), chunkedMetadata);

        Span<uint> segmentIds = stackalloc uint[1];
        int[] fieldLayouts = new int[Math.Max(_fieldCount, 1)];
        for (int field = 0; field < _fieldCount; field++)
        {
            List<int> segments = _columnSegments[field];
            int[] chunks = new int[segments.Count];
            for (int chunk = 0; chunk < segments.Count; chunk++)
            {
                segmentIds[0] = (uint)segments[chunk];
                chunks[chunk] = LayoutWriter.Write(
                    builder, flat, (ulong)_chunkRows[chunk], default, [], segmentIds);
            }

            int data = LayoutWriter.Write(
                builder, chunked, (ulong)_rowCount, chunkedMetadata[..metadataLength], chunks, []);

            fieldLayouts[field] = Zone(builder, field, data, ref zoned);
        }

        // A non-struct root has no struct level: its single chunked layout IS the root.
        int root = _isTabular
            ? LayoutWriter.Write(
                builder, structural, (ulong)_rowCount, default, fieldLayouts.AsSpan(0, _fieldCount), [])
            : fieldLayouts[0];

        return builder.FinishToArray(root);
    }

    /// <summary>
    /// Wraps a column's data layout in a <c>vortex.zoned</c> one, when it has a zone map. Child 0
    /// is the data and child 1 is the zones; the wrong order produces a file that decodes the zones
    /// as data and is caught by nothing until a value comes out wrong.
    /// </summary>
    private int Zone(FlatBufferBuilder builder, int field, int data, ref ushort zoned)
    {
        if (_zoneSegments is null || _zoneMetadata is null || _zoneSegments[field] < 0)
        {
            return data;
        }

        if (zoned == 0)
        {
            zoned = _layoutEncodings.Intern("vortex.zoned");
        }

        ushort flat = _layoutEncodings.Intern("vortex.flat");
        Span<uint> segment = stackalloc uint[1];
        segment[0] = (uint)_zoneSegments[field];

        // The zones child's row count is the zone count, which is the closed blocks' count.
        int zoneCount = _columns[field].Blocks.Count;
        int zones = LayoutWriter.Write(builder, flat, (ulong)zoneCount, default, [], segment);

        Span<int> children = stackalloc int[2];
        children[0] = data;
        children[1] = zones;
        return LayoutWriter.Write(
            builder, zoned, (ulong)_rowCount, _zoneMetadata[field], children, []);
    }

    private byte[] BuildFooter() =>
        Footer(_arrayEncodings.Ids, _layoutEncodings.Ids, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_segments));

    /// <summary>A footer over these encoding tables and segments.</summary>
    internal static byte[] Footer(IReadOnlyList<string> arrays, IReadOnlyList<string> layouts, ReadOnlySpan<SegmentSpec> segments)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();

        int[] arraySpecs = CreateStrings(builder, arrays);
        int[] layoutSpecs = CreateStrings(builder, layouts);

        int table = FooterWriter.Write(
            builder,
            arraySpecs,
            layoutSpecs,
            segments,
            [],
            0);

        return builder.FinishToArray(table);
    }

    /// <summary>Where the segments a postscript names lie; a length of 0 is an absent segment.</summary>
    internal readonly record struct PostscriptPlacement(
        long DTypeOffset, int DTypeLength, long LayoutOffset, int LayoutLength,
        long FooterOffset, int FooterLength, long StatisticsOffset, int StatisticsLength,
        long DirectoryOffset, int DirectoryLength);

    /// <summary>
    /// Writes the last bytes of every file this library writes: the identity, the postscript and
    /// the EOF record.
    /// </summary>
    /// <param name="sink">The file's sink, positioned right after the footer.</param>
    /// <param name="placement">The segments the postscript names.</param>
    /// <param name="identity">The identity the options pinned, or null for a fresh one.</param>
    /// <param name="metadata">The user metadata, each value a segment the postscript names by its key.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <remarks>
    /// The metadata and the identity go last before the postscript, so the tail every open reads
    /// covers them.
    /// </remarks>
    internal static async ValueTask WriteEndAsync(
        ISegmentSink sink, PostscriptPlacement placement, Guid? identity,
        IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> metadata, CancellationToken cancellationToken)
    {
        long[] metadataOffsets = new long[metadata.Count];
        for (int i = 0; i < metadata.Count; i++)
        {
            metadataOffsets[i] = sink.Position;
            await sink.WriteAsync(metadata[i].Value, cancellationToken).ConfigureAwait(false);
        }

        long identityOffset = sink.Position;
        await FileIdentity.WriteAsync(sink, identity, cancellationToken).ConfigureAwait(false);

        int postscriptLength;
        using (FlatBufferBuilder builder = new FlatBufferBuilder())
        {
            ReadOnlyMemory<byte> postscript = BuildPostscript(builder, placement, identityOffset, metadata, metadataOffsets);
            if (postscript.Length > VortexLimits.MaxPostscriptSize)
            {
                throw new InvalidOperationException(
                    $"The postscript is {postscript.Length} bytes; the format's ceiling is " +
                    $"{VortexLimits.MaxPostscriptSize}. Reduce the schema or the user metadata.");
            }

            postscriptLength = postscript.Length;
            await sink.WriteAsync(postscript, cancellationToken).ConfigureAwait(false);
        }

        // EOF: u16 version, u16 postscript length, then the magic. Read backwards by every reader.
        byte[] eof = ArrayPool<byte>.Shared.Rent(VortexFileFormat.EofSize);
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(eof, (ushort)VortexFileFormat.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(eof.AsSpan(2), (ushort)postscriptLength);
            VortexFileFormat.MagicBytes.CopyTo(eof.AsSpan(VortexFileFormat.EofMagicOffset));
            await sink.WriteAsync(eof.AsMemory(0, VortexFileFormat.EofSize), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(eof);
        }
    }

    /// <summary>Builds the postscript in <paramref name="builder"/> and lends its bytes.</summary>
    private static ReadOnlyMemory<byte> BuildPostscript(
        FlatBufferBuilder builder, in PostscriptPlacement placement, long identityOffset,
        IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> user, ReadOnlySpan<long> userOffsets)
    {
        // The metadata vector's entries must exist before the postscript table that lists them:
        // the index directory when there is one, the caller's own entries, then the identity,
        // which every file carries.
        int identitySegment = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)identityOffset, FileIdentity.Length, 0, 0, 0),
            CompressionScheme.None);
        int identity = PostscriptWriter.WriteMetadata(builder, FileIdentity.MetadataKeyUtf8, identitySegment);
        Span<int> metadata = stackalloc int[VortexLimits.MaxMetadataSegments];
        int entries = 0;
        if (placement.DirectoryLength > 0)
        {
            int segment = PostscriptWriter.WriteSegment(
                builder, new SegmentSpec((ulong)placement.DirectoryOffset, (uint)placement.DirectoryLength, 0, 0, 0),
                CompressionScheme.None);
            metadata[entries++] = PostscriptWriter.WriteMetadata(builder, IndexDirectory.MetadataKeyUtf8, segment);
        }

        for (int i = 0; i < user.Count; i++)
        {
            int segment = PostscriptWriter.WriteSegment(
                builder, new SegmentSpec((ulong)userOffsets[i], checked((uint)user[i].Value.Length), 0, 0, 0),
                CompressionScheme.None);
            metadata[entries++] = PostscriptWriter.WriteMetadata(builder, Encoding.UTF8.GetBytes(user[i].Key), segment);
        }

        metadata[entries++] = identity;

        int dtype = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)placement.DTypeOffset, (uint)placement.DTypeLength, 0, 0, 0),
            CompressionScheme.None);
        int layout = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)placement.LayoutOffset, (uint)placement.LayoutLength, 0, 0, 0),
            CompressionScheme.None);
        int footer = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)placement.FooterOffset, (uint)placement.FooterLength, 0, 0, 0),
            CompressionScheme.None);
        int statistics = placement.StatisticsLength == 0
            ? 0
            : PostscriptWriter.WriteSegment(
                builder, new SegmentSpec((ulong)placement.StatisticsOffset, (uint)placement.StatisticsLength, 0, 0, 0),
                CompressionScheme.None);

        int table = PostscriptWriter.Write(builder, dtype, layout, statistics, footer, metadata[..entries]);
        return builder.FinishMemory(table);
    }

    /// <summary>
    /// The format's <c>FileStatistics</c> table: one <c>ArrayStats</c> per top-level field of
    /// a struct root, one for any other root, each the merge of the column's closed blocks.
    /// </summary>
    /// <remarks>
    /// Only what the pass knows: bounds for the numeric domains the summaries hold, the summed null
    /// count, and the order flags when the pass tracked the column's order. They stay absent
    /// otherwise, because an absent statistic licenses nothing while a wrong one lies. The reader
    /// needs exactly one entry per field.
    /// </remarks>
    private byte[] BuildFileStatistics()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        ScalarStore scalars = new ScalarStore();
        int fields = _isTabular ? _fieldCount : 1;
        int[] entries = new int[fields];
        for (int field = 0; field < fields; field++)
        {
            DType column = _isTabular ? _schema.GetField(field) : _schema;
            ColumnWriter writer = _columns[field];
            BlockStats merged = writer.Chunk(0, writer.Blocks.Count);

            ArrayStatsValues values = default;
            values.MinPrecision = StatPrecision.Exact;
            values.MaxPrecision = StatPrecision.Exact;
            if (_append is not null)
            {
                AppendStatistics(field, ref values);
            }
            else if (merged.IsPresent)
            {
                values.NullCount = (ulong)merged.NullCount;
                values.IsSorted = merged.IsSorted;
                values.IsStrictSorted = merged.IsStrictSorted;
                if (merged.HasBounds && column.Kind == DTypeKind.Primitive)
                {
                    values.Min = ScalarProtobuf.SerializeValue(Bound(scalars, column.PType, merged.Min));
                    values.Max = ScalarProtobuf.SerializeValue(Bound(scalars, column.PType, merged.Max));
                }
            }

            entries[field] = ArrayWriter.WriteStats(builder, in values);
        }

        int vector = builder.CreateOffsetVector(entries);
        builder.StartTable();
        builder.AddOffset(SchemaFieldIds.FileStatisticsFieldStats, vector);
        int table = builder.EndTable();
        return builder.FinishToArray(table);
    }

    /// <summary>
    /// A block summary's bound as the scalar the reference writes for the column's type: the
    /// widened integer domain, and the float at its own width.
    /// </summary>
    private static ScalarValue Bound(ScalarStore scalars, PType ptype, FilterLiteral bound)
    {
        if (ptype.IsSignedInteger())
        {
            return scalars.Int64(bound.SignedValue);
        }

        if (ptype.IsUnsignedInteger())
        {
            return scalars.UInt64(bound.UnsignedValue);
        }

        return ptype switch
        {
            PType.F16 => scalars.F16((Half)bound.FloatValue),
            PType.F32 => scalars.F32((float)bound.FloatValue),
            _ => scalars.F64(bound.FloatValue),
        };
    }

    private static int[] CreateStrings(FlatBufferBuilder builder, IReadOnlyList<string> ids)
    {
        int[] offsets = new int[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            offsets[i] = builder.CreateString(ids[i]);
        }

        return offsets;
    }

    private void RequireMatchingSchema(DType batchSchema)
    {
        int fields = batchSchema.Kind == DTypeKind.Struct ? batchSchema.FieldCount : 1;
        if ((batchSchema.Kind == DTypeKind.Struct) != _isTabular || fields != _fieldCount)
        {
            throw new ArgumentException(
                $"The batch has {fields} column(s) under a {batchSchema.Kind} root; the file's " +
                $"schema has {_fieldCount} under a {_schema.Kind} one.",
                "batch");
        }
    }
}
