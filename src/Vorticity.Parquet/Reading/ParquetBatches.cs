using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A Parquet file's rows as batches of the columns a scan reads, a row group after the other: every
/// column chunk of the row group read in one request, or, from a source that does not read in place,
/// a large group in windows of batches, each read while the window before it is decoded; then cut a
/// batch at a time, each borrowed until the next is asked for.
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
    private readonly int _batchRows;
    private readonly ScanContext _context;
    private readonly SegmentRequestSet _chunks = new();

    /// <summary>Every column the scan reads: a flat field's own, and those under a nested field.</summary>
    private readonly ColumnChunkReader[] _readers;

    /// <summary>A field's assembler when it nests, else null.</summary>
    private readonly NestedFieldReader?[] _nested;

    /// <summary>A flat field's reader.</summary>
    private readonly int[] _flat;
    private readonly int[] _nodes;

    /// <summary>The filter's columns the page index may bound, or null when the scan prunes nothing.</summary>
    private readonly FilterColumns? _pruning;

    /// <summary>Whether the filter asks equalities Bloom filters may answer, and the scan reads indexes.</summary>
    private readonly bool _bloom;
    private readonly SegmentRequestSet _indexes = new();

    /// <summary>The pruning of row groups by the dictionaries of the filter's columns, or null when no part of it reads one alone.</summary>
    private readonly DictionaryPruning? _dictionaries;

    /// <summary>The batches of the row group being read the page index leaves, or null when it rules none out.</summary>
    private BlockMask? _live;

    /// <summary>Per leaf of the file, the offset index of its chunk in the row group being read, where one was read.</summary>
    private readonly PageLocation[]?[] _locations;
    private readonly SegmentRequestSet _offsets = new();

    /// <summary>Per reader, the pages of its chunk placed for a read of some of them; null for a chunk read whole.</summary>
    private readonly PageLocation[]?[] _maps;

    /// <summary>Per reader, what decrypts its column's pages, made the first time a chunk of it is encrypted.</summary>
    private readonly Encryption.PageDecryption?[] _decryptions;

    /// <summary>The ranges of the chunks the row group reads: whose they are, where each starts in its chunk, and its slot.</summary>
    private readonly List<(int Reader, int Start, int Slot)> _runSlots = [];
    private readonly List<(int Start, VortexBuffer Bytes)> _runs = [];
    private readonly List<(long From, long To)> _ranges = [];
    /// <summary>
    /// The windows the row group being read is cut into, when it is: the first read with the group,
    /// each other ahead of the batches it serves, while the window before it is decoded.
    /// </summary>
    private readonly List<ReadWindow> _windows = [];

    /// <summary>Per window past the first, the request that reads it, released with the group.</summary>
    private readonly List<SegmentRequestSet> _windowReads = [];

    /// <summary>The next window whose pages the readers are given, and its read in flight.</summary>
    private int _window;
    private Task? _ahead;

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
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.VerifyChecksums = file.Options.VerifyChecksums;
            reader.Counters = file.Counters;
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

        _maps = new PageLocation[]?[_readers.Length];
        _decryptions = new Encryption.PageDecryption?[_readers.Length];
        _locations = new PageLocation[]?[schema.Columns.Length];
        _nodes = new int[fields.Length];
        _pruning = FilterColumns.For(file, spec);
        _bloom = _pruning is not null && spec.Options.UseIndexes && BloomPruning.Asks(spec.Filter!);
        _dictionaries = DictionaryPruning.For(_pruning, _context);
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    public async ValueTask<bool> MoveNextAsync()
    {
        // The batch handed out last is dead: what it read goes back before the next is cut.
        _current?.Dispose();
        _context.ResetBatch();
        int rows;
        while (true)
        {
            while (_groupRead == _groupRows)
            {
                if (!await NextRowGroupAsync().ConfigureAwait(false))
                {
                    return false;
                }
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
            if (_window < _windows.Count && _groupRead < _groupRows)
            {
                await WindowsAsync((int)(_groupRead / _batchRows)).ConfigureAwait(false);
            }

            SkipRows(checked((int)(_groupRead - from)));
            _metrics.AddBlocksPruned(pruned);
        }

        if (_window < _windows.Count)
        {
            await WindowsAsync((int)(_groupRead / _batchRows)).ConfigureAwait(false);
        }

        for (int i = 0; i < _nodes.Length; i++)
        {
            _nodes[i] = _nested[i] is { } nested ? nested.Read(_context, rows) : _readers[_flat[i]].Read(_context, rows);
        }

        // A batch is a block: what a pruned one is not.
        _metrics.AddBlocksDecoded(1);
        CanonicalArena arena = _context.Canonical;
        int root = arena.AddStruct(_struct, rows, Validity.NonNullable, _nodes);
        _current = RecordBatch.Over(arena, root, _groupStart + _groupRead, _current);
        _groupRead += rows;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _current?.Dispose();
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.Dispose();
        }

        await DropWindowsAsync().ConfigureAwait(false);
        foreach (SegmentRequestSet set in _windowReads)
        {
            set.Dispose();
        }

        foreach (Encryption.PageDecryption? decryption in _decryptions)
        {
            decryption?.Dispose();
        }

        _chunks.Release();
        _chunks.Dispose();
        _indexes.Release();
        _indexes.Dispose();
        _offsets.Release();
        _offsets.Dispose();
        _dictionaries?.Dispose();
        _context.Dispose();
    }

    /// <summary>Reads the next row group the scan's rows reach: every chunk it reads, in one request, or the first of its windows.</summary>
    private async ValueTask<bool> NextRowGroupAsync()
    {
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.Release();
        }

        _chunks.Release();
        await DropWindowsAsync().ConfigureAwait(false);
        ParquetFooter footer = _file.Footer;
        RowGroupEntry group;
        while (true)
        {
            do
            {
                if (++_rowGroup >= footer.RowGroups.Length)
                {
                    return false;
                }

                group = footer.RowGroups[_rowGroup];
            }
            while (!_read[_rowGroup] || group.RowCount == 0 || (_rows is { } range && (group.FirstRow >= range.End || group.FirstRow + group.RowCount <= range.Start)));

            if (!_anticipated)
            {
                // A file the session maps is mapped now, so that the chunks are read in place.
                (_file.Reader as IReadAnticipation)?.AnticipateData();
                _anticipated = true;
            }

            // The page index of the filter's columns rules batches out; a group it leaves none of
            // is not read. The offset indexes it reads place those columns' pages for the read.
            Array.Clear(_locations);
            _live = _pruning is null ? null
                : await PagePruning.LiveAsync(_pruning, _rowGroup, group.RowCount, _batchRows, _indexes, _metrics, _cancellationToken, _locations).ConfigureAwait(false);
            if (_live is { LiveCount: 0 })
            {
                _metrics.AddBlocksPruned(_live.BlockCount);
                continue;
            }

            // Then the Bloom filters of the columns the filter's equalities ask about, and the
            // dictionaries of the columns it reads, each of which rules a group out whole.
            if (_bloom && await BloomPruning.RulesOutAsync(_pruning!, _rowGroup, _indexes, _metrics, _cancellationToken).ConfigureAwait(false))
            {
                _metrics.AddBlocksPruned((int)((group.RowCount + _batchRows - 1) / _batchRows));
                continue;
            }

            if (_dictionaries is not null && await _dictionaries.RulesOutAsync(_rowGroup, _metrics, _cancellationToken).ConfigureAwait(false))
            {
                _metrics.AddBlocksPruned((int)((group.RowCount + _batchRows - 1) / _batchRows));
                continue;
            }

            break;
        }

        // A range of rows that starts or ends inside the group: the batches before it stepped over,
        // and none read past it. Its first and last batches hold rows outside it, which the scan trims.
        (long first, long end) = ChunkReads.Window(_rows, group.FirstRow, group.RowCount, _batchRows);
        bool sparse = ChunkReads.Sparse(_file, first, end, group.RowCount, _live);
        bool windowed = false;
        if (sparse || ChunkReads.MayWindow(_file, _rowGroup, _leaves))
        {
            await ChunkReads.OffsetsAsync(_file, _rowGroup, _leaves, _locations, _offsets, _metrics, _cancellationToken).ConfigureAwait(false);
            windowed = ChunkReads.Windows(_file, _rowGroup, _leaves, _locations, _live, first, end, _batchRows, group.RowCount, _windows, _maps);
        }

        long bytes = 0;
        _runSlots.Clear();
        for (int i = 0; i < _readers.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(_rowGroup, _leaves[i]);
            if (chunk.Hidden)
            {
                throw new ParquetUnsupportedException("column key", ParquetComponentKind.Encryption,
                    $"The column '{footer.ColumnPaths[_leaves[i]]}' is encrypted with {Encryption.FileDecryptor.Describe(chunk.KeyMetadata.Of(footer.Bytes))}, whose key is not given.");
            }

            if (windowed)
            {
                // Its ranges are the windows'.
                continue;
            }

            _ranges.Clear();
            _maps[i] = ChunkReads.Plan(_file, chunk, _locations[_leaves[i]], sparse, _live, first, end, _batchRows, group.RowCount, _ranges);
            long start = _file.ChunkRange(chunk).Start;
            foreach ((long from, long to) in _ranges)
            {
                int slot = _chunks.Add(new SegmentSpec((ulong)from, (uint)(to - from), 0, 0, 0));
                _runSlots.Add((i, (int)(from - start), slot));
                bytes += to - from;
            }
        }

        if (windowed)
        {
            // The first window with the group; the next read now, while the first is decoded.
            foreach ((int reader, long from, long to) in _windows[0].Runs)
            {
                int slot = _chunks.Add(new SegmentSpec((ulong)from, (uint)(to - from), 0, 0, 0));
                _runSlots.Add((reader, (int)(from - _file.ChunkRange(footer.Chunk(_rowGroup, _leaves[reader])).Start), slot));
                bytes += to - from;
            }
        }

        ScanCounters.Note(_metrics, _runSlots.Count, bytes);
        await _file.Reader.ReadManyAsync(_chunks, _cancellationToken).ConfigureAwait(false);
        int next = 0;
        for (int i = 0; i < _readers.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(_rowGroup, _leaves[i]);
            _readers[i].Encrypted(chunk.IsEncrypted ? Decryption(i, chunk) : null);
            if (_maps[i] is not { } map)
            {
                _readers[i].Start(_chunks.GetBuffer(_runSlots[next++].Slot), chunk.Codec, group.RowCount);
                _dictionaries?.Hand(_leaves[i], _readers[i]);
                continue;
            }

            _runs.Clear();
            for (; next < _runSlots.Count && _runSlots[next].Reader == i; next++)
            {
                _runs.Add((_runSlots[next].Start, _chunks.GetBuffer(_runSlots[next].Slot)));
            }

            _readers[i].Start(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_runs), _file.ChunkRange(chunk).Length, map, chunk.Codec, group.RowCount);
            _dictionaries?.Hand(_leaves[i], _readers[i]);
        }

        if (windowed)
        {
            _window = 1;
            _ahead = _windows.Count > 1 ? ReadAheadAsync(_windows[1], WindowRead(1)) : null;
        }

        _groupStart = group.FirstRow;
        _groupRows = end;
        _groupRead = 0;
        if (first > 0)
        {
            SkipRows((int)first);
            _groupRead = first;
        }

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
        while (_window < _windows.Count && _windows[_window].FirstBatch <= batch)
        {
            ReadWindow window = _windows[_window];
            SegmentRequestSet read = _windowReads[_window - 1];
            Task ahead = _ahead!;
            _ahead = null;
            await ahead.ConfigureAwait(false);
            int run = 0;
            for (int i = 0; i < _readers.Length; i++)
            {
                _runs.Clear();
                long start = _file.ChunkRange(footer.Chunk(_rowGroup, _leaves[i])).Start;
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
            if (_window < _windows.Count)
            {
                _ahead = ReadAheadAsync(_windows[_window], WindowRead(_window));
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

        ScanCounters.Note(_metrics, window.Runs.Count, bytes);
        return _file.Reader.ReadManyAsync(read, _cancellationToken).AsTask();
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

        _windows.Clear();
        _window = 0;
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
