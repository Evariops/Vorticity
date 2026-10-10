using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A scan of a Parquet file: its row groups read as batches of the columns the scan reads, with the
/// scan's filter, rows and projection applied to each as they arrive.
/// </summary>
/// <remarks>
/// A row group the footer's statistics prove the filter selects no row of is not read; one they
/// prove the count of is counted without being read. A count that asks only for rows is the
/// footer's, read with no request.
/// </remarks>
internal sealed class ParquetScanSource(ParquetFile file) : StreamScanSource
{
    internal ParquetFile File => file;

    internal override VortexSchema Schema => file.Schema;

    internal override VortexSession Session => file.Session;

    internal override long RowBound => file.RowCount;

    private protected override string Kind => "Parquet file";

    private protected override bool ReadsColumns => true;

    /// <summary>A scan in the order of a column the file lies in streams it as it lies: no sort.</summary>
    private protected override bool ArrivesInOrder(ScanSpec spec) => spec.OrderPath is { } path && Ordered(path, spec.Descending);

    /// <summary>A group by on a column the file lies in, ascending, closes its groups as the scan reaches past them.</summary>
    internal override bool OrdersOnAsking(FieldExpr column) => Ordered(column.Path, descending: false);

    /// <summary>
    /// Whether the file's rows lie in the order of the column at <paramref name="path"/>, ascending or
    /// <paramref name="descending"/>, ties in the file's order: every row group declares its rows sorted
    /// on it first, in that direction; its statistics hold no null and, for a float, no NaN; and each
    /// row group's bounds come at or after the last one's. A cut bound is a bound still: the last
    /// group's upper bound at or before the next one's lower bound orders them whatever the cut.
    /// </summary>
    internal bool Ordered(string path, bool descending)
    {
        ParquetSchema schema = file.Compiled;
        int leaf = -1;
        foreach (ParquetField field in schema.Fields)
        {
            if (string.Equals(field.Name, path, StringComparison.Ordinal))
            {
                leaf = field.Column;
                break;
            }
        }

        if (leaf < 0)
        {
            return false;
        }

        ParquetColumn column = schema.Columns[leaf];
        bool floats = column.Physical is PhysicalType.Float or PhysicalType.Double || column.Form == LeafForm.Float16;
        bool decimals = ColumnBounds.IsDecimal(column);
        ParquetFooter footer = file.Footer;
        FilterLiteral last = default;
        bool any = false;
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            if (footer.RowGroups[group].RowCount == 0)
            {
                continue;
            }

            // A declaration is a hint the rows are read without: one malformed orders nothing.
            SortingColumn[] sorting;
            try
            {
                sorting = footer.SortingColumns(group);
            }
            catch (ParquetFormatException)
            {
                return false;
            }

            if (sorting.Length == 0 || sorting[0].Column != leaf || sorting[0].Descending != descending)
            {
                return false;
            }

            ColumnChunkMetadata chunk = footer.Chunk(group, leaf);
            ZoneBounds bounds = ColumnBounds.Of(column, chunk.Statistics, chunk.Source.Span);
            if (!bounds.HasNullCount || bounds.NullCount != 0 || !bounds.HasMin || !bounds.HasMax || (floats && (!bounds.HasNanCount || bounds.NanCount != 0)))
            {
                return false;
            }

            FilterLiteral first = descending ? bounds.Max : bounds.Min;
            if (any && (!ColumnBounds.TryOrder(last, first, decimals, out int order) || (descending ? order < 0 : order > 0)))
            {
                return false;
            }

            last = descending ? bounds.Min : bounds.Max;
            any = true;
        }

        return any;
    }

    internal override async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return 0;
        }

        // Without a filter or chosen rows, the count is the footer's.
        if (spec.Filter is null && spec.Take is null)
        {
            return spec.Rows is { } range ? Math.Max(0, Math.Min(range.End, file.RowCount) - Math.Max(range.Start, 0)) : file.RowCount;
        }

        // The row groups the statistics count are not read; the others are, alone.
        RowGroupPlan plan = RowGroupPlan.For(file, spec);
        metrics.AddBlocksPruned(plan.PrunedBlocks);
        long count = 0;
        bool[] undecided = new bool[plan.Read.Length];
        bool any = false;
        for (int group = 0; group < undecided.Length; group++)
        {
            if (!plan.Read[group])
            {
                continue;
            }

            if (plan.Proven[group] >= 0)
            {
                count += plan.Proven[group];
                metrics.AddSplitProven();
            }
            else
            {
                undecided[group] = true;
                any = true;
            }
        }

        return any ? count + await CountAsync(spec, metrics, undecided, cancellationToken).ConfigureAwait(false) : count;
    }

    internal override async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (!spec.MatchesNothing && spec.Filter is not null)
        {
            // A row group the statistics prove holds a match answers without a read.
            RowGroupPlan plan = RowGroupPlan.For(file, spec);
            foreach (long proven in plan.Proven)
            {
                if (proven > 0)
                {
                    return true;
                }
            }
        }

        return await base.AnyAsync(spec, metrics, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// Over every row, the footer answers where every row group bounds the column exactly or holds
    /// nothing but nulls in it, with no read, unless the scan prunes nothing; otherwise, and for a
    /// float whose extreme is a zero, whose sign the data's first zero decides, the rows are read.
    /// </remarks>
    internal override async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec is { Filter: null, Rows: null, Take: null, Live: null, MatchesNothing: false, Options.UseStatistics: true }
            && column is not FunctionFieldExpr
            && Leaf(column.Path) is int leaf
            && TryExtremes(leaf, out FilterLiteral low, out FilterLiteral high))
        {
            return min ? low : high;
        }

        return await base.ExtremeAsync(spec, column, min, metrics, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// Cut at the row groups, as a Vortex file's rows are at its chunks: a group the statistics rule
    /// out is dead rows that join the range before them. A file read in place is cut inside a row group
    /// too, where every column the scan reads starts a page on one of the scan's batches, as every
    /// column of a file of this writer does: a lane then reaches its first row by the headers of the
    /// pages before it, and decodes none of another lane's. Another writer's pages, cut by their bytes,
    /// rarely start together, and a lane that started inside one would decode it again; its row groups
    /// are cut whole. A row group is cut inside only where the groups are fewer than two a lane, since a
    /// range cut inside one decodes its dictionaries again.
    /// </remarks>
    internal override async ValueTask<RowRange[]?> PiecesAsync(ScanSpec spec, int degree, CancellationToken cancellationToken)
    {
        // An ordered read is one stream: pieces read side by side would come in no order.
        if (degree <= 1 || spec.Take is not null || spec.MatchesNothing || spec.OrderPath is not null)
        {
            return null;
        }

        ParquetFooter footer = file.Footer;
        RowRange whole = new RowRange(0, file.RowCount);
        RowRange rows = spec.Rows is { } asked ? asked.Intersect(whole) : whole;
        if (rows.IsEmpty)
        {
            return null;
        }

        RowGroupPlan plan = RowGroupPlan.For(file, spec);
        long alive = 0;
        int live = 0;
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            long groupAlive = plan.Read[group] ? Rows(footer.RowGroups[group]).Intersect(rows).Length : 0;
            alive += groupAlive;
            live += groupAlive > 0 ? 1 : 0;
        }

        int batchRows = ParquetBatches.RowsOf(spec);
        if (alive < 2L * batchRows)
        {
            return null;
        }

        // Inside a row group only where the groups are too few to keep every lane busy: a range cut
        // inside one decodes its dictionaries again.
        long[]?[]? inner = file.Reader.ReadsInPlace && live < 2 * degree
            ? await PageCutsAsync(plan, rows, batchRows, Leaves(spec), cancellationToken).ConfigureAwait(false)
            : null;
        Aggregating.LaneCuts cuts = new Aggregating.LaneCuts(rows, alive, degree, batchRows);
        long previous = rows.Start;
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            RowRange groupRows = Rows(footer.RowGroups[group]);
            if (inner?[group] is { } starts)
            {
                foreach (long at in starts)
                {
                    if (at > previous && at < rows.End)
                    {
                        cuts.Boundary(at, at - previous);
                        previous = at;
                    }
                }
            }

            if (groupRows.End <= previous || groupRows.End >= rows.End)
            {
                continue;
            }

            cuts.Boundary(groupRows.End, plan.Read[group] ? groupRows.Intersect(new RowRange(previous, groupRows.End)).Length : 0);
            previous = groupRows.End;
        }

        return cuts.Finish();

        static RowRange Rows(RowGroupEntry group) => new RowRange(group.FirstRow, group.FirstRow + group.RowCount);
    }

    /// <summary>
    /// Per row group the plan reads of two batches or more, the rows inside it where every one of
    /// <paramref name="leaves"/> starts a page on a batch of <paramref name="batchRows"/>, from their
    /// offset indexes; null for a group a column of which has none, or no such row.
    /// </summary>
    private async ValueTask<long[]?[]> PageCutsAsync(RowGroupPlan plan, RowRange rows, int batchRows, int[] leaves, CancellationToken cancellationToken)
    {
        ParquetFooter footer = file.Footer;
        long[]?[] cuts = new long[]?[footer.RowGroups.Length];
        using SegmentRequestSet requests = new();
        int[] slots = new int[leaves.Length];
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            RowGroupEntry entry = footer.RowGroups[group];
            if (!plan.Read[group] || entry.RowCount < 2L * batchRows || new RowRange(entry.FirstRow, entry.FirstRow + entry.RowCount).Intersect(rows).IsEmpty)
            {
                continue;
            }

            requests.Release();
            bool indexed = true;
            for (int i = 0; i < leaves.Length && indexed; i++)
            {
                ColumnChunkMetadata chunk = footer.Chunk(group, leaves[i]);
                indexed = !chunk.IsEncrypted && file.Holds(chunk.OffsetIndexOffset, chunk.OffsetIndexLength);
                slots[i] = indexed ? requests.Add(new SegmentSpec((ulong)chunk.OffsetIndexOffset, (uint)chunk.OffsetIndexLength, 0, 0, 0)) : -1;
            }

            if (!indexed || leaves.Length == 0)
            {
                continue;
            }

            await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            HashSet<long>? common = null;
            for (int i = 0; i < leaves.Length; i++)
            {
                HashSet<long> starts = [];
                foreach (PageLocation page in OffsetIndex.Read(requests.GetBuffer(slots[i]).Span, entry.RowCount))
                {
                    if (page.FirstRow > 0 && page.FirstRow % batchRows == 0)
                    {
                        starts.Add(page.FirstRow);
                    }
                }

                if (common is null)
                {
                    common = starts;
                }
                else
                {
                    common.IntersectWith(starts);
                }
            }

            if (common is { Count: > 0 })
            {
                long[] sorted = [.. common];
                Array.Sort(sorted);
                for (int i = 0; i < sorted.Length; i++)
                {
                    sorted[i] += entry.FirstRow;
                }

                cuts[group] = sorted;
            }
        }

        requests.Release();
        return cuts;
    }

    internal override bool TryBounds(int[] path, out FilterLiteral min, out FilterLiteral max)
    {
        min = FilterLiteral.Null;
        max = FilterLiteral.Null;
        return path.Length == 1
            && (uint)path[0] < (uint)file.Compiled.Fields.Length
            && Leaf(file.Compiled.Fields[path[0]].Name) is int leaf
            && TryExtremes(leaf, out min, out max)
            && min.Kind != FilterLiteralKind.Null;
    }

    /// <summary>The leaf at <paramref name="path"/>, one value a row: none under a list.</summary>
    private int? Leaf(string path)
    {
        foreach (ParquetColumn column in file.Compiled.Columns)
        {
            if (column.MaxRepetitionLevel == 0 && column.Form != LeafForm.Null && string.Equals(column.DottedPath, path, StringComparison.Ordinal))
            {
                return column.Ordinal;
            }
        }

        return null;
    }

    /// <summary>
    /// The least and the greatest value of <paramref name="leaf"/> over the file, from the footer:
    /// false unless every row group bounds it exactly or holds only nulls in it; both null when every
    /// row is.
    /// </summary>
    private bool TryExtremes(int leaf, out FilterLiteral min, out FilterLiteral max)
    {
        min = FilterLiteral.Null;
        max = FilterLiteral.Null;
        ParquetFooter footer = file.Footer;
        ParquetColumn column = file.Compiled.Columns[leaf];
        bool decimals = ColumnBounds.IsDecimal(column);
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            long rows = footer.RowGroups[group].RowCount;
            ColumnChunkMetadata chunk = footer.Chunk(group, leaf);
            ZoneBounds bounds = ColumnBounds.Of(column, chunk.Statistics, chunk.Source.Span);
            if (rows == 0 || (bounds.HasNullCount && bounds.NullCount == rows))
            {
                continue;
            }

            if (!bounds.IsExact)
            {
                return false;
            }

            TerminalScan.Keep(ref min, bounds.Min, wantMin: true, decimals);
            TerminalScan.Keep(ref max, bounds.Max, wantMin: false, decimals);
        }

        // Which zero a float's data meets first is the data's to say.
        return !(min.Kind == FilterLiteralKind.Float && min.FloatValue == 0)
            && !(max.Kind == FilterLiteralKind.Float && max.FloatValue == 0);
    }

    internal override async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        // Every row group the plan reads, its page index, its Bloom filters and its dictionaries read
        // as the scan reads them, and every chunk of the columns read of a group they leave a batch
        // of: a block is a batch.
        ParquetFooter footer = file.Footer;
        RowGroupPlan plan = RowGroupPlan.For(file, spec);
        FilterColumns? filter = FilterColumns.For(file, spec);
        int batchRows = ParquetBatches.RowsOf(spec);
        ScanCounters blooms = new();
        ScanCounters indexes = new();
        bool bloom = filter is not null && spec.Options.UseIndexes && BloomPruning.Asks(spec.Filter!);
        using DictionaryPruning? dictionaries = DictionaryPruning.For(filter);
        ScanCounters dictionaryReads = new();
        using SegmentRequestSet requests = new();
        long rows = 0;
        int segments = 0;
        long bytes = 0;
        int proven = 0;
        long provenRows = 0;
        int decoded = 0;
        int bloomPruned = 0;
        int pagePruned = 0;
        int dictionaryPruned = 0;
        int undecidedPruned = 0;
        int[] leaves = Leaves(spec);
        PageLocation[]?[] locations = new PageLocation[]?[file.Compiled.Columns.Length];
        ScanCounters offsets = new();
        List<(long From, long To)> runs = [];
        List<ReadWindow> windows = [];
        PageLocation[]?[] maps = new PageLocation[]?[leaves.Length];
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            if (!plan.Read[group])
            {
                continue;
            }

            RowGroupEntry entry = footer.RowGroups[group];
            int blocks = (int)((entry.RowCount + batchRows - 1) / batchRows);
            rows += entry.RowCount;
            Array.Clear(locations);
            BlockMask? mask = filter is null ? null
                : await PagePruning.LiveAsync(filter, group, entry.RowCount, batchRows, requests, indexes, cancellationToken, locations).ConfigureAwait(false);
            int liveBlocks = mask?.LiveCount ?? blocks;
            pagePruned += blocks - liveBlocks;
            if (liveBlocks > 0 && bloom
                && await BloomPruning.RulesOutAsync(filter!, group, requests, blooms, cancellationToken).ConfigureAwait(false))
            {
                bloomPruned += liveBlocks;
                liveBlocks = 0;
            }

            if (liveBlocks > 0 && dictionaries is not null
                && await dictionaries.RulesOutAsync(group, dictionaryReads, cancellationToken).ConfigureAwait(false))
            {
                dictionaryPruned += liveBlocks;
                liveBlocks = 0;
            }

            if (plan.Proven[group] >= 0)
            {
                proven += blocks;
                provenRows += plan.Proven[group];
            }
            else
            {
                decoded += liveBlocks;
                undecidedPruned += blocks - liveBlocks;
            }

            if (liveBlocks == 0)
            {
                continue;
            }

            // The ranges the read asks for, as the scan plans them: from a source that does not read in
            // place, the pages its batches need, placed by offset indexes it reads for them.
            (long first, long end) = ChunkReads.Window(spec.Rows, entry.FirstRow, entry.RowCount, batchRows);
            bool sparse = ChunkReads.Sparse(file, first, end, entry.RowCount, mask);
            bool windowed = false;
            windows.Clear();
            if (sparse || ChunkReads.MayWindow(file, group, leaves))
            {
                await ChunkReads.OffsetsAsync(file, group, leaves, locations, requests, offsets, cancellationToken).ConfigureAwait(false);
                windowed = ChunkReads.Windows(file, group, leaves, locations, mask, first, end, batchRows, entry.RowCount, windows, maps);
            }

            foreach (ReadWindow window in windows)
            {
                foreach ((int _, long from, long to) in window.Runs)
                {
                    bytes += to - from;
                    segments++;
                }
            }

            foreach (int leaf in windowed ? [] : leaves)
            {
                runs.Clear();
                ChunkReads.Plan(file, footer.Chunk(group, leaf), locations[leaf], sparse, mask, first, end, batchRows, entry.RowCount, runs);
                foreach ((long from, long to) in runs)
                {
                    bytes += to - from;
                    segments++;
                }
            }
        }

        requests.Release();
        int indexSegments = (int)indexes.SegmentRequests;
        long indexBytes = indexes.BytesRequested;
        int bloomSegments = (int)blooms.SegmentRequests;
        long bloomBytes = blooms.BytesRequested;
        int dictionarySegments = (int)dictionaryReads.SegmentRequests;
        long dictionaryBytes = dictionaryReads.BytesRequested;
        int live = plan.Blocks - plan.PrunedBlocks - bloomPruned - pagePruned - dictionaryPruned;
        ImmutableArray<PruningStep> pruning = spec.Filter is not null && spec.Options.UseStatistics
            ?
            [
                new PruningStep("row group statistics", plan.PrunedBlocks, 0, 0),
                new PruningStep("page index", pagePruned, indexSegments, indexBytes),
                new PruningStep("bloom filter", bloomPruned, bloomSegments, bloomBytes),
                new PruningStep("dictionary", dictionaryPruned, dictionarySegments, dictionaryBytes),
            ]
            : ImmutableArray<PruningStep>.Empty;
        segments += bloomSegments + dictionarySegments + (int)offsets.SegmentRequests;
        bytes += bloomBytes + dictionaryBytes + offsets.BytesRequested;
        bool unfiltered = spec.Filter is null && spec.Take is null;
        CountPlan count = unfiltered
            ? new CountPlan(true, rows, 0, 0, 0)
            : new CountPlan(decoded == 0, decoded == 0 ? provenRows : 0, plan.PrunedBlocks + undecidedPruned, proven, decoded);
        // An ordered scan streams a file that lies in its order, a column its sorting columns and
        // statistics say is sorted, and sorts the rows of any other in memory, or by runs past it.
        OrderPlan? order = spec.OrderPath is { } path
            ? new OrderPlan((Ordered(path, spec.Descending) ? KeySourceKind.SortedColumn : KeySourceKind.InMemory).ToString(), 0, null, spec.Descending)
            : null;
        return new ScanPlan(
            rows, plan.Blocks, live, segments + indexSegments, bytes + indexBytes, !spec.MatchesNothing && (live > 0 || plan.Blocks == 0), pruning, count, order);
    }

    private protected override IAsyncEnumerator<RecordBatch> Stream(ScanSpec spec, int[]? columns, ScanCounters metrics, CancellationToken cancellationToken)
    {
        RowGroupPlan plan = RowGroupPlan.For(file, spec);
        metrics.AddBlocksPruned(plan.PrunedBlocks);
        return new ParquetBatches(file, spec, columns, plan.Read, metrics, cancellationToken);
    }

    /// <summary>The batches of the row groups <paramref name="part"/> marks: a count's, of those its statistics did not decide.</summary>
    private protected override IAsyncEnumerator<RecordBatch> Stream(ScanSpec spec, int[]? columns, ScanCounters metrics, object part, CancellationToken cancellationToken) =>
        new ParquetBatches(file, spec, columns, (bool[])part, metrics, cancellationToken);

    /// <summary>The leaves of the columns a spec reads.</summary>
    private int[] Leaves(ScanSpec spec)
    {
        Schema.ParquetSchema schema = file.Compiled;
        List<int> leaves = [];
        for (int field = 0; field < schema.Fields.Length; field++)
        {
            if (spec.Projection is { IsAll: false } mask && !Contains(mask, field))
            {
                continue;
            }

            Schema.ParquetField parquet = schema.Fields[field];
            for (int leaf = parquet.FirstLeaf; leaf < parquet.FirstLeaf + parquet.LeafCount; leaf++)
            {
                leaves.Add(leaf);
            }
        }

        return [.. leaves];

        static bool Contains(Layouts.FieldMask mask, int field)
        {
            for (int i = 0; i < mask.NamedFieldCount; i++)
            {
                if (mask.GetNamedField(i) == field)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
