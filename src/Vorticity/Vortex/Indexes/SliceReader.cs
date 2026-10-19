using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>
/// Reads the rows of a sliced file's time slices without its footer: the postscript names the index
/// directory, the directory the slice table and the regions of the chunk directory, and those the
/// chunks that hold a slice's rows, decoded from their own bytes against the schema the caller knows.
/// </summary>
/// <remarks>
/// <para>
/// An open reads the footer, a node per chunk and column, which for an object of a period runs to
/// megabytes. A question that needs the rows of a slice or two (the edges of a window, an exact
/// lookup, the rows to show) needs none of it. Opening this reader costs three small dependent
/// requests (the EOF marker, the postscript, the index directory). The first read of a column adds
/// one for that column's chunk directory, then each slice costs one request, its chunks read together.
/// </para>
/// <para>
/// Like every index the directories are hints: a file without them, or with ones that do not decode or
/// do not cover the file's rows, opens no reader here, and its rows read through a whole open. Reads
/// may run at once: a column's chunks loaded twice are the same chunks.
/// </para>
/// </remarks>
internal sealed class SliceReader
{
    private readonly ISegmentReader _source;
    private readonly DType _schema;
    private readonly VortexReadOptions _options;
    private readonly string[] _encodings;
    private readonly IReadOnlyList<IndexSegment> _regions;
    private readonly ChunkSpan[]?[] _columns;
    private readonly long _rows;

    private SliceReader(
        ISegmentReader source, DType schema, VortexReadOptions options, SliceTable slices, string[] encodings,
        IReadOnlyList<IndexSegment> regions, long rows)
    {
        _source = source;
        _schema = schema;
        _options = options;
        _encodings = encodings;
        _regions = regions;
        _columns = new ChunkSpan[]?[regions.Count];
        _rows = rows;
        Slices = slices;
    }

    /// <summary>The file's time slices.</summary>
    internal SliceTable Slices { get; }

    /// <summary>
    /// A reader of the slices of the file <paramref name="source"/> holds, or null when it carries no
    /// usable slice table and chunk directory.
    /// </summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="fileLength">The file's length.</param>
    /// <param name="schema">The file's schema, a struct of its columns: the dataset's, which the file's footer would repeat.</param>
    /// <param name="options">What bounds the decodes; the defaults when null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="VortexFormatException">The file's end is not a Vortex file's.</exception>
    internal static async ValueTask<SliceReader?> OpenAsync(
        ISegmentReader source, long fileLength, DType schema, VortexReadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (schema.Kind != DTypeKind.Struct)
        {
            throw new ArgumentException("A sliced file's schema is a struct of its columns.", nameof(schema));
        }

        if (await VortexFile.ReadMetadataSpecAsync(source, fileLength, IndexDirectory.MetadataKey, cancellationToken)
            .ConfigureAwait(false) is not { } spec)
        {
            return null;
        }

        // Every run lies before the directory, which bounds them from above as at an open.
        IndexDirectory? directory;
        using (SegmentOwner owner = await source.ReadAsync(spec, cancellationToken).ConfigureAwait(false))
        {
            if (!IndexDirectory.TryParse(owner.Buffer.Span, IndexDirectory.UnknownRows, spec.Offset, out directory, out _))
            {
                return null;
            }
        }

        SliceTable? slices = null;
        string[]? encodings = null;
        IReadOnlyList<IndexSegment>? regions = null;
        foreach (IndexEntry entry in directory!.Entries)
        {
            if (entry.Kind == IndexKinds.Slices)
            {
                slices ??= SliceTable.FromBytes(entry.Options);
            }
            else if (entry.Kind == IndexKinds.SliceChunks && entry.Runs is [IndexRun run])
            {
                encodings = SliceChunks.EncodingsFrom(entry.Options);
                regions = run.Payload;
            }
        }

        long rows = (long)directory.RowCount;
        return slices is not null && encodings is not null && regions is not null
            && slices.Rows == rows && regions.Count == schema.FieldCount
            ? new SliceReader(source, schema, options ?? VortexReadOptions.Default, slices, encodings, regions, rows)
            : null;
    }

    /// <summary>
    /// A reader of the slices of a file its table of contents <paramref name="contents"/> describes, opened
    /// with no read at all: the contents name the file's schema, its slice table and the chunk directories
    /// the directory would. Null when they do not cover the schema's columns.
    /// </summary>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static SliceReader? Open(ISegmentReader source, VortexReadOptions? options, SynopsisContents contents)
    {
        ArgumentNullException.ThrowIfNull(source);
        DType schema = contents.SchemaIn(new DTypeArena());
        return schema.Kind == DTypeKind.Struct && contents.ChunkDirectories.Count == schema.FieldCount
            ? new SliceReader(source, schema, options ?? VortexReadOptions.Default, contents.Slices, [.. contents.Encodings], contents.ChunkDirectories, contents.Slices.Rows)
            : null;
    }

    /// <summary>
    /// The rows of slice <paramref name="slice"/>, as one batch whose first row is the slice's first row
    /// in the file: of every column, or of <paramref name="columns"/>, top-level indices in schema
    /// order. The chunks that hold them are read in one request set, after the chunk directories of the
    /// columns not read before.
    /// </summary>
    /// <param name="slice">The slice's index in <see cref="Slices"/>.</param>
    /// <param name="columns">The columns to read, in the order the batch holds them; every column when null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A batch the caller disposes.</returns>
    /// <exception cref="VortexFormatException">A column's chunk directory or a chunk does not decode.</exception>
    internal ValueTask<RecordBatch> ReadSliceAsync(int slice, int[]? columns, CancellationToken cancellationToken) =>
        ReadSpanAsync(Slices.Slices[slice], columns, cancellationToken);

    /// <summary>
    /// The rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="rows"/> of the file, as
    /// one batch, read as a slice's are: the blocks a lookup found, say. The chunks that hold them are read in
    /// one request set, after the chunk directories of the columns not read before.
    /// </summary>
    internal ValueTask<RecordBatch> ReadRowsAsync(long first, long rows, int[]? columns, CancellationToken cancellationToken) =>
        ReadSpanAsync(new TimeSlice(0, 1, first, rows), columns, cancellationToken);

    /// <summary>
    /// The rows of each of <paramref name="spans"/>, a batch per span in order, as <see cref="ReadRowsAsync"/>
    /// reads one: the chunks of them all read in one request set, a chunk two spans share read once, after
    /// the chunk directories of the columns not read before. Two round trips cold, one once the directories
    /// are in hand, however many spans. The caller disposes the batches.
    /// </summary>
    /// <param name="spans">The spans, by their first row in the file and their rows.</param>
    /// <param name="columns">The columns to read, in the order the batches hold them; every column when null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="VortexFormatException">A column's chunk directory or a chunk does not decode.</exception>
    internal async ValueTask<RecordBatch[]> ReadSpansAsync(IReadOnlyList<TimeSlice> spans, int[]? columns, CancellationToken cancellationToken)
    {
        int[] fields = columns ?? AllColumns();
        await LoadChunksAsync(fields, cancellationToken).ConfigureAwait(false);
        using SegmentRequestSet shared = new SegmentRequestSet();
        int[][] slots = new int[spans.Count][];
        for (int s = 0; s < slots.Length; s++)
        {
            slots[s] = ChunksOf(shared, spans[s], fields);
        }

        await _source.ReadManyAsync(shared, cancellationToken).ConfigureAwait(false);

        // Each batch owns a context of its own, its chunks views of the shared reads it retains.
        RecordBatch[] batches = new RecordBatch[spans.Count];
        int made = 0;
        try
        {
            for (; made < batches.Length; made++)
            {
                ScanContext context = new ScanContext(_encodings, _options);
                try
                {
                    int[] own = ChunksOf(context.Segments, spans[made], fields);
                    for (int i = 0; i < own.Length; i++)
                    {
                        if (!context.Segments.IsFilled(own[i]))
                        {
                            int read = slots[made][i];
                            context.Segments.SetSharedResult(own[i], shared.GetOwner(read), shared.GetBuffer(read));
                        }
                    }

                    context.Segments.Complete();
                    batches[made] = Batch(context, spans[made], fields, columns, own);
                }
                catch
                {
                    context.Dispose();
                    throw;
                }
            }

            return batches;
        }
        catch
        {
            for (int i = 0; i < made; i++)
            {
                batches[i].Dispose();
            }

            throw;
        }
    }

    private async ValueTask<RecordBatch> ReadSpanAsync(TimeSlice span, int[]? columns, CancellationToken cancellationToken)
    {
        int[] fields = columns ?? AllColumns();
        await LoadChunksAsync(fields, cancellationToken).ConfigureAwait(false);

        ScanContext context = new ScanContext(_encodings, _options);
        try
        {
            int[] slots = ChunksOf(context.Segments, span, fields);
            await _source.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
            return Batch(context, span, fields, columns, slots);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Registers in <paramref name="requests"/> the chunks of <paramref name="fields"/> that hold the span's rows: their slots, field by field.</summary>
    private int[] ChunksOf(SegmentRequestSet requests, TimeSlice span, int[] fields)
    {
        int chunkCount = 0;
        foreach (int field in fields)
        {
            chunkCount += SliceChunks.Overlapping(_columns[field]!, span.FirstRow, span.Rows).Length;
        }

        int[] slots = new int[chunkCount];
        int next = 0;
        foreach (int field in fields)
        {
            foreach (ChunkSpan chunk in SliceChunks.Overlapping(_columns[field]!, span.FirstRow, span.Rows))
            {
                slots[next++] = requests.Add(new SegmentSpec(chunk.Offset, chunk.Length, chunk.AlignmentExponent, 0, 0));
            }
        }

        return slots;
    }

    /// <summary>The span's rows decoded from the chunks <paramref name="context"/> holds, as a batch that adopts it.</summary>
    private RecordBatch Batch(ScanContext context, TimeSlice span, int[] fields, int[]? columns, int[] slots)
    {
        int length = checked((int)span.Rows);

        // A projection's struct is built in the context's own arena, and its fields decode at the
        // types it holds, so the batch's columns and its schema are the same nodes.
        DType schema = columns is null ? _schema : Projected(context.Types, fields);
        int[] decoded = new int[fields.Length];
        int next = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            decoded[i] = Column(context, schema.GetField(i), _columns[fields[i]]!, span, length, slots, ref next);
        }

        int root = context.Canonical.AddStruct(
            schema, length, schema.IsNullable ? Validity.AllValid : Validity.NonNullable, decoded);
        return RecordBatch.Adopt(context, root, span.FirstRow, null, null);
    }

    /// <summary>
    /// What column <paramref name="field"/> weighs in the file: the bytes of its chunks, as its chunk
    /// directory lists them, read the first time it is asked for.
    /// </summary>
    internal async ValueTask<long> ColumnBytesAsync(int field, CancellationToken cancellationToken)
    {
        await LoadChunksAsync([field], cancellationToken).ConfigureAwait(false);
        long bytes = 0;
        foreach (ChunkSpan chunk in _columns[field]!)
        {
            bytes += chunk.Length;
        }

        return bytes;
    }

    private int[] AllColumns()
    {
        int[] all = new int[_regions.Count];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = i;
        }

        return all;
    }

    /// <summary>Reads the chunk directories of the <paramref name="fields"/> not read yet, in one request set.</summary>
    private async ValueTask LoadChunksAsync(int[] fields, CancellationToken cancellationToken)
    {
        int missing = 0;
        foreach (int field in fields)
        {
            if ((uint)field >= (uint)_columns.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(fields), field, $"The file has {_columns.Length} columns.");
            }

            missing += _columns[field] is null ? 1 : 0;
        }

        if (missing == 0)
        {
            return;
        }

        using SegmentRequestSet requests = new SegmentRequestSet(missing);
        int[] slots = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            IndexSegment region = _regions[fields[i]];
            slots[i] = _columns[fields[i]] is null
                ? requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0))
                : -1;
        }

        await _source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < fields.Length; i++)
        {
            if (slots[i] < 0)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = requests.GetBuffer(slots[i]).Span;
            ChunkSpan[]? chunks = _regions[fields[i]].Holds(bytes) ? SliceChunks.ColumnFrom(bytes) : null;
            if (chunks is null || (chunks.Length == 0 ? 0 : chunks[^1].EndRow) != _rows)
            {
                throw new VortexFormatException(
                    $"The chunk directory of column {fields[i]} does not decode, or does not cover the file's {_rows} rows.");
            }

            _columns[fields[i]] = chunks;
        }
    }

    /// <summary>The struct of the <paramref name="fields"/> of the schema, built in <paramref name="arena"/>.</summary>
    private DType Projected(DTypeArena arena, int[] fields)
    {
        int[] names = new int[fields.Length];
        DType[] types = new DType[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            names[i] = arena.InternName(_schema.GetFieldNameUtf8(fields[i]));
            types[i] = DTypeImport.Into(arena, _schema.GetField(fields[i]));
        }

        return arena.Struct(names, types, _schema.Nullability);
    }

    /// <summary>One column's rows of the slice: its overlapping chunks decoded, cut to the slice and joined.</summary>
    private static int Column(
        ScanContext context, DType type, ChunkSpan[] column, TimeSlice span, int length, int[] slots, ref int next)
    {
        ReadOnlySpan<ChunkSpan> chunks = SliceChunks.Overlapping(column, span.FirstRow, span.Rows);
        int[] parts = new int[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            ChunkSpan chunk = chunks[i];
            context.Decode.LoadBlob(context.Segments.GetBuffer(slots[next++]));
            ArrayNode root = context.Nodes.Root;
            int node = context.Decode.DecodeRoot(in root, type, checked((int)chunk.Rows));
            long from = Math.Max(span.FirstRow, chunk.FirstRow);
            long to = Math.Min(span.FirstRow + span.Rows, chunk.EndRow);
            parts[i] = CanonicalSlice.Slice(context.Decode, node, (int)(from - chunk.FirstRow), (int)(to - from));
        }

        return parts.Length == 1 ? parts[0] : CanonicalConcat.Concat(context.Decode, type, length, parts);
    }
}
