// The canonical, uncompressed writer - docs/01-scope.md Phase 3's first milestone, and the one that
// unlocks cross-testing in the Vorticity -> Rust direction: until a file exists, the writer is
// untested in any meaningful sense, because our own reader will happily read back our own mistakes.
//
// The file it produces is deliberately the simplest valid one:
//
//     vortex.struct                 one child per root field (a tabular file)
//       └ vortex.zoned              one zone per batch, when the batches are uniform
//           ├ vortex.chunked        one child per batch written
//           │   └ vortex.flat       one segment: the batch's column, canonical and uncompressed
//           └ vortex.flat           one segment: the zones, one row each
//
// A file whose root dtype is NOT a struct -- `i64`, `utf8?` -- is legal and common in the corpus,
// and drops the struct level: the root is the chunked layout itself. Refusing those was the first
// thing the round-trip test caught.
//
// No compression yet. Zone maps ARE emitted (F11), but only when the batches handed in are uniform
// except for the last: a zone map declares ONE zone length and zone z covers
// [z * len, (z + 1) * len), so a ragged chunking has no zone length to declare. Rather than buffer
// and re-chunk -- which would throw away the streaming property the sink seam exists for -- the
// writer emits a plain chunked layout in that case, which every reader already handles.
//
// THE ORDER OF WRITES IS THE FORMAT'S, NOT A CHOICE. Segments go out as batches arrive, so nothing
// is buffered; the layout, footer and postscript can only be written once every segment's offset is
// known, which is why they are all in CompleteAsync. A sink that cannot seek loses nothing, which
// is the property that makes an S3 multipart upload trivial in the layer above
// (docs/03-architecture.md §3.8).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Editions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

/// <summary>Writes a Vortex file, one batch at a time, in a single forward pass.</summary>
public sealed class VortexFileWriter : IAsyncDisposable
{
    private readonly ISegmentSink _sink;
    private readonly DType _schema;
    private readonly int _fieldCount;
    private readonly EncodingDictionary _arrayEncodings;
    private readonly EncodingDictionary _layoutEncodings;
    private readonly List<SegmentSpec> _segments = [];

    /// <summary>Per root field, the segment index of each batch's column.</summary>
    private readonly List<int>[] _columnSegments;

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
    /// Rows per BLOCK, which is the zone length - docs/11-write-strategy.md §3.1.
    /// </summary>
    /// <remarks>
    /// A block is counted from row 0 of the file and owes nothing to the caller's batching or to the
    /// chunk the rows land in, which is the whole reason the zone map is no longer hostage to a
    /// uniform chunking. It is <see cref="VortexWriteOptions.RowBlockSize"/> -- upstream's
    /// <c>row_block_size</c>, the same quantity under the same name.
    /// <para>
    /// ZERO MEANS "THE BATCH IS THE BLOCK", which is what <c>RowBlockSize = null</c> asks for: that
    /// caller has taken the file's shape into its own hands and said its batches are its pruning
    /// unit. The zone map then needs the uniformity check <see cref="TryZoneLength"/> makes, because
    /// a ragged batching has no single zone length to declare -- the one case where that is still
    /// true.
    /// </para>
    /// </remarks>
    private readonly int _blockRows;

    /// <summary>Rows already folded into the open block, below <see cref="_blockRows"/>.</summary>
    private int _blockFilled;

    /// <summary>Blocks the chunks emitted so far have consumed; the next chunk starts here.</summary>
    private int _emittedBlocks;

    /// <summary>
    /// (Column chunk, field) pairs the chooser had to measure itself because the block range handed
    /// to it did not cover them.
    /// </summary>
    /// <remarks>
    /// THE FALLBACK IS SILENT BY DESIGN AND THAT IS EXACTLY WHY THIS EXISTS. `Choose` checks the row
    /// count and measures the column when it disagrees, so a wrong block range costs a pass and
    /// never a wrong plan -- which means `WrittenSizeTests` would stay byte-exact while every
    /// candidate quietly went back to walking the rows, and the whole of stage 2 would be undone by
    /// an off-by-one nobody could see. `StatisticsTests` holds this at zero over a range of batch
    /// shapes; it is the only thing that can.
    /// </remarks>
    internal long ChunksWithoutStatistics => _chunksWithoutStatistics;

    private long _chunksWithoutStatistics;

    /// <summary>
    /// (Column chunk, field) pairs of a comparable kind whose distinct table could not answer, so the
    /// chooser walked the chunk to build the dictionary it would otherwise have read off the table.
    /// </summary>
    /// <remarks>
    /// The same argument as <see cref="ChunksWithoutStatistics"/>, for docs/11 §3.2.2's table: the
    /// walk is byte-identical to the table by construction, so a table that quietly stopped serving
    /// would keep every byte-exact test green while every chunk paid the second pass again. Decided
    /// by <see cref="ChunkStats.TableServes"/>, the one predicate the chooser also uses.
    /// </remarks>
    internal long ChunksWithoutTable => _chunksWithoutTable;

    private long _chunksWithoutTable;

    /// <summary>
    /// (Column chunk, field) pairs whose distinct table answered: the dictionary was read off the
    /// table rather than walked for.
    /// </summary>
    /// <remarks>
    /// The positive half of <see cref="ChunksWithoutTable"/>, and the one a test needs: a table
    /// that is never expected to serve leaves the fallback counter at zero all the same. Whether a
    /// dictionary column's memory holds -- and so whether its table runs at all -- is exactly what
    /// `PlanMemoryTests` holds this counter to.
    /// </remarks>
    internal long ChunksFromTable => _chunksFromTable;

    private long _chunksFromTable;

    /// <summary>Canonical bytes to accumulate before emitting, or 0 for no byte threshold.</summary>
    private readonly long _blockBytes;

    /// <summary>Whether the schema holds a list or a map, so W-35's narrowing has anything to do.</summary>
    private readonly bool _mayShareChildren;

    /// <summary>
    /// The two arenas the accumulation ping-pongs between.
    /// </summary>
    /// <remarks>
    /// TWO, BECAUSE A BLOCK'S REMAINDER HAS TO SURVIVE THE BLOCK. The pending batches are
    /// materialized into <c>_transit[_current]</c>; emitting a block concatenates them, slices off
    /// the rows that go out, writes those, then copies what is LEFT into the other arena and resets
    /// the first. One arena would mean resetting storage the remainder still views.
    /// </remarks>
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
    private bool _completed;
    private byte[] _padding = new byte[VortexLimits.MaxAlignment];

    private VortexFileWriter(
        ISegmentSink sink, DType schema, bool compress, VortexEdition target, int rowBlock,
        long blockBytes)
    {
        _sink = sink;
        _schema = schema;
        _compress = compress;
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
        _columns = new ColumnWriter[Math.Max(_fieldCount, 1)];
        _fieldNodes = new int[Math.Max(_fieldCount, 1)];
        for (int i = 0; i < _columnSegments.Length; i++)
        {
            _columnSegments[i] = [];
            // THE TABLE HAS A CONSUMER ONLY IF THE EDITION CAN WRITE A DICTIONARY: docs/11 §3.2.2's
            // first liveness rule, the one the edition decides; plan memory adds the per-chunk one.
            _columns[i] = new ColumnWriter
            {
                EditionAllowsDictionary = ColumnCompressor.Allows(target, "vortex.dict"),
            };
        }
    }


    /// <summary>How many rows have been written.</summary>
    public long RowCount => _rowCount;

    /// <summary>The schema every batch must match.</summary>
    public DType Schema => _schema;

    /// <summary>Starts a file over <paramref name="sink"/>.</summary>
    /// <param name="sink">Where the bytes go.</param>
    /// <param name="schema">The file's dtype. A struct makes it tabular; anything else is one column.</param>
    /// <returns>The writer. The caller completes and disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> has not been set.</exception>
    public static VortexFileWriter Create(ISegmentSink sink, DType schema) =>
        Create(sink, schema, VortexWriteOptions.Default);

    /// <summary>Starts a file over <paramref name="sink"/> with explicit options.</summary>
    /// <param name="sink">Where the bytes go.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="options">Write-time policy.</param>
    /// <returns>The writer. The caller completes and disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> has not been set.</exception>
    public static VortexFileWriter Create(ISegmentSink sink, DType schema, VortexWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        if (schema.IsDefault)
        {
            throw new ArgumentException("The schema has not been set.", nameof(schema));
        }

        // The schema's extension ids are checked HERE rather than where the dtype is serialized:
        // the answer cannot change once the writer exists, and "this schema cannot be written to
        // this edition" is worth hearing before the first batch rather than at Complete.
        RequireSchemaInTarget(schema, options.TargetEdition);

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

        return new VortexFileWriter(
            sink, schema, options.Compress, options.TargetEdition, rowBlock, blockBytes);
    }

    /// <summary>Rejects a schema naming an extension dtype the target edition does not carry.</summary>
    private static void RequireSchemaInTarget(DType dtype, VortexEdition target)
    {
        if (dtype.Kind == DTypeKind.Extension)
        {
            string id = dtype.ExtensionId;
            if (!EditionRegistry.Contains(target, ComponentKind.DType, id))
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
            RequireSchemaInTarget(dtype.GetField(i), target);
        }

        if (dtype.Kind is DTypeKind.List or DTypeKind.FixedSizeList)
        {
            RequireSchemaInTarget(dtype.ElementType, target);
        }
        else if (dtype.Kind == DTypeKind.Extension)
        {
            RequireSchemaInTarget(dtype.StorageType, target);
        }
    }

    /// <summary>Creates a file at <paramref name="path"/>.</summary>
    /// <param name="path">The destination path; truncated if it exists.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <returns>The writer, which owns the underlying stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static VortexFileWriter Create(string path, DType schema) =>
        Create(path, schema, VortexWriteOptions.Default);

    /// <summary>Creates a file at <paramref name="path"/> with explicit options.</summary>
    /// <param name="path">The destination path; truncated if it exists.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="options">Write-time policy.</param>
    /// <returns>The writer, which owns the underlying stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static VortexFileWriter Create(string path, DType schema, VortexWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        System.IO.FileStream stream = new System.IO.FileStream(
            path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
        return Create(new StreamSegmentSink(stream, ownsStream: true), schema, options);
    }

    /// <summary>Appends <paramref name="batch"/> as one chunk of every column.</summary>
    /// <param name="batch">The rows. Its schema must equal <see cref="Schema"/>.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the batch's segments are with the sink.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="ArgumentException">The batch's schema does not match the file's.</exception>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    public async ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_completed, this);

        if (batch.RowCount == 0)
        {
            // An empty chunk is legal but pointless, and it would make the chunk lists of the
            // columns disagree with a reader's expectation of non-degenerate children.
            return;
        }

        RequireMatchingSchema(batch);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        // THE STATISTICS PASS RUNS HERE, BEFORE ANY COPY OR EMISSION - docs/11-write-strategy.md
        // §2. The batch is on the caller's own arena and the decode that produced it has just
        // touched every byte, so this is the one moment the column is in cache for free. It also
        // makes the summary independent of what happens next: whether these rows go out where they
        // lie, wait in transit, or straddle two chunks, their block is the same block.
        Ingest(batch);

        if (_rowBlock == 0)
        {
            // One call, one chunk: the shape before repartitioning existed, kept as an explicit
            // choice rather than as the absence of one.
            await EmitChunkAsync(batch.Arena, batch.RootIndex, batch.RowCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // NOTHING PENDING AND ALREADY BIG ENOUGH: write it where it lies. A caller handing over
        // batches that already satisfy both thresholds -- which is the shape a bulk load has --
        // then pays no transit copy at all, and the repartitioner costs it nothing.
        if (_pending.Count == 0 &&
            batch.RowCount >= _rowBlock &&
            batch.Arena.ByteSize(batch.RootIndex) >= _blockBytes &&
            batch.RowCount % _rowBlock == 0)
        {
            await EmitChunkAsync(batch.Arena, batch.RootIndex, batch.RowCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // MATERIALIZED, not borrowed. The batch's arena is the scan's and is reset the moment the
        // caller asks for the next batch, so rows that are going to be held have to own their
        // bytes -- which is exactly what CopyFrom is for.
        // NARROWED BEFORE IT IS OWNED, in three steps that copy nothing until the last -- W-35. A
        // batch cut from a large list chunk shares that chunk's elements WHOLE, so copying it as it
        // comes materializes the column to hold one batch of it (measured: 6 065 048 bytes for the
        // 48 KB the rows name, on the 1M-row `list` file). Referencing first costs records only,
        // compacting then works on views, and the copy pays for the window alone. Doing it here
        // rather than after the copy also makes the concatenation tight: `ConcatListView` rebases by
        // each chunk's WHOLE child length, so a wide child poisons every offset downstream of it.
        CanonicalArena transit = Transit();
        _pending.Add(_mayShareChildren
            ? transit.CopyFrom(
                transit, ChunkCompactor.Compact(transit, transit.ReferenceFrom(batch.Arena, batch.RootIndex)))
            : transit.CopyFrom(batch.Arena, batch.RootIndex));
        _pendingRows += batch.RowCount;
        _pendingBytes += transit.ByteSize(_pending[^1]);

        while (_pendingRows >= _rowBlock && _pendingBytes >= _blockBytes)
        {
            await EmitBlockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Folds one batch into the open block of every column, closing blocks as it crosses them.
    /// </summary>
    /// <remarks>
    /// A batch straddles blocks - 8 131 rows never line up with 8 192 - so the range handed to a
    /// column is cut at the boundary and the leftover continues the next block. Nothing here reads a
    /// row twice and nothing allocates: the field nodes go into the writer's own scratch.
    /// </remarks>
    private void Ingest(RecordBatch batch)
    {
        CanonicalArena arena = batch.Arena;
        if (_isTabular)
        {
            CanonicalNode root = arena.GetNode(batch.RootIndex);
            for (int field = 0; field < _fieldCount; field++)
            {
                _fieldNodes[field] = root.GetFieldIndex(field);
            }
        }
        else
        {
            _fieldNodes[0] = batch.RootIndex;
        }

        int rows = batch.RowCount;
        if (_blockRows == 0)
        {
            // The batch IS the block: the caller turned repartitioning off and its batches are its
            // own pruning unit.
            for (int field = 0; field < _fieldCount; field++)
            {
                _columns[field].Accumulate(arena, _fieldNodes[field], 0, rows);
            }

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
    /// <param name="arena">The arena holding the carried rows.</param>
    /// <param name="rootIndex">A carried node: a whole batch, or the cut tail of one.</param>
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

    /// <summary>Seals the open block of every column.</summary>
    private void CloseBlock()
    {
        for (int field = 0; field < _fieldCount; field++)
        {
            _columns[field].CloseBlock();
        }

        _blockFilled = 0;
    }

    /// <summary>The arena the pending rows live in, created on first use.</summary>
    private CanonicalArena Transit()
    {
        // A detached scan context: the writer needs an arena and the decode plumbing that
        // CanonicalConcat and CanonicalSlice take, and it has no file to scan. The SECOND one is
        // created only when a block actually splits, which for a file small enough to leave as one
        // chunk never happens.
        _transit ??= new ScanContext?[2];
        return (_transit[_current] ??= new ScanContext([])).Canonical;
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

        // WHAT IS CARRIED IS CUT FROM ONE BATCH, NEVER FROM THE CONCATENATION -- WRITE-AUDIT.md
        // W-31b. Slicing the concatenated block and carrying that was the shape before, and it made
        // the remainder grow without bound: a slice keeps the whole of what it was cut from for the
        // forms that share storage (`ListView`'s elements child, a `VarBinView`'s data buffers), so
        // the carry dragged every byte of every block already emitted and the next copy paid for all
        // of it again. `list`, `listview` and `map` did not merely slow down, they stopped: 270 MB
        // asked for in one buffer, above the 256 MiB ceiling.
        //
        // The boundary falls inside at most ONE pending batch. Everything before it goes out whole,
        // that one is cut in two, and what is carried is its tail plus whatever whole batches follow
        // -- each of them bounded by its own batch, whatever the file's length.
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

        // The remainder moves to the other arena BEFORE this one is reset, because a slice is a
        // view onto the storage the reset would hand back.
        ScanContext to = _transit[_current ^ 1] ??= new ScanContext([]);
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

        // THE TAIL IS PROBED AGAIN, INTO THE TABLES THE EMISSION JUST RESET. These rows were probed
        // under the chunk that has just gone out, some against entries that are now written; in
        // the chunk they will open they are new, and their codes must say so. Their statistics are
        // not touched -- the open block is still the open block -- only the tables see them again,
        // in the order the next chunk will hold them, which is first. Less than a block.
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

    /// <summary>Writes one chunk of every column from <paramref name="arena"/>.</summary>
    private async ValueTask EmitChunkAsync(
        CanonicalArena arena, int rootIndex, long rows, CancellationToken cancellationToken)
    {
        // WHICH BLOCKS THIS CHUNK COVERS. A chunk is emitted in whole multiples of the block length
        // and chunks start at row 0, so a chunk is a contiguous, block-aligned range and its
        // statistics are the sum of its blocks' -- docs/11-write-strategy.md §3.3. The last chunk of
        // the file may end inside a block, which is why the count rounds up. With repartitioning off
        // the batch is both the chunk and the block, so the range is one.
        int blocks = _blockRows > 0
            ? checked((int)((rows + _blockRows - 1) / _blockRows))
            : 1;

        for (int field = 0; field < _fieldCount; field++)
        {
            int node = _isTabular ? arena.GetNode(rootIndex).GetFieldIndex(field) : rootIndex;

            // Z1b-c2b: the constant form stops here. The writer has no `vortex.constant` on the wire
            // and the zone summariser has no case for the kind, so the element is expanded ONCE, at
            // the boundary, and both of the calls below see what they have always seen. Doing it
            // inside the blob writer alone left the zone map out and the file 112 bytes short of the
            // bytes the corpus was written with -- close enough to pass a ratio and wrong.
            node = ArrayBlobWriter.Materialize(arena, node);

            // W-35, and it is at the boundary for Z1b-c2b's reason above: a chunk cut from a batch
            // shares that batch's list elements whole, and what the blob writer is handed is what
            // lands in the file. Both calls below must see the narrowed node or the zone map would
            // summarise rows the segment no longer holds.
            if (_mayShareChildren)
            {
                node = ChunkCompactor.Compact(arena, node);
            }

            // THE CHUNK'S STATISTICS, HANDED TO THE CHOOSER. They were computed at ingest over
            // exactly these rows, so every candidate that used to measure the column again reads
            // them instead. `Choose` checks the row count against the node it is given and falls
            // back to measuring when they disagree, so this can only ever cost a pass.
            ChunkStats stats = new ChunkStats(_columns[field], _emittedBlocks, blocks);
            if (stats.Stats.Rows != rows)
            {
                _chunksWithoutStatistics++;
            }

            // A table plan memory turned off (docs/11 §3.2.2) was never expected to serve, and is
            // not a fallback; a table that was running and cannot answer is.
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
                ArrayBlobWriter.Write(arena, node, _arrayEncodings, _compress, stats);
            _columnSegments[field].Add(
                await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false));

            // The chunk is written, so the per-block scratch behind it has no further reader. The
            // compact summaries stay — the zone map wants them at `CompleteAsync` — and only the
            // sized buffers go back to the pool.
            _columns[field].ReleaseChunk(_emittedBlocks, blocks);
        }

        _emittedBlocks += blocks;
        _chunkRows.Add(rows);
        _rowCount += rows;
    }

    /// <summary>
    /// Writes the layout, footer, postscript and EOF marker, and finishes the file.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when the file is whole.</returns>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;

        // A file with no batches still has to start with the magic.
        await StartAsync(cancellationToken).ConfigureAwait(false);

        // THE TRAILING BLOCK IS CLOSED BEFORE THE LAST CHUNK GOES OUT, and the order is
        // load-bearing: the chunk emitted below covers this block, and `EmitChunkAsync` reads the
        // CLOSED blocks to hand the chooser its statistics. Closing afterwards would hand it an
        // absent summary for the one chunk most files have.
        //
        // The file's last block is short unless the row count is a multiple of the block length, and
        // a short LAST zone is exactly what the format allows. Closing it here rather than at ingest
        // is what makes "short" mean "the file ended", never "the batch ended".
        if (_blockFilled > 0)
        {
            CloseBlock();
        }

        // The last chunk goes out whatever its size: the row-block multiple and the byte target are
        // conditions on the chunks BEFORE the last one, exactly as upstream has it.
        if (_pendingRows > 0)
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

        // The zones arrays are segments like any other and must be written BEFORE the footer that
        // records them.
        await WriteZoneMapsAsync(cancellationToken).ConfigureAwait(false);

        long dtypeOffset = _sink.Position;
        byte[] dtype = DTypeFlatBuffers.Serialize(_schema);
        await _sink.WriteAsync(dtype, cancellationToken).ConfigureAwait(false);

        long layoutOffset = _sink.Position;
        byte[] layout = BuildLayout();
        await _sink.WriteAsync(layout, cancellationToken).ConfigureAwait(false);

        long footerOffset = _sink.Position;
        byte[] footer = BuildFooter();
        await _sink.WriteAsync(footer, cancellationToken).ConfigureAwait(false);

        byte[] postscript = BuildPostscript(
            dtypeOffset, dtype.Length, layoutOffset, layout.Length, footerOffset, footer.Length);
        if (postscript.Length > VortexLimits.MaxPostscriptSize)
        {
            throw new InvalidOperationException(
                $"The postscript is {postscript.Length} bytes; the format's ceiling is " +
                $"{VortexLimits.MaxPostscriptSize}. Reduce the schema or the user metadata.");
        }

        await _sink.WriteAsync(postscript, cancellationToken).ConfigureAwait(false);

        // EOF: u16 version, u16 postscript length, then the magic. Read backwards by every reader.
        byte[] eof = new byte[VortexFileFormat.EofSize];
        BinaryPrimitives.WriteUInt16LittleEndian(eof, (ushort)VortexFileFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(eof.AsSpan(2), (ushort)postscript.Length);
        VortexFileFormat.MagicBytes.CopyTo(eof.AsSpan(VortexFileFormat.EofMagicOffset));
        await _sink.WriteAsync(eof, cancellationToken).ConfigureAwait(false);

        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes the file if it is not complete, then releases the sink.</summary>
    /// <returns>A task that completes when everything is released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // The transit contexts own pooled native blocks. Dropping them on the floor sends every
        // one through ~NativeSegmentOwner instead of back to AlignedBufferPool.Shared, which is
        // the leak `ZoneMapWriter` had too: a finalizer-thread free per block, and a pool that
        // never refills. CompleteAsync resets the arena it emitted from; this returns the storage.
        if (_transit is not null)
        {
            for (int i = 0; i < _transit.Length; i++)
            {
                _transit[i]?.Dispose();
                _transit[i] = null;
            }
        }

        if (_sink is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds and writes one zones segment per column, when the chunking allows a zone map at all.
    /// </summary>
    /// <remarks>
    /// ZoneMap documents zone z as covering [z * ZoneLength, (z + 1) * ZoneLength) with only the
    /// last zone short. A CHUNK cannot honour that -- its size is the caller's batching and the byte
    /// target together, so a ragged chunking has no zone length to declare and five corpus files
    /// lost their zone map for it (WRITE-ARCHITECTURE.md §3.7). A BLOCK honours it by construction:
    /// it is counted from row 0 of the file, so zone z is [z * _blockRows, (z+1) * _blockRows)
    /// whatever the chunks did, and the last one is short exactly when the file's row count is not a
    /// multiple. Zones therefore no longer line up with segments, which costs nothing: pruning a
    /// zone inside a live segment saves decode rather than bytes read, and
    /// docs/11-write-strategy.md §6.2 is that trade written down.
    /// </remarks>
    private async ValueTask WriteZoneMapsAsync(CancellationToken cancellationToken)
    {
        // Below core2026.08.0 there is no `vortex.zoned` layout to put one in. Omitted rather than
        // approximated with the legacy `vortex.stats`: pruning is an optimization, so dropping it
        // costs correctness nothing, while writing a layout no release of Vortex has ever produced
        // - 0.86.1 cannot emit `vortex.stats` at all - would ship an untestable format path.
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

            if (!ZoneMapWriter.TryBuild(
                    column, _columns[field].Blocks, _arrayEncodings, zoneLength,
                    out byte[] metadata, out ArrayBlobWriter.BlobLease blob))
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

    /// <summary>The one zone length the closed blocks can be described by, if there is one.</summary>
    /// <remarks>
    /// With repartitioning on -- the default -- this is a formality: every block but the last is
    /// <see cref="_blockRows"/> rows by construction, so the answer is always yes and always that.
    /// It can still say no for a caller who set <c>RowBlockSize = null</c> and then handed over
    /// batches of 700 and 1024: those blocks have no single length, and a zone map that declared one
    /// anyway would attach every bound to the wrong rows -- the one failure docs/08-semantics.md §1
    /// says must never happen. Such a file gets a plain chunked layout, which every reader handles.
    /// </remarks>
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

    /// <summary>
    /// Writes the leading <c>VTXF</c> magic, once, before anything else.
    /// </summary>
    /// <remarks>
    /// docs/02-format.md §1 puts it at offset 0 and every reader checks it there. Forgetting it
    /// produces a file whose footer, layout and segments are all perfectly correct and which no
    /// reader will open -- which is exactly what the first run of the round-trip test reported.
    /// </remarks>
    private async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        byte[] magic = VortexFileFormat.MagicBytes.ToArray();
        await _sink.WriteAsync(magic, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pads to 64 bytes, writes <paramref name="blob"/>, and records where it landed.
    /// </summary>
    /// <remarks>
    /// The alignment is the segment's, and it is what makes the blob's own internal alignments real:
    /// ArrayBlobWriter placed each buffer at its width relative to the blob's start, so the blob has
    /// to start somewhere that satisfies the widest of them. 64 is the format's ceiling
    /// (VortexLimits.MaxAlignment), so one choice covers every buffer a canonical array can have.
    /// </remarks>
    private async ValueTask<int> WriteSegmentAsync(
        ArrayBlobWriter.BlobLease blob, CancellationToken cancellationToken)
    {
        long position = _sink.Position;
        long aligned = (position + VortexLimits.MaxAlignment - 1) & ~((long)VortexLimits.MaxAlignment - 1);
        int padding = (int)(aligned - position);
        if (padding > 0)
        {
            await _sink.WriteAsync(_padding.AsMemory(0, padding), cancellationToken).ConfigureAwait(false);
        }

        await _sink.WriteAsync(blob.Memory, cancellationToken).ConfigureAwait(false);

        // blob.Length, never blob.Memory.Length's array: the rental is longer than the blob (W-4).
        _segments.Add(new SegmentSpec(
            (ulong)aligned, (uint)blob.Length, (byte)VortexLimits.MaxAlignmentExponent, 0, 0));
        return _segments.Count - 1;
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
    /// Wraps a column's data layout in a <c>vortex.zoned</c> one, when it has a zone map.
    /// </summary>
    /// <remarks>
    /// Child 0 is the data and child 1 is the zones, which is the order ZonedLayoutReader reads and
    /// the order upstream writes. Getting them the wrong way round produces a file that decodes the
    /// zones as data and is caught by nothing until a value comes out wrong.
    /// </remarks>
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

        // The zones child's row count IS the zone count, and it is the closed blocks' count -- which
        // is ceil(_rowCount / _blockRows) by construction, the shape ZoneMap documents.
        int zoneCount = _columns[field].Blocks.Count;
        int zones = LayoutWriter.Write(builder, flat, (ulong)zoneCount, default, [], segment);

        Span<int> children = stackalloc int[2];
        children[0] = data;
        children[1] = zones;
        return LayoutWriter.Write(
            builder, zoned, (ulong)_rowCount, _zoneMetadata[field], children, []);
    }

    private byte[] BuildFooter()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();

        int[] arraySpecs = CreateStrings(builder, _arrayEncodings.Ids);
        int[] layoutSpecs = CreateStrings(builder, _layoutEncodings.Ids);

        int table = FooterWriter.Write(
            builder,
            arraySpecs,
            layoutSpecs,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_segments),
            [],
            0);

        return builder.FinishToArray(table);
    }

    private byte[] BuildPostscript(
        long dtypeOffset, int dtypeLength, long layoutOffset, int layoutLength,
        long footerOffset, int footerLength)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();

        int dtype = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)dtypeOffset, (uint)dtypeLength, 0, 0, 0),
            CompressionScheme.None);
        int layout = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)layoutOffset, (uint)layoutLength, 0, 0, 0),
            CompressionScheme.None);
        int footer = PostscriptWriter.WriteSegment(
            builder, new SegmentSpec((ulong)footerOffset, (uint)footerLength, 0, 0, 0),
            CompressionScheme.None);

        int table = PostscriptWriter.Write(builder, dtype, layout, statisticsSegment: 0, footer, []);
        return builder.FinishToArray(table);
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

    private void RequireMatchingSchema(RecordBatch batch)
    {
        DType batchSchema = batch.Schema;
        int fields = batchSchema.Kind == DTypeKind.Struct ? batchSchema.FieldCount : 1;
        if ((batchSchema.Kind == DTypeKind.Struct) != _isTabular || fields != _fieldCount)
        {
            throw new ArgumentException(
                $"The batch has {fields} column(s) under a {batchSchema.Kind} root; the file's " +
                $"schema has {_fieldCount} under a {_schema.Kind} one.",
                nameof(batch));
        }
    }
}
