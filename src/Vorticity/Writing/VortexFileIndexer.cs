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
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// An index fragment — runs, a directory bound to the file, a trailer — and what building it did.
/// Every offset in it counts from its first byte, so it may be stored anywhere.
/// </summary>
public sealed record IndexFragment(ReadOnlyMemory<byte> Bytes, IReadOnlyList<IndexWriteReport> Reports);

/// <summary>Builds indexes over a file that already exists; no data byte moves.</summary>
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
    /// <exception cref="VortexUnsupportedException">The file's layout is not one this can index.</exception>
    internal static async ValueTask<IReadOnlyList<IndexWriteReport>> AppendIndexesAsync(
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
                VortexFileRepair.ThrowIfTorn(path, file, "An index");
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
                        file, sink, policy, options, encodings, previous?.Entries ?? [], length,
                        new RowRange(0, file.RowCount), cancellationToken).ConfigureAwait(false);
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
    /// Indexes the rows <paramref name="rows"/> of <paramref name="file"/> under
    /// <paramref name="policy"/> into a fragment, leaving the file untouched; a reader adds it to
    /// the file's own index with <see cref="VortexReadOptions.IndexFragments"/>.
    /// </summary>
    /// <param name="file">The file, open. It is read, never written.</param>
    /// <param name="policy">What to build.</param>
    /// <param name="rows">
    /// The rows to index, whole blocks: from a block boundary to a block boundary, or to the file's
    /// end. The whole file is <c>new RowRange(0, file.RowCount)</c>.
    /// </param>
    /// <param name="storeToken">
    /// The store's token for the file, which binds the fragment to a file written without an
    /// identity; null to bind by the identity alone.
    /// </param>
    /// <param name="contentHash">
    /// The XXH3-128 of the file's bytes when the caller knows it, recorded for verification; null to
    /// record none. No reader computes it.
    /// </param>
    /// <param name="options">As for <see cref="AppendIndexesAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// A fragment over a range covers only the blocks of that range, so a key source refuses the
    /// entry until fragments cover the whole file. A dictionary probe follows the file's chunks
    /// rather than the rows, and is written whole or not at all.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty, past the file, or not whole blocks.</exception>
    /// <exception cref="InvalidOperationException">The file has no identity and no store token was given.</exception>
    /// <exception cref="VortexUnsupportedException">The file's layout is not one this can index.</exception>
    internal static async ValueTask<IndexFragment> BuildFragmentAsync(
        VortexFile file,
        WritePolicy policy,
        RowRange rows,
        string? storeToken = null,
        UInt128? contentHash = null,
        VortexWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(policy);
        VortexFileRepair.ThrowIfTorn("The file", file, "A fragment");
        if (file.StoredIdentity is null && storeToken is null)
        {
            throw new InvalidOperationException(
                "A fragment is bound to its file by the file's identity, or by the store's token for a file " +
                "written without one (13 §7): this file has no identity, and no token was given.");
        }

        MemoryStream bytes = new MemoryStream();
        StreamSegmentSink sink = new StreamSegmentSink(bytes);
        await using (sink.ConfigureAwait(false))
        {
            IReadOnlyList<IndexWriteReport> reports = await WriteContainerAsync(
                file, sink, policy, rows, storeToken, contentHash, options, cancellationToken).ConfigureAwait(false);
            return new IndexFragment(bytes.ToArray(), reports);
        }
    }

    /// <summary>Writes one container: the magic, the runs, the bound directory and the trailer.</summary>
    private static async ValueTask<IReadOnlyList<IndexWriteReport>> WriteContainerAsync(
        VortexFile file, StreamSegmentSink sink, WritePolicy policy, RowRange rows, string? token, UInt128? hash,
        VortexWriteOptions? options, CancellationToken cancellationToken)
    {
        await sink.WriteAsync(IndexContainer.Magic.ToArray(), cancellationToken).ConfigureAwait(false);
        EncodingDictionary encodings = new EncodingDictionary(
            ComponentKind.Array, options?.TargetEdition ?? EditionRegistry.Newest);
        using IndexWriter indexes = await BuildAsync(
            file, sink, policy, options, encodings, [], 0, rows, cancellationToken).ConfigureAwait(false);
        FragmentBinding binding = new FragmentBinding(file.FileLength, file.StoredIdentity, token, hash, [.. encodings.Ids]);
        long offset = sink.Position;
        byte[] directory = indexes.Directory(file.RowCount, binding)!;
        await sink.WriteAsync(directory, cancellationToken).ConfigureAwait(false);
        await sink.WriteAsync(IndexContainer.Trailer(offset, directory.Length), cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        return [.. indexes.Reports];
    }

    /// <summary>Feeds the range's rows to the builders and writes their runs.</summary>
    private static async ValueTask<IndexWriter> BuildAsync(
        VortexFile file, StreamSegmentSink sink, WritePolicy policy, VortexWriteOptions? options,
        EncodingDictionary encodings, IReadOnlyList<IndexEntry> previous, long previousEof, RowRange range,
        CancellationToken cancellationToken)
    {
        DType schema = file.DType;
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

        // A range is whole blocks: a run covers blocks, and a block cut in two would be claimed by
        // two fragments or by neither.
        bool whole = range.Start == 0 && range.End == rows;
        if (!whole && (range.IsEmpty || range.End > rows || range.Start % blockRows != 0
            || (range.End % blockRows != 0 && range.End != rows)))
        {
            throw new ArgumentOutOfRangeException(
                "rows",
                $"A fragment indexes whole blocks of {blockRows} rows of a file of {rows}: " +
                $"[{range.Start}, {range.End}) is not.");
        }

        IndexWriter indexes = new IndexWriter(
            policy, schema, isTabular: true, fields,
            options?.IndexBudgetPerMille ?? VortexWriteOptions.Default.IndexBudgetPerMille, blockRows, options?.KeyEncoder,
            options?.ScratchDirectory, options?.ScratchMemoryBytes ?? IndexWriter.DefaultScratchMemoryBytes,
            options?.WideRowsAbove ?? uint.MaxValue)
        {
            Fences = options?.Fences ?? FenceShape.Default,
        };
        try
        {
            indexes.Preserve(previous, previousEof);

            // The builders' block numbers start where the range does, so their runs say which blocks
            // of the file they cover whatever part of it this pass reads.
            indexes.Begin(checked((int)(range.Start / blockRows)), range.Start);

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

                // Every chunk for the dictionary probe, which describes the file's layout and not
                // the rows a pass reads; only the rows the range names for the builders.
                chunkRows.Add(end - start);
                long from = Math.Max(start, range.Start);
                long to = Math.Min(end, range.End);
                if (from >= to)
                {
                    continue;
                }

                await foreach (RecordBatch batch in file.ScanBuilder()
                    .Rows(new RowRange(from, to))
                    .WithPruning(false)
                    .WithIndexes(false)
                    .ExecuteAsync()
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    (filled, block) = Feed(indexes, columns, batch, fieldNodes, blockRows, filled, block);
                }

                if (to == range.End && filled > 0)
                {
                    // The last block of the range, which only the end of the file can cut short.
                    indexes.CloseBlock(columns);
                    filled = 0;
                }

                // `Auto` judges a filter against its column's written bytes: the chunk's segments.
                for (int field = 0; field < fields; field++)
                {
                    (List<(LayoutNode Flat, long Start)> own, _) = VortexFileWriter.AppendPlan.ColumnChunks(tree, field);
                    indexes.AddColumnBytes(field, file.SegmentSpecs[(int)own[c].Flat.Segments[0]].Length);
                }

                int firstBlock = checked((int)(from / blockRows));
                indexes.CloseChunk(firstBlock, VortexFileWriter.ChunkBlocks(to - from, blockRows), from, to - from);
                indexes.Judge();

                // The budget before the bytes, as on the write path: the file's own data bytes are
                // known here, the indexer reading a file that is already whole.
                if (indexes.TryOpenFlush(file.FileLength + indexes.FileBytes))
                {
                    await FlushAsync(indexes, sink, encodings, cancellationToken).ConfigureAwait(false);
                }
            }

            indexes.EndOfData();
            indexes.Judge();
            indexes.SettleBudget(file.FileLength);
            await FlushAsync(indexes, sink, encodings, cancellationToken).ConfigureAwait(false);
            indexes.Close(columns, chunkRows, blockRows, file.FileLength);
            await indexes.WriteFencePagesAsync(sink, cancellationToken).ConfigureAwait(false);
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
        byte[] dtype = DTypeFlatBuffers.Serialize(file.DType);
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

        // A new postscript is a new version of the bytes, even though no data byte moved: an index
        // built against the old bytes does not describe it.
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
