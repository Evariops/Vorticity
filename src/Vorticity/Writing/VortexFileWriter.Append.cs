// Appending to an existing file - docs/11-write-strategy.md §3.8, docs/10-indexes.md §8.
//
// THE OLD BYTES STAY WHERE THEY ARE. The old segments keep their ids and their offsets, and the
// array encodings their indices, so every chunk already written is referenced again as it is; the
// new chunks, the zone maps, the new index runs, a new directory, footer and postscript follow the
// old end of file, and the old postscript becomes dead bytes. Blocks are counted from row 0 of the
// file, so the new blocks are numbered from the old row count and fall exactly where one write
// would have put them.
//
// ONE CHUNK IS READ AGAIN, AT MOST. A zone map allows only its last zone to be short, and the last
// block's filters and runs were built on a short block; so when the old row count is not a whole
// number of blocks, the last chunk is decoded, its rows become the first rows written, its segment
// is dropped from the layout, and the index runs reaching into it are replaced. Every other old
// block is taken from what the file already says of it: its zone -- rows, nulls, exact bounds --
// for the zone map written again over the whole file, and the file statistics for the old rows'
// bounds and order.
//
// WHAT THE WRITER READS, IT READS FROM ITS OWN SHAPE. The file must be a struct of columns, each a
// chunked layout of flat segments (a zoned wrapper allowed), with the same chunks in every column
// -- the shape this writer produces; anything else is refused and a rewrite is the answer.
//
// NOT ATOMIC. The caller is single-writer and flushes; a torn append leaves the tail invalid, the
// new directory records the old end of file, and `VortexFileRepair` truncates back to the last
// valid postscript.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

public sealed partial class VortexFileWriter
{
    /// <summary>
    /// What an append carries, in one field: a plain write pays a reference and nothing else, which
    /// the write allocation ceilings held to four bytes.
    /// </summary>
    private AppendState? _append;

    private sealed class AppendState
    {
        internal required OldColumn[] Old { get; init; }

        internal required bool Reopened { get; init; }

        internal required int Boundary { get; init; }

        internal required bool[] NoZoneMap { get; init; }

        internal ScalarStore Scalars { get; } = new ScalarStore();
    }

    /// <summary>
    /// Opens <paramref name="path"/> to continue it: the batches written next follow its rows
    /// (docs/11-write-strategy.md §3.8).
    /// </summary>
    /// <param name="path">A file this writer produced, or one of the same shape.</param>
    /// <param name="options">
    /// Write-time policy; null to take the file's own. <see cref="VortexWriteOptions.RowBlockSize"/>
    /// is the file's zone length whatever is given, and the index policy is the file's directory's
    /// when <paramref name="options"/> is null. <see cref="VortexWriteOptions.KeyEncoder"/> is never
    /// stored, so a file with composite keys wants it given again.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads and the first writes.</param>
    /// <returns>The writer; the caller writes, completes and disposes it.</returns>
    /// <remarks>
    /// When the file's row count is not a whole number of blocks, its last chunk is read and written
    /// again, which costs one chunk whatever the file's size. The append is not atomic:
    /// <see cref="VortexFileRepair.RepairAsync"/> truncates a torn one.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="VortexUnsupportedException">The file's layout is not one this writer can continue.</exception>
    /// <exception cref="VortexFormatException">The file is malformed.</exception>
    public static async ValueTask<VortexFileWriter> AppendAsync(
        string path, VortexWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        AppendPlan plan;
        VortexFile file = await VortexFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            plan = await AppendPlan.ReadAsync(file, options, cancellationToken).ConfigureAwait(false);
        }

        VortexWriteOptions effective = (options ?? VortexWriteOptions.Default).ForAppend(
            plan.BlockRows,
            options is null ? plan.Policy ?? WritePolicy.Auto : options.Indexes,
            plan.Statistics is not null && (options?.FileStatistics ?? true),
            options?.IndexBudgetPerMille ?? plan.BudgetPerMille);

        FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        VortexFileWriter writer;
        try
        {
            if (stream.Length != plan.FileLength)
            {
                throw new IOException($"{path} changed while it was being opened for an append.");
            }

            stream.Seek(0, SeekOrigin.End);
            writer = Create(new StreamSegmentSink(stream, ownsStream: true, plan.FileLength), plan.Schema, effective);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            writer.Resume(plan);
            foreach (RecordBatch batch in plan.Reopened)
            {
                CanonicalArena arena = batch.Arena;
                using (batch)
                {
                    await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                // The owned copy rented from the pool; the writer has taken what it keeps.
                arena.Reset();
            }
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return writer;
    }

    /// <summary>Takes over the old file: segments, encodings, chunks, blocks, index runs.</summary>
    private void Resume(AppendPlan plan)
    {
        _started = true;
        _arrayEncodings.Seed(plan.ArrayEncodings);
        _segments.AddRange(plan.Segments);
        _chunkRows.AddRange(plan.KeptChunkRows);
        _rowCount = plan.KeptRows;
        _emittedBlocks = plan.Boundary;
        bool[] noZoneMap = new bool[_fieldCount];
        _append = new AppendState
        {
            Old = plan.Columns,
            Reopened = plan.Reopened.Count > 0,
            Boundary = plan.Boundary,
            NoZoneMap = noZoneMap,
        };
        for (int field = 0; field < _fieldCount; field++)
        {
            _columnSegments[field].AddRange(plan.Columns[field].KeptSegments);
            _columns[field].Seed(plan.Columns[field].Blocks);
            noZoneMap[field] = !plan.Columns[field].HasZones && plan.Boundary > 0;
        }

        _indexes?.Continue(plan.Entries, plan.Boundary, plan.KeptRows, plan.FileLength);
    }

    /// <summary>
    /// The file statistics of an appended column: the old rows' from the old statistics and zones,
    /// the new rows' from the pass, and the order across the seam decided from the two.
    /// </summary>
    private void AppendStatistics(int field, ref ArrayStatsValues values)
    {
        AppendState append = _append!;
        OldColumn old = append.Old[field];
        ColumnWriter writer = _columns[field];
        BlockStats all = writer.Chunk(0, writer.Blocks.Count);
        BlockStats fresh = writer.Chunk(append.Boundary, writer.Blocks.Count - append.Boundary);
        DType column = _isTabular ? _schema.GetField(field) : _schema;

        // Nulls: the old blocks' zones are exact, the new blocks' counts too.
        if (old.HasZones || append.Boundary == 0)
        {
            values.NullCount = (ulong)all.NullCount;
        }

        // Bounds: the old statistics cover every old row, and every old row is in the file.
        FilterLiteral? min = old.Min;
        FilterLiteral? max = old.Max;
        if (fresh.HasBounds)
        {
            min = min is { } m ? Lesser(m, fresh.Min) : fresh.Min;
            max = max is { } x ? Greater(x, fresh.Max) : fresh.Max;
        }

        bool oldHasValues = old.NonNull != 0;
        if (column.Kind == DTypeKind.Primitive && min is { } low && max is { } high
            && (old.BoundsExact || !oldHasValues))
        {
            values.Min = ScalarProtobuf.SerializeValue(Bound(append.Scalars, column.PType, low));
            values.Max = ScalarProtobuf.SerializeValue(Bound(append.Scalars, column.PType, high));
        }

        (values.IsSorted, values.IsStrictSorted) = Order(old, fresh);
    }


    /// <summary>Whether the file is sorted across the seam, from the two halves.</summary>
    private (bool? Sorted, bool? Strict) Order(OldColumn old, BlockStats fresh)
    {
        bool? oldSorted = old.Sorted;
        bool? oldStrict = old.Strict;
        bool? newSorted = fresh.IsPresent ? fresh.IsSorted : true;
        bool? newStrict = fresh.IsPresent ? fresh.IsStrictSorted : true;
        if (oldSorted is null || newSorted is null)
        {
            return (null, null);
        }

        if (_append!.Reopened || _append.Boundary == 0)
        {
            // The re-opened chunk is written again, so the seam is inside the new half, and the old
            // half's own order covers the seam before it.
            return (oldSorted.Value && newSorted.Value, AndNullable(oldStrict, newStrict));
        }

        if (!oldSorted.Value || !newSorted.Value)
        {
            return (false, false);
        }

        if (old.NonNull < 0)
        {
            return (null, null);
        }

        // A null sorts below every value: one after the old values unsorts the file.
        if (fresh.NullCount > 0 && old.NonNull > 0)
        {
            return (false, false);
        }

        if (old.NonNull == 0)
        {
            // Only nulls before: sorted; strict unless a null meets a null.
            bool repeat = old.Rows > 0 && fresh.NullCount > 0;
            return (true, AndNullable(oldStrict, newStrict) is { } s ? s && !repeat : null);
        }

        if (old.Max is not { } last || !fresh.HasBounds || !old.BoundsExact)
        {
            return (null, null);
        }

        int seam = Compare(last, fresh.Min);
        bool sorted = seam <= 0;
        bool? strict = AndNullable(oldStrict, newStrict) is { } both ? both && seam < 0 : null;
        return (sorted, sorted ? strict : false);
    }

    private static bool? AndNullable(bool? a, bool? b) => a is null || b is null ? null : a.Value && b.Value;

    private static int Compare(FilterLiteral a, FilterLiteral b) => a.Kind switch
    {
        FilterLiteralKind.Signed => a.SignedValue.CompareTo(b.SignedValue),
        FilterLiteralKind.Unsigned => a.UnsignedValue.CompareTo(b.UnsignedValue),
        _ => a.FloatValue.CompareTo(b.FloatValue),
    };

    private static FilterLiteral Lesser(FilterLiteral a, FilterLiteral b) => Compare(a, b) <= 0 ? a : b;

    private static FilterLiteral Greater(FilterLiteral a, FilterLiteral b) => Compare(a, b) >= 0 ? a : b;

    /// <summary>What an append takes from the old file, read before a byte of it is written.</summary>
    internal sealed class AppendPlan
    {
        internal required DType Schema { get; init; }

        internal required long FileLength { get; init; }

        internal required int BlockRows { get; init; }

        internal required int Boundary { get; init; }

        internal required long KeptRows { get; init; }

        internal required List<long> KeptChunkRows { get; init; }

        internal required List<string> ArrayEncodings { get; init; }

        internal required List<SegmentSpec> Segments { get; init; }

        internal required OldColumn[] Columns { get; init; }

        internal required List<RecordBatch> Reopened { get; init; }

        internal required IReadOnlyList<IndexEntry> Entries { get; init; }

        internal WritePolicy? Policy { get; init; }

        internal int BudgetPerMille { get; init; } = IndexDirectory.DefaultBudgetPerMille;

        internal FileStatistics? Statistics { get; init; }

        internal static async ValueTask<AppendPlan> ReadAsync(
            VortexFile file, VortexWriteOptions? options, CancellationToken cancellationToken)
        {
            DType schema = file.Schema;
            if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
            {
                throw Refused("its root is not a struct of columns");
            }

            LayoutTree tree = file.LayoutTree;
            int fields = schema.FieldCount;
            List<(LayoutNode Flat, long Start)>[] chunks = new List<(LayoutNode, long)>[fields];
            long zoneLength = 0;
            bool[] zoned = new bool[fields];
            for (int field = 0; field < fields; field++)
            {
                (chunks[field], long length) = ColumnChunks(tree, field);
                zoned[field] = length > 0;
                if (length > 0)
                {
                    if (zoneLength != 0 && zoneLength != length)
                    {
                        throw Refused("its columns have different zone lengths");
                    }

                    zoneLength = length;
                }

                if (field > 0 && !SameChunks(chunks[0], chunks[field]))
                {
                    throw Refused("its columns are not chunked alike");
                }
            }

            int blockRows = zoneLength > 0
                ? checked((int)zoneLength)
                : options?.RowBlockSize ?? VortexWriteOptions.Default.RowBlockSize ?? 0;
            if (blockRows <= 0)
            {
                throw Refused("it has no block length: no zone map, and no RowBlockSize to take one from");
            }

            long rows = file.RowCount;
            List<(LayoutNode Flat, long Start)> first = chunks[0];
            bool reopen = rows % blockRows != 0 && first.Count > 0;
            int keptChunks = reopen ? first.Count - 1 : first.Count;
            long keptRows = reopen ? first[^1].Start : rows;
            for (int c = 1; c < keptChunks; c++)
            {
                if (first[c].Start % blockRows != 0)
                {
                    throw Refused($"its chunk at row {first[c].Start} does not start a block of {blockRows}");
                }
            }

            if (keptRows % blockRows != 0)
            {
                throw Refused($"its last kept chunk ends at row {keptRows}, inside a block of {blockRows}");
            }

            int boundary = checked((int)(keptRows / blockRows));

            // The old zones, for the blocks kept.
            IndexDirectory? directory = file.HasIndexDirectory
                ? await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false)
                : null;
            HashSet<int>[] dictBlocks = DictBlocks(directory, schema);
            OldColumn[] columns = new OldColumn[fields];
            ZonePruner? zones = null;
            if (boundary > 0 && zoneLength > 0)
            {
                VortexExpr every = Expr.IsNotNull(Expr.Field(schema.GetFieldName(0)));
                for (int field = 1; field < fields; field++)
                {
                    every = Expr.And(every, Expr.IsNotNull(Expr.Field(schema.GetFieldName(field))));
                }

                zones = (await ZonePruningPlan.PlanAsync(file, tree, every, cancellationToken).ConfigureAwait(false)).Zones;
            }

            FileStatistics? statistics = file.HasFileStatistics ? file.Statistics : null;
            for (int field = 0; field < fields; field++)
            {
                DType dtype = schema.GetField(field);
                ZoneColumn? column = zones?.Column(schema.GetFieldName(field));
                BlockStats[] blocks = new BlockStats[boundary];
                bool hasZones = column is { HasStatistics: true } && zoned[field];
                for (int z = 0; z < boundary; z++)
                {
                    long zoneRows = Math.Min(blockRows, rows - ((long)z * blockRows));
                    byte scheme = dictBlocks[field].Contains(z) ? (byte)(ColumnScheme.Dict + 1) : (byte)0;
                    if (!hasZones)
                    {
                        blocks[z] = BlockStats.Summary(zoneRows, 0, dtype.Kind == DTypeKind.Primitive, null, null, scheme);
                        continue;
                    }

                    ZoneBounds bounds = column!.Bounds(z);
                    if (!bounds.HasNullCount)
                    {
                        hasZones = false;
                        blocks[z] = BlockStats.Summary(zoneRows, 0, dtype.Kind == DTypeKind.Primitive, null, null, scheme);
                        continue;
                    }

                    bool exact = bounds.IsExact && bounds.HasMin && bounds.HasMax;
                    blocks[z] = BlockStats.Summary(
                        zoneRows, bounds.NullCount, dtype.Kind == DTypeKind.Primitive,
                        exact ? bounds.Min : null, exact ? bounds.Max : null, scheme);
                }

                columns[field] = OldColumn.From(statistics, field, rows, hasZones, blocks, chunks[field], keptChunks);
            }

            // The re-opened chunk, owned: the file is closed before the append writes.
            List<RecordBatch> reopened = [];
            if (reopen)
            {
                await foreach (RecordBatch batch in file.Scan()
                    .Rows(new RowRange(keptRows, rows))
                    .WithPruning(false)
                    .WithIndexes(false)
                    .ExecuteAsync()
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    CanonicalArena owned = new CanonicalArena();
                    int root = owned.CopyFrom(batch.Arena, batch.RootIndex);
                    reopened.Add(new RecordBatch(owned, root, batch.StartRow));
                }
            }

            List<string> encodings = new List<string>(file.ArrayEncodingCount);
            for (int i = 0; i < file.ArrayEncodingCount; i++)
            {
                encodings.Add(file.GetArrayEncodingId(i));
            }

            List<long> keptChunkRows = [];
            for (int c = 0; c < keptChunks; c++)
            {
                long end = c + 1 < first.Count ? first[c + 1].Start : rows;
                keptChunkRows.Add(end - first[c].Start);
            }

            return new AppendPlan
            {
                Schema = schema,
                FileLength = file.FileLength,
                BlockRows = blockRows,
                Boundary = boundary,
                KeptRows = keptRows,
                KeptChunkRows = keptChunkRows,
                ArrayEncodings = encodings,
                Segments = [.. file.SegmentSpecs],
                Columns = columns,
                Reopened = reopened,
                Entries = directory?.Entries ?? [],
                Policy = directory?.Policy,
                BudgetPerMille = directory?.BudgetPerMille ?? IndexDirectory.DefaultBudgetPerMille,
                Statistics = statistics,
            };
        }

        /// <summary>Per column, the blocks a dictionary probe claimed.</summary>
        private static HashSet<int>[] DictBlocks(IndexDirectory? directory, DType schema)
        {
            HashSet<int>[] blocks = new HashSet<int>[schema.FieldCount];
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i] = [];
            }

            foreach (IndexEntry entry in directory?.Entries ?? [])
            {
                if (entry.Kind != IndexKinds.DictProbe || entry.ColumnPath.Count != 1 || entry.ColumnPath[0] >= (uint)blocks.Length)
                {
                    continue;
                }

                foreach (IndexRun run in entry.Runs)
                {
                    for (ulong b = run.FirstBlock; b < run.EndBlock && b < int.MaxValue; b++)
                    {
                        blocks[entry.ColumnPath[0]].Add((int)b);
                    }
                }
            }

            return blocks;
        }

        /// <summary>A column's flat chunks with their first rows, and its zone length (0 when unzoned).</summary>
        internal static (List<(LayoutNode Flat, long Start)> Chunks, long ZoneLength) ColumnChunks(LayoutTree tree, int field)
        {
            LayoutNode root = tree.Root;
            if (root.Encoding != LayoutEncodingId.Struct || field + (root.DType.IsNullable ? 1 : 0) >= root.ChildCount)
            {
                throw Refused("its root layout is not a struct of columns");
            }

            LayoutNode node = root.GetChild(field + (root.DType.IsNullable ? 1 : 0));
            long zoneLength = 0;
            if (node.Encoding == LayoutEncodingId.Zoned && node.ChildCount == 2)
            {
                if (node.TryGetZoneMap(out ZoneMap map))
                {
                    zoneLength = map.ZoneLength;
                }

                node = node.GetChild(0);
            }

            if (node.Encoding == LayoutEncodingId.Flat && node.Segments.Length == 1)
            {
                return ([(node, 0)], zoneLength);
            }

            if (node.Encoding != LayoutEncodingId.Chunked)
            {
                throw Refused($"column {field} is a {node.EncodingIdText} layout, not chunks of flat segments");
            }

            ReadOnlySpan<long> offsets = node.ChunkOffsets;
            List<(LayoutNode, long)> chunks = new List<(LayoutNode, long)>(node.ChildCount);
            for (int i = 0; i < node.ChildCount; i++)
            {
                LayoutNode chunk = node.GetChild(i);
                if (chunk.Encoding != LayoutEncodingId.Flat || chunk.Segments.Length != 1)
                {
                    throw Refused($"a chunk of column {field} is a {chunk.EncodingIdText} layout, not one flat segment");
                }

                chunks.Add((chunk, offsets[i]));
            }

            return (chunks, zoneLength);
        }

        internal static bool SameChunks(List<(LayoutNode Flat, long Start)> a, List<(LayoutNode Flat, long Start)> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Start != b[i].Start)
                {
                    return false;
                }
            }

            return true;
        }

        internal static VortexUnsupportedException Refused(string why) =>
            new VortexUnsupportedException(
                "append",
                "layout",
                $"This file cannot be appended to: {why}. Rewrite it instead (docs/11-write-strategy.md §3.8).");
    }

    /// <summary>What the old file says of one column.</summary>
    internal sealed class OldColumn
    {
        internal required BlockStats[] Blocks { get; init; }

        internal required List<int> KeptSegments { get; init; }

        internal required bool HasZones { get; init; }

        internal required long Rows { get; init; }

        internal long NonNull { get; init; }

        internal FilterLiteral? Min { get; init; }

        internal FilterLiteral? Max { get; init; }

        internal bool BoundsExact { get; init; }

        internal bool? Sorted { get; init; }

        internal bool? Strict { get; init; }

        internal static OldColumn From(
            FileStatistics? statistics, int field, long rows, bool hasZones, BlockStats[] blocks,
            List<(LayoutNode Flat, long Start)> chunks, int keptChunks)
        {
            List<int> segments = new List<int>(keptChunks);
            for (int c = 0; c < keptChunks; c++)
            {
                segments.Add(checked((int)chunks[c].Flat.Segments[0]));
            }

            long nulls = -1;
            FilterLiteral? min = null;
            FilterLiteral? max = null;
            bool exact = false;
            bool? sorted = null;
            bool? strict = null;
            if (statistics is not null && field < statistics.FieldCount)
            {
                FieldStatistics stats = statistics.GetField(field);
                if (stats.TryGetNullCount(out ulong n))
                {
                    nulls = (long)Math.Min(n, (ulong)rows);
                }

                if (stats.HasMin && stats.HasMax
                    && FileStatisticsPruner.TryLiteral(stats.Min, out FilterLiteral low)
                    && FileStatisticsPruner.TryLiteral(stats.Max, out FilterLiteral high)
                    && low.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned or FilterLiteralKind.Float)
                {
                    min = low;
                    max = high;
                    exact = stats.MinPrecision == StatPrecision.Exact && stats.MaxPrecision == StatPrecision.Exact;
                }

                if (stats.TryGetIsSorted(out bool s))
                {
                    sorted = s;
                }

                if (stats.TryGetIsStrictSorted(out bool t))
                {
                    strict = t;
                }
            }

            return new OldColumn
            {
                Blocks = blocks,
                KeptSegments = segments,
                HasZones = hasZones,
                Rows = rows,
                NonNull = nulls < 0 ? -1 : rows - nulls,
                Min = min,
                Max = max,
                BoundsExact = exact,
                Sorted = sorted,
                Strict = strict,
            };
        }
    }
}
