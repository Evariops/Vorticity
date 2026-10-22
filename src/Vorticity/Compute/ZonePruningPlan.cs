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
    /// empty mask. A column whose every question hashed postings answer over the whole file has no
    /// zone map decoded: the postings say where its values are.
    /// </remarks>
    internal static async ValueTask<BlockMask?> RefineAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<PruningStep>? steps = null, Scanning.ScanCounters? metrics = null, bool indexes = true) =>
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
    /// the whole file: a step counts only the blocks it pruned among them, and the mask leaves the
    /// others dead, which no structure asks about.
    /// </param>
    internal static async ValueTask<PruningPlan> PlanAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<PruningStep>? steps = null, Scanning.ScanCounters? metrics = null, bool indexes = true,
        BlockMask? scope = null)
    {
        long blockRows = Scanning.SplitPlan.NaturalBatchRows(tree);

        // The locating indexes are asked first what they answer: a column whose every question hashed
        // postings answer over the whole file, a declared identifier's, has no zone map decoded, the
        // bounds of every block, which values spread over the file leave wide -- read to prune none.
        Indexes.KeyIndexPruner? locating = indexes && blockRows > 0
            ? await Indexes.KeyIndexPruner.BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false)
            : null;
        (ZonePruner? zones, int segments, long bytes) =
            await BuildCountedAsync(file, tree, filter, metrics, locating, cancellationToken).ConfigureAwait(false);
        // The price of consulting a structure belongs to the scan's cost: the zone maps read here
        // are bytes the scan asked its source for, and they go to the same sink the batches feed,
        // so that the sink and the source agree about what was requested.
        Scanning.ScanCounters.Note(metrics, segments, bytes);

        // The path directories first among the indexes: one small read refutes the whole file, or the
        // slices that hold no path the filter asks for.
        List<RowRange>? pathRanges = null;
        int pathSegments = 0;
        long pathBytes = 0;
        if (indexes && blockRows > 0 && VariantColumns(filter) is { } variants)
        {
            (bool refuted, pathRanges, pathSegments, pathBytes) = await RefutedByPathsAsync(file, filter, variants, cancellationToken).ConfigureAwait(false);
            Scanning.ScanCounters.Note(metrics, pathSegments, pathBytes);
            if (refuted)
            {
                BlockMask none = new BlockMask(tree.Root.RowCount, blockRows);
                int all = Counted(none, scope);
                none.KillAll();
                steps?.Add(new PruningStep("path directory", all, pathSegments, pathBytes));
                Diagnostics.VortexEventSource.Pruned(none.BlockCount, none.BlockCount);
                return new PruningPlan(none, zones);
            }
        }

        Indexes.BloomPruner? blooms = indexes && blockRows > 0
            ? await Indexes.BloomPruner.BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false)
            : null;
        Dictionary<string, List<(string Key, Types.Variants.PathClass Class)>>? keysRead = indexes && blockRows > 0 ? KeysRead(filter) : null;
        if (zones is null && blooms is null && locating is null && pathRanges is null && keysRead is null)
        {
            if (pathSegments > 0)
            {
                steps?.Add(new PruningStep("path directory", 0, pathSegments, pathBytes));
            }

            return default;
        }

        // The blocks the scan's rows do not reach are dead before any structure runs, which asks only
        // live blocks: a lane's piece of a day of 4.32 G rows asked a filter on a text column of every
        // block of the file, 527 k comparisons a lane where its rows reach a few hundred blocks.
        BlockMask live = new BlockMask(tree.Root.RowCount, blockRows);
        if (scope is not null && scope.BlockRows == blockRows && scope.BlockCount == live.BlockCount)
        {
            live.Keep(scope.Words);
        }

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

        if (pathSegments > 0)
        {
            before = Counted(live, scope);
            foreach (RowRange range in pathRanges ?? [])
            {
                KillWithin(live, range);
            }

            steps?.Add(new PruningStep("path directory", before - Counted(live, scope), pathSegments, pathBytes));
        }

        // The block statistics of the shredded keys the filter reads in one class, as a zone map's.
        if (keysRead is not null && !live.IsEmpty)
        {
            before = Counted(live, scope);
            (int read, long readBytes) = await RefineByKeysAsync(file, filter, keysRead, live, cancellationToken).ConfigureAwait(false);
            Scanning.ScanCounters.Note(metrics, read, readBytes);
            if (read > 0)
            {
                steps?.Add(new PruningStep("path statistics", before - Counted(live, scope), read, readBytes));
            }
        }

        if (blooms is not null && !live.IsEmpty)
        {
            before = Counted(live, scope);
            await blooms.RefineAsync(file, live, cancellationToken).ConfigureAwait(false);
            Scanning.ScanCounters.Note(metrics, blooms.Segments, blooms.Bytes);
            Diagnostics.VortexEventSource.RunsRead(blooms.Segments);
            steps?.Add(new PruningStep("bloom filter", before - Counted(live, scope), blooms.Segments, blooms.Bytes));
        }

        // The locating indexes last: a positive answer, and the dearest to consult.
        bool located = false;
        if (locating is not null && !live.IsEmpty)
        {
            before = Counted(live, scope);
            await locating.RefineAsync(file, live, scope, cancellationToken).ConfigureAwait(false);
            Scanning.ScanCounters.Note(metrics, locating.Segments, locating.Bytes);
            Diagnostics.VortexEventSource.RunsRead(locating.Segments);
            steps?.Add(new PruningStep("locating index", before - Counted(live, scope), locating.Segments, locating.Bytes));
            located = locating.LocatesRows;
        }

        Diagnostics.VortexEventSource.Pruned(live.BlockCount - live.LiveCount, live.BlockCount);
        return new PruningPlan(live, zones, located);
    }

    /// <summary>
    /// Whether the path directories of <paramref name="file"/> leave a row that may match
    /// <paramref name="filter"/>: true unless the filter's fenced predicates on paths ask what no
    /// directory holds. A file without directories, or a filter on no path, may match.
    /// </summary>
    internal static async ValueTask<bool> PathsMayMatchAsync(VortexFile file, VortexExpr filter, CancellationToken cancellationToken) =>
        VariantColumns(filter) is not { } columns
        || !(await RefutedByPathsAsync(file, filter, columns, cancellationToken).ConfigureAwait(false)).Refuted;

    /// <summary>The variant columns the fenced predicates of <paramref name="filter"/> read paths of, by name; null for none.</summary>
    private static List<string>? VariantColumns(VortexExpr filter)
    {
        List<string>? columns = null;
        Collect(filter, ref columns);
        return columns;

        static void Collect(VortexExpr expr, ref List<string>? columns)
        {
            switch (expr)
            {
                case LogicalExpr logical:
                    Collect(logical.Left, ref columns);
                    Collect(logical.Right, ref columns);
                    return;
                case VariantFilterExpr { Inner: ComparisonExpr { Field: VariantFieldExpr path } }:
                    Add(path, ref columns);
                    return;
                case VariantFilterExpr { Inner: InExpr { Field: VariantFieldExpr path } }:
                    Add(path, ref columns);
                    return;
                case VariantFilterExpr { Inner: NullCheckExpr { Field: VariantFieldExpr path } }:
                    Add(path, ref columns);
                    return;
                case VariantFilterExpr { Inner: StringMatchExpr { Field: VariantFieldExpr path } }:
                    Add(path, ref columns);
                    return;
            }
        }

        static void Add(VariantFieldExpr path, ref List<string>? columns)
        {
            columns ??= [];
            if (!columns.Contains(path.Column.Path))
            {
                columns.Add(path.Column.Path);
            }
        }
    }

    /// <summary>
    /// The paths of <paramref name="column"/> the fenced predicates of <paramref name="filter"/> read, the predicates
    /// <see cref="Indexes.VariantDirectory.MayMatch(VortexExpr, IReadOnlyDictionary{string, Indexes.VariantDirectory}, int)"/> asks a directory about, each its keys in UTF-8; a path through an
    /// element of an array, which no directory answers for, is none of them.
    /// </summary>
    private static List<byte[][]> PathsOf(VortexExpr filter, string column)
    {
        List<byte[][]> paths = [];
        Collect(filter, column, paths);
        return paths;

        static void Collect(VortexExpr expr, string column, List<byte[][]> paths)
        {
            switch (expr)
            {
                case LogicalExpr logical:
                    Collect(logical.Left, column, paths);
                    Collect(logical.Right, column, paths);
                    return;
                case VariantFilterExpr { Inner: ComparisonExpr { Field: VariantFieldExpr path } }:
                    Add(path, column, paths);
                    return;
                case VariantFilterExpr { Inner: InExpr { Field: VariantFieldExpr path } }:
                    Add(path, column, paths);
                    return;
                case VariantFilterExpr { Inner: NullCheckExpr { Field: VariantFieldExpr path } }:
                    Add(path, column, paths);
                    return;
                case VariantFilterExpr { Inner: StringMatchExpr { Field: VariantFieldExpr path } }:
                    Add(path, column, paths);
                    return;
            }
        }

        static void Add(VariantFieldExpr path, string column, List<byte[][]> paths)
        {
            if (!string.Equals(path.Column.Path, column, StringComparison.Ordinal))
            {
                return;
            }

            byte[][] keys = new byte[path.Steps.Length][];
            for (int i = 0; i < keys.Length; i++)
            {
                if (path.Steps[i].KeyUtf8 is not { } key)
                {
                    return;
                }

                keys[i] = key;
            }

            paths.Add(keys);
        }
    }

    /// <summary>
    /// Whether the path directories of <paramref name="columns"/> prove that no row of
    /// <paramref name="file"/> matches <paramref name="filter"/>; when they do not, the rows of the runs
    /// of slices they prove hold none, or null; and the segments and bytes read for them.
    /// </summary>
    private static async ValueTask<(bool Refuted, List<RowRange>? Ranges, int Segments, long Bytes)> RefutedByPathsAsync(
        VortexFile file, VortexExpr filter, List<string> columns, CancellationToken cancellationToken)
    {
        Dictionary<string, Indexes.VariantDirectory>? directories = null;
        int segments = 0;
        long bytes = 0;
        foreach (string column in columns)
        {
            int field = file.DType.Kind == DTypeKind.Struct ? file.DType.IndexOfField(column) : -1;
            if (field < 0)
            {
                continue;
            }

            // The directory holds the entries of the filter's paths alone, all its predicates ask of it.
            (Indexes.VariantDirectory? directory, int read, long readBytes) =
                await Indexes.VariantDirectory.ReadAsync(file, field, PathsOf(filter, column), cancellationToken).ConfigureAwait(false);
            segments += read;
            bytes += readBytes;
            if (directory is not null)
            {
                (directories ??= new Dictionary<string, Indexes.VariantDirectory>(StringComparer.Ordinal))[column] = directory;
            }
        }

        if (directories is null)
        {
            return (false, null, segments, bytes);
        }

        if (!Indexes.VariantDirectory.MayMatch(filter, directories))
        {
            return (true, null, segments, bytes);
        }

        // Slice by slice, when every directory names the file's slices.
        int slices = -1;
        foreach (Indexes.VariantDirectory directory in directories.Values)
        {
            slices = slices < 0 || slices == directory.SliceCount ? directory.SliceCount : 0;
        }

        if (slices <= 0 || await Indexes.SliceTable.ReadAsync(file, cancellationToken).ConfigureAwait(false) is not { } table
            || table.Slices.Count != slices)
        {
            return (false, null, segments, bytes);
        }

        List<RowRange>? ranges = null;
        long start = -1;
        long end = -1;
        for (int slice = 0; slice < slices; slice++)
        {
            Indexes.TimeSlice cut = table.Slices[slice];
            if (Indexes.VariantDirectory.MayMatch(filter, directories, slice))
            {
                continue;
            }

            // Neighbouring slices refuted make one run, so that a block astride them dies too.
            if (start >= 0 && end == cut.FirstRow)
            {
                end = cut.FirstRow + cut.Rows;
                ranges![^1] = new RowRange(start, end);
                continue;
            }

            (start, end) = (cut.FirstRow, cut.FirstRow + cut.Rows);
            (ranges ??= []).Add(new RowRange(start, end));
        }

        return (false, ranges, segments, bytes);
    }

    /// <summary>
    /// The keys the fenced predicates of <paramref name="filter"/> read one step deep and in one class
    /// whose typed column holds every value of it, an integer, a text or a boolean, by column; null for none.
    /// </summary>
    private static Dictionary<string, List<(string Key, Types.Variants.PathClass Class)>>? KeysRead(VortexExpr filter)
    {
        Dictionary<string, List<(string, Types.Variants.PathClass)>>? keys = null;
        Collect(filter, ref keys);
        return keys;

        static void Collect(VortexExpr expr, ref Dictionary<string, List<(string, Types.Variants.PathClass)>>? keys)
        {
            switch (expr)
            {
                case LogicalExpr logical:
                    Collect(logical.Left, ref keys);
                    Collect(logical.Right, ref keys);
                    return;
                case VariantFilterExpr fence when fence.Inner switch
                {
                    ComparisonExpr { Field: VariantFieldExpr compared } => compared,
                    InExpr { Field: VariantFieldExpr member } => member,
                    NullCheckExpr { Field: VariantFieldExpr held, IsNull: false } => held,
                    _ => null,
                } is { } path:
                    if (KeyOf(path) is { } key)
                    {
                        keys ??= new Dictionary<string, List<(string, Types.Variants.PathClass)>>(StringComparer.Ordinal);
                        if (!keys.TryGetValue(path.Column.Path, out List<(string, Types.Variants.PathClass)>? list))
                        {
                            keys[path.Column.Path] = list = [];
                        }

                        if (!list.Contains(key))
                        {
                            list.Add(key);
                        }
                    }

                    return;
            }
        }
    }

    /// <summary>The one key and the class <paramref name="path"/> reads, when block statistics can answer it; null otherwise.</summary>
    private static (string Key, Types.Variants.PathClass Class)? KeyOf(VariantFieldExpr path) =>
        path.Steps is [{ IsKey: true } step] && path.Read switch
        {
            VariantRead.AsInt64 => Types.Variants.PathClass.Integer,
            VariantRead.AsString => Types.Variants.PathClass.String,
            VariantRead.AsBool => Types.Variants.PathClass.Boolean,
            _ => (Types.Variants.PathClass?)null,
        } is { } @class
            ? (step.Key!, @class)
            : null;

    /// <summary>
    /// Kills the live blocks of <paramref name="live"/> that the block statistics of the keys in
    /// <paramref name="keys"/> prove hold no row matching <paramref name="filter"/>, and returns the
    /// segments and bytes read for them.
    /// </summary>
    private static async ValueTask<(int Segments, long Bytes)> RefineByKeysAsync(
        VortexFile file, VortexExpr filter, Dictionary<string, List<(string Key, Types.Variants.PathClass Class)>> keys, BlockMask live,
        CancellationToken cancellationToken)
    {
        Dictionary<(string Column, string Key, Types.Variants.PathClass Class), (int Block, Indexes.PathEntry Entry)[]>? stats = null;
        int segments = 0;
        long bytes = 0;
        foreach ((string column, List<(string Key, Types.Variants.PathClass Class)> wanted) in keys)
        {
            int field = file.DType.Kind == DTypeKind.Struct ? file.DType.IndexOfField(column) : -1;
            if (field < 0)
            {
                continue;
            }

            (Indexes.VariantBlockStats? read, int count, long readBytes) =
                await Indexes.VariantBlockStats.ReadAsync(file, field, wanted, cancellationToken).ConfigureAwait(false);
            segments += count;
            bytes += readBytes;
            if (read is null)
            {
                continue;
            }

            foreach ((string key, Types.Variants.PathClass @class) in wanted)
            {
                if (read.IndexOf(key, @class) is int at and >= 0)
                {
                    (stats ??= [])[(column, key, @class)] = read.Blocks[at];
                }
            }
        }

        if (stats is null)
        {
            return (segments, bytes);
        }

        for (int block = 0; block < live.BlockCount; block++)
        {
            if (live.IsLive(block) && !BlockMayMatch(filter, stats, block))
            {
                live.Kill(block);
            }
        }

        return (segments, bytes);
    }

    /// <summary>
    /// Whether block <paramref name="block"/> may hold a row matching <paramref name="filter"/>, by the
    /// block statistics in <paramref name="stats"/>: a key they cover with no entry for the block holds
    /// no value of its class there.
    /// </summary>
    private static bool BlockMayMatch(
        VortexExpr filter, Dictionary<(string Column, string Key, Types.Variants.PathClass Class), (int Block, Indexes.PathEntry Entry)[]> stats, int block)
    {
        switch (filter)
        {
            case LogicalExpr { IsAnd: true } both:
                return BlockMayMatch(both.Left, stats, block) && BlockMayMatch(both.Right, stats, block);
            case LogicalExpr either:
                return BlockMayMatch(either.Left, stats, block) || BlockMayMatch(either.Right, stats, block);
            case VariantFilterExpr { Inner: ComparisonExpr { Field: VariantFieldExpr path } comparison }:
                return Entry(path, stats, block, out Indexes.PathEntry? entry) is not true
                    || (entry is not null && Indexes.VariantDirectory.Within(entry, comparison.Op, comparison.Value));
            case VariantFilterExpr { Inner: InExpr { Field: VariantFieldExpr path } membership }:
            {
                if (Entry(path, stats, block, out Indexes.PathEntry? member) is not true)
                {
                    return true;
                }

                foreach (FilterLiteral literal in membership.Literals)
                {
                    if (member is not null && Indexes.VariantDirectory.Within(member, ComparisonOp.Equal, literal))
                    {
                        return true;
                    }
                }

                return false;
            }

            case VariantFilterExpr { Inner: NullCheckExpr { Field: VariantFieldExpr path, IsNull: false } }:
                return Entry(path, stats, block, out Indexes.PathEntry? held) is not true || held is not null;
            default:
                return true;
        }
    }

    /// <summary>Whether the statistics cover <paramref name="path"/>, and its entry for <paramref name="block"/>, null when the block holds none of its values.</summary>
    private static bool Entry(
        VariantFieldExpr path, Dictionary<(string Column, string Key, Types.Variants.PathClass Class), (int Block, Indexes.PathEntry Entry)[]> stats,
        int block, out Indexes.PathEntry? entry)
    {
        entry = null;
        if (KeyOf(path) is not { } key || !stats.TryGetValue((path.Column.Path, key.Key, key.Class), out (int Block, Indexes.PathEntry Entry)[]? blocks))
        {
            return false;
        }

        int low = 0;
        int high = blocks.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int at = blocks[middle].Block;
            if (at == block)
            {
                entry = blocks[middle].Entry;
                break;
            }

            if (at < block)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return true;
    }

    /// <summary>Kills the blocks of <paramref name="live"/> that lie wholly inside <paramref name="rows"/>.</summary>
    private static void KillWithin(BlockMask live, RowRange rows)
    {
        long blockRows = live.BlockRows;
        for (long block = (rows.Start + blockRows - 1) / blockRows; block < live.BlockCount; block++)
        {
            if (Math.Min((block + 1) * blockRows, live.RowCount) > rows.End)
            {
                return;
            }

            live.Kill((int)block);
        }
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
        VortexFile file, FieldExpr field, Scanning.ScanCounters? metrics, CancellationToken cancellationToken)
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
        (int segments, long bytes) = await SettleAsync(file, [new Candidate(field, node, map)], columns, metrics, cancellationToken).ConfigureAwait(false);
        Scanning.ScanCounters.Note(metrics, segments, bytes);
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
            await BuildCountedAsync(file, tree, filter, metrics: null, locating: null, cancellationToken).ConfigureAwait(false);
        return pruner;
    }

    /// <summary>
    /// <see cref="BuildAsync"/>, with the segments and bytes it read to build the pruner -- what
    /// consulting the zone maps cost this scan -- and without the columns <paramref name="locating"/>
    /// answers alone.
    /// </summary>
    private static async ValueTask<(ZonePruner? Pruner, int Segments, long Bytes)> BuildCountedAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, Scanning.ScanCounters? metrics,
        Indexes.KeyIndexPruner? locating, CancellationToken cancellationToken)
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
            if (!NamedBefore(fields, i) && locating?.AnswersAlone(fields[i].Path) != true
                && TryLocate(tree, fields[i], out LayoutNode node, out ZoneMap map))
            {
                candidates.Add(new Candidate(fields[i], node, map));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, 0, 0);
        }

        // A zone map a scan of this file has decoded, or decodes now, is the file's, and is not read again.
        ZoneColumn[] columns = new ZoneColumn[candidates.Count];
        (int segments, long bytes) = await SettleAsync(file, candidates, columns, metrics, cancellationToken).ConfigureAwait(false);
        ZonePruner pruner = new ZonePruner(filter, columns);
        return (pruner.IsUseful ? pruner : null, segments, bytes);
    }

    /// <summary>
    /// Fills in the zone maps of the candidates <paramref name="columns"/> lacks: a map the file keeps
    /// is taken, one another scan decodes now awaited, and the others decoded here, in one read, and
    /// kept on the file. Returns the segments and bytes this scan asked for.
    /// </summary>
    private static async ValueTask<(int Segments, long Bytes)> SettleAsync(
        VortexFile file, List<Candidate> candidates, ZoneColumn[] columns, Scanning.ScanCounters? metrics,
        CancellationToken cancellationToken)
    {
        int segments = 0;
        long bytes = 0;
        while (true)
        {
            Claims claims = default;
            Task<ZoneColumn?>? other = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (columns[i] is not null)
                {
                    continue;
                }

                if (file.ClaimZones(candidates[i].Node.Index, out Task<ZoneColumn?>? flight, out bool claimed) is { } kept)
                {
                    columns[i] = kept;
                }
                else if (claimed)
                {
                    claims.Add(i, candidates.Count);
                }
                else
                {
                    other ??= flight;
                }
            }

            if (!claims.IsEmpty)
            {
                try
                {
                    (int read, long asked) = await DecodeAsync(file, candidates, columns, claims, metrics, cancellationToken).ConfigureAwait(false);
                    segments += read;
                    bytes += asked;
                }
                finally
                {
                    // Landed, or given back to the scans awaiting it when the decoding failed.
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (claims.Has(i))
                        {
                            file.LandZones(candidates[i].Node.Index, columns[i]);
                        }
                    }
                }
            }

            if (other is null)
            {
                return (segments, bytes);
            }

            // Then a look again: the map is kept, or its decoding failed and this scan claims it.
            file.NoteZoneWait();
            await other.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads and decodes the zone maps of the candidates this scan <paramref name="claims"/>, and fills
    /// them in; returns the segments and bytes asked for.
    /// </summary>
    private static async ValueTask<(int Segments, long Bytes)> DecodeAsync(
        VortexFile file, List<Candidate> candidates, ZoneColumn[] columns, Claims claims,
        Scanning.ScanCounters? metrics, CancellationToken cancellationToken)
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
            if (!claims.Has(i))
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
        int segments = Scanning.ScanCounters.Unread(context.Segments, file.Segments, out long bytes);

        await file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < candidates.Count; i++)
        {
            if (!claims.Has(i))
            {
                continue;
            }

            Candidate candidate = candidates[i];
            LayoutNode zones = candidate.Node.GetChild(1);
            FieldMask all = FieldMask.All;
            int root = LayoutReaderTable.Require(in zones)
                .Execute(in zones, new RowRange(0, zones.RowCount), in all, context);

            columns[i] = Extract(context, root, candidate, candidate.Node.RowCount);
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

    /// <summary>The candidates whose decoding a scan claimed: a word of the first 64, a flag each past them.</summary>
    private struct Claims
    {
        private ulong _first;
        private bool[]? _more;

        internal readonly bool IsEmpty => _first == 0 && _more is null;

        /// <summary>Claims candidate <paramref name="candidate"/> of <paramref name="count"/>.</summary>
        internal void Add(int candidate, int count)
        {
            if (candidate < 64)
            {
                _first |= 1UL << candidate;
            }
            else
            {
                (_more ??= new bool[count - 64])[candidate - 64] = true;
            }
        }

        internal readonly bool Has(int candidate) =>
            candidate < 64 ? (_first & (1UL << candidate)) != 0 : _more is { } more && more[candidate - 64];
    }
}
