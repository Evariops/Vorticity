using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// What one object's synopsis answers of an aggregation that asked for states (<see cref="StateRequest"/>):
/// the table whose rows stand for the object's rows, the buckets of it the window keeps whole, and the
/// slices at the window's edges, whose rows are read. Chosen from the table of contents the object's
/// entry carries, with no read: the tables, their keys, measures and rows, and the slice table.
/// </summary>
/// <param name="Table">The table read.</param>
/// <param name="Kept">The buckets of the table whose rows are folded as states: each inside the window, and one key of time over it.</param>
/// <param name="Edges">The slices whose rows are read, the window or a key of time cutting through them.</param>
/// <param name="KeyFilter">The part of the filter on the table's keys, evaluated on its rows; null for none.</param>
internal sealed record SynopsisAnswer(SynopsisTableInfo Table, HashSet<long> Kept, IReadOnlyList<TimeSlice> Edges, VortexExpr? KeyFilter)
{
    /// <summary>
    /// The answer of the object of <paramref name="contents"/> to <paramref name="request"/> under
    /// <paramref name="filter"/>, or null when its rows are read whole: a part of the filter on the instant
    /// that is not a bound, or on a column no table keys, a key or a measure no table holds, or a window and
    /// keys of time that cut every table's buckets but the slices' and no table of slices answers.
    /// </summary>
    /// <param name="contents">The object's table of contents.</param>
    /// <param name="schema">The object's schema, which names the tables' keys and measures.</param>
    /// <param name="instant">The dataset's instant, the first column of its clustering key.</param>
    /// <param name="request">What the aggregation asks.</param>
    /// <param name="filter">The scan's filter, null for every row.</param>
    internal static SynopsisAnswer? Of(SynopsisContents contents, DType schema, string instant, StateRequest request, VortexExpr? filter)
    {
        if (contents.Tables.Count == 0 || contents.Slices.Slices.Count == 0 || schema.IndexOfField(instant) < 0)
        {
            return null;
        }

        // The filter, as conjuncts: bounds on the instant make the window, the rest must read keys alone.
        long low = long.MinValue;
        long high = long.MaxValue;
        List<VortexExpr> onKeys = [];
        HashSet<int> keyed = [];
        foreach (VortexExpr conjunct in Conjuncts(filter))
        {
            HashSet<string> paths = new(StringComparer.Ordinal);
            conjunct.CollectFields(paths);
            if (paths.Contains(instant))
            {
                if (paths.Count != 1 || !Bound(conjunct, instant, ref low, ref high))
                {
                    return null;
                }

                continue;
            }

            foreach (string path in paths)
            {
                int field = schema.IndexOfField(path);
                if (field < 0)
                {
                    return null;
                }

                keyed.Add(field);
            }

            onKeys.Add(conjunct);
        }

        // The keys: a column a table keys, or functions of one; a function of the instant, a key of time.
        List<FunctionFieldExpr> times = [];
        foreach (FieldExpr key in request.Keys)
        {
            FieldExpr column = key;
            while (column is FunctionFieldExpr function)
            {
                column = function.Column;
            }

            if (string.Equals(column.Path, instant, StringComparison.Ordinal))
            {
                if (key is not FunctionFieldExpr time)
                {
                    return null;
                }

                times.Add(time);
                continue;
            }

            int field = schema.IndexOfField(column.Path);
            if (field < 0)
            {
                return null;
            }

            keyed.Add(field);
        }

        HashSet<int> measured = [];
        foreach (string? input in request.Inputs)
        {
            if (input is not null)
            {
                int field = schema.IndexOfField(input);
                if (field < 0)
                {
                    return null;
                }

                measured.Add(field);
            }
        }

        // The tables that hold the keys and the measures, fewest rows first: the first whose every bucket
        // is whole or out of the window answers; past them, the runs of the slices, the edges read as rows.
        List<SynopsisTableInfo> candidates = [];
        foreach ((SynopsisTableInfo table, IndexSegment _) in contents.Tables)
        {
            if (Holds(table.Keys, keyed) && Holds(table.Measures, measured))
            {
                candidates.Add(table);
            }
        }

        candidates.Sort(static (a, b) => a.Rows.CompareTo(b.Rows));
        VortexExpr? keyFilter = onKeys.Count == 0 ? null : And(onKeys);
        IReadOnlyList<TimeSlice> slices = contents.Slices.Slices;
        foreach (SynopsisTableInfo table in candidates)
        {
            HashSet<long> kept = [];
            List<TimeSlice> edges = [];
            bool whole = true;
            foreach ((long bucket, long start, long end, int first) in Buckets(table.Bucket, slices, contents.Slices.Unit))
            {
                switch (Fate(start, end, low, high, times))
                {
                    case BucketFate.Kept:
                        kept.Add(bucket);
                        break;
                    case BucketFate.Edge when table.Bucket == SynopsisBucket.Slice:
                        edges.Add(slices[first]);
                        break;
                    case BucketFate.Edge:
                        whole = false;
                        break;
                    default:
                        break;
                }

                if (!whole)
                {
                    break;
                }
            }

            if (whole)
            {
                return new SynopsisAnswer(table, kept, edges, keyFilter);
            }
        }

        return null;
    }

    /// <summary>
    /// The batch of states of the table's rows read into <paramref name="tables"/>: its bucket named as the
    /// instant, its keys and states as they lie, and selected the rows of the kept buckets the key filter
    /// holds for; null when the table is not among those read.
    /// </summary>
    /// <param name="tables">The table, read.</param>
    /// <param name="instant">The dataset's instant, whose name the buckets take.</param>
    /// <param name="startRow">The object's first row in the dataset.</param>
    internal RecordBatch? Batch(SynopsisTableSet tables, string instant, long startRow)
    {
        if (tables.Types is not { } types)
        {
            return null;
        }

        foreach ((SynopsisTableInfo table, int node) in tables.Tables)
        {
            if (!ReferenceEquals(table, Table) && table != Table)
            {
                continue;
            }

            CanonicalArena arena = tables.Arena;
            CanonicalNode rows = arena.GetNode(node);
            DType rowType = rows.DType;
            int count = rows.Length;
            string[] names = new string[rowType.FieldCount];
            DType[] dtypes = new DType[rowType.FieldCount];
            int[] fields = new int[rowType.FieldCount];
            for (int f = 0; f < names.Length; f++)
            {
                names[f] = f == 0 ? instant : rowType.GetFieldName(f);
                dtypes[f] = rowType.GetField(f);
                fields[f] = rows.GetFieldIndex(f);
            }

            int root = arena.AddStruct(types.Struct(names, dtypes, Nullability.NonNullable), count, Validity.NonNullable, fields);

            // The rows of the buckets kept, then those the filter on the keys holds for.
            ReadOnlySpan<long> buckets = arena.GetNode(fields[0]).Values.Cast<long>();
            int words = Math.Max((count + 63) >> 6, 1);
            VortexBuffer selection = arena.Allocate(words * sizeof(ulong), sizeof(ulong), out Span<byte> bits);
            bits.Clear();
            Span<ulong> kept = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bits);
            Span<byte> states = KeyFilter is null ? default : new byte[count];
            if (KeyFilter is { } keyFilter)
            {
                FilterEvaluator.Evaluate(keyFilter, arena, root, count, states);
            }

            int selected = 0;
            for (int r = 0; r < count; r++)
            {
                if (Kept.Contains(buckets[r]) && (states.IsEmpty || states[r] == Trilean.True))
                {
                    kept[r >> 6] |= 1UL << (r & 63);
                    selected++;
                }
            }

            RecordBatch batch = new RecordBatch(arena, root, startRow) { CarriesStates = true };
            batch.Select(selection, selected);
            return batch;
        }

        return null;
    }

    private enum BucketFate
    {
        Out,
        Kept,
        Edge,
    }

    /// <summary>
    /// Whether a bucket of instants [<paramref name="start"/>, <paramref name="end"/>) lies inside the
    /// window [<paramref name="low"/>, <paramref name="high"/>] with one value of each key of time over it,
    /// outside the window, or across one of them.
    /// </summary>
    private static BucketFate Fate(long start, long end, long low, long high, List<FunctionFieldExpr> times)
    {
        long last = end - 1;
        if (last < low || start > high)
        {
            return BucketFate.Out;
        }

        if (start < low || last > high)
        {
            return BucketFate.Edge;
        }

        foreach (FunctionFieldExpr time in times)
        {
            if (Image(time, start) is not { } first || Image(time, last) is not { } other || first != other)
            {
                return BucketFate.Edge;
            }
        }

        return BucketFate.Kept;
    }

    /// <summary>An instant through a key's functions, the innermost first; null where one does not map it.</summary>
    private static long? Image(FunctionFieldExpr time, long instant)
    {
        long? value = time.Column is FunctionFieldExpr inner ? Image(inner, instant) : instant;
        if (value is not { } input || !time.Function.TryMap(FilterLiteral.From(input), out FilterLiteral mapped) || mapped.Kind != FilterLiteralKind.Signed)
        {
            return null;
        }

        return mapped.SignedValue;
    }

    /// <summary>
    /// Each bucket of a table over <paramref name="slices"/>: its value in the table's rows, the instants
    /// its slices span, and the first of them. A slice is its own bucket; an hour holds the slices that
    /// start in it, which a slice wider than an hour carries past it; the object holds every slice.
    /// </summary>
    private static IEnumerable<(long Bucket, long Start, long End, int First)> Buckets(SynopsisBucket kind, IReadOnlyList<TimeSlice> slices, TimeUnit unit)
    {
        switch (kind)
        {
            case SynopsisBucket.Slice:
                for (int s = 0; s < slices.Count; s++)
                {
                    yield return (slices[s].Start, slices[s].Start, slices[s].End, s);
                }

                break;
            case SynopsisBucket.Hour:
                for (int s = 0; s < slices.Count;)
                {
                    long hour = TimeSlicer.Floor(slices[s].Start, unit, SliceWidth.Hour);
                    int last = s;
                    long end = slices[s].End;
                    while (last + 1 < slices.Count && TimeSlicer.Floor(slices[last + 1].Start, unit, SliceWidth.Hour) == hour)
                    {
                        last++;
                        end = Math.Max(end, slices[last].End);
                    }

                    yield return (hour, slices[s].Start, end, s);
                    s = last + 1;
                }

                break;
            default:
                yield return (0, slices[0].Start, slices[^1].End, 0);
                break;
        }
    }

    /// <summary>Whether <paramref name="have"/> holds every field of <paramref name="need"/>.</summary>
    private static bool Holds(int[] have, HashSet<int> need)
    {
        foreach (int field in need)
        {
            if (Array.IndexOf(have, field) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Narrows the window by a comparison of the instant itself with an integer, in its units; false for
    /// any other predicate on it.
    /// </summary>
    private static bool Bound(VortexExpr conjunct, string instant, ref long low, ref long high)
    {
        if (conjunct is not ComparisonExpr { Value.Kind: FilterLiteralKind.Signed } comparison
            || comparison.Field.GetType() != typeof(FieldExpr) || !string.Equals(comparison.Field.Path, instant, StringComparison.Ordinal))
        {
            return false;
        }

        long value = comparison.Value.SignedValue;
        switch (comparison.Op)
        {
            case ComparisonOp.Equal:
                low = Math.Max(low, value);
                high = Math.Min(high, value);
                return true;
            case ComparisonOp.Greater:
                low = value == long.MaxValue ? long.MaxValue : Math.Max(low, value + 1);
                high = value == long.MaxValue ? long.MinValue : high;
                return true;
            case ComparisonOp.GreaterOrEqual:
                low = Math.Max(low, value);
                return true;
            case ComparisonOp.Less:
                high = value == long.MinValue ? long.MinValue : Math.Min(high, value - 1);
                low = value == long.MinValue ? long.MaxValue : low;
                return true;
            case ComparisonOp.LessOrEqual:
                high = Math.Min(high, value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The conjuncts of <paramref name="filter"/>: its operands under its conjunctions, none for no filter.</summary>
    private static IEnumerable<VortexExpr> Conjuncts(VortexExpr? filter)
    {
        if (filter is null)
        {
            yield break;
        }

        if (filter is LogicalExpr { IsAnd: true } and)
        {
            foreach (VortexExpr part in Conjuncts(and.Left))
            {
                yield return part;
            }

            foreach (VortexExpr part in Conjuncts(and.Right))
            {
                yield return part;
            }

            yield break;
        }

        yield return filter;
    }

    private static VortexExpr And(List<VortexExpr> parts)
    {
        VortexExpr joined = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            joined = Expr.And(joined, parts[i]);
        }

        return joined;
    }
}
