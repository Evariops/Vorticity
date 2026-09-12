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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
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
    private readonly EncodingDictionary _arrayEncodings = new EncodingDictionary();
    private readonly EncodingDictionary _layoutEncodings = new EncodingDictionary();
    private readonly List<SegmentSpec> _segments = [];

    /// <summary>Per root field, the segment index of each batch's column.</summary>
    private readonly List<int>[] _columnSegments;

    /// <summary>Per batch, its row count; every field's chunk list has the same shape.</summary>
    private readonly List<long> _chunkRows = [];

    /// <summary>Per root field, one summary per written batch, for the zone map.</summary>
    private readonly List<ZoneStatistics>[] _columnZones;

    private readonly bool _isTabular;
    private int[]? _zoneSegments;
    private byte[][]? _zoneMetadata;
    private long _rowCount;
    private bool _started;
    private bool _completed;
    private byte[] _padding = new byte[VortexLimits.MaxAlignment];

    private VortexFileWriter(ISegmentSink sink, DType schema)
    {
        _sink = sink;
        _schema = schema;
        _isTabular = schema.Kind == DTypeKind.Struct;

        // A non-struct root is one column whose layout IS the root, with no struct level above it.
        _fieldCount = _isTabular ? schema.FieldCount : 1;
        _columnSegments = new List<int>[Math.Max(_fieldCount, 1)];
        _columnZones = new List<ZoneStatistics>[Math.Max(_fieldCount, 1)];
        for (int i = 0; i < _columnSegments.Length; i++)
        {
            _columnSegments[i] = [];
            _columnZones[i] = [];
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
    public static VortexFileWriter Create(ISegmentSink sink, DType schema)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (schema.IsDefault)
        {
            throw new ArgumentException("The schema has not been set.", nameof(schema));
        }

        return new VortexFileWriter(sink, schema);
    }

    /// <summary>Creates a file at <paramref name="path"/>.</summary>
    /// <param name="path">The destination path; truncated if it exists.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <returns>The writer, which owns the underlying stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static VortexFileWriter Create(string path, DType schema)
    {
        ArgumentNullException.ThrowIfNull(path);
        System.IO.FileStream stream = new System.IO.FileStream(
            path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
        return Create(new StreamSegmentSink(stream, ownsStream: true), schema);
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

        for (int field = 0; field < _fieldCount; field++)
        {
            int node = _isTabular
                ? batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(field)
                : batch.RootIndex;
            byte[] blob = ArrayBlobWriter.Write(batch.Arena, node, _arrayEncodings);
            _columnSegments[field].Add(await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false));

            // Summarized from the canonical column before the arena is reused, which is the only
            // moment the values are in hand.
            _columnZones[field].Add(ZoneStatistics.Compute(batch.Arena, node));
        }

        _chunkRows.Add(batch.RowCount);
        _rowCount += batch.RowCount;
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

        if (_sink is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds and writes one zones segment per column, when the chunking allows a zone map at all.
    /// </summary>
    /// <remarks>
    /// The uniformity rule is the format's, not a simplification: ZoneMap documents zone z as
    /// covering [z * ZoneLength, (z + 1) * ZoneLength) with only the last zone short, so a ragged
    /// chunking cannot be described by any single zone length. A writer that declared one anyway
    /// would hand every reader bounds attached to the wrong rows -- the one failure mode
    /// docs/08-semantics.md §1 says must never happen.
    /// </remarks>
    private async ValueTask WriteZoneMapsAsync(CancellationToken cancellationToken)
    {
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
                    column, _columnZones[field], _arrayEncodings, zoneLength,
                    out byte[] metadata, out byte[] blob))
            {
                continue;
            }

            _zoneMetadata[field] = metadata;
            _zoneSegments[field] = await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The one zone length the written chunks can be described by, if there is one.
    /// </summary>
    private bool TryZoneLength(out uint zoneLength)
    {
        zoneLength = 0;
        if (_chunkRows.Count == 0)
        {
            return false;
        }

        long first = _chunkRows[0];
        if (first <= 0 || first > uint.MaxValue)
        {
            return false;
        }

        // Every chunk but the last must be exactly the zone length; the last may be short.
        for (int i = 0; i < _chunkRows.Count - 1; i++)
        {
            if (_chunkRows[i] != first)
            {
                return false;
            }
        }

        if (_chunkRows[^1] > first)
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
    private async ValueTask<int> WriteSegmentAsync(byte[] blob, CancellationToken cancellationToken)
    {
        long position = _sink.Position;
        long aligned = (position + VortexLimits.MaxAlignment - 1) & ~((long)VortexLimits.MaxAlignment - 1);
        int padding = (int)(aligned - position);
        if (padding > 0)
        {
            await _sink.WriteAsync(_padding.AsMemory(0, padding), cancellationToken).ConfigureAwait(false);
        }

        await _sink.WriteAsync(blob, cancellationToken).ConfigureAwait(false);

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

        int zoneCount = _chunkRows.Count;
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
