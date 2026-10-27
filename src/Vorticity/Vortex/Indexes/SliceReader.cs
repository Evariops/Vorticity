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
/// Reads the rows of a sliced file's slices without its footer: the postscript names the index
/// directory, the directory the slice table and the regions of the chunk directory, and those the
/// chunks that hold a slice's rows, decoded from their own bytes against the schema the caller knows.
/// </summary>
/// <remarks>
/// <para>
/// An open reads the footer, a node per chunk and column, which for an object of a day's rows runs to
/// megabytes. A question that needs the rows of a slice or two (the edges of a range of the key, an exact
/// lookup, the rows to show) needs none of it. Opening this reader costs three small dependent
/// requests (the EOF marker, the postscript, the index directory). The first read of a column adds
/// one for that column's chunk directory, then each slice costs one request, its chunks read together.
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
internal sealed class SliceReader : IDisposable
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

    private SliceReader(
        ISegmentReader source, DType schema, VortexReadOptions options, SliceTable slices, string[] encodings,
        IReadOnlyList<IndexSegment> regions, long rows, ObjectContents? contents = null)
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
        Slices = slices;
    }

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

    /// <summary>The file's slices.</summary>
    internal SliceTable Slices { get; }

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
    /// the directory would, a paged column's page table, whose pages the contents' first rows place. Null when
    /// they do not cover the schema's columns.
    /// </summary>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static SliceReader? Open(ISegmentReader source, VortexReadOptions? options, ObjectContents contents)
    {
        ArgumentNullException.ThrowIfNull(source);
        DType schema = contents.SchemaType;
        return schema.Kind == DTypeKind.Struct && contents.ChunkDirectories.Count == schema.FieldCount
            ? new SliceReader(
                source, schema, options ?? VortexReadOptions.Default, contents.Slices, [.. contents.Encodings], contents.ChunkDirectories, contents.Slices.Rows,
                contents)
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
    /// it covers in part, when the column's marks say where its granules lie, as the ranges of its region they hold.
    /// </summary>
    private ChunkRead[] ChunksOf(SegmentRequestSet requests, ScanContext context, RowRange span, int[] fields, GranuleBytes granules)
    {
        List<ChunkRead> reads = [];
        foreach (int field in fields)
        {
            ColumnMarks? marks = System.Threading.Volatile.Read(ref _marks[field]);
            foreach (ChunkSpan chunk in Overlapping(field, span.Start, span.Length))
            {
                if (marks is not null && Granules(requests, context, chunk, span, marks, _schema.GetField(field), granules) is { } ranged)
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
    private const double WholeShare = 0.75;

    /// <summary>
    /// The read of the span's granules of <paramref name="chunk"/>, their ranges registered in <paramref name="requests"/>;
    /// null when the span covers the chunk whole, its geometry is not known, or its granules hold most of its bytes.
    /// </summary>
    private static ChunkRead? Granules(
        SegmentRequestSet requests, ScanContext context, ChunkSpan chunk, RowRange span, ColumnMarks marks, DType type,
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
            || !granules.TryPlan(in root, Width(type), checked((int)chunk.Rows), start, count, layout, geometry, marks.GranuleRows)
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

    /// <summary>The bytes of a value of <paramref name="type"/>, a number or the storage of one; 0 for another type.</summary>
    private static int Width(DType type) => type.Kind switch
    {
        DTypeKind.Primitive => type.PType.ByteWidth(),
        DTypeKind.Extension => Width(type.StorageType),
        _ => 0,
    };

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
            decoded[i] = Column(context, schema.GetField(i), Overlapping(fields[i], span.Start, span.Length).Length, span, length, reads, ref next);
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
    /// span, or its granules decoded alone over the region they were read into, joined.
    /// </summary>
    private static int Column(
        ScanContext context, DType type, int chunks, RowRange span, int length, ChunkRead[] reads, ref int next)
    {
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
                int node = context.Decode.DecodeRoot(in root, type, checked((int)chunk.Rows));
                parts[i] = CanonicalSlice.Slice(context.Decode, node, (int)(from - chunk.FirstRow), (int)(to - from));
                continue;
            }

            context.Decode.LoadBlob(read.Tree.Span, read.Region);
            ArrayNode ranged = context.Nodes.Root;
            parts[i] = context.Decode.DecodeRootRange(
                in ranged, type, checked((int)chunk.Rows), (int)(from - chunk.FirstRow), (int)(to - from), keepEncoding: false);
        }

        return parts.Length == 1 ? parts[0] : CanonicalConcat.Concat(context.Decode, type, length, parts);
    }
}
