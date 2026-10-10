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
            ZoneBounds bounds = ColumnBounds.Of(column, footer.Chunk(group, leaf).Statistics, footer.Bytes);
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
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            if (!plan.Read[group])
            {
                continue;
            }

            RowGroupEntry entry = footer.RowGroups[group];
            int blocks = (int)((entry.RowCount + batchRows - 1) / batchRows);
            rows += entry.RowCount;
            BlockMask? mask = filter is null ? null
                : await PagePruning.LiveAsync(filter, group, entry.RowCount, batchRows, requests, indexes, cancellationToken).ConfigureAwait(false);
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

            foreach (int leaf in leaves)
            {
                bytes += file.ChunkRange(footer.Chunk(group, leaf)).Length;
                segments++;
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
        segments += bloomSegments + dictionarySegments;
        bytes += bloomBytes + dictionaryBytes;
        bool unfiltered = spec.Filter is null && spec.Take is null;
        CountPlan count = unfiltered
            ? new CountPlan(true, rows, 0, 0, 0)
            : new CountPlan(decoded == 0, decoded == 0 ? provenRows : 0, plan.PrunedBlocks + undecidedPruned, proven, decoded);
        return new ScanPlan(
            rows, plan.Blocks, live, segments + indexSegments, bytes + indexBytes, !spec.MatchesNothing && (live > 0 || plan.Blocks == 0), pruning, count, null);
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
