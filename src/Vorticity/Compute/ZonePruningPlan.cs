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

/// <summary>
/// Builds the zone-map pruner for one scan, from the zones child of each <c>vortex.zoned</c>
/// layout the filter touches: a struct with one column per aggregate and one row per zone. A zones
/// child is read the first time a scan of the file needs it, in a scan context of its own that is
/// disposed straight afterwards, which is why the bounds are copied out of its arena; the file then
/// keeps them, and the scans after it read nothing.
/// </summary>
/// <remarks>
/// Every step contributes nothing rather than failing: no zoned layout on a column's path, a zone
/// map whose aggregates this build does not know, an aggregate with no usable column, a zones
/// child that will not decode -- each of them leaves that column unable to prune, and the scan
/// then reads everything it would have read without a zone map.
/// </remarks>
internal static class ZonePruningPlan
{
    /// <summary>
    /// The live blocks of one scan: every pruning structure the file carries, run over one mask,
    /// cheapest first.
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
    /// counts. The Bloom filters come next, over the blocks still live, and the list stops at an
    /// empty mask.
    /// </remarks>
    internal static async ValueTask<BlockMask?> RefineAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<PruningStep>? steps = null, Scanning.ScanMetrics? metrics = null, bool indexes = true) =>
        (await PlanAsync(file, tree, filter, cancellationToken, steps, metrics, indexes).ConfigureAwait(false)).Live;

    /// <summary>The mask, and the structures that refined it -- which a count asks again, per block.</summary>
    /// <param name="Live">The mask of live blocks, or null when no structure can prune anything.</param>
    /// <param name="Zones">The zone-map pruner, or null when no column has a usable map.</param>
    /// <param name="Located">
    /// Whether sorted runs refined the mask as a locating index. The exact cover would consult the
    /// same runs again, through another reader, for rows the mask already confines to the blocks
    /// that hold them, so a scan does not ask it.
    /// </param>
    internal readonly record struct PruningPlan(BlockMask? Live, ZonePruner? Zones, bool Located = false);

    /// <summary>
    /// <see cref="RefineAsync"/>, keeping the structures beside the mask: a terminal operation
    /// asks the zone maps for a block's count after the mask has said the block is live.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="tree">Its parsed layout tree.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <param name="steps">Receives what each structure pruned, for <c>Explain</c>; null when nobody asks.</param>
    /// <param name="metrics">The scan's sink, to which the reads made here are added; null when nobody asks.</param>
    /// <param name="indexes">Whether the file's index directory takes part.</param>
    /// <param name="scope">
    /// The blocks the scan's rows reach, over blocks of the natural batch size, when they are not
    /// the whole file: a step counts only the blocks it pruned among them.
    /// </param>
    internal static async ValueTask<PruningPlan> PlanAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<PruningStep>? steps = null, Scanning.ScanMetrics? metrics = null, bool indexes = true,
        BlockMask? scope = null)
    {
        (ZonePruner? zones, int segments, long bytes) =
            await BuildCountedAsync(file, tree, filter, metrics, cancellationToken).ConfigureAwait(false);
        // The price of consulting a structure belongs to the scan's cost: the zone maps read here
        // are bytes the scan asked its source for, and they go to the same sink the batches feed,
        // so that the sink and the source agree about what was requested.
        Scanning.ScanMetrics.Note(metrics, segments, bytes);

        long blockRows = Scanning.SplitPlan.NaturalBatchRows(tree);
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
        int before = Counted(live, scope);
        if (zones is not null)
        {
            zones.Refine(live);
            // What each structure pruned, and what consulting it cost, for `Explain`: the blocks
            // that were live when it ran and are not afterwards -- so a later structure is
            // credited only with what the earlier ones left it -- against the segments read for
            // it.
            steps?.Add(new PruningStep("zone map", before - Counted(live, scope), segments, bytes));
        }

        if (blooms is not null && !live.IsEmpty)
        {
            before = Counted(live, scope);
            await blooms.RefineAsync(file, live, cancellationToken).ConfigureAwait(false);
            Scanning.ScanMetrics.Note(metrics, blooms.Segments, blooms.Bytes);
            Diagnostics.VortexEventSource.RunsRead(blooms.Segments);
            steps?.Add(new PruningStep("bloom filter", before - Counted(live, scope), blooms.Segments, blooms.Bytes));
        }

        // The locating indexes last: a positive answer, and the dearest to consult.
        bool located = false;
        if (locating is not null && !live.IsEmpty)
        {
            before = Counted(live, scope);
            await locating.RefineAsync(file, live, cancellationToken).ConfigureAwait(false);
            Scanning.ScanMetrics.Note(metrics, locating.Segments, locating.Bytes);
            Diagnostics.VortexEventSource.RunsRead(locating.Segments);
            steps?.Add(new PruningStep("locating index", before - Counted(live, scope), locating.Segments, locating.Bytes));
            located = locating.LocatesRows;
        }

        Diagnostics.VortexEventSource.Pruned(live.BlockCount - live.LiveCount, live.BlockCount);
        return new PruningPlan(live, zones, located);
    }

    /// <summary>The live blocks of <paramref name="live"/>, among those of <paramref name="scope"/> when there is one.</summary>
    private static int Counted(BlockMask live, BlockMask? scope) =>
        scope is null ? live.LiveCount : live.LiveCountWithin(scope);

    /// <summary>
    /// The zones of <paramref name="field"/>'s column as a pruner reads them, decoded once for the file,
    /// their read added to <paramref name="metrics"/>; null when the column has no zone map. What tells a
    /// group by whether its key lies scattered over its span or in the order of the rows.
    /// </summary>
    internal static async ValueTask<ZoneColumn?> ZonesAsync(
        VortexFile file, FieldExpr field, Scanning.ScanMetrics? metrics, CancellationToken cancellationToken)
    {
        if (!TryLocate(file.LayoutTree, field, out LayoutNode node, out ZoneMap map))
        {
            return null;
        }

        if (file.DecodedZones(node.Index) is { } decoded)
        {
            return decoded;
        }

        ZoneColumn[] columns = new ZoneColumn[1];
        (int segments, long bytes) = await DecodeAsync(file, [new Candidate(field, node, map)], columns, metrics, cancellationToken).ConfigureAwait(false);
        Scanning.ScanMetrics.Note(metrics, segments, bytes);
        return columns[0];
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
        VortexFile file, LayoutTree tree, VortexExpr filter, Scanning.ScanMetrics? metrics,
        CancellationToken cancellationToken)
    {
        // The filter's own field references, whose paths are already split and encoded; a filter
        // names a handful of columns, so a repeated one is found by looking back.
        List<FieldExpr> fields = [];
        Scanning.ScanBuilder.FieldsOf(filter, fields);
        if (fields.Count == 0)
        {
            return (null, 0, 0);
        }

        List<Candidate> candidates = [];
        for (int i = 0; i < fields.Count; i++)
        {
            if (!NamedBefore(fields, i) && TryLocate(tree, fields[i], out LayoutNode node, out ZoneMap map))
            {
                candidates.Add(new Candidate(fields[i], node, map));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, 0, 0);
        }

        // A zone map a scan of this file has decoded is the file's, and is not read again.
        ZoneColumn[] columns = new ZoneColumn[candidates.Count];
        int missing = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (file.DecodedZones(candidates[i].Node.Index) is { } decoded)
            {
                columns[i] = decoded;
            }
            else
            {
                missing++;
            }
        }

        int segments = 0;
        long bytes = 0;
        if (missing > 0)
        {
            (segments, bytes) = await DecodeAsync(file, candidates, columns, metrics, cancellationToken).ConfigureAwait(false);
        }

        ZonePruner pruner = new ZonePruner(filter, columns);
        return (pruner.IsUseful ? pruner : null, segments, bytes);
    }

    /// <summary>
    /// Reads and decodes the zone maps of the candidates <paramref name="columns"/> lacks, fills
    /// them in and keeps them on the file; returns the segments and bytes asked for.
    /// </summary>
    private static async ValueTask<(int Segments, long Bytes)> DecodeAsync(
        VortexFile file, List<Candidate> candidates, ZoneColumn[] columns, Scanning.ScanMetrics? metrics,
        CancellationToken cancellationToken)
    {
        // Sized for metadata rather than for a batch, and that choice is most of what pruning
        // costs: this context reads one zone map per filtered column -- a struct of a few
        // aggregates, one row per zone -- and is disposed as soon as the bounds are out, so arenas
        // dimensioned for millions of rows would allocate for a handful of bounds.
        using ScanContext context = new ScanContext(file, ScanContext.MetadataCapacity);
        // The zone maps' own rows are values the flat reader materializes, and the sink counts
        // them like any other value it decodes.
        context.Metrics = metrics;

        // One registration pass over every zones child, then a single coalesced read for all of
        // them: the same register-then-execute split a batch uses, which is what keeps a filter on
        // five columns to one round trip rather than five.
        for (int i = 0; i < candidates.Count; i++)
        {
            if (columns[i] is not null)
            {
                continue;
            }

            LayoutNode zones = candidates[i].Node.GetChild(1);
            FieldMask all = FieldMask.All;
            LayoutReaderTable.Require(in zones)
                .RegisterSegments(in zones, new RowRange(0, zones.RowCount), in all, context.Segments);
        }

        // Counted at the asking, like a batch's requests: the distinct zone-map segments and their
        // bytes, whatever the source then does about them.
        int segments = Scanning.ScanMetrics.Unread(context.Segments, file.Segments, out long bytes);

        await file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < candidates.Count; i++)
        {
            if (columns[i] is not null)
            {
                continue;
            }

            Candidate candidate = candidates[i];
            LayoutNode zones = candidate.Node.GetChild(1);
            FieldMask all = FieldMask.All;
            int root = LayoutReaderTable.Require(in zones)
                .Execute(in zones, new RowRange(0, zones.RowCount), in all, context);

            columns[i] = Extract(context, root, candidate, candidate.Node.RowCount);
            file.KeepZones(candidate.Node.Index, columns[i]);
        }

        return (segments, bytes);
    }

    /// <summary>
    /// Walks <paramref name="path"/> from the root and returns the <c>vortex.zoned</c> node that
    /// covers the column it names, if there is one.
    /// </summary>
    /// <remarks>
    /// Only a zoned node whose own dtype is the column's is accepted. A zoned layout above a struct
    /// carries aggregates over the struct, whose state columns are themselves structs; reading a
    /// scalar bound out of one would be reading a different statistic than the filter is asking
    /// about.
    /// </remarks>
    private static bool TryLocate(LayoutTree tree, FieldExpr path, out LayoutNode node, out ZoneMap map)
    {
        node = tree.Root;
        map = default;

        byte[][] segments = path.SegmentsUtf8;
        for (int i = 0; i < segments.Length; i++)
        {
            // Descend through any zoned or chunked wrapper to the struct that has the field.
            if (!TryDescendToStruct(ref node))
            {
                return false;
            }

            DType dtype = node.DType;
            int field = dtype.IndexOfField(segments[i]);
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

    /// <summary>Whether a field before <paramref name="index"/> names the same path.</summary>
    private static bool NamedBefore(List<FieldExpr> fields, int index)
    {
        for (int i = 0; i < index; i++)
        {
            if (string.Equals(fields[i].Path, fields[index].Path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
                    // Only a float column that holds a NaN carries one, and a proof that counts
                    // without decoding needs it: the bounds exclude NaN rows, so
                    // `rows - null_count` overstates a comparison's matches by exactly this many.
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

        // A decimal's bounds are read as the unscaled values a decimal filter carries, and the
        // column says so, because those bytes order as numbers and not as strings.
        bool isDecimal = IsDecimal(candidate.Node.DType);
        int count = zones.Length;

        // A numeric column's zones go straight into columns of their own; a bound of another kind
        // than the column's, which no writer makes, sends them back to one summary per zone.
        ZoneTable? table = isDecimal ? null : NumericKind(candidate.Node.DType) is { } kind ? new ZoneTable(kind, count) : null;
        ZoneBounds[]? bounds = table is null ? new ZoneBounds[count] : null;
        for (int z = 0; z < count; z++)
        {
            FilterLiteral min = default;
            FilterLiteral max = default;
            bool hasMin = minColumn >= 0 &&
                TryReadBound(context.Canonical, zones.GetFieldIndex(minColumn), z, isDecimal, out min);
            bool hasMax = maxColumn >= 0 &&
                TryReadBound(context.Canonical, zones.GetFieldIndex(maxColumn), z, isDecimal, out max);

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

            ZoneBounds zone = ZoneBounds.Create(
                min, hasMin,
                max, hasMax,
                exact,
                nulls,
                hasNulls,
                nans,
                hasNans);
            if (table is not null && !table.TrySet(z, zone, ZoneColumn.RowsInZone(z, map.ZoneLength, rowCount)))
            {
                bounds = new ZoneBounds[count];
                for (int earlier = 0; earlier < z; earlier++)
                {
                    bounds[earlier] = table.Bounds(earlier);
                }

                table = null;
            }

            if (bounds is not null)
            {
                bounds[z] = zone;
            }
        }

        return table is not null
            ? new ZoneColumn(candidate.Field, map.ZoneLength, rowCount, table)
            : new ZoneColumn(candidate.Field, map.ZoneLength, rowCount, bounds!, isDecimal);
    }

    /// <summary>Whether a column's dtype is a decimal, through any extension over one.</summary>
    private static bool IsDecimal(DType dtype)
    {
        dtype = Storage(dtype);
        return !dtype.IsDefault && dtype.Kind == DTypeKind.Decimal;
    }

    /// <summary>
    /// The kind a numeric column's bounds are read as -- signed, unsigned or float -- through any
    /// extension over one, or null for any other column.
    /// </summary>
    private static FilterLiteralKind? NumericKind(DType dtype)
    {
        dtype = Storage(dtype);
        if (dtype.IsDefault || dtype.Kind != DTypeKind.Primitive)
        {
            return null;
        }

        PType ptype = dtype.PType;
        return ptype.IsSignedInteger() ? FilterLiteralKind.Signed
            : ptype.IsUnsignedInteger() ? FilterLiteralKind.Unsigned
            : ptype.IsFloat() ? FilterLiteralKind.Float
            : null;
    }

    /// <summary>The dtype an extension stores its values as, through any number of them.</summary>
    private static DType Storage(DType dtype)
    {
        for (int i = 0; i < VortexLimits.MaxDTypeDepth && !dtype.IsDefault && dtype.Kind == DTypeKind.Extension; i++)
        {
            dtype = dtype.StorageType;
        }

        return dtype;
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
    /// bound; one whose flag is clear has it in <c>bound</c>. A reader that does not know this
    /// shape sees no maximum at all on a string column and prunes from the minimum alone.
    /// </remarks>
    private static bool TryReadBound(
        CanonicalArena arena, int nodeIndex, int zone, bool isDecimal, out FilterLiteral literal)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Struct)
        {
            return Read(arena, nodeIndex, zone, isDecimal, out literal);
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

        return Read(arena, node.GetFieldIndex(bound), zone, isDecimal, out literal);
    }

    private static bool Read(CanonicalArena arena, int nodeIndex, int zone, bool isDecimal, out FilterLiteral literal) =>
        isDecimal
            ? LiteralReader.TryReadDecimal(arena, nodeIndex, zone, out literal)
            : LiteralReader.TryRead(arena, nodeIndex, zone, out literal);

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
