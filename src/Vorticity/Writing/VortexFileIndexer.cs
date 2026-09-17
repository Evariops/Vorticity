// Indexing a file that already exists - docs/10-indexes.md §8, the second and third ways.
//
// POST-HOC INDEXING BY APPEND. The file is read chunk by chunk, block by block, and every row is fed
// to the builders exactly as the writer would have fed it; the runs, a new directory, a footer and a
// postscript that reference the same data segments are appended. The layout and the dtype are the
// old bytes again; the footer is written anew only because a payload may name an array encoding the
// file had not used, and the footer's table is where encodings are named. No data byte moves.
//
// THE FILE IS NOT WRITTEN WHILE IT IS READ. A memory-mapped file is open for reading only, so the
// whole tail -- runs, directory, footer, postscript -- is laid out first in a scratch file at the
// offsets it will have, and copied behind the file once the reader is closed. A process that dies
// before the copy leaves the file as it was; one that dies during it leaves a tail
// `VortexFileRepair` removes.
//
// THE SIDECAR. The same runs and directory in `file.vortex.idx`, for stores that cannot append:
// the directory carries the file's length and SHA-256, and the encoding table the payloads use.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Editions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

/// <summary>Builds indexes over a file that already exists (docs/10-indexes.md §8).</summary>
public static class VortexFileIndexer
{
    /// <summary>
    /// Indexes <paramref name="path"/> under <paramref name="policy"/> and appends the runs, with a
    /// new directory, footer and postscript over the same data segments.
    /// </summary>
    /// <param name="path">The file: this writer's shape, a struct of chunked flat columns.</param>
    /// <param name="policy">What to build; an old entry of another kind or column is kept.</param>
    /// <param name="options">The budget, the key encoder and the block length when the file has no zone map; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read and the writes.</param>
    /// <returns>What became of every index the policy asked for.</returns>
    /// <exception cref="VortexUnsupportedException">The file's layout is not one this can index.</exception>
    public static async ValueTask<IReadOnlyList<IndexWriteReport>> AppendIndexesAsync(
        string path, WritePolicy policy, VortexWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(policy);
        string scratch = path + ".indexing-" + Guid.NewGuid().ToString("N");
        IReadOnlyList<IndexWriteReport> reports;
        long length;
        try
        {
            VortexFile file = await VortexFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                length = file.FileLength;
                FileStream tail = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                StreamSegmentSink sink = new StreamSegmentSink(tail, ownsStream: true, length);
                await using (sink.ConfigureAwait(false))
                {
                    EncodingDictionary encodings = new EncodingDictionary(ComponentKind.Array, options?.TargetEdition ?? EditionRegistry.Newest);
                    List<string> old = new List<string>(file.ArrayEncodingCount);
                    for (int i = 0; i < file.ArrayEncodingCount; i++)
                    {
                        old.Add(file.GetArrayEncodingId(i));
                    }

                    encodings.Seed(old);
                    IndexDirectory? previous = file.HasIndexDirectory
                        ? await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                    using IndexWriter indexes = await BuildAsync(
                        file, sink, policy, options, encodings, previous?.Entries ?? [], length, cancellationToken).ConfigureAwait(false);
                    reports = [.. indexes.Reports];
                    await WriteTailAsync(file, sink, indexes, encodings, options?.Identity, cancellationToken).ConfigureAwait(false);
                    await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // The reader is closed: the tail goes behind the file.
            FileStream target = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false))
            {
                if (target.Length != length)
                {
                    throw new IOException($"{path} changed while it was being indexed.");
                }

                target.Seek(0, SeekOrigin.End);
                FileStream source = new FileStream(scratch, FileMode.Open, FileAccess.Read, FileShare.None);
                await using (source.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            System.IO.File.Delete(scratch);
        }

        return reports;
    }

    /// <summary>
    /// Indexes <paramref name="path"/> under <paramref name="policy"/> into a sidecar file, leaving
    /// the file untouched; a reader takes it with <see cref="VortexReadOptions.IndexSidecarPath"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="policy">What to build.</param>
    /// <param name="sidecarPath">Where the sidecar goes; null for <c>path + ".idx"</c>.</param>
    /// <param name="options">As for <see cref="AppendIndexesAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the read and the writes.</param>
    /// <returns>What became of every index the policy asked for.</returns>
    /// <exception cref="VortexUnsupportedException">The file's layout is not one this can index.</exception>
    public static async ValueTask<IReadOnlyList<IndexWriteReport>> WriteSidecarAsync(
        string path, WritePolicy policy, string? sidecarPath = null, VortexWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(policy);
        sidecarPath ??= path + ".idx";
        VortexFile file = await VortexFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            FileStream stream = new FileStream(sidecarPath, FileMode.Create, FileAccess.Write, FileShare.None);
            StreamSegmentSink sink = new StreamSegmentSink(stream, ownsStream: true);
            await using (sink.ConfigureAwait(false))
            {
                await sink.WriteAsync(IndexSidecar.Magic.ToArray(), cancellationToken).ConfigureAwait(false);
                EncodingDictionary encodings = new EncodingDictionary(
                    ComponentKind.Array, options?.TargetEdition ?? EditionRegistry.Newest);
                using IndexWriter indexes = await BuildAsync(
                    file, sink, policy, options, encodings, [], 0, cancellationToken).ConfigureAwait(false);
                byte[] sha = await IndexSidecar.HashAsync(file.Segments, file.FileLength, cancellationToken).ConfigureAwait(false);
                long offset = sink.Position;
                byte[] directory = indexes.Directory(file.RowCount, (file.FileLength, sha, [.. encodings.Ids]))!;
                await sink.WriteAsync(directory, cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync(IndexSidecar.Trailer(offset, directory.Length), cancellationToken).ConfigureAwait(false);
                await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                return [.. indexes.Reports];
            }
        }
    }

    /// <summary>Feeds every row to the builders and writes their runs to <paramref name="sink"/>.</summary>
    private static async ValueTask<IndexWriter> BuildAsync(
        VortexFile file, StreamSegmentSink sink, WritePolicy policy, VortexWriteOptions? options,
        EncodingDictionary encodings, IReadOnlyList<IndexEntry> previous, long previousEof,
        CancellationToken cancellationToken)
    {
        DType schema = file.Schema;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            throw VortexFileWriter.AppendPlan.Refused("its root is not a struct of columns");
        }

        int fields = schema.FieldCount;
        LayoutTree tree = file.LayoutTree;
        (List<(LayoutNode Flat, long Start)> chunks, long zoneLength) = VortexFileWriter.AppendPlan.ColumnChunks(tree, 0);
        for (int field = 1; field < fields; field++)
        {
            (List<(LayoutNode Flat, long Start)> other, long length) = VortexFileWriter.AppendPlan.ColumnChunks(tree, field);
            if (!VortexFileWriter.AppendPlan.SameChunks(chunks, other) || (length != 0 && zoneLength != 0 && length != zoneLength))
            {
                throw VortexFileWriter.AppendPlan.Refused("its columns are not chunked alike");
            }

            zoneLength = Math.Max(zoneLength, length);
        }

        int blockRows = zoneLength > 0
            ? checked((int)zoneLength)
            : options?.RowBlockSize ?? VortexWriteOptions.Default.RowBlockSize ?? 0;
        if (blockRows <= 0)
        {
            throw VortexFileWriter.AppendPlan.Refused("it has no block length: no zone map, and no RowBlockSize to take one from");
        }

        long rows = file.RowCount;
        for (int c = 1; c < chunks.Count; c++)
        {
            if (chunks[c].Start % blockRows != 0)
            {
                throw VortexFileWriter.AppendPlan.Refused($"its chunk at row {chunks[c].Start} does not start a block of {blockRows}");
            }
        }

        IndexWriter indexes = new IndexWriter(
            policy, schema, isTabular: true, fields,
            options?.IndexBudgetPerMille ?? VortexWriteOptions.Default.IndexBudgetPerMille, blockRows, options?.KeyEncoder);
        try
        {
            indexes.Preserve(previous, previousEof);

            // The columns as the dictionary probe and the first-block rule read them: a summary per
            // block, and the chunks that are dictionaries.
            int blockCount = VortexFileWriter.ChunkBlocks(rows, blockRows);
            ColumnWriter[] columns = new ColumnWriter[fields];
            List<long> chunkRows = [];
            bool[] dict = new bool[fields * chunks.Count];
            for (int field = 0; field < fields; field++)
            {
                (List<(LayoutNode Flat, long Start)> own, _) = VortexFileWriter.AppendPlan.ColumnChunks(tree, field);
                for (int c = 0; c < own.Count; c++)
                {
                    dict[(field * chunks.Count) + c] = await IsDictionaryAsync(file, own[c].Flat, cancellationToken).ConfigureAwait(false);
                }
            }

            for (int field = 0; field < fields; field++)
            {
                BlockStats[] blocks = new BlockStats[blockCount];
                for (int c = 0; c < chunks.Count; c++)
                {
                    long start = chunks[c].Start;
                    long end = c + 1 < chunks.Count ? chunks[c + 1].Start : rows;
                    byte scheme = dict[(field * chunks.Count) + c] ? (byte)(ColumnScheme.Dict + 1) : (byte)0;
                    for (long b = start / blockRows; b < VortexFileWriter.ChunkBlocks(end, blockRows); b++)
                    {
                        long blockEnd = Math.Min((b + 1) * blockRows, rows);
                        blocks[b] = BlockStats.Summary(blockEnd - (b * blockRows), 0, false, null, null, scheme);
                    }
                }

                columns[field] = new ColumnWriter();
                columns[field].Seed(blocks);
            }

            int[] fieldNodes = new int[fields];
            int filled = 0;
            int block = 0;
            for (int c = 0; c < chunks.Count; c++)
            {
                long start = chunks[c].Start;
                long end = c + 1 < chunks.Count ? chunks[c + 1].Start : rows;
                chunkRows.Add(end - start);
                await foreach (RecordBatch batch in file.Scan()
                    .Rows(new RowRange(start, end))
                    .WithPruning(false)
                    .WithIndexes(false)
                    .ExecuteAsync()
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    (filled, block) = Feed(indexes, columns, batch, fieldNodes, blockRows, filled, block);
                }

                if (c + 1 == chunks.Count && filled > 0)
                {
                    indexes.CloseBlock(columns);
                    filled = 0;
                }

                // `Auto` judges a filter against its column's written bytes: the chunk's segments.
                for (int field = 0; field < fields; field++)
                {
                    (List<(LayoutNode Flat, long Start)> own, _) = VortexFileWriter.AppendPlan.ColumnChunks(tree, field);
                    indexes.AddColumnBytes(field, file.SegmentSpecs[(int)own[c].Flat.Segments[0]].Length);
                }

                int firstBlock = checked((int)(start / blockRows));
                indexes.CloseChunk(firstBlock, VortexFileWriter.ChunkBlocks(end - start, blockRows), start, end - start);
                indexes.Judge();
                await FlushAsync(indexes, sink, encodings, cancellationToken).ConfigureAwait(false);
            }

            indexes.EndOfData();
            indexes.Judge();
            await FlushAsync(indexes, sink, encodings, cancellationToken).ConfigureAwait(false);
            indexes.Close(columns, chunkRows, blockRows, file.FileLength);
            return indexes;
        }
        catch
        {
            indexes.Dispose();
            throw;
        }
    }

    /// <summary>One batch into the builders, cut at block boundaries; the open block's fill and number after it.</summary>
    private static (int Filled, int Block) Feed(
        IndexWriter indexes, ColumnWriter[] columns, RecordBatch batch, int[] fieldNodes, int blockRows, int filled, int block)
    {
        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        for (int field = 0; field < fieldNodes.Length; field++)
        {
            fieldNodes[field] = root.GetFieldIndex(field);
        }

        int count = batch.RowCount;
        int offset = 0;
        while (offset < count)
        {
            int take = Math.Min(blockRows - filled, count - offset);
            for (int field = 0; field < fieldNodes.Length; field++)
            {
                indexes.Accumulate(field, arena, fieldNodes[field], offset, take);
            }

            indexes.AccumulateKeys(arena, fieldNodes, offset, take);
            indexes.AccumulateNested(arena, fieldNodes, offset, take);
            offset += take;
            filled += take;
            if (filled == blockRows)
            {
                indexes.CloseBlock(columns);
                filled = 0;
                block++;
            }
        }

        return (filled, block);
    }

    private static async ValueTask FlushAsync(
        IndexWriter indexes, StreamSegmentSink sink, EncodingDictionary encodings, CancellationToken cancellationToken)
    {
        while (indexes.TryTakePayload(encodings, out ArrayBlobWriter.BlobLease blob, out PendingPayload? payload))
        {
            using (blob)
            {
                long before = sink.Position;
                long aligned = (before + VortexLimits.MaxAlignment - 1) & ~((long)VortexLimits.MaxAlignment - 1);
                if (aligned > before)
                {
                    await sink.WriteAsync(new byte[aligned - before], cancellationToken).ConfigureAwait(false);
                }

                await sink.WriteAsync(blob.Memory, cancellationToken).ConfigureAwait(false);
                IndexSegment segment = IndexSegment.Of(aligned, blob.Memory.Span, (byte)VortexLimits.MaxAlignmentExponent);
                indexes.Placed(payload!, segment, sink.Position - before, sink.Position);
            }
        }
    }

    /// <summary>Whether a flat chunk's root array is a dictionary, read from its segment.</summary>
    private static async ValueTask<bool> IsDictionaryAsync(VortexFile file, LayoutNode flat, CancellationToken cancellationToken)
    {
        using ScanContext context = new ScanContext(file, ScanContext.MetadataCapacity);
        int slot = context.Segments.Add(file.SegmentSpecs[(int)flat.Segments[0]]);
        await file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
        return RootIsDictionary(context, flat, slot);
    }

    private static bool RootIsDictionary(ScanContext context, LayoutNode flat, int slot)
    {
        VortexBuffer segment = context.Segments.GetBuffer(slot);
        Arrays.Metadata.FlatLayoutMetadata metadata = Arrays.Metadata.FlatLayoutMetadata.Read(flat.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            context.Decode.LoadBlob(metadata.ArrayEncodingTree, segment);
        }
        else
        {
            context.Decode.LoadBlob(segment);
        }

        return context.Nodes.Root.Encoding == ArrayEncodingId.Dict;
    }

    /// <summary>The statistics, directory, dtype, layout, footer, postscript and EOF, after the runs.</summary>
    private static async ValueTask WriteTailAsync(
        VortexFile file, StreamSegmentSink sink, IndexWriter indexes, EncodingDictionary encodings,
        Guid? identity, CancellationToken cancellationToken)
    {
        OldTail old = await OldTail.ReadAsync(file, cancellationToken).ConfigureAwait(false);

        long statisticsOffset = 0;
        int statisticsLength = 0;
        if (old.Statistics is { } statistics)
        {
            statisticsOffset = sink.Position;
            statisticsLength = statistics.Length;
            await sink.WriteAsync(statistics, cancellationToken).ConfigureAwait(false);
        }

        long directoryOffset = sink.Position;
        byte[]? directory = indexes.Directory(file.RowCount);
        if (directory is not null)
        {
            await sink.WriteAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        long dtypeOffset = sink.Position;
        byte[] dtype = DTypeFlatBuffers.Serialize(file.Schema);
        await sink.WriteAsync(dtype, cancellationToken).ConfigureAwait(false);

        long layoutOffset = sink.Position;
        await sink.WriteAsync(old.Layout, cancellationToken).ConfigureAwait(false);

        List<string> layouts = new List<string>(file.LayoutEncodingCount);
        for (int i = 0; i < file.LayoutEncodingCount; i++)
        {
            layouts.Add(file.GetLayoutEncodingId(i));
        }

        long footerOffset = sink.Position;
        byte[] footer = VortexFileWriter.Footer(encodings.Ids, layouts, file.SegmentSpecs);
        await sink.WriteAsync(footer, cancellationToken).ConfigureAwait(false);

        // A NEW POSTSCRIPT IS A NEW VERSION of the bytes, even though no data byte moved: the file
        // an index outside it was built against is not this one (docs/13-dataset.md §7).
        await VortexFileWriter.WriteEndAsync(
            sink,
            new VortexFileWriter.PostscriptPlacement(
                dtypeOffset, dtype.Length, layoutOffset, old.Layout.Length, footerOffset, footer.Length,
                statisticsOffset, statisticsLength, directoryOffset, directory?.Length ?? 0),
            identity,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The layout and statistics bytes of the old postscript, which the new one names again.</summary>
    private sealed class OldTail
    {
        internal required byte[] Layout { get; init; }

        internal byte[]? Statistics { get; init; }

        internal static async ValueTask<OldTail> ReadAsync(VortexFile file, CancellationToken cancellationToken)
        {
            long length = file.FileLength;
            int window = (int)Math.Min(length, VortexFileFormat.MaxPostscriptSize + VortexFileFormat.EofSize);
            (SegmentSpec layout, SegmentSpec? statistics) specs;
            using (SegmentOwner tail = await file.Segments.ReadRangeAsync(length - window, window, 1, cancellationToken).ConfigureAwait(false))
            {
                specs = Specs(tail.Buffer);
            }

            byte[] layoutBytes = await ReadAsync(file, specs.layout, cancellationToken).ConfigureAwait(false);
            byte[]? statisticsBytes = specs.statistics is { } s
                ? await ReadAsync(file, s, cancellationToken).ConfigureAwait(false)
                : null;
            return new OldTail { Layout = layoutBytes, Statistics = statisticsBytes };
        }

        private static async ValueTask<byte[]> ReadAsync(VortexFile file, SegmentSpec spec, CancellationToken cancellationToken)
        {
            using SegmentOwner owner = await file.Segments.ReadRangeAsync((long)spec.Offset, (int)spec.Length, 1, cancellationToken).ConfigureAwait(false);
            return owner.Buffer.Span.ToArray();
        }

        private static (SegmentSpec Layout, SegmentSpec? Statistics) Specs(VortexBuffer tail)
        {
            ReadOnlySpan<byte> bytes = tail.Span;
            int eof = bytes.Length - VortexFileFormat.EofSize;
            int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[(eof + VortexFileFormat.EofPostscriptLengthOffset)..]);
            int budget = VortexLimits.MaxFlatBufferTables;
            PostscriptView view = PostscriptView.Root(bytes.Slice(eof - length, length), ref budget);
            if (view.Layout.Compression != CompressionScheme.None
                || (view.HasStatistics && view.Statistics.Compression != CompressionScheme.None))
            {
                throw VortexFileWriter.AppendPlan.Refused("its layout or statistics segment is compressed");
            }

            SegmentSpec layout = view.Layout.ToSegmentSpec();
            SegmentSpec? statistics = view.HasStatistics ? view.Statistics.ToSegmentSpec() : null;
            return (layout, statistics);
        }
    }
}
