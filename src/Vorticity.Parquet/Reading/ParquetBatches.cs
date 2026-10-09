using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
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
/// column chunk of the row group read in one request, then cut a batch at a time, each borrowed
/// until the next is asked for.
/// </summary>
/// <remarks>
/// A batch holds <see cref="BatchRows"/> rows, or fewer at the end of a row group, which a batch never
/// crosses: on a file this package wrote, that is one page of every column. The batches go through
/// one context, whose arena holds what a batch copies and whose caps bound what it decodes.
/// </remarks>
internal sealed class ParquetBatches : IAsyncEnumerator<RecordBatch>
{
    /// <summary>The rows of a batch: the block the writer cuts its pages on.</summary>
    internal const int BatchRows = 8_192;

    private readonly ParquetFile _file;
    private readonly RowRange? _rows;
    private readonly int[] _leaves;
    private readonly DType _struct;
    private readonly ScanCounters _metrics;
    private readonly CancellationToken _cancellationToken;
    private readonly int _batchRows;
    private readonly ScanContext _context;
    private readonly SegmentRequestSet _chunks = new();
    private readonly ColumnChunkReader[] _readers;
    private readonly int[] _slots;
    private readonly int[] _nodes;
    private RecordBatch? _current;
    private int _rowGroup = -1;
    private long _groupStart;
    private long _groupRows;
    private long _groupRead;
    private bool _anticipated;

    internal ParquetBatches(ParquetFile file, ScanSpec spec, int[]? columns, ScanCounters metrics, CancellationToken cancellationToken)
    {
        _file = file;
        _rows = spec.Rows;
        _metrics = metrics;
        _cancellationToken = cancellationToken;
        _batchRows = spec.Options.BatchRows is > 0 and < BatchRows ? spec.Options.BatchRows : BatchRows;
        ParquetSchema schema = file.Compiled;
        int[] fields = columns ?? Every(schema.Fields.Length);
        VortexField[] read = new VortexField[fields.Length];
        _leaves = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            ParquetField field = schema.Fields[fields[i]];
            if (field.Column < 0)
            {
                throw new ParquetUnsupportedException(field.Name, ParquetComponentKind.Feature,
                    $"The column '{field.Name}' is nested; this version of the reader reads flat columns only.");
            }

            read[i] = new VortexField(field.Name, field.Type);
            _leaves[i] = field.Column;
        }

        DTypeArena types = new();
        _struct = VortexTypes.ToDType(VortexSchema.Create(read), types);
        DType validity = types.Bool(Nullability.NonNullable);
        long cap = file.Options.MaxDecompressedSize;
        _context = new ScanContext([], new VortexReadOptions { MaxDecompressedBytes = cap });
        _readers = new ColumnChunkReader[fields.Length];
        for (int i = 0; i < _readers.Length; i++)
        {
            _readers[i] = new ColumnChunkReader(schema.Columns[_leaves[i]], _struct.GetField(i), validity, file.Session.Options.EnginePool, cap);
        }

        _slots = new int[fields.Length];
        _nodes = new int[fields.Length];
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    public async ValueTask<bool> MoveNextAsync()
    {
        // The batch handed out last is dead: what it read goes back before the next is cut.
        _current?.Dispose();
        _context.ResetBatch();
        while (_groupRead == _groupRows)
        {
            if (!await NextRowGroupAsync().ConfigureAwait(false))
            {
                return false;
            }
        }

        int rows = (int)Math.Min(_batchRows, _groupRows - _groupRead);
        for (int i = 0; i < _readers.Length; i++)
        {
            _nodes[i] = _readers[i].Read(_context, rows);
        }

        CanonicalArena arena = _context.Canonical;
        int root = arena.AddStruct(_struct, rows, Validity.NonNullable, _nodes);
        _current = RecordBatch.Over(arena, root, _groupStart + _groupRead, _current);
        _groupRead += rows;
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _current?.Dispose();
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.Dispose();
        }

        _chunks.Release();
        _chunks.Dispose();
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Reads the next row group the scan's rows reach: every chunk it reads, in one request.</summary>
    private async ValueTask<bool> NextRowGroupAsync()
    {
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.Release();
        }

        _chunks.Release();
        ParquetFooter footer = _file.Footer;
        RowGroupEntry group;
        do
        {
            if (++_rowGroup >= footer.RowGroups.Length)
            {
                return false;
            }

            group = footer.RowGroups[_rowGroup];
        }
        while (group.RowCount == 0 || (_rows is { } range && (group.FirstRow >= range.End || group.FirstRow + group.RowCount <= range.Start)));

        if (!_anticipated)
        {
            // A file the session maps is mapped now, so that the chunks are read in place.
            (_file.Reader as IReadAnticipation)?.AnticipateData();
            _anticipated = true;
        }

        long bytes = 0;
        for (int i = 0; i < _readers.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(_rowGroup, _leaves[i]);
            (long start, int length) = _file.ChunkRange(chunk);
            _slots[i] = _chunks.Add(new SegmentSpec((ulong)start, (uint)length, 0, 0, 0));
            bytes += length;
        }

        ScanCounters.Note(_metrics, _readers.Length, bytes);
        await _file.Reader.ReadManyAsync(_chunks, _cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < _readers.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(_rowGroup, _leaves[i]);
            _readers[i].Start(_chunks.GetBuffer(_slots[i]), chunk.Codec, group.RowCount);
        }

        _groupStart = group.FirstRow;
        _groupRows = group.RowCount;
        _groupRead = 0;
        return true;
    }

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
