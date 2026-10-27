using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
/// Reads spans of a file's rows without its footer: the postscript names the index directory, the
/// directory the regions of the chunk directory, and those the chunks that hold a span's rows, decoded
/// from their own bytes against the schema the caller knows; or the file's table of contents names them.
/// </summary>
/// <remarks>
/// <para>
/// An open reads the footer, a node per chunk and column, which for an object of a day's rows runs to
/// megabytes. A question that needs a few spans of rows (the granules a key's range keeps, an exact
/// lookup, the rows to show) needs none of it. Opening this reader costs three small dependent
/// requests (the EOF marker, the postscript, the index directory), none from a table of contents. The
/// first read of a column adds one for that column's chunk directory, then each span costs one request,
/// its chunks read together.
/// </para>
/// <para>
/// Like every index the directories are hints: a file without them, or with ones that do not decode or
/// do not cover the file's rows, opens no reader here, and its rows read through a whole open. Reads
/// may run at once: a column's chunks loaded twice are the same chunks.
/// </para>
/// <para>
/// A column's chunks lie in an array rented from the shared pool, given back when the reader is let go: a reader is
/// made for a read or two, the rows of a window's edges or of the blocks a lookup found, and each parsed its columns'
/// directories into arrays of their own, a quarter of a megabyte a column of an hour of 180 M rows.
/// </para>
/// </remarks>
internal sealed class SpanReader : IDisposable
{
    private readonly ISegmentReader _source;
    private readonly DType _schema;
    private readonly VortexReadOptions _options;
    private readonly string[] _encodings;
    private readonly IReadOnlyList<IndexSegment> _regions;
    private readonly ChunkSpan[]?[] _columns;
    private readonly int[] _counts;
    private readonly System.Threading.Lock _gate = new System.Threading.Lock();
    private readonly long _rows;

    // How the paged columns' directories lie, when the table of contents says, and what was read of them: null for a file none
    // of whose directories is paged, whose readers hold none of it.
    private readonly PagedColumns? _paged;

    // Each column's geometry, its region named by the table of contents, and what a read loaded of it: null for a file that
    // keeps none, whose chunks a read reads whole.
    private readonly IReadOnlyList<IndexSegment>? _markRegions;
    private readonly ColumnMarks?[] _marks;
    private readonly bool[] _marksRead;

    // Each column's object dictionary, whose codes its chunks hold: read with the chunks, and gathered through.
    private readonly IReadOnlyList<ColumnDictionary?> _dictionaries;

    private SpanReader(
        ISegmentReader source, DType schema, VortexReadOptions options, string[] encodings,
        IReadOnlyList<IndexSegment> regions, long rows, ObjectContents? contents = null, IReadOnlyList<ColumnDictionary?>? dictionaries = null)
    {
        _source = source;
        _schema = schema;
        _options = options;
        _encodings = encodings;
        _regions = regions;
        _columns = new ChunkSpan[]?[regions.Count];
        _counts = new int[regions.Count];
        _rows = rows;
        _paged = contents?.Paging is { } paging ? new PagedColumns(paging, regions.Count, contents) : null;
        _markRegions = contents?.Marks is { Count: > 0 } marks && marks.Count == regions.Count ? marks : null;
        _marks = new ColumnMarks?[regions.Count];
        _marksRead = new bool[regions.Count];
        _dictionaries = contents?.Dictionaries is { Count: > 0 } named ? named : dictionaries ?? [];
    }

    /// <summary>The file's rows.</summary>
    internal long Rows => _rows;

    /// <summary>Column <paramref name="field"/>'s object dictionary, or null when its chunks hold its values.</summary>
    private ColumnDictionary? DictionaryOf(int field) => field < _dictionaries.Count ? _dictionaries[field] : null;

    /// <summary>Whether column <paramref name="field"/>'s directory lies in pages, which a read of a span reads those of alone.</summary>
    private bool Paged(int field) => _paged is { } paged && paged.Paging.Pages(field);

    /// <summary>
    /// How a file's paged directories lie, and what a reader read of them: each column's page table, and its pages, each its
    /// chunks. The page tables are the contents' too (<see cref="ObjectContents.PageTable"/>), which every later read of
    /// the object starts from, its pages asked with their checksums.
    /// </summary>
    private sealed class PagedColumns(ChunkPaging paging, int columns, ObjectContents contents)
    {
        internal ChunkPaging Paging { get; } = paging;

        internal ObjectContents Contents { get; } = contents;

        internal SliceChunks.PageTable?[] Tables { get; } = new SliceChunks.PageTable?[columns];

        internal ChunkSpan[]?[]?[] Pages { get; } = new ChunkSpan[]?[]?[columns];

        /// <summary>Column <paramref name="field"/>'s page table, this reader's or the contents' when an earlier read loaded it; null when none did.</summary>
        internal SliceChunks.PageTable? Table(int field)
        {
            if (System.Threading.Volatile.Read(ref Tables[field]) is { } held)
            {
                return held;
            }

            if (Contents.PageTable(field) is { } kept)
            {
                System.Threading.Interlocked.CompareExchange(ref Tables[field], kept, null);
                System.Threading.Interlocked.CompareExchange(ref Pages[field], new ChunkSpan[]?[kept.Checksums.Length], null);
            }

            return System.Threading.Volatile.Read(ref Tables[field]);
        }
    }

    /// <summary>The chunks of column <paramref name="field"/>, loaded first.</summary>
    private ReadOnlySpan<ChunkSpan> Chunks(int field) =>
        System.Threading.Volatile.Read(ref _columns[field]) is { } chunks ? chunks.AsSpan(0, _counts[field]) : throw new InvalidOperationException($"Column {field}'s chunks were not loaded.");

    /// <summary>Gives the columns' chunks back to the shared pool: no read of the reader after.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            for (int field = 0; field < _columns.Length; field++)
            {
                if (_columns[field] is { } chunks)
                {
                    _columns[field] = null;
                    System.Buffers.ArrayPool<ChunkSpan>.Shared.Return(chunks);
                }
            }
        }
    }

    /// <summary>
    /// A reader of the rows of the file <paramref name="source"/> holds, or null when it carries no
    /// usable chunk directory.
    /// </summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="fileLength">The file's length.</param>
    /// <param name="schema">The file's schema, a struct of its columns: the dataset's, which the file's footer would repeat.</param>
    /// <param name="options">What bounds the decodes; the defaults when null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="VortexFormatException">The file's end is not a Vortex file's.</exception>
    internal static async ValueTask<SpanReader?> OpenAsync(
        ISegmentReader source, long fileLength, DType schema, VortexReadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (schema.Kind != DTypeKind.Struct)
        {
            throw new ArgumentException("A file read by spans has a schema that is a struct of its columns.", nameof(schema));
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

        string[]? encodings = null;
        IReadOnlyList<IndexSegment>? regions = null;
        ColumnDictionary?[]? dictionaries = [];
        foreach (IndexEntry entry in directory!.Entries)
        {
            if (entry.Kind == IndexKinds.SliceChunks && entry.Runs is [IndexRun run])
            {
                encodings = SliceChunks.EncodingsFrom(entry.Options);
                regions = run.Payload;
            }
            else if (entry.Kind == IndexKinds.Marks)
            {
                // The columns' object dictionaries, which their chunks cannot be read without.
                dictionaries = ColumnDictionary.From(entry.Options, schema.FieldCount);
            }
        }

        long rows = (long)directory.RowCount;
        return encodings is not null && regions is not null && dictionaries is not null && regions.Count == schema.FieldCount
            ? new SpanReader(source, schema, options ?? VortexReadOptions.Default, encodings, regions, rows, dictionaries: dictionaries)
            : null;
    }

    /// <summary>
    /// A reader of the rows of a file its table of contents <paramref name="contents"/> describes, opened
    /// with no read at all: the contents name the file's schema, its rows and the chunk directories
    /// the directory would, a paged column's page table, whose pages the contents' first rows place. Null when
    /// they do not cover the schema's columns.
    /// </summary>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static SpanReader? Open(ISegmentReader source, VortexReadOptions? options, ObjectContents contents)
    {
        ArgumentNullException.ThrowIfNull(source);
        DType schema = contents.SchemaType;
        return schema.Kind == DTypeKind.Struct && contents.ChunkDirectories.Count == schema.FieldCount
            ? new SpanReader(
                source, schema, options ?? VortexReadOptions.Default, [.. contents.Encodings], contents.ChunkDirectories, contents.Rows,
                contents)
            : null;
    }

    /// <summary>
    /// The rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="rows"/> of the file, as
    /// one batch whose first row is the span's first row in the file: of every column, or of <paramref name="columns"/>,
    /// top-level indices in schema order. The chunks that hold them are read in one request set, after the chunk
    /// directories of the columns not read before: the granules a key's range keeps, the blocks a lookup found.
    /// </summary>
    /// <exception cref="VortexFormatException">A column's chunk directory or a chunk does not decode.</exception>
    internal ValueTask<RecordBatch> ReadRowsAsync(long first, long rows, int[]? columns, CancellationToken cancellationToken) =>
        ReadSpanAsync(RowRange.FromLength(first, rows), columns, cancellationToken);

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
    internal async ValueTask<RecordBatch[]> ReadSpansAsync(IReadOnlyList<RowRange> spans, int[]? columns, CancellationToken cancellationToken)
    {
        int[] fields = columns ?? AllColumns();
        await LoadAsync(fields, spans, cancellationToken).ConfigureAwait(false);
        using SegmentRequestSet shared = new SegmentRequestSet();

        // Each batch owns a context of its own, its chunks views of the shared reads it retains, and the regions of the chunks
        // it reads the granules of in its arena: planned in it before the reads, filled from them after.
        ScanContext[] contexts = new ScanContext[spans.Count];
        ChunkRead[][] reads = new ChunkRead[spans.Count][];
        RecordBatch[] batches = new RecordBatch[spans.Count];
        int made = 0;
        try
        {
            GranuleBytes granules = new GranuleBytes();
            for (int s = 0; s < spans.Count; s++)
            {
                contexts[s] = new ScanContext(_encodings, _options);
                reads[s] = ChunksOf(shared, contexts[s], spans[s], fields, granules);
            }

            await _source.ReadManyAsync(shared, cancellationToken).ConfigureAwait(false);
            for (; made < batches.Length; made++)
            {
                ScanContext context = contexts[made];
                Fill(context, shared, reads[made]);
                context.Segments.Complete();
                batches[made] = Batch(context, spans[made], fields, columns, reads[made]);
                contexts[made] = null!;
            }

            return batches;
        }
        catch
        {
            for (int i = 0; i < made; i++)
            {
                batches[i].Dispose();
            }

            foreach (ScanContext? context in contexts)
            {
                context?.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// The object dictionary column <paramref name="field"/> is written against: its values' segment read in one request and
    /// decoded, sorted as the writer wrote them. Null when the column's chunks hold its values, or the values are of no kind a
    /// dictionary holds.
    /// </summary>
    /// <exception cref="VortexFormatException">The values do not decode.</exception>
    internal async ValueTask<Writing.ObjectDictionary?> DictionaryAsync(int field, CancellationToken cancellationToken)
    {
        if (DictionaryOf(field) is not { } named)
        {
            return null;
        }

        ScanContext context = new ScanContext(_encodings, _options);
        try
        {
            int request = context.Segments.Add(new SegmentSpec(named.Offset, named.Length, named.AlignmentExponent, 0, 0));
            await _source.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
            context.Segments.Complete();
            context.Decode.LoadBlob(context.Segments.GetBuffer(request));
            ArrayNode root = context.Nodes.Root;
            int values = context.Canonical.Decoded(context.Decode.DecodeRoot(in root, _schema.GetField(field), checked((int)named.Values)));
            return Writing.ObjectDictionary.Of(context.Canonical, values);
        }
        finally
        {
            context.Dispose();
        }
    }

    private async ValueTask<RecordBatch> ReadSpanAsync(RowRange span, int[]? columns, CancellationToken cancellationToken)
    {
        int[] fields = columns ?? AllColumns();
        // The span a paged column reads the pages of; none for a file whose directories are all read whole.
        await LoadAsync(fields, _paged is null ? [] : [span], cancellationToken).ConfigureAwait(false);

        ScanContext context = new ScanContext(_encodings, _options);
        try
        {
            ChunkRead[] reads = ChunksOf(context.Segments, context, span, fields, new GranuleBytes());
            await _source.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
            Fill(context, context.Segments, reads);
            return Batch(context, span, fields, columns, reads);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What reading one chunk of a span takes: its whole segment, at <see cref="Whole"/>; or the ranges of its region of
    /// buffers that the span's granules hold, read into <see cref="Region"/>, which its tree is loaded over.
    /// </summary>
    private sealed class ChunkRead(ChunkSpan chunk)
    {
        internal ChunkSpan Chunk { get; } = chunk;

        /// <summary>The whole segment's slot in the requests it was registered in, then in the context's own; -1 for granules.</summary>
        internal int Whole { get; set; } = -1;

        /// <summary>The chunk's tree, for granules.</summary>
        internal ReadOnlyMemory<byte> Tree { get; init; }

        /// <summary>The chunk's region of buffers, in the batch's arena, zeroed, for granules.</summary>
        internal VortexBuffer Region { get; init; }

        /// <summary>The first slot of the ranges, registered one after another.</summary>
        internal int FirstSlot { get; init; }

        /// <summary>Where each range lies in the region.</summary>
        internal (long Start, long End)[] Ranges { get; init; } = [];
    }

    /// <summary>
    /// Registers in <paramref name="requests"/> what reading the chunks of <paramref name="fields"/> that hold the span's rows
    /// takes, field by field: a chunk the span covers whole, or whose granules cannot be read alone, as its segment; a chunk
    /// it covers in part, when the column's marks say where its granules lie, as the ranges of its region they hold. A
    /// column written against an object dictionary reads its values' segment first, which every span shares.
    /// </summary>
    private ChunkRead[] ChunksOf(SegmentRequestSet requests, ScanContext context, RowRange span, int[] fields, GranuleBytes granules)
    {
        List<ChunkRead> reads = [];
        foreach (int field in fields)
        {
            int width = GranulePlans.WidthOf(_schema.GetField(field));
            if (DictionaryOf(field) is { } dictionary)
            {
                ChunkSpan values = new ChunkSpan(dictionary.Offset, dictionary.Length, dictionary.AlignmentExponent, 0, dictionary.Values, -1);
                reads.Add(new ChunkRead(values) { Whole = requests.Add(new SegmentSpec(values.Offset, values.Length, values.AlignmentExponent, 0, 0)) });
                width = dictionary.Codes.ByteWidth();
            }

            ColumnMarks? marks = System.Threading.Volatile.Read(ref _marks[field]);
            foreach (ChunkSpan chunk in Overlapping(field, span.Start, span.Length))
            {
                if (marks is not null && Granules(requests, context, chunk, span, marks, width, granules) is { } ranged)
                {
                    reads.Add(ranged);
                    continue;
                }

                reads.Add(new ChunkRead(chunk) { Whole = requests.Add(new SegmentSpec(chunk.Offset, chunk.Length, chunk.AlignmentExponent, 0, 0)) });
            }
        }

        return [.. reads];
    }

    /// <summary>The share of a chunk's bytes past which its granules are read with the rest of it, in one request.</summary>
    private const double WholeShare = GranulePlans.WholeShare;

    /// <summary>
    /// The read of the span's granules of <paramref name="chunk"/>, their ranges registered in <paramref name="requests"/>;
    /// null when the span covers the chunk whole, its geometry is not known, or its granules hold most of its bytes.
    /// </summary>
    private static ChunkRead? Granules(
        SegmentRequestSet requests, ScanContext context, ChunkSpan chunk, RowRange span, ColumnMarks marks, int width,
        GranuleBytes granules)
    {
        long from = Math.Max(span.Start, chunk.FirstRow);
        long to = Math.Min(span.End, chunk.EndRow);
        if ((from == chunk.FirstRow && to == chunk.EndRow) || (uint)chunk.Ordinal >= (uint)marks.Chunks.Length)
        {
            return null;
        }

        ChunkGeometry geometry = marks.Chunks[chunk.Ordinal];
        (long Start, long Length)[] layout = ArrayBlobReader.BufferLayout(geometry.Tree.Span, out long regionLength);
        if (regionLength > chunk.Length || regionLength <= 0)
        {
            return null;
        }

        VortexBuffer region = context.Canonical.Allocate(checked((int)regionLength), 1 << chunk.AlignmentExponent, out _);
        context.Decode.LoadBlob(geometry.Tree.Span, region);
        ArrayNode root = context.Nodes.Root;
        int start = (int)(from - chunk.FirstRow);
        int count = (int)(to - from);
        if (!context.Decode.DecodesRange(in root)
            || !granules.TryPlan(in root, width, checked((int)chunk.Rows), start, count, layout, geometry, marks.GranuleRows)
            || granules.Bytes > WholeShare * chunk.Length)
        {
            return null;
        }

        (long Start, long End)[] ranges = [.. granules.Ranges];
        int first = -1;
        foreach ((long rangeStart, long rangeEnd) in ranges)
        {
            int slot = requests.Add(new SegmentSpec(chunk.Offset + (ulong)rangeStart, checked((uint)(rangeEnd - rangeStart)), 0, 0, 0));
            first = first < 0 ? slot : first;
        }

        return new ChunkRead(chunk) { Tree = geometry.Tree, Region = region, FirstSlot = first, Ranges = ranges };
    }

    /// <summary>
    /// The reads of a span made the context's: a whole segment read into shared requests lent to the context, a chunk's
    /// granules copied into its region from wherever they were read.
    /// </summary>
    private static void Fill(ScanContext context, SegmentRequestSet requests, ChunkRead[] reads)
    {
        bool shared = !ReferenceEquals(requests, context.Segments);
        foreach (ChunkRead read in reads)
        {
            if (read.Ranges.Length == 0)
            {
                if (shared)
                {
                    ChunkSpan chunk = read.Chunk;
                    int own = context.Segments.Add(new SegmentSpec(chunk.Offset, chunk.Length, chunk.AlignmentExponent, 0, 0));
                    if (!context.Segments.IsFilled(own))
                    {
                        context.Segments.SetSharedResult(own, requests.GetOwner(read.Whole), requests.GetBuffer(read.Whole));
                    }

                    read.Whole = own;
                }

                continue;
            }

            // The region is the batch's arena's, writable: the decode that follows reads it as the chunk's bytes.
            Span<byte> region = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(read.Region.Span), read.Region.Length);
            for (int r = 0; r < read.Ranges.Length; r++)
            {
                (long start, long end) = read.Ranges[r];
                requests.GetBuffer(read.FirstSlot + r).Span[..(int)(end - start)].CopyTo(region[(int)start..]);
            }
        }
    }

    /// <summary>The span's rows decoded from the chunks <paramref name="context"/> holds, as a batch that adopts it.</summary>
    private RecordBatch Batch(ScanContext context, RowRange span, int[] fields, int[]? columns, ChunkRead[] reads)
    {
        int length = checked((int)span.Length);

        // A projection's struct is built in the context's own arena, and its fields decode at the
        // types it holds, so the batch's columns and its schema are the same nodes.
        DType schema = columns is null ? _schema : Projected(context.Types, fields);
        int[] decoded = new int[fields.Length];
        int next = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            decoded[i] = Column(
                context, schema.GetField(i), DictionaryOf(fields[i]), Overlapping(fields[i], span.Start, span.Length).Length, span, length, reads, ref next);
        }

        int root = context.Canonical.AddStruct(
            schema, length, schema.IsNullable ? Validity.AllValid : Validity.NonNullable, decoded);
        return RecordBatch.Adopt(context, root, span.Start, null, null);
    }

    /// <summary>
    /// The rows strictly inside <paramref name="first"/> to <paramref name="end"/> at which a chunk of one of
    /// <paramref name="fields"/> starts, in order and once each: where the span may be cut so that each part reads chunks
    /// of its own but those the cut falls in, as an open file's splits are cut. The directories of the columns not read
    /// before are read first, the pages that span reaches of a paged one.
    /// </summary>
    internal async ValueTask<long[]> ChunkStartsAsync(int[] fields, long first, long end, CancellationToken cancellationToken)
    {
        RowRange span = new RowRange(first, end);
        await LoadAsync(fields, [span], cancellationToken).ConfigureAwait(false);
        SortedSet<long> starts = [];
        foreach (int field in fields)
        {
            foreach (ChunkSpan chunk in Overlapping(field, first, end - first))
            {
                if (chunk.FirstRow > first && chunk.FirstRow < end)
                {
                    starts.Add(chunk.FirstRow);
                }
            }
        }

        return [.. starts];
    }

    /// <summary>
    /// Whether a chunk of one of <paramref name="fields"/> starts strictly inside one of <paramref name="spans"/>: whether a
    /// read of those columns at some rows of the spans alone may read fewer chunks than one over the spans whole. The
    /// directories of the columns not read before are read first, the pages the spans reach of a paged one.
    /// </summary>
    internal async ValueTask<bool> CutsAsync(int[] fields, IReadOnlyList<RowRange> spans, CancellationToken cancellationToken)
    {
        await LoadAsync(fields, spans, cancellationToken).ConfigureAwait(false);
        foreach (RowRange span in spans)
        {
            foreach (int field in fields)
            {
                foreach (ChunkSpan chunk in Overlapping(field, span.Start, span.Length))
                {
                    if (chunk.FirstRow > span.Start && chunk.FirstRow < span.End)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads, the first time it is asked for, what a read of column <paramref name="field"/>'s rows starts from: its chunk
    /// directory, or a paged column's page table, whose pages a read reads as its spans reach them. What a reader that learns
    /// late which rows it reads, a lookup after its postings, reads first, beside its other reads.
    /// </summary>
    internal ValueTask LoadDirectoryAsync(int field, CancellationToken cancellationToken) => LoadAsync([field], [], cancellationToken);

    private int[] AllColumns()
    {
        int[] all = new int[_regions.Count];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = i;
        }

        return all;
    }

    /// <summary>
    /// The chunks of column <paramref name="field"/>, in row order, that hold a row of <c>[first, first + rows)</c>, loaded first
    /// (<see cref="LoadAsync"/>): of its whole directory, or of the pages that hold those rows.
    /// </summary>
    private ReadOnlySpan<ChunkSpan> Overlapping(int field, long first, long rows)
    {
        if (!Paged(field))
        {
            return SliceChunks.Overlapping(Chunks(field), first, rows);
        }

        (int from, int to) = _paged!.Paging.PagesOf(first, rows);
        ChunkSpan[]?[] pages = System.Threading.Volatile.Read(ref _paged!.Pages[field]) ?? throw Unloaded(field, from);
        if (from == to)
        {
            return SliceChunks.Overlapping(System.Threading.Volatile.Read(ref pages[from]) ?? throw Unloaded(field, from), first, rows);
        }

        // A span across pages: their chunks end to end.
        List<ChunkSpan> joined = [];
        for (int page = from; page <= to; page++)
        {
            joined.AddRange(System.Threading.Volatile.Read(ref pages[page]) ?? throw Unloaded(field, page));
        }

        return SliceChunks.Overlapping(joined.ToArray(), first, rows);
    }

    private static InvalidOperationException Unloaded(int field, int page) => new($"Page {page} of column {field}'s chunk directory was not loaded.");

    /// <summary>
    /// Reads in one request set what reading <paramref name="spans"/> of the <paramref name="fields"/> needs and was not read:
    /// a column's whole chunk directory; or a paged column's page table and the pages that hold the spans' rows, each page
    /// checked against its checksum in the table, and its chunks against the rows the table of contents places it at.
    /// </summary>
    /// <exception cref="VortexFormatException">A directory, a page table or a page does not decode, or does not cover the file's rows.</exception>
    private async ValueTask LoadAsync(int[] fields, IReadOnlyList<RowRange> spans, CancellationToken cancellationToken)
    {
        foreach (int field in fields)
        {
            if ((uint)field >= (uint)_columns.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(fields), field, $"The file has {_columns.Length} columns.");
            }
        }

        // The columns' geometry beside their directories, in a request set of its own read at once with theirs.
        ValueTask marks = LoadMarksAsync(fields, cancellationToken);
        try
        {
            if (_paged is null)
            {
                await LoadWholeAsync(fields, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await LoadPagedAsync(fields, spans, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await marks.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads what the <paramref name="spans"/> of the <paramref name="fields"/> need of a file whose directories lie in pages:
    /// a column's whole directory, or a paged one's page table and the pages that hold the spans' rows.
    /// </summary>
    private async ValueTask LoadPagedAsync(int[] fields, IReadOnlyList<RowRange> spans, CancellationToken cancellationToken)
    {
        // What each column asks, by its slot: its directory or its page table (page -1), and its pages, a page with its checksum
        // when its table is known, which the synopsis cache keeps it by. A read whose directories are all in hand, as a warm
        // question's, asks nothing and makes no set.
        List<(int Field, int Page, IndexSegment Region)>? asked = null;
        foreach (int field in fields)
        {
            IndexSegment region = _regions[field];
            bool paged = Paged(field);
            SliceChunks.PageTable? table = paged ? _paged!.Table(field) : null;
            if (paged ? table is null : System.Threading.Volatile.Read(ref _columns[field]) is null)
            {
                Ask(ref asked, field, -1, region);
            }

            if (!paged)
            {
                continue;
            }

            // A page lies past the table, at its place among the column's pages.
            ChunkSpan[]?[]? pages = System.Threading.Volatile.Read(ref _paged!.Pages[field]);
            for (int s = 0; s < spans.Count; s++)
            {
                (int from, int to) = _paged!.Paging.PagesOf(spans[s].Start, spans[s].Length);
                for (int page = from; page <= to; page++)
                {
                    if (pages is not null && System.Threading.Volatile.Read(ref pages[page]) is not null)
                    {
                        continue;
                    }

                    (long offset, int length) = _paged.Paging.Page(page);
                    Ask(ref asked, field, page, new IndexSegment(region.Offset + region.Length + (ulong)offset, (uint)length, 0, table?.Checksums[page]));
                }
            }
        }

        if (asked is null)
        {
            return;
        }

        using SegmentRequestSet requests = new SegmentRequestSet(asked.Count);
        int[] slots = new int[asked.Count];
        IndexSegment[] regions = new IndexSegment[asked.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            IndexSegment held = asked[i].Region;
            slots[i] = requests.Add(new SegmentSpec(held.Offset, held.Length, held.AlignmentExponent, 0, 0));
            regions[slots[i]] = held;
        }

        await _source.ReadRegionsAsync(requests, regions.AsMemory(0, requests.Count), cancellationToken).ConfigureAwait(false);

        // The directories and the tables first, a page checked against its table.
        for (int i = 0; i < slots.Length; i++)
        {
            if (asked[i].Page < 0)
            {
                Took(asked[i].Field, requests.GetBuffer(slots[i]).Span);
            }
        }

        for (int i = 0; i < slots.Length; i++)
        {
            (int field, int page, IndexSegment _) = asked[i];
            if (page < 0)
            {
                continue;
            }

            long end = page + 1 < _paged!.Paging.PageRows.Length ? _paged.Paging.PageRows[page + 1] : _rows;
            if (SliceChunks.PageFrom(System.Threading.Volatile.Read(ref _paged!.Tables[field])!, page, requests.GetBuffer(slots[i]).Span, _paged.Paging.PageRows[page]) is not { } chunks
                || chunks[^1].EndRow != end)
            {
                throw new VortexFormatException(
                    $"Page {page} of column {field}'s chunk directory does not hold its checksum, or chunks from row {_paged.Paging.PageRows[page]} to {end}.");
            }

            // A page two reads loaded at once keeps the first's chunks.
            lock (_gate)
            {
                _paged!.Pages[field]![page] ??= chunks;
            }
        }
    }

    /// <summary>
    /// Reads the geometry of the <paramref name="fields"/> not read yet, when the file keeps it: a region a column, which a
    /// read of a few granules plans their bytes by. A region that does not decode leaves its column's chunks read whole.
    /// </summary>
    private async ValueTask LoadMarksAsync(int[] fields, CancellationToken cancellationToken)
    {
        if (_markRegions is null)
        {
            return;
        }

        int missing = 0;
        foreach (int field in fields)
        {
            missing += System.Threading.Volatile.Read(ref _marksRead[field]) ? 0 : 1;
        }

        if (missing == 0)
        {
            return;
        }

        using SegmentRequestSet requests = new SegmentRequestSet(missing);
        int[] slots = new int[fields.Length];
        IndexSegment[] regions = new IndexSegment[missing];
        for (int i = 0; i < fields.Length; i++)
        {
            IndexSegment region = _markRegions[fields[i]];
            slots[i] = System.Threading.Volatile.Read(ref _marksRead[fields[i]])
                ? -1
                : requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            if (slots[i] >= 0)
            {
                regions[slots[i]] = region;
            }
        }

        await _source.ReadRegionsAsync(requests, regions.AsMemory(0, requests.Count), cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < fields.Length; i++)
        {
            if (slots[i] < 0)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = requests.GetBuffer(slots[i]).Span;
            ColumnMarks? marks = _markRegions[fields[i]].Holds(bytes) ? ColumnMarks.From(bytes.ToArray()) : null;
            lock (_gate)
            {
                _marks[fields[i]] ??= marks;
                System.Threading.Volatile.Write(ref _marksRead[fields[i]], true);
            }
        }
    }

    /// <summary>
    /// Reads the whole chunk directories of the <paramref name="fields"/> not read yet, in one request set, a file none of
    /// whose directories is paged: none at all, and nothing made, once they are in hand.
    /// </summary>
    private async ValueTask LoadWholeAsync(int[] fields, CancellationToken cancellationToken)
    {
        int missing = 0;
        foreach (int field in fields)
        {
            missing += System.Threading.Volatile.Read(ref _columns[field]) is null ? 1 : 0;
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
            slots[i] = System.Threading.Volatile.Read(ref _columns[fields[i]]) is null
                ? requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0))
                : -1;
        }

        await _source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < fields.Length; i++)
        {
            if (slots[i] >= 0)
            {
                Took(fields[i], requests.GetBuffer(slots[i]).Span);
            }
        }
    }

    /// <summary>Asks <paramref name="region"/>, page <paramref name="page"/> of <paramref name="field"/> or its directory, once.</summary>
    private static void Ask(ref List<(int Field, int Page, IndexSegment Region)>? asked, int field, int page, IndexSegment region)
    {
        for (int i = 0; asked is not null && i < asked.Count; i++)
        {
            if (asked[i].Field == field && asked[i].Page == page)
            {
                return;
            }
        }

        (asked ??= []).Add((field, page, region));
    }

    /// <summary>Takes column <paramref name="field"/>'s whole chunk directory, or a paged column's page table, read as <paramref name="bytes"/>.</summary>
    private void Took(int field, ReadOnlySpan<byte> bytes)
    {
        if (Paged(field))
        {
            if (!_regions[field].Holds(bytes) || SliceChunks.TableFrom(bytes) is not { } table
                || table.PageEntries != _paged!.Paging.PageEntries || table.Count != _paged.Paging.Count || table.Checksums.Length != _paged.Paging.PageRows.Length)
            {
                throw new VortexFormatException($"The page table of column {field}'s chunk directory does not decode, or is not the one its table of contents says.");
            }

            lock (_gate)
            {
                _paged!.Tables[field] ??= table;
                _paged!.Pages[field] ??= new ChunkSpan[]?[table.Checksums.Length];
            }

            _paged.Contents.KeepPageTable(field, table);
            return;
        }

        ChunkSpan[] chunks = [];
        int count = _regions[field].Holds(bytes) ? SliceChunks.ColumnInto(bytes, out chunks) : -1;
        if (count < 0 || (count == 0 ? 0 : chunks[count - 1].EndRow) != _rows)
        {
            if (count >= 0)
            {
                System.Buffers.ArrayPool<ChunkSpan>.Shared.Return(chunks);
            }

            throw new VortexFormatException(
                $"The chunk directory of column {field} does not decode, or does not cover the file's {_rows} rows.");
        }

        // A column two reads loaded at once keeps the first's chunks; the other's go back.
        lock (_gate)
        {
            if (_columns[field] is null)
            {
                _counts[field] = count;
                System.Threading.Volatile.Write(ref _columns[field], chunks);
                return;
            }
        }

        System.Buffers.ArrayPool<ChunkSpan>.Shared.Return(chunks);
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

    /// <summary>
    /// One column's rows of the span: its <paramref name="chunks"/> chunks that overlap it, each decoded whole and cut to the
    /// span, or its granules decoded alone over the region they were read into, joined. A column written against an object
    /// dictionary reads its values first, its chunks as codes, and gathers the one through the other.
    /// </summary>
    private static int Column(
        ScanContext context, DType type, ColumnDictionary? dictionary, int chunks, RowRange span, int length, ChunkRead[] reads, ref int next)
    {
        int values = -1;
        DType chunkType = type;
        if (dictionary is { } named)
        {
            context.Decode.LoadBlob(context.Segments.GetBuffer(reads[next++].Whole));
            ArrayNode valuesRoot = context.Nodes.Root;
            values = context.Decode.DecodeRoot(in valuesRoot, type, checked((int)named.Values));
            chunkType = context.Types.Primitive(named.Codes, type.Nullability);
        }

        int[] parts = new int[chunks];
        for (int i = 0; i < chunks; i++)
        {
            ChunkRead read = reads[next++];
            ChunkSpan chunk = read.Chunk;
            long from = Math.Max(span.Start, chunk.FirstRow);
            long to = Math.Min(span.End, chunk.EndRow);
            if (read.Ranges.Length == 0)
            {
                context.Decode.LoadBlob(context.Segments.GetBuffer(read.Whole));
                ArrayNode root = context.Nodes.Root;
                int node = context.Decode.DecodeRoot(in root, chunkType, checked((int)chunk.Rows));
                parts[i] = CanonicalSlice.Slice(context.Decode, node, (int)(from - chunk.FirstRow), (int)(to - from));
                continue;
            }

            context.Decode.LoadBlob(read.Tree.Span, read.Region);
            ArrayNode ranged = context.Nodes.Root;
            parts[i] = context.Decode.DecodeRootRange(
                in ranged, chunkType, checked((int)chunk.Rows), (int)(from - chunk.FirstRow), (int)(to - from), keepEncoding: false);
        }

        int joined = parts.Length == 1 ? parts[0] : CanonicalConcat.Concat(context.Decode, chunkType, length, parts);
        if (values < 0)
        {
            return joined;
        }

        int codes = context.Canonical.Decoded(joined);
        if (context.Canonical.GetNode(codes).Kind == CanonicalKind.Constant)
        {
            codes = context.Canonical.MaterializeConstant(codes);
        }

        return Layouts.DictLayoutReader.Gather(context, type, values, checked((int)dictionary!.Value.Values), codes, length);
    }
}
