using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Expressions;
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
    /// <param name="proofs">
    /// Whether the zone maps should prove the filter true where they can, a count or a settling
    /// asking: the Bloom filters of a value a conjunct leaves out are read for the blocks they prove
    /// it absent from (<see cref="ZonePruner.NoteAbsent"/>), which a scan of rows would read for
    /// nothing.
    /// </param>
    /// <param name="question">What the question the scan belongs to read already, its variant directories kept for its other scans; null for none.</param>
    internal static async ValueTask<PruningPlan> PlanAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, CancellationToken cancellationToken,
        List<PruningStep>? steps = null, Scanning.ScanCounters? metrics = null, bool indexes = true,
        BlockMask? scope = null, bool proofs = false, Indexes.QuestionReads? question = null)
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
        if (proofs && indexes && zones is not null && blockRows > 0)
        {
            await NoteAbsentAsync(file, tree, filter, zones, blockRows, steps, metrics, cancellationToken).ConfigureAwait(false);
        }

        // The path directories first among the indexes: one small read refutes the whole file, or the
        // groups of its rows that hold no path the filter asks for.
        List<RowRange>? pathRanges = null;
        bool pathsConsulted = false;
        int pathSegments = 0;
        long pathBytes = 0;
        if (indexes && blockRows > 0 && VariantColumns(filter) is { } variants)
        {
            (bool refuted, pathRanges, pathSegments, pathBytes, pathsConsulted) = await RefutedByPathsAsync(file, filter, variants, cancellationToken, question).ConfigureAwait(false);
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

        using Indexes.BloomPruner? blooms = indexes && blockRows > 0
            ? await Indexes.BloomPruner.BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false)
            : null;
        Dictionary<string, List<(string Key, Types.Variants.PathClass Class)>>? keysRead = indexes && blockRows > 0 ? KeysRead(filter) : null;
        if (zones is null && blooms is null && locating is null && pathRanges is null && keysRead is null)
        {
            if (pathsConsulted)
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

        // Whether a directory answered, not whether one was read: a question keeps the directories its scans read (2026-11-02).
        if (pathsConsulted)
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
    /// Notes to <paramref name="zones"/>, for each value a conjunct of <paramref name="filter"/> leaves
    /// out, <c>x != v</c>, <c>NOT x IN (…)</c> or <c>NOT list_contains(x, v)</c>, the blocks the Bloom
    /// filters of <c>x</c> prove hold no row of it: the complement of those they keep for the value's
    /// own predicate.
    /// </summary>
    private static async ValueTask NoteAbsentAsync(
        VortexFile file, LayoutTree tree, VortexExpr filter, ZonePruner zones, long blockRows, List<PruningStep>? steps,
        Scanning.ScanCounters? metrics, CancellationToken cancellationToken)
    {
        List<VortexExpr> conjuncts = [];
        Conjuncts(filter, conjuncts);
        foreach (VortexExpr conjunct in conjuncts)
        {
            (VortexExpr? excluded, VortexExpr? found, FieldExpr? field) = conjunct switch
            {
                ComparisonExpr { Op: ComparisonOp.NotEqual } other when other.Value.Kind != FilterLiteralKind.Null =>
                    (other, new ComparisonExpr(other.Field, ComparisonOp.Equal, other.Value), other.Field),
                NotExpr { Operand: InExpr set } => (set, set, set.Field),
                NotExpr { Operand: ListContainsExpr contains } when contains.Value.Kind != FilterLiteralKind.Null => (contains, contains, contains.Field),
                _ => ((VortexExpr?)null, (VortexExpr?)null, (FieldExpr?)null),
            };

            if (excluded is null || found is null || field is null)
            {
                continue;
            }

            // The filters are read where they weigh less than the column whose blocks they would spare.
            using Indexes.BloomPruner? blooms = await Indexes.BloomPruner.BuildAsync(file, found, blockRows, cancellationToken).ConfigureAwait(false);
            if (blooms is null || blooms.PayloadBytes >= ColumnBytes(file, tree, field))
            {
                continue;
            }

            BlockMask mayHold = new BlockMask(tree.Root.RowCount, blockRows);
            await blooms.RefineAsync(file, mayHold, cancellationToken).ConfigureAwait(false);
            Scanning.ScanCounters.Note(metrics, blooms.Segments, blooms.Bytes);
            steps?.Add(new PruningStep("bloom filter, a value left out", 0, blooms.Segments, blooms.Bytes));
            zones.NoteAbsent(excluded, mayHold);
        }

        // The bytes of the top-level column field reads, all of them, or none known for a file of no struct.
        static long ColumnBytes(VortexFile file, LayoutTree tree, FieldExpr field)
        {
            int dot = field.Path.IndexOf('.', StringComparison.Ordinal);
            int at = file.DType.Kind == DTypeKind.Struct ? file.DType.IndexOfField(dot < 0 ? field.Path : field.Path[..dot]) : -1;
            return at >= 0 && at < tree.Root.ChildCount ? LayoutTree.BytesUnder(tree.Root.GetChild(at), file.SegmentSpecs) : 0;
        }

        static void Conjuncts(VortexExpr expr, List<VortexExpr> into)
        {
            if (expr is LogicalExpr { IsAnd: true } and)
            {
                Conjuncts(and.Left, into);
                Conjuncts(and.Right, into);
            }
            else
            {
                into.Add(expr);
            }
        }
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
        ColumnsRead visitor = default;
        VariantFilters.Visit(filter, ref visitor);
        return visitor.Columns;
    }

    /// <summary>The variant columns the paths shown read, each once, made at the first.</summary>
    private struct ColumnsRead : IFencedVisitor
    {
        internal List<string>? Columns;

        public void Visit(VortexExpr predicate, VariantFieldExpr path)
        {
            Columns ??= [];
            if (!Columns.Contains(path.Column.Path))
            {
                Columns.Add(path.Column.Path);
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
        PathsRead visitor = new PathsRead(column);
        VariantFilters.Visit(filter, ref visitor);
        return visitor.Paths;
    }

    /// <summary>The keys of the paths of one variant column the paths shown read.</summary>
    private struct PathsRead(string column) : IFencedVisitor
    {
        internal List<byte[][]> Paths = [];

        public readonly void Visit(VortexExpr predicate, VariantFieldExpr path)
        {
            if (string.Equals(path.Column.Path, column, StringComparison.Ordinal) && KeysOf(path) is { } keys)
            {
                Paths.Add(keys);
            }
        }
    }

    /// <summary>The keys of <paramref name="path"/> in UTF-8; null for a path through an element of an array.</summary>
    private static byte[][]? KeysOf(VariantFieldExpr path)
    {
        byte[][] keys = new byte[path.Steps.Length][];
        for (int i = 0; i < keys.Length; i++)
        {
            if (path.Steps[i].KeyUtf8 is not { } key)
            {
                return null;
            }

            keys[i] = key;
        }

        return keys;
    }

    /// <summary>
    /// Whether the path directories of <paramref name="columns"/> prove that no row of
    /// <paramref name="file"/> matches <paramref name="filter"/>; when they do not, the rows of the runs
    /// of the groups they prove hold none, or null; and the segments and bytes read for them.
    /// </summary>
    private static async ValueTask<(bool Refuted, List<RowRange>? Ranges, int Segments, long Bytes, bool Consulted)> RefutedByPathsAsync(
        VortexFile file, VortexExpr filter, List<string> columns, CancellationToken cancellationToken, Indexes.QuestionReads? question = null)
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
                await Indexes.VariantDirectory.ReadAsync(file, field, PathsOf(filter, column), cancellationToken, question).ConfigureAwait(false);
            segments += read;
            bytes += readBytes;
            if (directory is not null)
            {
                (directories ??= new Dictionary<string, Indexes.VariantDirectory>(StringComparer.Ordinal))[column] = directory;
            }
        }

        if (directories is null)
        {
            return (false, null, segments, bytes, false);
        }

        if (!Indexes.VariantDirectory.MayMatch(filter, directories))
        {
            return (true, null, segments, bytes, true);
        }

        // Group by group of rows, when every directory names the same groups of the file's rows.
        long groupRows = -1;
        long rows = -1;
        foreach (Indexes.VariantDirectory directory in directories.Values)
        {
            groupRows = groupRows < 0 || groupRows == directory.GroupRows ? directory.GroupRows : 0;
            rows = directory.Rows;
        }

        if (groupRows <= 0)
        {
            return (false, null, segments, bytes, true);
        }

        int groups = (int)((rows + groupRows - 1) / groupRows);
        List<RowRange>? ranges = null;
        long start = -1;
        long end = -1;
        for (int group = 0; group < groups; group++)
        {
            RowRange cut = new RowRange(group * groupRows, Math.Min((group + 1) * groupRows, rows));
            if (Indexes.VariantDirectory.MayMatch(filter, directories, group))
            {
                continue;
            }

            // Neighbouring groups refuted make one run, so that a block astride them dies too.
            if (start >= 0 && end == cut.Start)
            {
                end = cut.End;
                ranges![^1] = new RowRange(start, end);
                continue;
            }

            (start, end) = (cut.Start, cut.End);
            (ranges ??= []).Add(new RowRange(start, end));
        }

        return (false, ranges, segments, bytes, true);
    }

    /// <summary>
    /// The keys the fenced predicates of <paramref name="filter"/> read one step deep and in one class
    /// whose typed column holds every value of it, an integer, a text or a boolean, by column; null for none.
    /// </summary>
    private static Dictionary<string, List<(string Key, Types.Variants.PathClass Class)>>? KeysRead(VortexExpr filter)
    {
        StatisticsKeys visitor = default;
        VariantFilters.Visit(filter, ref visitor);
        return visitor.Keys;
    }

    /// <summary>The keys block statistics can answer that the comparisons, memberships and presences shown read, by column.</summary>
    private struct StatisticsKeys : IFencedVisitor
    {
        internal Dictionary<string, List<(string Key, Types.Variants.PathClass Class)>>? Keys;

        public void Visit(VortexExpr predicate, VariantFieldExpr path)
        {
            if (predicate is not (ComparisonExpr or InExpr or NullCheckExpr { IsNull: false }) || KeyOf(path) is not { } key)
            {
                return;
            }

            Keys ??= new Dictionary<string, List<(string, Types.Variants.PathClass)>>(StringComparer.Ordinal);
            if (!Keys.TryGetValue(path.Column.Path, out List<(string, Types.Variants.PathClass)>? list))
            {
                Keys[path.Column.Path] = list = [];
            }

            if (!list.Contains(key))
            {
                list.Add(key);
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
        KeyStats? stats = null;
        int segments = 0;
        long bytes = 0;

        // The statistics read, their blocks in rented arrays, given back once the blocks are judged.
        List<Indexes.VariantBlockStats>? held = null;
        try
        {
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

                (held ??= []).Add(read);

                foreach ((string key, Types.Variants.PathClass @class) in wanted)
                {
                    if (read.IndexOf(key, @class) is int at and >= 0)
                    {
                        (stats ??= new KeyStats()).Add(column, key, @class, read.Blocks[at]);
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
        finally
        {
            if (held is not null)
            {
                foreach (Indexes.VariantBlockStats read in held)
                {
                    read.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Whether block <paramref name="block"/> may hold a row matching <paramref name="filter"/>, by the
    /// block statistics in <paramref name="stats"/>: a key they cover with no entry for the block holds
    /// no value of its class there.
    /// </summary>
    private static bool BlockMayMatch(VortexExpr filter, KeyStats stats, int block) =>
        VariantFilters.MayMatch(filter, new BlockJudge(stats, block));

    /// <summary>The block statistics' answer for one fenced predicate in one block.</summary>
    private readonly struct BlockJudge(KeyStats stats, int block) : IFencedJudge
    {
        public bool MayMatch(VortexExpr predicate) => BlockMayMatchFenced(predicate, stats, block);
    }

    /// <summary>
    /// The block statistics of the keys a filter reads, by column, key and class; and by the path of a predicate that reads
    /// one, found the first time a block's judge asks for it: the judge asks each path of each predicate at every live block,
    /// which hashed the column's name and the key's each time.
    /// </summary>
    private sealed class KeyStats
    {
        private readonly Dictionary<(string Column, string Key, Types.Variants.PathClass Class), ReadOnlyMemory<Indexes.BlockBound>> _byKey = [];
        private readonly Dictionary<VariantFieldExpr, (Types.Variants.PathClass Class, ReadOnlyMemory<Indexes.BlockBound>? Blocks)> _byPath =
            new Dictionary<VariantFieldExpr, (Types.Variants.PathClass, ReadOnlyMemory<Indexes.BlockBound>?)>(ReferenceEqualityComparer.Instance);

        internal void Add(string column, string key, Types.Variants.PathClass @class, ReadOnlyMemory<Indexes.BlockBound> blocks) => _byKey[(column, key, @class)] = blocks;

        /// <summary>The blocks of the key <paramref name="path"/> reads, and its class; null blocks when the statistics do not cover it.</summary>
        internal (Types.Variants.PathClass Class, ReadOnlyMemory<Indexes.BlockBound>? Blocks) Of(VariantFieldExpr path)
        {
            if (!_byPath.TryGetValue(path, out (Types.Variants.PathClass Class, ReadOnlyMemory<Indexes.BlockBound>? Blocks) found))
            {
                // The blocks typed nullable in the branch that finds them: typed as memory, the other branch's null took the
                // conversion from an array and became an empty memory, a key the statistics leave out held in no block, which
                // refuted every block of a filter that reads it beside a key they cover.
                found = KeyOf(path) is { } key && _byKey.TryGetValue((path.Column.Path, key.Key, key.Class), out ReadOnlyMemory<Indexes.BlockBound> blocks)
                    ? (key.Class, (ReadOnlyMemory<Indexes.BlockBound>?)blocks)
                    : (default, null);
                _byPath[path] = found;
            }

            return found;
        }
    }

    /// <summary>Whether block <paramref name="block"/> may hold a row matching the fenced <paramref name="predicate"/>, by <paramref name="stats"/>.</summary>
    private static bool BlockMayMatchFenced(VortexExpr predicate, KeyStats stats, int block)
    {
        switch (predicate)
        {
            case ComparisonExpr { Field: VariantFieldExpr path } comparison:
                return !Entry(path, stats, block, out Types.Variants.PathClass @class, out bool holds, out Indexes.BlockBound entry)
                    || (holds && Within(@class, entry, comparison.Op, comparison.Value));
            case InExpr { Field: VariantFieldExpr path } membership:
            {
                if (!Entry(path, stats, block, out Types.Variants.PathClass memberClass, out bool memberHolds, out Indexes.BlockBound member))
                {
                    return true;
                }

                foreach (FilterLiteral literal in membership.Literals)
                {
                    if (memberHolds && Within(memberClass, member, ComparisonOp.Equal, literal))
                    {
                        return true;
                    }
                }

                return false;
            }

            case NullCheckExpr { Field: VariantFieldExpr path, IsNull: false }:
                return !Entry(path, stats, block, out _, out bool held, out _) || held;
            default:
                return true;
        }
    }

    /// <summary>Whether a value of <paramref name="class"/> within <paramref name="entry"/>'s bounds may compare to <paramref name="value"/> by <paramref name="op"/>.</summary>
    private static bool Within(Types.Variants.PathClass @class, in Indexes.BlockBound entry, ComparisonOp op, FilterLiteral value) =>
        Indexes.VariantDirectory.Within(@class, entry.Low, entry.High, nans: false, entry.MinText, entry.MaxText, op, value);

    /// <summary>
    /// Whether the statistics cover <paramref name="path"/>; and when they do, whether block <paramref name="block"/> holds
    /// values of it (<paramref name="holds"/>), <paramref name="entry"/> then its bounds, and the key's class.
    /// </summary>
    private static bool Entry(
        VariantFieldExpr path, KeyStats stats, int block, out Types.Variants.PathClass @class, out bool holds, out Indexes.BlockBound entry)
    {
        holds = false;
        entry = default;
        (@class, ReadOnlyMemory<Indexes.BlockBound>? held) = stats.Of(path);
        if (held is not { } memory)
        {
            return false;
        }

        ReadOnlySpan<Indexes.BlockBound> blocks = memory.Span;
        int low = 0;
        int high = blocks.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int at = blocks[middle].Block;
            if (at == block)
            {
                entry = blocks[middle];
                holds = true;
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
