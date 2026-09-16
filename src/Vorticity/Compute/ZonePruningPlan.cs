// Reading the zone maps a filter can use, once per scan.
//
// The zones child of a `vortex.zoned` layout is a struct with one column per aggregate, one row per
// zone. Phase 1 parsed its SHAPE and never read it, because nothing consumed it; this is what
// consumes it.
//
// It happens once, before the first batch, in a scan context of its own that is disposed
// immediately afterwards. That context is the reason ZoneBounds copies its values out: the batch
// contexts reset their arenas at every boundary, and a pruner that held arena indices would be
// reading freed memory by the second batch.
//
// A column contributes nothing rather than failing, at every step: no zoned layout on its path, a
// zone map whose aggregates this build does not know, an aggregate with no usable column, a zones
// child that will not decode -- all of them leave that column unable to prune, and the scan reads
// everything it would have read anyway. docs/08-semantics.md §4 requires exactly that of an
// unknown aggregate, and the same reflex is right for every other gap.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Builds the zone-map pruner for one scan.</summary>
internal static class ZonePruningPlan
{
    /// <summary>
    /// The live blocks of one scan: every pruning structure the file carries, run over one mask,
    /// cheapest first (docs/11-write-strategy.md §6.1).
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="tree">Its parsed layout tree.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <param name="steps">
    /// Receives what each structure pruned, in the order they ran, for <c>Explain</c>; null when
    /// nobody asks.
    /// </param>
    /// <param name="metrics">The scan's sink, to which the reads made here are added; null when nobody asks.</param>
    /// <returns>
    /// The mask, or <see langword="null"/> when no structure can prune anything -- which a scan
    /// reads as "every split is live", the same object graph it has without a filter.
    /// </returns>
    /// <param name="indexes">Whether the file's index directory takes part (<c>WithIndexes</c>).</param>
    /// <remarks>
    /// The zone map is first in line: it is already loaded and answers from min/max and null
    /// counts. The Bloom filters of docs/10-indexes.md §5.1 come next, over the blocks still live,
    /// and the list stops at an empty mask.
    /// </remarks>
    internal static async ValueTask<BlockMask?> RefineAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<Scan.PruningStep>? steps = null, Scan.ScanMetrics? metrics = null, bool indexes = true) =>
        (await PlanAsync(file, tree, filter, cancellationToken, steps, metrics, indexes).ConfigureAwait(false)).Live;

    /// <summary>The mask, and the structures that refined it -- which a count asks again, per block.</summary>
    /// <param name="Live">The mask of live blocks, or null when no structure can prune anything.</param>
    /// <param name="Zones">The zone-map pruner, or null when no column has a usable map.</param>
    internal readonly record struct PruningPlan(BlockMask? Live, ZonePruner? Zones);

    /// <summary>
    /// <see cref="RefineAsync"/>, keeping the structures beside the mask: a terminal
    /// (docs/12-index-reads.md §5.2) asks the zone maps for a block's count after the mask has
    /// said the block is live.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="tree">Its parsed layout tree.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <param name="steps">Receives what each structure pruned, for <c>Explain</c>; null when nobody asks.</param>
    /// <param name="metrics">The scan's sink, to which the reads made here are added; null when nobody asks.</param>
    /// <param name="indexes">Whether the file's index directory takes part.</param>
    internal static async ValueTask<PruningPlan> PlanAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<Scan.PruningStep>? steps = null, Scan.ScanMetrics? metrics = null, bool indexes = true)
    {
        (ZonePruner? zones, int segments, long bytes) =
            await BuildCountedAsync(file, tree, filter, metrics, cancellationToken).ConfigureAwait(false);
        // THE PRICE OF CONSULTING A STRUCTURE IS PART OF THE SCAN'S COST: the zone maps read here
        // are bytes the scan asked its source for, and they go to the same sink the batches feed
        // (docs/11 §6.4), so that the sink and the source agree to the request.
        metrics?.AddRequests(segments, bytes);

        long blockRows = Scan.SplitPlan.NaturalBatchRows(tree);
        Indexes.BloomPruner? blooms = indexes && blockRows > 0
            ? await Indexes.BloomPruner.BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false)
            : null;
        Indexes.KeyIndexPruner? locating = indexes && blockRows > 0
            ? await Indexes.KeyIndexPruner.BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false)
            : null;
        if (zones is null && blooms is null && locating is null)
        {
            return default;
        }

        BlockMask live = new BlockMask(tree.Root.RowCount, blockRows);
        int before = live.LiveCount;
        if (zones is not null)
        {
            zones.Refine(live);
            // What each structure pruned, and what consulting it cost, for `Explain` (docs/11
            // §6.4): the blocks that were live when it ran and are not afterwards -- so a later
            // structure is credited only with what the earlier ones left it -- against the
            // segments read for it.
            steps?.Add(new Scan.PruningStep("zone map", before - live.LiveCount, segments, bytes));
        }

        if (blooms is not null && !live.IsEmpty)
        {
            before = live.LiveCount;
            await blooms.RefineAsync(file, live, cancellationToken).ConfigureAwait(false);
            metrics?.AddRequests(blooms.Segments, blooms.Bytes);
            steps?.Add(new Scan.PruningStep("bloom filter", before - live.LiveCount, blooms.Segments, blooms.Bytes));
        }

        // The locating indexes last: a positive answer, and the dearest to consult.
        if (locating is not null && !live.IsEmpty)
        {
            before = live.LiveCount;
            await locating.RefineAsync(file, live, cancellationToken).ConfigureAwait(false);
            metrics?.AddRequests(locating.Segments, locating.Bytes);
            steps?.Add(new Scan.PruningStep("locating index", before - live.LiveCount, locating.Segments, locating.Bytes));
        }

        return new PruningPlan(live, zones);
    }

    /// <summary>
    /// Decodes the zone maps of every column <paramref name="filter"/> reads.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="tree">Its parsed layout tree.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="cancellationToken">Cancels the one read this makes.</param>
    /// <returns>A pruner, or <see langword="null"/> when no column can prune anything.</returns>
    internal static async ValueTask<ZonePruner?> BuildAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken)
    {
        (ZonePruner? pruner, _, _) =
            await BuildCountedAsync(file, tree, filter, metrics: null, cancellationToken).ConfigureAwait(false);
        return pruner;
    }

    /// <summary>
    /// <see cref="BuildAsync"/>, with the segments and bytes it read to build the pruner -- what
    /// consulting the zone maps cost this scan.
    /// </summary>
    private static async ValueTask<(ZonePruner? Pruner, int Segments, long Bytes)> BuildCountedAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, Scan.ScanMetrics? metrics,
        CancellationToken cancellationToken)
    {
        List<string> paths = [];
        filter.CollectFields(paths);
        if (paths.Count == 0)
        {
            return (null, 0, 0);
        }

        List<Candidate> candidates = [];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Count; i++)
        {
            if (!seen.Add(paths[i]))
            {
                continue;
            }

            if (TryLocate(tree, paths[i], out LayoutNode node, out ZoneMap map))
            {
                candidates.Add(new Candidate(Expr.Field(paths[i]), node, map));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, 0, 0);
        }

        // PERF-AUDIT-v2.md R30, and it is the whole cost of pruning. This context reads one zone
        // map per filtered column -- a struct of a few aggregates, one row per zone -- and is
        // disposed on the next line but one. Sized for a batch it allocated 18 192 bytes, which the
        // F-9 probe measured as 97,3 % of everything pruning costs over not pruning: the arenas
        // were dimensioned for millions of rows to hold sixty-four bounds.
        ZoneColumn[] columns = new ZoneColumn[candidates.Count];
        using ScanContext context = new ScanContext(file, ScanContext.MetadataCapacity);
        // The zone maps' own rows are values the flat reader materializes, and the sink counts
        // them like any other -- the same way `FlatLayoutReader.ValuesDecoded` always has.
        context.Metrics = metrics;

        // One registration pass over every zones child, then ONE coalesced read for all of them:
        // the same register-then-execute split a batch uses (docs/03-architecture.md §3.6), which
        // is what keeps a filter on five columns to one round trip rather than five.
        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate candidate = candidates[i];
            LayoutNode zones = candidate.Node.GetChild(1);
            FieldMask all = FieldMask.All;
            LayoutReaderTable.Require(in zones)
                .RegisterSegments(in zones, new RowRange(0, zones.RowCount), in all, context.Segments);
        }

        // Counted at the asking, like a batch's requests: the distinct zone-map segments and their
        // bytes, whatever the source then does about them.
        int segments = context.Segments.Count;
        long bytes = 0;
        for (int i = 0; i < segments; i++)
        {
            bytes += context.Segments.GetSpec(i).Length;
        }

        await file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate candidate = candidates[i];
            LayoutNode zones = candidate.Node.GetChild(1);
            FieldMask all = FieldMask.All;
            int root = LayoutReaderTable.Require(in zones)
                .Execute(in zones, new RowRange(0, zones.RowCount), in all, context);

            columns[i] = Extract(context, root, candidate, candidate.Node.RowCount);
        }

        ZonePruner pruner = new ZonePruner(filter, columns);
        return (pruner.IsUseful ? pruner : null, segments, bytes);
    }

    /// <summary>
    /// Walks <paramref name="path"/> from the root and returns the <c>vortex.zoned</c> node that
    /// covers the column it names, if there is one.
    /// </summary>
    /// <remarks>
    /// Only a zoned node whose own dtype IS the column's is accepted. A zoned layout above a struct
    /// carries aggregates over the struct, whose state columns are themselves structs; reading a
    /// scalar bound out of one would be reading a different statistic than the filter is asking
    /// about.
    /// </remarks>
    private static bool TryLocate(LayoutTree tree, string path, out LayoutNode node, out ZoneMap map)
    {
        node = tree.Root;
        map = default;

        string[] segments = path.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            // Descend through any zoned or chunked wrapper to the struct that has the field.
            if (!TryDescendToStruct(ref node))
            {
                return false;
            }

            DType dtype = node.DType;
            int field = dtype.IndexOfField(System.Text.Encoding.UTF8.GetBytes(segments[i]));
            if (field < 0)
            {
                return false;
            }

            int validityChildren = dtype.IsNullable ? 1 : 0;
            int child = field + validityChildren;
            if (child >= node.ChildCount)
            {
                return false;
            }

            node = node.GetChild(child);
        }

        return node.Encoding == LayoutEncodingId.Zoned &&
               node.ChildCount == 2 &&
               node.TryGetZoneMap(out map) &&
               map.IsPruningAvailable;
    }

    /// <summary>Walks past zoned wrappers until a struct layout is reached.</summary>
    private static bool TryDescendToStruct(ref LayoutNode node)
    {
        for (int guard = 0; guard < VortexLimits.MaxLayoutDepth; guard++)
        {
            if (node.Encoding == LayoutEncodingId.Struct)
            {
                return !node.DType.IsDefault && node.DType.Kind == DTypeKind.Struct;
            }

            if (node.Encoding == LayoutEncodingId.Zoned && node.ChildCount == 2)
            {
                node = node.GetChild(0);
                continue;
            }

            return false;
        }

        return false;
    }

    /// <summary>Copies the per-zone bounds out of the decoded zones struct.</summary>
    private static ZoneColumn Extract(
        ScanContext context, int root, Candidate candidate, long rowCount)
    {
        ZoneMap map = candidate.Map;
        CanonicalNode zones = context.Canonical.GetNode(root);
        if (zones.Kind != CanonicalKind.Struct)
        {
            return new ZoneColumn(candidate.Field, 0, rowCount, []);
        }

        int minColumn = -1;
        int maxColumn = -1;
        int nullColumn = -1;
        int nanColumn = -1;
        bool exact = true;

        for (int i = 0; i < map.AggregateCount; i++)
        {
            int column = map.GetColumnIndex(i);
            if (column < 0 || column >= zones.FieldCount)
            {
                continue;
            }

            switch (map.GetAggregate(i))
            {
                case AggregateId.Min:
                    minColumn = column;
                    break;
                case AggregateId.BoundedMin:
                    minColumn = column;
                    exact = false;
                    break;
                case AggregateId.Max:
                    maxColumn = column;
                    break;
                case AggregateId.BoundedMax:
                    maxColumn = column;
                    exact = false;
                    break;
                case AggregateId.NullCount:
                    nullColumn = column;
                    break;
                case AggregateId.NanCount:
                    // Only a float column that holds a NaN carries one (the reference writer
                    // emits it for nothing else), and a count-only proof needs it: the bounds
                    // exclude NaN rows, so `rows - null_count` overstates a comparison's matches
                    // by exactly this many (docs/12-index-reads.md §5.2).
                    nanColumn = column;
                    break;
                default:
                    break;
            }
        }

        if (minColumn < 0 && maxColumn < 0 && nullColumn < 0)
        {
            return new ZoneColumn(candidate.Field, 0, rowCount, []);
        }

        int count = zones.Length;
        ZoneBounds[] bounds = new ZoneBounds[count];
        for (int z = 0; z < count; z++)
        {
            FilterLiteral min = default;
            FilterLiteral max = default;
            bool hasMin = minColumn >= 0 &&
                TryReadBound(context.Canonical, zones.GetFieldIndex(minColumn), z, out min);
            bool hasMax = maxColumn >= 0 &&
                TryReadBound(context.Canonical, zones.GetFieldIndex(maxColumn), z, out max);

            long nulls = 0;
            bool hasNulls = false;
            if (nullColumn >= 0 &&
                LiteralReader.TryRead(
                    context.Canonical, zones.GetFieldIndex(nullColumn), z, out FilterLiteral nullCount))
            {
                hasNulls = TryCount(nullCount, out nulls);
            }

            long nans = 0;
            bool hasNans = false;
            if (nanColumn >= 0 &&
                LiteralReader.TryRead(
                    context.Canonical, zones.GetFieldIndex(nanColumn), z, out FilterLiteral nanCount))
            {
                hasNans = TryCount(nanCount, out nans);
            }

            bounds[z] = ZoneBounds.Create(
                min, hasMin,
                max, hasMax,
                exact,
                nulls,
                hasNulls,
                nans,
                hasNans);
        }

        return new ZoneColumn(candidate.Field, map.ZoneLength, rowCount, bounds);
    }

    /// <summary>
    /// Reads zone <paramref name="zone"/>'s bound out of an aggregate column, whichever of the
    /// two shapes the reference gives it.
    /// </summary>
    /// <remarks>
    /// <c>min</c>, <c>max</c> and <c>bounded_min</c> are plain scalars. <c>bounded_max</c> is
    /// <c>Struct({bound: element?, unknown: bool})</c> (ZoneMapSchema.BoundedMaxPartial): a
    /// maximum bounded to N bytes is the truncated prefix stepped up, which does not exist for an
    /// all-0xFF prefix, and <c>unknown</c> says so for the zone. A zone whose flag is set has no
    /// bound; one whose flag is clear has it in <c>bound</c>. Until this read the shape, a
    /// string column's bounded maximum was never seen, and every string predicate pruned and
    /// proved from its minimum alone.
    /// </remarks>
    private static bool TryReadBound(CanonicalArena arena, int nodeIndex, int zone, out FilterLiteral literal)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Struct)
        {
            return LiteralReader.TryRead(arena, nodeIndex, zone, out literal);
        }

        literal = default;
        int bound = node.DType.IndexOfField("bound"u8);
        int unknown = node.DType.IndexOfField("unknown"u8);
        if (bound < 0 || unknown < 0)
        {
            return false;
        }

        if (LiteralReader.TryRead(arena, node.GetFieldIndex(unknown), zone, out FilterLiteral flag) &&
            flag.Kind == FilterLiteralKind.Bool && flag.BoolValue)
        {
            return false;
        }

        return LiteralReader.TryRead(arena, node.GetFieldIndex(bound), zone, out literal);
    }

    private static bool TryCount(FilterLiteral literal, out long count)
    {
        switch (literal.Kind)
        {
            case FilterLiteralKind.Unsigned when literal.UnsignedValue <= long.MaxValue:
                count = (long)literal.UnsignedValue;
                return true;
            case FilterLiteralKind.Signed when literal.SignedValue >= 0:
                count = literal.SignedValue;
                return true;
            default:
                count = 0;
                return false;
        }
    }

    private readonly struct Candidate
    {
        internal Candidate(FieldExpr field, LayoutNode node, ZoneMap map)
        {
            Field = field;
            Node = node;
            Map = map;
        }

        internal FieldExpr Field { get; }

        internal LayoutNode Node { get; }

        internal ZoneMap Map { get; }
    }
}
