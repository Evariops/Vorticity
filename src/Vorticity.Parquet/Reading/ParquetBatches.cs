using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A Parquet file's rows as batches of the columns a scan reads, a row group after the other: every
/// column chunk of the row group read in one request, or, from a source that does not read in place,
/// a large group in windows of batches, each read while the window before it is decoded; then cut a
/// batch at a time, each borrowed until the next is asked for. From a source that does not read in
/// place, the next row group is chosen and read while one is decoded.
/// </summary>
/// <remarks>
/// A batch holds <see cref="BatchRows"/> rows, or fewer at the end of a row group, which a batch never
/// crosses: on a file this package wrote, that is one page of every column. The batches go through
/// one context, whose arena holds what a batch copies and whose caps bound what it decodes. A field
/// of lists, maps or structs is read by every column under it, and assembled from their levels.
/// </remarks>
internal sealed class ParquetBatches : IAsyncEnumerator<RecordBatch>
{
    /// <summary>The rows of a batch: the block the writer cuts its pages on.</summary>
    internal const int BatchRows = 8_192;

    private readonly ParquetFile _file;
    private readonly RowRange? _rows;

    /// <summary>Per row group, whether the scan reads it: the plan's, which statistics may have pruned.</summary>
    private readonly bool[] _read;

    /// <summary>The column each reader reads.</summary>
    private readonly int[] _leaves;
    private readonly DType _struct;
    private readonly ScanCounters _metrics;
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Cancels the reads made ahead, of the next group and of the next window, when the scan is
    /// disposed: an early end waits for no transfer it no longer needs. The scan's token cancels it too.
    /// </summary>
    private readonly CancellationTokenSource _abandon;
    private readonly int _batchRows;
    private readonly ScanContext _context;

    /// <summary>Every column the scan reads: a flat field's own, and those under a nested field.</summary>
    private readonly ColumnChunkReader[] _readers;

    /// <summary>A field's assembler when it nests, else null.</summary>
    private readonly NestedFieldReader?[] _nested;

    /// <summary>A flat field's reader.</summary>
    private readonly int[] _flat;
    private readonly int[] _nodes;

    /// <summary>
    /// Per field, the context its readers decode into when a batch's fields decode side by side on
    /// the scan's lanes, each into an arena of its own: made for the first batch that does.
    /// </summary>
    private ScanContext[]? _fieldContexts;

    /// <summary>Whether a batch's fields may decode side by side: those of a scan of several, on several lanes.</summary>
    private readonly bool _mayDecodeAcross;

    /// <summary>The scan's degree, the threads a batch's fields decode on, the reading one included.</summary>
    private readonly int _degree;
    private WorkFan? _fan;
    private FieldDecoding? _decoding;

    /// <summary>The fields from which a batch's fields decode side by side whatever the batches before took.</summary>
    private const int AcrossFields = 8;

    /// <summary>
    /// The ticks the batches before must take to decode, on average, for a batch of fewer fields to
    /// decode side by side, 50 microseconds: below, waking the threads costs more than the decode
    /// they would share.
    /// </summary>
    private static readonly long AcrossTicks = Stopwatch.Frequency / 20_000;

    /// <summary>
    /// The ticks the batches before took to decode, side by side or not, each weighing an eighth: the
    /// batch that begins a page takes many times the others'.
    /// </summary>
    private long _decoded;

    /// <summary>Whether the last batch's fields decoded side by side, into the fields' contexts.</summary>
    private bool _across;

    /// <summary>The pipeline a row group's batches decode in ahead of the read, made for the first group that does.</summary>
    private FieldPipeline? _pipeline;

    /// <summary>The row group's batch the pipeline hands out next, or -1 while the group's batches decode as they are asked for.</summary>
    private int _piped = -1;

    /// <summary>The filter's columns the page index may bound, or null when the scan prunes nothing.</summary>
    private readonly FilterColumns? _pruning;

    /// <summary>Whether the filter asks equalities Bloom filters may answer, and the scan reads indexes.</summary>
    private readonly bool _bloom;
    private readonly SegmentRequestSet _indexes = new();

    /// <summary>The pruning of row groups by the dictionaries of the filter's columns, or null when no part of it reads one alone.</summary>
    private readonly DictionaryPruning? _dictionaries;

    /// <summary>The batches of the row group being read the page index leaves, or null when it rules none out.</summary>
    private BlockMask? _live;
    private readonly SegmentRequestSet _offsets = new();

    /// <summary>Per reader, what decrypts its column's pages, made the first time a chunk of it is encrypted.</summary>
    private readonly Encryption.PageDecryption?[] _decryptions;
    private readonly List<(int Start, VortexBuffer Bytes)> _runs = [];
    private readonly List<(long From, long To)> _ranges = [];

    /// <summary>The row group being read, and the one chosen and read while it is decoded.</summary>
    private GroupRead _group;
    private GroupRead _spare;

    /// <summary>
    /// The choice and read of the row group after the one being read, in flight while it is decoded:
    /// false when none is left. Only from a source that does not read in place, and for a scan that
    /// takes no number of rows, which a group past it would read for nothing.
    /// </summary>
    private Task<bool>? _planned;
    private readonly bool _readsAhead;

    /// <summary>The last row group chosen, to be read or being read.</summary>
    private int _chosen = -1;

    /// <summary>Per window past the first, the request that reads it, released with the group.</summary>
    private readonly List<SegmentRequestSet> _windowReads = [];

    /// <summary>The next window whose pages the readers are given, and its read in flight.</summary>
    private int _window;
    private Task? _ahead;

    /// <summary>The first window past the first not given back yet: those before it every reader was past.</summary>
    private int _released;

    /// <summary>Per reader, where its chunk of the row group being read starts in the file.</summary>
    private readonly long[] _chunkStarts;

    private RecordBatch? _current;
    private int _rowGroup = -1;
    private long _groupStart;
    private long _groupRows;
    private long _groupRead;
    private bool _anticipated;

    /// <summary>
    /// The batches of <paramref name="columns"/>, or every field, in the row groups
    /// <paramref name="groups"/> marks; a variant as its group of the columns the file holds when
    /// <paramref name="storage"/>, as a check of the columns' own values reads it.
    /// </summary>
    internal ParquetBatches(ParquetFile file, ScanSpec spec, int[]? columns, bool[] groups, ScanCounters metrics, CancellationToken cancellationToken, bool storage = false)
    {
        _file = file;
        _rows = spec.Rows;
        _read = groups;
        _metrics = metrics;
        _cancellationToken = cancellationToken;
        _abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _batchRows = RowsOf(spec);
        ParquetSchema schema = file.Compiled;
        int[] fields = columns ?? Every(schema.Fields.Length);
        VortexField[] read = new VortexField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            ParquetField field = schema.Fields[fields[i]];
            read[i] = new VortexField(field.Name, storage ? field.StorageType : field.Type);
        }

        DTypeArena types = new();
        _struct = VortexTypes.ToDType(VortexSchema.Create(read), types);
        DType validity = types.Bool(Nullability.NonNullable);
        long cap = file.Options.MaxDecompressedSize;
        AlignedBufferPool pool = file.Session.Options.EnginePool;
        _context = new ScanContext([], new VortexReadOptions { MaxDecompressedBytes = cap });
        List<ColumnChunkReader> readers = [];
        _nested = new NestedFieldReader?[fields.Length];
        _flat = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            ParquetField field = schema.Fields[fields[i]];
            if (field.Column >= 0)
            {
                _flat[i] = readers.Count;
                readers.Add(new ColumnChunkReader(schema.Columns[field.Column], _struct.GetField(i), validity, pool, cap));
            }
            else
            {
                NestedFieldReader nested = new(field, _struct.GetField(i), schema, types, validity, pool, cap, storage);
                _nested[i] = nested;
                readers.AddRange(nested.Readers);
            }
        }

        _readers = readers.ToArray();

        // A scan given lanes decompresses each column's next pages on them while the pages before are
        // read: as many at once as its degree, shared by the columns.
        int degree = spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : file.Session.Options.MaxDegreeOfParallelism;
        PageLanes? lanes = degree > 1 ? new PageLanes(degree) : null;
        _degree = degree;
        _mayDecodeAcross = degree > 1 && fields.Length > 1;
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.VerifyChecksums = file.Options.VerifyChecksums;
            reader.Counters = file.Counters;
            reader.Lanes = lanes;
        }

        // A scan that keeps encodings reads a flat column's dictionary pages as dictionary nodes.
        for (int i = 0; i < fields.Length; i++)
        {
            if (_nested[i] is null)
            {
                _readers[_flat[i]].KeepEncodings = spec.KeepEncodings;
            }
        }

        _leaves = new int[_readers.Length];
        for (int i = 0; i < _readers.Length; i++)
        {
            _leaves[i] = _readers[i].Column.Ordinal;
        }

        _decryptions = new Encryption.PageDecryption?[_readers.Length];
        _chunkStarts = new long[_readers.Length];
        _group = new GroupRead(schema.Columns.Length, _readers.Length);
        _spare = new GroupRead(schema.Columns.Length, _readers.Length);
        _readsAhead = !file.Reader.ReadsInPlace && spec.Take is null;
        _nodes = new int[fields.Length];
        _pruning = FilterColumns.For(file, spec);
        _bloom = _pruning is not null && spec.Options.UseIndexes && BloomPruning.Asks(spec.Filter!);

        // A group chosen while another is decoded decodes its dictionaries apart from the batch's arena.
        _dictionaries = DictionaryPruning.For(_pruning, _readsAhead ? null : _context);
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    public async ValueTask<bool> MoveNextAsync()
    {
        // The batch handed out last is dead: what it read goes back before the next is cut.
        _current?.Dispose();
        _context.ResetBatch();
        if (_across)
        {
            foreach (ScanContext context in _fieldContexts!)
            {
                context.ResetBatch();
            }
        }

        // The batch just dead frees its slot of the pipeline for the batch a pipeline's depth on.
        if (_piped > 0)
        {
            _pipeline!.Release(_piped - 1);
        }

        // The windows every reader is past go back as soon as the batch that held their last pages
        // is dead: a group holds a few windows of its reads, not all of them.
        if (_released < _window)
        {
            ReleasePassed();
        }

        int rows;
        while (true)
        {
            while (_groupRead == _groupRows)
            {
                if (_piped >= 0)
                {
                    _pipeline!.Finish();
                    _piped = -1;
                }

                if (!await NextRowGroupAsync().ConfigureAwait(false))
                {
                    return false;
                }

                Pipe();
            }

            rows = (int)Math.Min(_batchRows, _groupRows - _groupRead);
            if (_live is null || _live.IsLive((int)(_groupRead / _batchRows)))
            {
                break;
            }

            // The batches the page index rules out, as many as follow one another: every column steps
            // over their rows at once, so that a page they cover whole, which a sparse read did not
            // read, is stepped over whole.
            long from = _groupRead;
            int pruned = 0;
            while (_groupRead < _groupRows && !_live.IsLive((int)(_groupRead / _batchRows)))
            {
                _groupRead += Math.Min(_batchRows, _groupRows - _groupRead);
                pruned++;
            }

            // A skip that ends inside a page reads it: the windows up to the batch it ends at come first.
            if (_window < _group.Windows.Count && _groupRead < _groupRows)
            {
                await WindowsAsync((int)(_groupRead / _batchRows)).ConfigureAwait(false);
            }

            SkipRows(checked((int)(_groupRead - from)));
            _metrics.AddBlocksPruned(pruned);
        }

        if (_window < _group.Windows.Count)
        {
            await WindowsAsync((int)(_groupRead / _batchRows)).ConfigureAwait(false);
        }

        CanonicalArena arena = _context.Canonical;
        long began = Stopwatch.GetTimestamp();
        if (_piped >= 0)
        {
            // Decoded ahead, a field at a time: the batch's arena references their nodes where they lie.
            int slot = _pipeline!.Wait(_piped++);
            _file.Counters.AddPiped();
            for (int i = 0; i < _nodes.Length; i++)
            {
                int node = _pipeline.Node(i, slot, out ScanContext context);
                _nodes[i] = arena.ReferenceFrom(context.Canonical, node);
            }

            _across = false;
        }
        else if (_mayDecodeAcross && (_nodes.Length >= AcrossFields || _decoded >= AcrossTicks))
        {
            ScanContext[] fieldContexts = _fieldContexts ??= FieldContexts();
            // The fields side by side, each into its own arena, whose nodes the batch's then
            // references: their bytes stay where they were decoded, until the batch is dead.
            WorkFan fan = _fan ??= WorkFan.Rent(_degree);
            FieldDecoding decoding = _decoding ??= new FieldDecoding(this);
            decoding.Order(fan.Items(_nodes.Length));
            fan.Run(decoding, _nodes.Length, null, rows);
            for (int i = 0; i < _nodes.Length; i++)
            {
                _nodes[i] = arena.ReferenceFrom(fieldContexts[i].Canonical, _nodes[i]);
            }

            _across = true;
        }
        else
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                _nodes[i] = Read(_context, i, rows);
            }

            _across = false;
        }

        _decoded += (Stopwatch.GetTimestamp() - began - _decoded) / 8;

        // A batch is a block: what a pruned one is not.
        _metrics.AddBlocksDecoded(1);
        int root = arena.AddStruct(_struct, rows, Validity.NonNullable, _nodes);
        _current = RecordBatch.Over(arena, root, _groupStart + _groupRead, _current);
        _groupRead += rows;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _abandon.CancelAsync().ConfigureAwait(false);
        _current?.Dispose();

        // The lanes still decoding batches ahead are waited for before their readers go.
        if (_piped >= 0)
        {
            _pipeline!.Finish();
            _piped = -1;
        }

        _pipeline?.Dispose();
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.Dispose();
        }

        await DropWindowsAsync().ConfigureAwait(false);
        foreach (SegmentRequestSet set in _windowReads)
        {
            set.Dispose();
        }

        // The group chosen ahead, and its read, waited for: their bytes go back with their requests.
        if (_planned is { } planned)
        {
            _planned = null;
            try
            {
                await planned.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A read nobody waits for has no one to report to.
            }
        }

        await _group.SettleAsync().ConfigureAwait(false);
        await _spare.SettleAsync().ConfigureAwait(false);
        _group.Dispose();
        _spare.Dispose();

        foreach (Encryption.PageDecryption? decryption in _decryptions)
        {
            decryption?.Dispose();
        }

        _indexes.Release();
        _indexes.Dispose();
        _offsets.Release();
        _offsets.Dispose();
        _dictionaries?.Dispose();
        _context.Dispose();
        if (_fieldContexts is { } contexts)
        {
            foreach (ScanContext context in contexts)
            {
                context.Dispose();
            }
        }

        if (_fan is { } fan)
        {
            _fan = null;
            WorkFan.Return(fan);
        }

        _abandon.Dispose();
    }

    /// <summary>
    /// Starts the row group's batches decoding ahead of the read, a field at a time, when its fields
    /// would decode side by side and are all flat, its rows read whole and in place, none pruned.
    /// </summary>
    private void Pipe()
    {
        if (!_mayDecodeAcross || _live is not null || _group.Windowed || _groupRead == _groupRows
            || !(_nodes.Length >= AcrossFields || _decoded >= AcrossTicks) || Array.Exists(_nested, nested => nested is not null))
        {
            return;
        }

        if (_pipeline is null)
        {
            ColumnChunkReader[] readers = new ColumnChunkReader[_nodes.Length];
            for (int i = 0; i < readers.Length; i++)
            {
                readers[i] = _readers[_flat[i]];
            }

            _pipeline = new FieldPipeline(readers, _context.Options, _degree);
        }

        _pipeline.Start(_groupRows - _groupRead, _batchRows);
        _piped = 0;
    }

    /// <summary>A context a field, with the batch's read options.</summary>
    private ScanContext[] FieldContexts()
    {
        ScanContext[] contexts = new ScanContext[_nodes.Length];
        for (int i = 0; i < contexts.Length; i++)
        {
            contexts[i] = new ScanContext([], _context.Options);
        }

        return contexts;
    }

    /// <summary>Field <paramref name="field"/>'s next <paramref name="rows"/> rows, decoded into <paramref name="context"/>.</summary>
    private int Read(ScanContext context, int field, int rows) =>
        _nested[field] is { } nested ? nested.Read(context, rows) : _readers[_flat[field]].Read(context, rows);

    /// <summary>
    /// A batch's fields decoded by the work fan, a field an item, each into its own context, on
    /// whichever of the scan's threads claims it.
    /// </summary>
    /// <remarks>
    /// The items go longest first, by what each field's took the batch before: the reading thread
    /// claims the first while the others wake, and the field that holds every batch is not the one
    /// that waits for a thread.
    /// </remarks>
    private sealed class FieldDecoding(ParquetBatches batches) : IFanWork
    {
        /// <summary>Per field, the ticks its item took the last time it ran.</summary>
        private readonly long[] _took = new long[batches._nodes.Length];

        /// <summary>Lays the fields out in <paramref name="order"/>, longest first.</summary>
        internal void Order(Span<int> order)
        {
            for (int f = 0; f < order.Length; f++)
            {
                int at = f;
                while (at > 0 && _took[order[at - 1]] < _took[f])
                {
                    order[at] = order[at - 1];
                    at--;
                }

                order[at] = f;
            }
        }

        public void Run(WorkFan fan, int item)
        {
            int field = fan.Item(item);
            long began = Stopwatch.GetTimestamp();
            batches._nodes[field] = batches.Read(batches._fieldContexts![field], field, fan.Value);
            _took[field] = Stopwatch.GetTimestamp() - began;
        }
    }

    /// <summary>
    /// Starts the next row group the scan's rows reach: the one chosen and read while the last was
    /// decoded, or chosen and read now; then, from a source that does not read in place, the choice
    /// and read of the one after it.
    /// </summary>
    private async ValueTask<bool> NextRowGroupAsync()
    {
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.Release();
        }

        await DropWindowsAsync().ConfigureAwait(false);
        _group.Release();
        bool chosen;
        if (_planned is { } planned)
        {
            _planned = null;
            chosen = await planned.ConfigureAwait(false);
            (_group, _spare) = (_spare, _group);
        }
        else
        {
            chosen = await ChooseAsync(_group, _cancellationToken).ConfigureAwait(false);
        }

        if (!chosen)
        {
            return false;
        }

        GroupRead read = _group;
        ParquetFooter footer = _file.Footer;
        _rowGroup = read.RowGroup;
        _live = read.Live;
        Task reading = read.Reading!;
        read.Reading = null;
        await reading.ConfigureAwait(false);
        int next = 0;
        for (int i = 0; i < _readers.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(_rowGroup, _leaves[i]);
            _chunkStarts[i] = _file.ChunkRange(chunk).Start;
            _readers[i].Encrypted(chunk.IsEncrypted ? Decryption(i, chunk) : null);
            if (read.Maps[i] is not { } map)
            {
                _readers[i].Start(read.Chunks.GetBuffer(read.RunSlots[next++].Slot), chunk.Codec, read.Group.RowCount);
                _dictionaries?.Hand(_leaves[i], _readers[i]);
                continue;
            }

            _runs.Clear();
            for (; next < read.RunSlots.Count && read.RunSlots[next].Reader == i; next++)
            {
                _runs.Add((read.RunSlots[next].Start, read.Chunks.GetBuffer(read.RunSlots[next].Slot)));
            }

            _readers[i].Start(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_runs), _file.ChunkRange(chunk).Length, map, chunk.Codec, read.Group.RowCount);
            _dictionaries?.Hand(_leaves[i], _readers[i]);
        }

        if (read.Windowed)
        {
            _window = 1;
            _released = 1;
            _ahead = read.Windows.Count > 1 ? ReadAheadAsync(read.Windows[1], WindowRead(1)) : null;
        }

        // The next group chosen and read now, on a thread of the pool, while this one is decoded: a
        // read the source completes before it returns, from the system's cache, then delays no batch.
        // Its dictionaries handed over above, the pruning holds none of this one's.
        if (_readsAhead)
        {
            GroupRead spare = _spare;
            CancellationToken abandon = _abandon.Token;
            _planned = Task.Run(() => ChooseAsync(spare, abandon), abandon);
        }

        _groupStart = read.Group.FirstRow;
        _groupRows = read.End;
        _groupRead = 0;
        if (read.First > 0)
        {
            SkipRows((int)read.First);
            _groupRead = read.First;
        }

        return true;
    }

    /// <summary>
    /// Chooses the next row group the scan's rows reach that its statistics, page index, Bloom
    /// filters and dictionaries leave rows of, into <paramref name="read"/>, and starts the read of
    /// its chunks, or of the first of its windows: false when none is left.
    /// </summary>
    private async Task<bool> ChooseAsync(GroupRead read, CancellationToken cancellationToken)
    {
        ParquetFooter footer = _file.Footer;
        RowGroupEntry group;
        BlockMask? live;
        while (true)
        {
            do
            {
                if (++_chosen >= footer.RowGroups.Length)
                {
                    return false;
                }

                group = footer.RowGroups[_chosen];
            }
            while (!_read[_chosen] || group.RowCount == 0 || (_rows is { } range && (group.FirstRow >= range.End || group.FirstRow + group.RowCount <= range.Start)));

            if (!_anticipated)
            {
                // A file the session maps is mapped now, so that the chunks are read in place.
                (_file.Reader as IReadAnticipation)?.AnticipateData();
                _anticipated = true;
            }

            // The page index of the filter's columns rules batches out; a group it leaves none of
            // is not read. The offset indexes it reads place those columns' pages for the read.
            Array.Clear(read.Locations);
            live = _pruning is null ? null
                : await PagePruning.LiveAsync(_pruning, _chosen, group.RowCount, _batchRows, _indexes, _metrics, cancellationToken, read.Locations).ConfigureAwait(false);
            if (live is { LiveCount: 0 })
            {
                _metrics.AddBlocksPruned(live.BlockCount);
                continue;
            }

            // Then the Bloom filters of the columns the filter's equalities ask about, and the
            // dictionaries of the columns it reads, each of which rules a group out whole.
            if (_bloom && await BloomPruning.RulesOutAsync(_pruning!, _chosen, _indexes, _metrics, cancellationToken).ConfigureAwait(false))
            {
                _metrics.AddBlocksPruned((int)((group.RowCount + _batchRows - 1) / _batchRows));
                continue;
            }

            if (_dictionaries is not null && await _dictionaries.RulesOutAsync(_chosen, _metrics, cancellationToken).ConfigureAwait(false))
            {
                _metrics.AddBlocksPruned((int)((group.RowCount + _batchRows - 1) / _batchRows));
                continue;
            }

            break;
        }

        // A range of rows that starts or ends inside the group: the batches before it stepped over,
        // and none read past it. Its first and last batches hold rows outside it, which the scan trims.
        (long first, long end) = ChunkReads.Window(_rows, group.FirstRow, group.RowCount, _batchRows);
        read.RowGroup = _chosen;
        read.Group = group;
        read.Live = live;
        read.First = first;
        read.End = end;
        bool sparse = ChunkReads.Sparse(_file, first, end, group.RowCount, live);
        read.Windowed = false;
        read.Windows.Clear();
        if (sparse || ChunkReads.MayWindow(_file, _chosen, _leaves))
        {
            await ChunkReads.OffsetsAsync(_file, _chosen, _leaves, read.Locations, _offsets, _metrics, cancellationToken).ConfigureAwait(false);
            read.Windowed = ChunkReads.Windows(_file, _chosen, _leaves, read.Locations, live, first, end, _batchRows, group.RowCount, read.Windows, read.Maps);
        }

        long bytes = 0;
        read.RunSlots.Clear();
        for (int i = 0; i < _readers.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(_chosen, _leaves[i]);
            if (chunk.Hidden)
            {
                throw new ParquetUnsupportedException("column key", ParquetComponentKind.Encryption,
                    $"The column '{footer.ColumnPaths[_leaves[i]]}' is encrypted with {Encryption.FileDecryptor.Describe(chunk.KeyMetadata.Of(footer.Bytes))}, whose key is not given.");
            }

            if (read.Windowed)
            {
                // Its ranges are the windows'.
                continue;
            }

            _ranges.Clear();
            read.Maps[i] = ChunkReads.Plan(_file, chunk, read.Locations[_leaves[i]], sparse, live, first, end, _batchRows, group.RowCount, _ranges);
            long start = _file.ChunkRange(chunk).Start;
            foreach ((long from, long to) in _ranges)
            {
                int slot = read.Chunks.Add(new SegmentSpec((ulong)from, (uint)(to - from), 0, 0, 0));
                read.RunSlots.Add((i, (int)(from - start), slot));
                bytes += to - from;
            }
        }

        if (read.Windowed)
        {
            // The first window with the group; the next read once the group starts, while the first is decoded.
            foreach ((int reader, long from, long to) in read.Windows[0].Runs)
            {
                int slot = read.Chunks.Add(new SegmentSpec((ulong)from, (uint)(to - from), 0, 0, 0));
                read.RunSlots.Add((reader, (int)(from - _file.ChunkRange(footer.Chunk(_chosen, _leaves[reader])).Start), slot));
                bytes += to - from;
            }
        }

        ScanCounters.Note(_metrics, read.RunSlots.Count, bytes);
        read.Reading = _file.Reader.ReadManyAsync(read.Chunks, cancellationToken).AsTask();
        return true;
    }

    /// <summary>
    /// Gives the readers the pages of every window up to the one that serves batch
    /// <paramref name="batch"/>, each read ahead while the one before it was decoded, and starts the
    /// read of the window after the last given.
    /// </summary>
    private async ValueTask WindowsAsync(int batch)
    {
        ParquetFooter footer = _file.Footer;
        List<ReadWindow> windows = _group.Windows;
        while (_window < windows.Count && windows[_window].FirstBatch <= batch)
        {
            ReadWindow window = windows[_window];
            SegmentRequestSet read = _windowReads[_window - 1];
            Task ahead = _ahead!;
            _ahead = null;
            await ahead.ConfigureAwait(false);
            int run = 0;
            for (int i = 0; i < _readers.Length; i++)
            {
                _runs.Clear();
                long start = _chunkStarts[i];
                for (; run < window.Runs.Count && window.Runs[run].Reader == i; run++)
                {
                    _runs.Add(((int)(window.Runs[run].From - start), read.GetBuffer(window.Slots[run])));
                }

                if (_runs.Count > 0)
                {
                    _readers[i].Extend(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_runs));
                }
            }

            _window++;
            if (_window < windows.Count)
            {
                _ahead = ReadAheadAsync(windows[_window], WindowRead(_window));
            }
        }
    }

    /// <summary>The read of <paramref name="window"/> into <paramref name="read"/>, started now and counted as the scan's.</summary>
    private Task ReadAheadAsync(ReadWindow window, SegmentRequestSet read)
    {
        long bytes = 0;
        window.Slots.Clear();
        foreach ((int _, long from, long to) in window.Runs)
        {
            window.Slots.Add(read.Add(new SegmentSpec((ulong)from, (uint)(to - from), 0, 0, 0)));
            bytes += to - from;
        }

        // On a thread of the pool: a read the source completes before it returns, from the system's
        // cache, then runs beside the decoding of the window before it.
        ScanCounters.Note(_metrics, window.Runs.Count, bytes);
        CancellationToken abandon = _abandon.Token;
        ISegmentReader source = _file.Reader;
        return Task.Run(() => source.ReadManyAsync(read, abandon).AsTask(), abandon);
    }

    /// <summary>The request that reads window <paramref name="window"/>, past the first, made the first time a group has so many.</summary>
    private SegmentRequestSet WindowRead(int window)
    {
        while (_windowReads.Count < window)
        {
            _windowReads.Add(new SegmentRequestSet());
        }

        return _windowReads[window - 1];
    }

    /// <summary>
    /// Waits for a window read ahead the scan no longer needs, its failure dropped, and gives back what
    /// the group's windows read.
    /// </summary>
    private async ValueTask DropWindowsAsync()
    {
        if (_ahead is { } ahead)
        {
            _ahead = null;
            try
            {
                await ahead.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A read nobody waits for has no one to report to.
            }
        }

        foreach (SegmentRequestSet read in _windowReads)
        {
            read.Release();
        }

        _window = 0;
        _released = 0;
    }

    /// <summary>
    /// Gives back, in order, the windows past the first whose every range each reader is past, its
    /// next byte and every page it holds beyond the range's end.
    /// </summary>
    private void ReleasePassed()
    {
        List<ReadWindow> windows = _group.Windows;
        while (_released < _window)
        {
            foreach ((int reader, long _, long to) in windows[_released].Runs)
            {
                if (_readers[reader].Earliest < to - _chunkStarts[reader])
                {
                    return;
                }
            }

            _windowReads[_released - 1].Release();
            _released++;
        }
    }

    /// <summary>
    /// A row group chosen and read: where it is, the batches its page index leaves, its chunks'
    /// pages placed, its windows, and the request that reads its chunks, or the first of its windows.
    /// </summary>
    private sealed class GroupRead(int leaves, int readers) : IDisposable
    {
        internal int RowGroup { get; set; }

        internal RowGroupEntry Group { get; set; }

        /// <summary>The rows of the group the scan reads, from the first batch they reach to the end of the last.</summary>
        internal long First { get; set; }

        internal long End { get; set; }

        /// <summary>The batches the page index leaves, or null when it rules none out.</summary>
        internal BlockMask? Live { get; set; }

        /// <summary>Per leaf of the file, the offset index of its chunk, where one was read.</summary>
        internal PageLocation[]?[] Locations { get; } = new PageLocation[]?[leaves];

        /// <summary>Per reader, the pages of its chunk placed for a read of some of them; null for a chunk read whole.</summary>
        internal PageLocation[]?[] Maps { get; } = new PageLocation[]?[readers];

        /// <summary>Whether the group is read in windows, and the windows: the first read with the group.</summary>
        internal bool Windowed { get; set; }

        internal List<ReadWindow> Windows { get; } = [];

        /// <summary>The request that reads the group's chunks, or its first window.</summary>
        internal SegmentRequestSet Chunks { get; } = new();

        /// <summary>The ranges of the chunks the request reads: whose they are, where each starts in its chunk, and its slot.</summary>
        internal List<(int Reader, int Start, int Slot)> RunSlots { get; } = [];

        /// <summary>The request's read, in flight until the group starts.</summary>
        internal Task? Reading { get; set; }

        /// <summary>Waits for a read the scan no longer needs, its failure dropped.</summary>
        internal async ValueTask SettleAsync()
        {
            if (Reading is { } reading)
            {
                Reading = null;
                try
                {
                    await reading.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A read nobody waits for has no one to report to.
                }
            }
        }

        /// <summary>Gives back what the group read, for the next group it is chosen for.</summary>
        internal void Release() => Chunks.Release();

        public void Dispose() => Chunks.Dispose();
    }

    /// <summary>
    /// What decrypts the pages of reader <paramref name="reader"/>'s chunk <paramref name="chunk"/> of
    /// the row group being read: the reader's own, made again only where the chunk's key is another.
    /// </summary>
    private Encryption.PageDecryption Decryption(int reader, in ColumnChunkMetadata chunk)
    {
        ParquetFooter footer = _file.Footer;
        Encryption.FileDecryptor decryptor = footer.Decryptor!;
        int leaf = _leaves[reader];
        byte[] key = decryptor.ColumnKey(footer.ColumnPaths[leaf], chunk.KeyMetadata.Of(footer.Bytes), chunk.Crypto == ChunkCrypto.FooterKey)
            ?? throw new ParquetUnsupportedException("column key", ParquetComponentKind.Encryption,
                $"The column '{footer.ColumnPaths[leaf]}' is encrypted with {Encryption.FileDecryptor.Describe(chunk.KeyMetadata.Of(footer.Bytes))}, whose key is not given.");
        Encryption.PageDecryption? decryption = _decryptions[reader];
        if (decryption is null || !decryption.Under(key))
        {
            decryption?.Dispose();
            decryption = new Encryption.PageDecryption(key, decryptor);
            _decryptions[reader] = decryption;
        }

        bool dictionary = chunk.DictionaryPageOffset > 0 && chunk.DictionaryPageOffset < chunk.DataPageOffset;
        decryption.Start(footer.Ordinal(_rowGroup), leaf, dictionary);
        return decryption;
    }

    /// <summary>Steps every column over its next <paramref name="rows"/> rows: a page they cover whole by its header alone.</summary>
    private void SkipRows(int rows)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (_nested[i] is { } nested)
            {
                nested.Skip(_context, rows);
            }
            else
            {
                _readers[_flat[i]].Skip(_context, rows);
            }
        }
    }

    /// <summary>The rows of a batch of <paramref name="spec"/>: <see cref="BatchRows"/>, or fewer when the scan asks for fewer.</summary>
    internal static int RowsOf(ScanSpec spec) => spec.Options.BatchRows is > 0 and < BatchRows ? spec.Options.BatchRows : BatchRows;

    private static int[] Every(int count)
    {
        int[] all = new int[count];
        for (int i = 0; i < count; i++)
        {
            all[i] = i;
        }

        return all;
    }
}
