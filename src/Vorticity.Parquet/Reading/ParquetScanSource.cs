using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Vorticity.Scanning;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A scan of a Parquet file: its row groups read as batches of the columns the scan reads, with the
/// scan's filter, rows and projection applied to each as they arrive.
/// </summary>
/// <remarks>
/// Nothing is pruned yet: every row group the scan's rows reach is read. A count that asks only for
/// rows is the footer's, read with no request.
/// </remarks>
internal sealed class ParquetScanSource(ParquetFile file) : StreamScanSource
{
    internal ParquetFile File => file;

    internal override VortexSchema Schema => file.Schema;

    internal override VortexSession Session => file.Session;

    internal override long RowBound => file.RowCount;

    private protected override string Kind => "Parquet file";

    private protected override bool ReadsColumns => true;

    internal override ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return new ValueTask<long>(0);
        }

        // Without a filter or chosen rows, the count is the footer's.
        return spec.Filter is null && spec.Take is null
            ? new ValueTask<long>(spec.Rows is { } range ? Math.Max(0, Math.Min(range.End, file.RowCount) - Math.Max(range.Start, 0)) : file.RowCount)
            : base.CountAsync(spec, metrics, cancellationToken);
    }

    internal override ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        // Every row group the rows reach, every chunk of the columns read: a block is a batch.
        ParquetFooter footer = file.Footer;
        long rows = 0;
        int blocks = 0;
        int segments = 0;
        long bytes = 0;
        int[] leaves = Leaves(spec);
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            RowGroupEntry entry = footer.RowGroups[group];
            if (entry.RowCount == 0 || (spec.Rows is { } range && (entry.FirstRow >= range.End || entry.FirstRow + entry.RowCount <= range.Start)))
            {
                continue;
            }

            rows += entry.RowCount;
            blocks += (int)((entry.RowCount + ParquetBatches.BatchRows - 1) / ParquetBatches.BatchRows);
            foreach (int leaf in leaves)
            {
                bytes += file.ChunkRange(footer.Chunk(group, leaf)).Length;
                segments++;
            }
        }

        bool exact = spec.Filter is null && spec.Take is null;
        return new ValueTask<ScanPlan>(new ScanPlan(
            rows, blocks, blocks, segments, bytes, !spec.MatchesNothing, ImmutableArray<PruningStep>.Empty,
            new CountPlan(exact, exact ? rows : 0, 0, 0, exact ? 0 : blocks), null));
    }

    private protected override IAsyncEnumerator<RecordBatch> Stream(ScanSpec spec, int[]? columns, ScanCounters metrics, CancellationToken cancellationToken) =>
        new ParquetBatches(file, spec, columns, metrics, cancellationToken);

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
