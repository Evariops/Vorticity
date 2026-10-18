using System;
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
/// directory, the directory the slice table and the chunk directory, and those the chunks that hold a
/// slice's rows, decoded from their own bytes against the schema the caller knows.
/// </summary>
/// <remarks>
/// <para>
/// An open reads the footer, a node per chunk and column, which for an object of a period runs to
/// megabytes. A question that needs the rows of a slice or two (the edges of a window, an exact
/// lookup, the rows to show) needs none of it. This reader costs four small dependent requests (the
/// EOF marker, the postscript, the index directory, the chunk directory) and then one request a slice,
/// whose chunks are read together.
/// </para>
/// <para>
/// Like every index the directories are hints: a file without them, or with one that does not decode
/// or does not cover the file's rows, opens no reader here, and its rows read through a whole open.
/// </para>
/// </remarks>
internal sealed class SliceReader
{
    private readonly ISegmentReader _source;
    private readonly DType _schema;
    private readonly VortexReadOptions _options;
    private readonly string[] _encodings;

    private SliceReader(ISegmentReader source, DType schema, VortexReadOptions options, SliceTable slices, SliceChunks chunks)
    {
        _source = source;
        _schema = schema;
        _options = options;
        _encodings = [.. chunks.Encodings];
        Slices = slices;
        Chunks = chunks;
    }

    /// <summary>The file's time slices.</summary>
    internal SliceTable Slices { get; }

    /// <summary>Where every column's chunks lie.</summary>
    internal SliceChunks Chunks { get; }

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
        IndexSegment? region = null;
        foreach (IndexEntry entry in directory!.Entries)
        {
            if (entry.Kind == IndexKinds.Slices)
            {
                slices ??= SliceTable.FromBytes(entry.Options);
            }
            else if (entry.Kind == IndexKinds.SliceChunks && entry.Runs is [{ Payload: [IndexSegment payload] }])
            {
                region = payload;
            }
        }

        long rows = (long)directory.RowCount;
        if (slices is null || region is not { } chunkRegion || slices.Rows != rows)
        {
            return null;
        }

        SliceChunks? chunks;
        SegmentSpec chunkSpec = new SegmentSpec(chunkRegion.Offset, chunkRegion.Length, chunkRegion.AlignmentExponent, 0, 0);
        using (SegmentOwner owner = await source.ReadAsync(chunkSpec, cancellationToken).ConfigureAwait(false))
        {
            chunks = chunkRegion.Holds(owner.Buffer.Span) ? SliceChunks.FromBytes(owner.Buffer.Span) : null;
        }

        return chunks is not null && Covers(chunks, schema.FieldCount, rows)
            ? new SliceReader(source, schema, options ?? VortexReadOptions.Default, slices, chunks)
            : null;
    }

    /// <summary>Whether every one of the <paramref name="columns"/> has chunks summing to the file's <paramref name="rows"/>.</summary>
    private static bool Covers(SliceChunks chunks, int columns, long rows)
    {
        if (chunks.Columns.Count != columns)
        {
            return false;
        }

        foreach (ChunkSpan[] column in chunks.Columns)
        {
            long held = column.Length == 0 ? 0 : column[^1].EndRow;
            if (held != rows)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The rows of slice <paramref name="slice"/>, every column, as one batch whose first row is the
    /// slice's first row in the file. The chunks that hold them are read in one request set.
    /// </summary>
    /// <param name="slice">The slice's index in <see cref="Slices"/>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A batch the caller disposes.</returns>
    /// <exception cref="VortexFormatException">A chunk does not decode as its column.</exception>
    internal async ValueTask<RecordBatch> ReadSliceAsync(int slice, CancellationToken cancellationToken)
    {
        TimeSlice span = Slices.Slices[slice];
        int length = checked((int)span.Rows);
        int fields = _schema.FieldCount;
        ScanContext context = new ScanContext(_encodings, _options);
        try
        {
            int chunkCount = 0;
            for (int field = 0; field < fields; field++)
            {
                chunkCount += Chunks.Overlapping(field, span.FirstRow, span.Rows).Length;
            }

            int[] slots = new int[chunkCount];
            int next = 0;
            for (int field = 0; field < fields; field++)
            {
                foreach (ChunkSpan chunk in Chunks.Overlapping(field, span.FirstRow, span.Rows))
                {
                    slots[next++] = context.Segments.Add(new SegmentSpec(chunk.Offset, chunk.Length, chunk.AlignmentExponent, 0, 0));
                }
            }

            await _source.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);

            int[] columns = new int[fields];
            next = 0;
            for (int field = 0; field < fields; field++)
            {
                columns[field] = Column(context, field, span, length, slots, ref next);
            }

            int root = context.Canonical.AddStruct(
                _schema, length, _schema.IsNullable ? Validity.AllValid : Validity.NonNullable, columns);
            return RecordBatch.Adopt(context, root, span.FirstRow, null, null);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>One column's rows of the slice: its overlapping chunks decoded, cut to the slice and joined.</summary>
    private int Column(ScanContext context, int field, TimeSlice span, int length, int[] slots, ref int next)
    {
        DType type = _schema.GetField(field);
        ReadOnlySpan<ChunkSpan> chunks = Chunks.Overlapping(field, span.FirstRow, span.Rows);
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
