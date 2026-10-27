using System;
using System.Collections.Generic;
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

    private KeyPruning(SortKey tree, LayoutColumn?[] sources, int skip, SummaryPruner pruner)
    {
        _tree = tree;
        _sources = sources;
        _skip = skip;
        _pruner = pruner;
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
                return new KeyPruning(tree, sources, skip, pruner);
            }
        }

        return null;
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
        last.IsEmpty || Judge(first, last, 0, default);

    /// <summary>
    /// The granules of an object's sparse index that may hold a row the filter keeps, in ranges <c>[first, end)</c>, ascending;
    /// every page in hand. <paramref name="partition"/> is the object's partition, row-encoded as it leads its tree key, which
    /// the index leaves out; empty for a dataset without one.
    /// </summary>
    internal List<(int First, int End)> Granules(GranuleKeys keys, ReadOnlySpan<byte> partition)
    {
        List<(int First, int End)> kept = [];
        if (keys.Granules == 0)
        {
            return kept;
        }

        // The pages first: a page runs from its first key to the next page's, the last one to the object's last key.
        List<(int First, int End)> pages = [];
        Search(keys, partition, 0, keys.Pages, granules: false, pages);
        foreach ((int firstPage, int endPage) in pages)
        {
            int from = firstPage * keys.PageEntries;
            int to = (int)Math.Min((long)endPage * keys.PageEntries, keys.Granules);
            Search(keys, partition, from, to, granules: true, kept);
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
    private void Search(GranuleKeys keys, ReadOnlySpan<byte> partition, int from, int to, bool granules, List<(int First, int End)> kept)
    {
        if (from >= to)
        {
            return;
        }

        if (!Judge(EntryKey(keys, from, granules), NextKey(keys, to, granules), _skip, partition))
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
            Search(keys, partition, at, Math.Min(at + step, to), granules, kept);
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
    private bool Judge(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, int from, ReadOnlySpan<byte> partition)
    {
        List<ColumnSummary> box = [];
        if (from > 0 && !partition.IsEmpty)
        {
            FilterLiteral value = RowEncoder.ReadFirstValue(partition, _tree.TypeAt(0));
            Bound(box, 0, value, value);
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
            Bound(box, position, RowEncoder.ReadFirstValue(lowValue, type), RowEncoder.ReadFirstValue(highValue, type));

            // Past the first component the two keys part on, the next ones are free.
            if (!lowValue.SequenceEqual(highValue))
            {
                break;
            }

            lowAt += lowLength;
            highAt += highLength;
        }

        return box.Count == 0 || _pruner.MayMatch(Merged(box), 1);
    }

    /// <summary>
    /// Adds to <paramref name="box"/> what values from <paramref name="low"/> to <paramref name="high"/> of the component at
    /// <paramref name="position"/> say of its source: its bounds, through the function's preimage for a function's. A null
    /// bound, which the order puts last, leaves its side open.
    /// </summary>
    private void Bound(List<ColumnSummary> box, int position, FilterLiteral low, FilterLiteral high)
    {
        if (_sources[position] is not { } source || low.Kind == FilterLiteralKind.Null)
        {
            return;
        }

        bool hasHigh = high.Kind != FilterLiteralKind.Null;
        string path = NameOf(source);
        if (source.Function is not { } function)
        {
            box.Add(new ColumnSummary(path, low, true, hasHigh ? high : default, hasHigh, false, 0, false));
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

        box.Add(new ColumnSummary(path, function.Literal(from), true, bounded ? function.Literal(to) : default, bounded, false, 0, false));
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
                box[i] = new ColumnSummary(left.Path, min, hasMin, max, hasMax, false, 0, false);
                box.RemoveAt(j);
            }
        }

        return box;
    }
}
