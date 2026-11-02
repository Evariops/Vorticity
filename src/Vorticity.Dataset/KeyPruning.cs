using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// What a filter asks of a layout's key, judged over the keys that bound the rows of an object and of its granules
/// (spec A, 7.2 and 7.3). Rows between two keys of the tree's order hold, on the components the two share, those values;
/// on the next one, a value between theirs; on the rest, anything. That box, mapped to the columns the components read
/// (a function's bounds to its source's through the function's preimage), is judged by the summaries' pruner: a box it
/// refutes holds no row the filter keeps. Over an object's sparse index, a range of granules refuted is passed over, and
/// one that is not and spans more than one granule is cut in eight, each judged in turn: the exclusion search, first over
/// the index's pages, then over the granules of the pages kept.
/// </summary>
/// <remarks>
/// It prunes well where the components before the one a filter bounds hold few values, a filter on the hour under
/// <c>(Service, Hour)</c> keeping in each service the granules of its hours. A component read from a variant's key bounds
/// the filter's reads of that key in that class (spec E, 5.2): they are judged under a name of their own, which no column
/// takes, as a column is under its path.
/// </remarks>
internal sealed class KeyPruning
{
    /// <summary>The parts a range of granules the filter does not refute is cut in.</summary>
    private const int Fanout = 8;

    private readonly SortKey _tree;
    private readonly LayoutColumn?[] _sources;
    private readonly int _skip;
    private readonly SummaryPruner _pruner;

    // The filter as the key reads it, and its conjuncts that read the components' columns alone, as one predicate, made at the
    // first object whose index it would spare: Unkeyed for none.
    private readonly VortexExpr _judged;
    private SummaryPruner? _keyed;

    /// <summary>What <see cref="_keyed"/> holds for a filter none of whose conjuncts reads the key's columns alone.</summary>
    private static readonly SummaryPruner Unkeyed = new SummaryPruner(Expr.Literal(FilterLiteral.From(true)));

    // The conjuncts of the filter that read the components an object's sparse index keeps of each granule's key alone, the
    // first LayoutPlan.Prefix: what the index may refute a granule by, made at the first object whose index it would read.
    private readonly LayoutColumn?[] _indexedSources;
    private SummaryPruner? _indexed;

    private KeyPruning(SortKey tree, LayoutColumn?[] sources, int skip, SummaryPruner pruner, VortexExpr judged, LayoutColumn?[] indexedSources)
    {
        _tree = tree;
        _sources = sources;
        _skip = skip;
        _pruner = pruner;
        _judged = judged;
        _indexedSources = indexedSources;
    }

    /// <summary>
    /// Whether the filter bounds a component an object's sparse index keeps of each granule's key, the first
    /// <see cref="LayoutPlan.Prefix"/>: else every granule's box holds every value the filter reads, and the index refutes
    /// none. The components past the prefix vary within a granule, which its zone maps judge: the pods of a route, fifth
    /// under a prefix of two, read every object's index for no granule refuted.
    /// </summary>
    internal bool BoundsIndexed => !ReferenceEquals(_indexed ??= Keyed(_judged, _indexedSources), Unkeyed);

    /// <summary>
    /// Whether the sparse index of an object whose summaries are <paramref name="summaries"/>, over <paramref name="rows"/>
    /// rows, may refute a granule: the filter bounds a component the index keeps (<see cref="BoundsIndexed"/>), and the
    /// object's summaries do not prove those bounds over every row of it. A window that holds an object's rows whole, the
    /// newest day of a window that ends where its data does, refutes none of its granules.
    /// </summary>
    internal bool IndexMayRefute(ObjectSummaries summaries, long rows)
    {
        SummaryPruner indexed = _indexed ??= Keyed(_judged, _indexedSources);
        return !ReferenceEquals(indexed, Unkeyed) && !indexed.MustMatch(summaries.Columns, rows);
    }

    /// <summary>
    /// The pruning of <paramref name="filter"/> over <paramref name="schema"/>'s key; null when the dataset has no key, or the
    /// filter reads none of the columns the partition and the components read.
    /// </summary>
    internal static KeyPruning? Of(DatasetSchema schema, VortexExpr? filter)
    {
        if (filter is null || schema.Tree is not { } tree || schema.Layout is not { } plan)
        {
            return null;
        }

        LayoutColumn?[] sources = new LayoutColumn?[tree.Count];
        int skip = plan.Partition is null ? 0 : 1;
        if (plan.Partition is { } partition)
        {
            sources[0] = partition;
        }

        for (int i = 0; i < plan.Components.Count && skip + i < sources.Length; i++)
        {
            sources[skip + i] = plan.Components[i];
        }

        // The filter as the key reads it: each read of a variant's key a component reads, a column of its own name.
        VortexExpr judged = Aggregating.GroupPredicates.Rewrite(
            FunctionFieldExpr.Ranges(filter), field => field is VariantFieldExpr path && Named(sources, path) is { } name ? new ColumnFieldExpr(name) : field);
        SummaryPruner pruner = new SummaryPruner(judged);
        foreach (LayoutColumn? source in sources)
        {
            if (source is not null && Reads(pruner, NameOf(source)))
            {
                LayoutColumn?[] indexed = new LayoutColumn?[Math.Min(plan.Prefix, plan.Components.Count)];
                for (int i = 0; i < indexed.Length; i++)
                {
                    indexed[i] = plan.Components[i];
                }

                return new KeyPruning(tree, sources, skip, pruner, judged, indexed);
            }
        }

        return null;
    }

    /// <summary>
    /// The conjuncts of <paramref name="judged"/> that read the columns of <paramref name="sources"/> alone, as one predicate:
    /// what a box of the key judges whole; <see cref="Unkeyed"/> when none does.
    /// </summary>
    private static SummaryPruner Keyed(VortexExpr judged, LayoutColumn?[] sources)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        foreach (LayoutColumn? source in sources)
        {
            if (source is not null)
            {
                names.Add(NameOf(source));
            }
        }

        VortexExpr? keyed = null;
        List<string> paths = [];
        foreach (VortexExpr conjunct in Conjuncts(judged))
        {
            paths.Clear();
            conjunct.CollectFields(paths);
            if (paths.Count > 0 && paths.TrueForAll(names.Contains))
            {
                keyed = keyed is null ? conjunct : Expr.And(keyed, conjunct);
            }
        }

        return keyed is null ? Unkeyed : new SummaryPruner(keyed);

        static IEnumerable<VortexExpr> Conjuncts(VortexExpr expr)
        {
            if (expr is LogicalExpr { IsAnd: true } and)
            {
                foreach (VortexExpr left in Conjuncts(and.Left))
                {
                    yield return left;
                }

                foreach (VortexExpr right in Conjuncts(and.Right))
                {
                    yield return right;
                }
            }
            else
            {
                yield return expr;
            }
        }
    }

    /// <summary>
    /// The name a component's values are judged under: its column's path, or, for a read of a variant's key, its text behind
    /// a character no column's name holds.
    /// </summary>
    private static string NameOf(LayoutColumn source) => source.Read is { } read ? "\0" + read.Text : source.Value.SourcePath;

    /// <summary>The name of the component that reads <paramref name="path"/>, the same key in the same class; null for none.</summary>
    private static string? Named(LayoutColumn?[] sources, VariantFieldExpr path)
    {
        foreach (LayoutColumn? source in sources)
        {
            if (source?.Read is { } read && string.Equals(read.Text, path.Text, StringComparison.Ordinal))
            {
                return NameOf(source);
            }
        }

        return null;
    }

    /// <summary>Whether the summaries' pruner reads the column at <paramref name="path"/>.</summary>
    private static bool Reads(SummaryPruner pruner, string path)
    {
        foreach (string read in pruner.Paths)
        {
            if (string.Equals(read, path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an object whose rows lie from tree key <paramref name="first"/> to tree key <paramref name="last"/>, both
    /// row-encoded without the uid, may hold a row the filter keeps: its key range judged as one granule.
    /// </summary>
    internal bool MayHold(ReadOnlySpan<byte> first, ReadOnlySpan<byte> last) =>
        last.IsEmpty || Judge(first, last, 0, default, []);

    /// <summary>
    /// Whether every row of an object whose rows lie from tree key <paramref name="first"/> to tree key <paramref name="last"/>
    /// keeps the filter's conjuncts on the key's columns: no granule of its sparse index can then be refuted, and the index
    /// has nothing to prune. A time window that holds a day's objects whole, under a key the instant leads, reads none of
    /// their indexes.
    /// </summary>
    internal bool HoldsWhole(ReadOnlySpan<byte> first, ReadOnlySpan<byte> last)
    {
        SummaryPruner keyed = _keyed ??= Keyed(_judged, _sources);
        if (ReferenceEquals(keyed, Unkeyed) || last.IsEmpty)
        {
            return false;
        }

        List<ColumnSummary> box = Box(first, last, 0, default, nonNull: true, []);
        return box.Count > 0 && keyed.MustMatch(Merged(box), 1);
    }

    /// <summary>
    /// The granules of an object's sparse index that may hold a row the filter keeps, in ranges <c>[first, end)</c>, ascending;
    /// every page in hand. <paramref name="partition"/> is the object's partition, row-encoded as it leads its tree key, which
    /// the index leaves out; empty for a dataset without one.
    /// </summary>
    /// <remarks>
    /// Found once an object a question: a question at 32 lanes asks them of an object for each of its parts, and searched the
    /// whole index each time, a list of summaries made at each node, 23 MB a question of the services under 5xx over the last
    /// quarter of an hour (2026-11-02).
    /// </remarks>
    internal List<(int First, int End)> Granules(GranuleKeys keys, ReadOnlySpan<byte> partition)
    {
        // Made at the first object asked, a pruning that asks none holding nothing; the dictionary its own lock.
        Dictionary<GranuleKeys, (byte[] Partition, (int First, int End)[] Ranges)> known = Volatile.Read(ref _kept)
            ?? Interlocked.CompareExchange(ref _kept, new(ReferenceEqualityComparer.Instance), null) ?? _kept!;
        lock (known)
        {
            if (known.TryGetValue(keys, out (byte[] Partition, (int First, int End)[] Ranges) found) && partition.SequenceEqual(found.Partition))
            {
                return [.. found.Ranges];
            }
        }

        List<(int First, int End)> kept = Search(keys, partition);
        lock (known)
        {
            known[keys] = (partition.ToArray(), [.. kept]);
        }

        return kept;
    }

    // The granules each object's index keeps, by the index, with the partition they were searched under.
    private Dictionary<GranuleKeys, (byte[] Partition, (int First, int End)[] Ranges)>? _kept;

    /// <summary>The searches of an object's index this pruning made, which an object asked again does not repeat.</summary>
    internal int Searches => _searches;

    private int _searches;

    private List<(int First, int End)> Search(GranuleKeys keys, ReadOnlySpan<byte> partition)
    {
        Interlocked.Increment(ref _searches);
        List<(int First, int End)> kept = [];
        if (keys.Granules == 0)
        {
            return kept;
        }

        // The pages first: a page runs from its first key to the next page's, the last one to the object's last key. One list
        // of summaries serves every node of the search, and each text or binary the keys hold is read once.
        List<ColumnSummary> box = new List<ColumnSummary>(_tree.Count + 1);
        Dictionary<byte[], FilterLiteral> values = new Dictionary<byte[], FilterLiteral>(ByteKeys.Instance);
        List<(int First, int End)> pages = [];
        Search(keys, partition, 0, keys.Pages, granules: false, pages, box, values);
        foreach ((int firstPage, int endPage) in pages)
        {
            int from = firstPage * keys.PageEntries;
            int to = (int)Math.Min((long)endPage * keys.PageEntries, keys.Granules);
            Search(keys, partition, from, to, granules: true, kept, box, values);
        }

        // Neighbouring ranges as one; none when the object's box meets the filter's and no granule's does.
        if (kept.Count == 0)
        {
            return kept;
        }

        int merged = 0;
        for (int i = 1; i < kept.Count; i++)
        {
            if (kept[i].First <= kept[merged].End)
            {
                kept[merged] = (kept[merged].First, Math.Max(kept[merged].End, kept[i].End));
            }
            else
            {
                kept[++merged] = kept[i];
            }
        }

        kept.RemoveRange(merged + 1, kept.Count - merged - 1);
        return kept;
    }

    /// <summary>
    /// The exclusion search over entries <paramref name="from"/> to <paramref name="to"/>, pages or granules: a range the
    /// box of its bounds refutes is dropped, one of a single entry kept, any other cut in <see cref="Fanout"/>.
    /// </summary>
    private void Search(
        GranuleKeys keys, ReadOnlySpan<byte> partition, int from, int to, bool granules, List<(int First, int End)> kept, List<ColumnSummary> box,
        Dictionary<byte[], FilterLiteral> values)
    {
        if (from >= to)
        {
            return;
        }

        if (!Judge(EntryKey(keys, from, granules), NextKey(keys, to, granules), _skip, partition, box, values))
        {
            return;
        }

        if (to - from == 1)
        {
            kept.Add((from, to));
            return;
        }

        int step = Math.Max(1, (to - from + Fanout - 1) / Fanout);
        for (int at = from; at < to; at += step)
        {
            Search(keys, partition, at, Math.Min(at + step, to), granules, kept, box, values);
        }
    }

    /// <summary>The key of entry <paramref name="entry"/>: a granule's, or a page's first.</summary>
    private static ReadOnlySpan<byte> EntryKey(GranuleKeys keys, int entry, bool granules) =>
        granules ? keys.Key(entry) : keys.PageKey(entry);

    /// <summary>The key entry <paramref name="entry"/> − 1 runs to: entry <paramref name="entry"/>'s, or the last key past the last.</summary>
    private static ReadOnlySpan<byte> NextKey(GranuleKeys keys, int entry, bool granules) =>
        granules
            ? entry == keys.Granules ? keys.LastKey : keys.Key(entry)
            : entry == keys.Pages ? keys.LastKey : keys.PageKey(entry);

    /// <summary>
    /// Whether rows between <paramref name="low"/> and <paramref name="high"/>, keys of the tree's components from position
    /// <paramref name="from"/> on, both included, may hold a row the filter keeps; <paramref name="partition"/> the value of
    /// the positions before, which every row holds.
    /// </summary>
    private bool Judge(
        ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, int from, ReadOnlySpan<byte> partition, List<ColumnSummary> box,
        Dictionary<byte[], FilterLiteral>? values = null)
    {
        box.Clear();
        Box(low, high, from, partition, nonNull: false, box, values);
        return box.Count == 0 || _pruner.MayMatch(Merged(box), 1);
    }

    /// <summary>
    /// What rows between <paramref name="low"/> and <paramref name="high"/> hold of the components' columns, as
    /// <see cref="Judge"/> reads them; with <paramref name="nonNull"/>, a column bounded on both sides by values holds no
    /// null, which the order puts after every value. Into <paramref name="box"/>, which it returns.
    /// </summary>
    private List<ColumnSummary> Box(
        ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, int from, ReadOnlySpan<byte> partition, bool nonNull, List<ColumnSummary> box,
        Dictionary<byte[], FilterLiteral>? values = null)
    {
        if (from > 0 && !partition.IsEmpty)
        {
            FilterLiteral value = Value(partition, 0, values);
            Bound(box, 0, value, value, nonNull);
        }

        int lowAt = 0;
        int highAt = 0;
        for (int position = from; position < _tree.Count && lowAt < low.Length && highAt < high.Length; position++)
        {
            DType type = _tree.TypeAt(position);
            int lowLength = RowEncoder.ValueLength(low[lowAt..], type);
            int highLength = RowEncoder.ValueLength(high[highAt..], type);
            if (lowLength <= 0 || highLength <= 0 || lowAt + lowLength > low.Length || highAt + highLength > high.Length)
            {
                break;
            }

            ReadOnlySpan<byte> lowValue = low.Slice(lowAt, lowLength);
            ReadOnlySpan<byte> highValue = high.Slice(highAt, highLength);
            Bound(box, position, Value(lowValue, position, values), Value(highValue, position, values), nonNull);

            // Past the first component the two keys part on, the next ones are free.
            if (!lowValue.SequenceEqual(highValue))
            {
                break;
            }

            lowAt += lowLength;
            highAt += highLength;
        }

        return box;
    }

    /// <summary>
    /// The value <paramref name="encoded"/> holds, of the component at <paramref name="position"/>: a text's or a binary's read
    /// once a search, by its bytes, into <paramref name="values"/> when there are some. Each granule's bounds made their
    /// values' bytes anew: a search of a service's objects read the same few names thousands of times, a tenth of what the
    /// questions of a node allocated (2026-11-02).
    /// </summary>
    private FilterLiteral Value(ReadOnlySpan<byte> encoded, int position, Dictionary<byte[], FilterLiteral>? values)
    {
        if (values is null || _tree.WidthAt(position) != RowEncoder.VariableWidth)
        {
            return RowEncoder.ReadFirstValue(encoded, _tree.TypeAt(position));
        }

        Dictionary<byte[], FilterLiteral>.AlternateLookup<ReadOnlySpan<byte>> lookup = values.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (!lookup.TryGetValue(encoded, out FilterLiteral value))
        {
            value = RowEncoder.ReadFirstValue(encoded, _tree.TypeAt(position));
            lookup[encoded] = value;
        }

        return value;
    }

    /// <summary>
    /// Adds to <paramref name="box"/> what values from <paramref name="low"/> to <paramref name="high"/> of the component at
    /// <paramref name="position"/> say of its source: its bounds, through the function's preimage for a function's. A null
    /// bound, which the order puts last, leaves its side open. With <paramref name="nonNull"/>, a column bounded on both
    /// sides holds no null.
    /// </summary>
    private void Bound(List<ColumnSummary> box, int position, FilterLiteral low, FilterLiteral high, bool nonNull)
    {
        if (_sources[position] is not { } source || low.Kind == FilterLiteralKind.Null)
        {
            return;
        }

        bool hasHigh = high.Kind != FilterLiteralKind.Null;
        bool noNull = nonNull && hasHigh;
        string path = NameOf(source);
        if (source.Function is not { } function)
        {
            box.Add(new ColumnSummary(path, low, true, hasHigh ? high : default, hasHigh, false, 0, noNull));
            return;
        }

        // The source's values whose image lies from the low bound to the high one: from the least whose image reaches the
        // low, to before the least whose image passes the high.
        if (!ValueFunction.TryInteger(low, out Int128 lowValue) || !function.TryLeast(lowValue, out Int128 from))
        {
            return;
        }

        bool bounded = false;
        Int128 to = 0;
        if (hasHigh && ValueFunction.TryInteger(high, out Int128 highValue) && function.TryLeast(highValue + 1, out Int128 past))
        {
            bounded = true;
            to = past - 1;
        }

        box.Add(new ColumnSummary(path, function.Literal(from), true, bounded ? function.Literal(to) : default, bounded, false, 0, noNull && bounded));
    }

    /// <summary>The box's bounds, those of one column intersected.</summary>
    private static List<ColumnSummary> Merged(List<ColumnSummary> box)
    {
        for (int i = 0; i < box.Count; i++)
        {
            for (int j = box.Count - 1; j > i; j--)
            {
                if (!string.Equals(box[i].Path, box[j].Path, StringComparison.Ordinal))
                {
                    continue;
                }

                ColumnSummary left = box[i];
                ColumnSummary right = box[j];
                bool hasMin = left.HasMin || right.HasMin;
                bool hasMax = left.HasMax || right.HasMax;
                FilterLiteral min = !left.HasMin ? right.Min : !right.HasMin ? left.Min : KeyOrder.Ieee(left.Min, right.Min) >= 0 ? left.Min : right.Min;
                FilterLiteral max = !left.HasMax ? right.Max : !right.HasMax ? left.Max : KeyOrder.Ieee(left.Max, right.Max) <= 0 ? left.Max : right.Max;
                box[i] = new ColumnSummary(left.Path, min, hasMin, max, hasMax, false, 0, left.HasNullCount || right.HasNullCount);
                box.RemoveAt(j);
            }
        }

        return box;
    }
}
