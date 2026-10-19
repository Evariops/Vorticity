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

/// <summary>One table of an answer, and the buckets of it whose rows are folded as states: each inside the window, with one value of every key of time over it.</summary>
/// <param name="Table">The table read.</param>
/// <param name="Kept">Its buckets folded.</param>
internal sealed record SynopsisPart(SynopsisTableInfo Table, HashSet<long> Kept)
{
    /// <summary>
    /// The instant a folded bucket's states carry where it is not the bucket's own value: the first the
    /// object holds in it. A bucket is named by its start, which a slice wider than the object's rows puts
    /// before them, and an object's bucket by zero, while a key of time is one value over the instants held.
    /// </summary>
    internal Dictionary<long, long> Instants { get; } = [];

    /// <summary>Folds <paramref name="bucket"/>, its states carrying <paramref name="first"/>, the first instant the object holds in it.</summary>
    internal void Fold(long bucket, long first)
    {
        Kept.Add(bucket);
        if (first != bucket)
        {
            Instants[bucket] = first;
        }
    }
}

/// <summary>
/// What one object's synopsis answers of an aggregation that asked for states (<see cref="StateRequest"/>):
/// the coarsest table that holds the question's keys and measures for the buckets the window keeps whole,
/// the runs of the slices for the slices inside the buckets the window or a key of time cuts, and the rows
/// of the slices they cut themselves. Chosen from the table of contents the object's entry carries, with no
/// read: the tables, their keys, measures and rows, and the slice table.
/// </summary>
/// <param name="Parts">The tables read, each with the buckets it folds.</param>
/// <param name="Edges">The slices whose rows are read, the window or a key of time cutting through them.</param>
/// <param name="KeyFilter">The part of the filter on the tables' keys, evaluated on their rows; null for none.</param>
/// <param name="Request">What the aggregation asks: the states a row taken out must be taken out of.</param>
/// <param name="Excluded">
/// The values of identifiers the filter leaves out, <c>id != v || id == null</c>: the whole minus the part, the
/// part (<c>id = v</c>) the rows the identifier's index finds, taken out of the tables as a delete's are, and
/// what the filter keeps (the conjunct itself).
/// </param>
internal sealed record SynopsisAnswer(
    IReadOnlyList<SynopsisPart> Parts, IReadOnlyList<TimeSlice> Edges, VortexExpr? KeyFilter, StateRequest Request,
    IReadOnlyList<(VortexExpr Part, VortexExpr Kept)> Excluded)
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
    /// <param name="held">
    /// The least and the greatest instant the object's rows hold, from its summary, which lie outwards if
    /// at all: a bucket spans no instant past them, however wide its slices' unit, so an hour's object cut
    /// in slices of a day still holds one hour. Null when the summary does not say.
    /// </param>
    /// <param name="identifiers">The dataset's identifiers, whose values a filter may leave out (<see cref="Excluded"/>).</param>
    internal static SynopsisAnswer? Of(
        SynopsisContents contents, DType schema, string instant, StateRequest request, VortexExpr? filter, (long Least, long Greatest)? held = null,
        IReadOnlyList<string>? identifiers = null)
    {
        if (contents.Tables.Count == 0 || contents.Slices.Slices.Count == 0 || schema.IndexOfField(instant) < 0)
        {
            return null;
        }

        // The filter, as conjuncts: bounds on the instant make the window, the rest must read keys alone.
        long low = long.MinValue;
        long high = long.MaxValue;
        List<VortexExpr> onKeys = [];
        List<(VortexExpr, VortexExpr)> excluded = [];
        HashSet<int> keyed = [];
        foreach (VortexExpr conjunct in Conjuncts(filter))
        {
            if (Exclusion(conjunct, identifiers) is { } part)
            {
                excluded.Add((part, conjunct));
                continue;
            }

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

        // The tables that hold the keys and the measures: the one of fewest rows answers the buckets the
        // window keeps whole, the runs of the slices of fewest rows the slices inside the buckets it cuts,
        // and the slices cut are read as rows. Without a table of slices, a bucket cut is read as rows.
        SynopsisTableInfo? coarsest = null;
        SynopsisTableInfo? sliced = null;
        foreach ((SynopsisTableInfo table, IndexSegment _) in contents.Tables)
        {
            if (Holds(table.Keys, keyed) && Holds(table.Measures, measured))
            {
                coarsest = coarsest is null || table.Rows < coarsest.Rows ? table : coarsest;
                sliced = table.Bucket == SynopsisBucket.Slice && (sliced is null || table.Rows < sliced.Rows) ? table : sliced;
            }
        }

        if (coarsest is null)
        {
            return null;
        }

        IReadOnlyList<TimeSlice> slices = contents.Slices.Slices;
        SynopsisPart whole = new SynopsisPart(coarsest, []);
        SynopsisPart? runs = sliced is null || ReferenceEquals(sliced, coarsest) ? null : new SynopsisPart(sliced, []);
        List<TimeSlice> edges = [];
        long least = held?.Least ?? long.MinValue;
        long past = held is { Greatest: < long.MaxValue } bounds ? bounds.Greatest + 1 : long.MaxValue;
        BucketFate FateOf(long start, long end) =>
            Math.Max(start, least) >= Math.Min(end, past) ? BucketFate.Out : Fate(Math.Max(start, least), Math.Min(end, past), low, high, times);
        foreach ((long bucket, long start, long end, int first, int last) in Buckets(coarsest.Bucket, slices, contents.Slices.Unit))
        {
            switch (FateOf(start, end))
            {
                case BucketFate.Kept:
                    whole.Fold(bucket, Math.Max(start, least));
                    break;
                case BucketFate.Edge:
                    for (int s = first; s <= last; s++)
                    {
                        BucketFate fate = coarsest.Bucket == SynopsisBucket.Slice ? BucketFate.Edge : FateOf(slices[s].Start, slices[s].End);
                        if (fate == BucketFate.Kept && runs is not null)
                        {
                            runs.Fold(slices[s].Start, Math.Max(slices[s].Start, least));
                        }
                        else if (fate != BucketFate.Out)
                        {
                            edges.Add(slices[s]);
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        // An object whose every row the edges hold is read as rows, with no table.
        List<SynopsisPart> parts = [];
        if (whole.Kept.Count > 0)
        {
            parts.Add(whole);
        }

        if (runs is { Kept.Count: > 0 })
        {
            parts.Add(runs);
        }

        return parts.Count == 0 && edges.Count > 0 ? null : new SynopsisAnswer(parts, edges, onKeys.Count == 0 ? null : And(onKeys), request, excluded);
    }

    /// <summary>
    /// The part a conjunct leaves out when it is the complement of one value of an identifier under
    /// three-valued logic, <c>id != v || id == null</c> in either order: the equality <c>id = v</c>, whose rows
    /// the identifier's index finds; null for any other conjunct.
    /// </summary>
    private static VortexExpr? Exclusion(VortexExpr conjunct, IReadOnlyList<string>? identifiers)
    {
        if (identifiers is not { Count: > 0 } || conjunct is not LogicalExpr { IsAnd: false } or)
        {
            return null;
        }

        (VortexExpr a, VortexExpr b) = (or.Left, or.Right);
        if (a is NullCheckExpr)
        {
            (a, b) = (b, a);
        }

        if (a is ComparisonExpr { Op: ComparisonOp.NotEqual } other && other.Value.Kind != FilterLiteralKind.Null
            && b is NullCheckExpr { IsNull: true } missing
            && other.Field.GetType() == typeof(FieldExpr) && missing.Field.GetType() == typeof(FieldExpr)
            && string.Equals(other.Field.Path, missing.Field.Path, StringComparison.Ordinal))
        {
            foreach (string identifier in identifiers)
            {
                if (string.Equals(identifier, other.Field.Path, StringComparison.Ordinal))
                {
                    return Expr.Eq(other.Field, Expr.Literal(other.Value));
                }
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="table"/> is one of the answer's, which the read of the synopsis fetches.</summary>
    internal bool Reads(SynopsisTableInfo table)
    {
        foreach (SynopsisPart part in Parts)
        {
            if (ReferenceEquals(part.Table, table))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The batch of states of <paramref name="part"/>'s table, read into <paramref name="tables"/>: its bucket
    /// named as the instant, its keys and states as they lie, and selected the rows of the part's buckets
    /// the key filter holds for; null when the table is not among those read.
    /// </summary>
    /// <param name="tables">The tables, read.</param>
    /// <param name="part">The table and its buckets.</param>
    /// <param name="instant">The dataset's instant, whose name the buckets take.</param>
    /// <param name="startRow">The object's first row in the dataset.</param>
    /// <param name="removed">
    /// Rows to take out of the table's states first, a delete's or those a question excludes (see
    /// <see cref="SynopsisCorrection"/>); null for none.
    /// </param>
    /// <param name="schema">The object's schema, which names the measures of the rows taken out.</param>
    /// <param name="slices">The object's slice table, which places a row taken out in its bucket.</param>
    /// <returns>The batch; null when the table is not among those read, or a row cannot come out exactly.</returns>
    internal RecordBatch? Batch(
        SynopsisTableSet tables, SynopsisPart part, string instant, long startRow,
        IReadOnlyList<RecordBatch>? removed = null, DType schema = default, SliceTable? slices = null)
    {
        if (tables.Types is not { } types)
        {
            return null;
        }

        foreach ((SynopsisTableInfo table, int node) in tables.Tables)
        {
            if (!ReferenceEquals(table, part.Table))
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

            if (removed is { Count: > 0 }
                && (slices is null || !SynopsisCorrection.TryRemove(arena, table, rowType, fields, schema, removed, slices.Slices, slices.Unit, part.Kept, Request.Reads)))
            {
                return null;
            }

            int bucketed = fields[0];
            if (part.Instants.Count > 0)
            {
                fields[0] = InstantsOf(arena, bucketed, dtypes[0], count, part.Instants);
            }

            int root = arena.AddStruct(types.Struct(names, dtypes, Nullability.NonNullable), count, Validity.NonNullable, fields);

            // The rows of the buckets kept, then those the filter on the keys holds for, but a row whose
            // every row was taken out, whose group the rows would not make.
            ReadOnlySpan<long> buckets = arena.GetNode(bucketed).Values.Cast<long>();
            ReadOnlySpan<long> stood = arena.GetNode(fields[table.Keys.Length + 1]).Values.Cast<long>();
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
                if (part.Kept.Contains(buckets[r]) && stood[r] > 0 && (states.IsEmpty || states[r] == Trilean.True))
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

    /// <summary>The instants a table's rows carry as states, by row: each bucket's own value, or the first instant the object holds in it.</summary>
    private static int InstantsOf(CanonicalArena arena, int buckets, DType type, int count, Dictionary<long, long> instants)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> carried = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        ReadOnlySpan<long> values = arena.GetNode(buckets).Values.Cast<long>();
        for (int r = 0; r < count; r++)
        {
            carried[r] = instants.TryGetValue(values[r], out long first) ? first : values[r];
        }

        return arena.AddPrimitive(type, count, Validity.NonNullable, PType.I64, buffer);
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
    /// its slices span, and the first and last of them. A slice is its own bucket; an hour holds the slices
    /// that start in it, which a slice wider than an hour carries past it; the object holds every slice.
    /// </summary>
    private static IEnumerable<(long Bucket, long Start, long End, int First, int Last)> Buckets(SynopsisBucket kind, IReadOnlyList<TimeSlice> slices, TimeUnit unit)
    {
        switch (kind)
        {
            case SynopsisBucket.Slice:
                for (int s = 0; s < slices.Count; s++)
                {
                    yield return (slices[s].Start, slices[s].Start, slices[s].End, s, s);
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

                    yield return (hour, slices[s].Start, end, s, last);
                    s = last + 1;
                }

                break;
            default:
                yield return (0, slices[0].Start, slices[^1].End, 0, slices.Count - 1);
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
