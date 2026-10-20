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
/// <param name="table">The table read.</param>
/// <remarks>
/// The buckets are held in order, as an object's slices come: a few an object, searched where a set of them was a
/// set to build for each object and a hash for each row of its tables.
/// </remarks>
internal sealed class SynopsisPart(SynopsisTableInfo table)
{
    private long[] _kept = [];
    private int _count;

    /// <summary>The table read.</summary>
    internal SynopsisTableInfo Table { get; } = table;

    /// <summary>Its buckets folded, ascending.</summary>
    internal ReadOnlySpan<long> Kept => _kept.AsSpan(0, _count);

    /// <summary>How many buckets it folds.</summary>
    internal int KeptCount => _count;

    /// <summary>
    /// The instant a folded bucket's states carry where it is not the bucket's own value: the first the
    /// object holds in it. A bucket is named by its start, which a slice wider than the object's rows puts
    /// before them, and an object's bucket by zero, while a key of time is one value over the instants held.
    /// Null while every bucket carries its own.
    /// </summary>
    internal Dictionary<long, long>? Instants { get; private set; }

    /// <summary>The fields of the table's rows read: every one, or those the answer reads of them (<see cref="SynopsisAnswer.Project"/>).</summary>
    internal Layouts.FieldMask Fields { get; set; }

    /// <summary>Folds <paramref name="bucket"/>, its states carrying <paramref name="first"/>, the first instant the object holds in it.</summary>
    internal void Fold(long bucket, long first)
    {
        // In order as they come, a bucket coming twice held once.
        int at = Kept.BinarySearch(bucket);
        if (at < 0)
        {
            at = ~at;
            if (_count == _kept.Length)
            {
                Array.Resize(ref _kept, Math.Max(4, 2 * _count));
            }

            _kept.AsSpan(at, _count - at).CopyTo(_kept.AsSpan(at + 1));
            _kept[at] = bucket;
            _count++;
        }

        if (first != bucket)
        {
            (Instants ??= [])[bucket] = first;
        }
    }

    /// <summary>Whether the part folds <paramref name="bucket"/>.</summary>
    internal bool Holds(long bucket) => Kept.BinarySearch(bucket) >= 0;

    /// <summary>Whether <paramref name="chunk"/> holds a bucket the part folds.</summary>
    internal bool Folds(TableChunk chunk)
    {
        int at = Kept.BinarySearch(chunk.FirstBucket);
        at = at < 0 ? ~at : at;
        return at < _count && _kept[at] <= chunk.LastBucket;
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
    IReadOnlyList<(VortexExpr Part, VortexExpr Kept)> Excluded) : ISynopsisReads
{
    /// <summary>
    /// The keys of the question no table holds but the object holds one value of, its partition columns: each a
    /// column of that value beside the table's own in every batch of states.
    /// </summary>
    internal IReadOnlyList<(string Name, FilterLiteral Value)> Constants { get; init; } = [];

    /// <summary>
    /// The paths of a variant the question reads, by the name their table gives them, <c>Attributes["route"]</c>, and
    /// the name the question gives them, its text: the columns of their values and of their states are renamed so in
    /// every batch, where the keys, the filter and the measures find them. Null when the question reads none.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? Renames { get; init; }

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
    /// <param name="partition">
    /// The one value each partition column holds in the object: a conjunct on them alone is true or false for every
    /// row, a false one leaving nothing to fold; null without partition columns.
    /// </param>
    internal static SynopsisAnswer? Of(
        SynopsisContents contents, DType schema, string instant, StateRequest request, VortexExpr? filter, (long Least, long Greatest)? held = null,
        IReadOnlyList<string>? identifiers = null, IReadOnlyDictionary<string, FilterLiteral>? partition = null) =>
        Of(new SynopsisQuestion(request, filter, instant, identifiers), contents, schema, held, partition);

    /// <summary>
    /// The answer of the object of <paramref name="contents"/> to <paramref name="question"/>, its filter, keys and
    /// measures sorted once for every object it asks (<see cref="SynopsisQuestion"/>): an object's answer builds its
    /// parts, its buckets and its edges, and no set, list or closure besides.
    /// </summary>
    internal static SynopsisAnswer? Of(
        SynopsisQuestion question, SynopsisContents contents, DType schema, (long Least, long Greatest)? held = null,
        IReadOnlyDictionary<string, FilterLiteral>? partition = null)
    {
        string instant = question.Instant;
        StateRequest request = question.Request;
        if (contents.Tables.Count == 0 || contents.Slices.Slices.Count == 0 || schema.IndexOfField(instant) < 0)
        {
            return null;
        }

        // The filter, as conjuncts: bounds on the instant make the window, the rest must read keys alone.
        long low = long.MinValue;
        long high = long.MaxValue;
        VortexExpr[] conjuncts = question.Conjuncts;
        Span<int> onKeys = conjuncts.Length <= MaxFields ? stackalloc int[MaxFields] : new int[conjuncts.Length];
        int onKeyCount = 0;
        ulong joined = 0;
        List<(VortexExpr, VortexExpr)>? excluded = null;
        FieldSet keyed = new FieldSet(stackalloc int[MaxFields]);
        Dictionary<string, string>? renames = null;
        for (int c = 0; c < conjuncts.Length; c++)
        {
            VortexExpr conjunct = conjuncts[c];
            if (partition is not null && PartitionFilter.Decided(conjunct, partition) is { } holds)
            {
                if (!holds)
                {
                    return new SynopsisAnswer([], [], null, request, []);
                }

                continue;
            }

            if (Exclusion(conjunct, question.Identifiers, schema) is { } part)
            {
                (excluded ??= []).Add((part, conjunct));
                continue;
            }

            string[] columns = question.Columns[c];
            if (Array.IndexOf(columns, instant) >= 0)
            {
                if (columns.Length != 1 || !Bound(conjunct, instant, ref low, ref high))
                {
                    return null;
                }

                continue;
            }

            if (!(question.ReadsPaths[c] ? KeyedBy(conjunct, contents, schema, ref keyed, ref renames) : KeyedBy(columns, schema, ref keyed)))
            {
                return null;
            }

            onKeys[onKeyCount++] = c;
            joined |= c < 64 ? 1UL << c : 0;
        }

        // The keys: a column a table keys, or functions of one; a function of the instant, a key of time; a partition
        // column, one value over the object.
        List<(string, FilterLiteral)>? constants = null;
        for (int k = 0; k < request.Keys.Count; k++)
        {
            FieldExpr key = request.Keys[k];
            if (key.GetType() == typeof(FieldExpr) && partition is not null && partition.TryGetValue(key.Path, out FilterLiteral constant))
            {
                (constants ??= []).Add((key.Path, constant));
                continue;
            }

            if (key is VariantFieldExpr path)
            {
                if (Counted(contents, schema, path, ref renames) is not { } entry || !keyed.Add(entry))
                {
                    return null;
                }

                continue;
            }

            FieldExpr column = key;
            while (column is FunctionFieldExpr function)
            {
                column = function.Column;
            }

            // A key of time is one of the question's, which the fates of the buckets read.
            if (string.Equals(column.Path, instant, StringComparison.Ordinal))
            {
                if (key is not FunctionFieldExpr)
                {
                    return null;
                }

                continue;
            }

            int field = schema.IndexOfField(column.Path);
            if (field < 0 || !keyed.Add(field))
            {
                return null;
            }
        }

        FieldSet measured = new FieldSet(stackalloc int[MaxFields]);
        for (int i = 0; i < request.Inputs.Count; i++)
        {
            if (request.InputFields[i] is VariantFieldExpr path)
            {
                if (Counted(contents, schema, path, ref renames, measure: true) is not { } entry || !measured.Add(entry))
                {
                    return null;
                }
            }
            else if (request.Inputs[i] is { } input)
            {
                int field = schema.IndexOfField(input);
                if (field < 0 || !measured.Add(field))
                {
                    return null;
                }
            }
        }

        // The tables that hold the keys and the measures: the one of fewest rows answers the buckets the
        // window keeps whole, the runs of the slices of fewest rows the slices inside the buckets it cuts,
        // and the slices cut are read as rows. Without a table of slices, a bucket cut is read as rows.
        SynopsisTableInfo? coarsest = null;
        SynopsisTableInfo? sliced = null;
        for (int t = 0; t < contents.Tables.Count; t++)
        {
            SynopsisTableInfo table = contents.Tables[t].Table;
            if (Holds(table.Keys, keyed.Fields) && Holds(table.Measures, measured.Fields))
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
        TimeUnit unit = contents.Slices.Unit;
        SynopsisPart whole = new SynopsisPart(coarsest);
        SynopsisPart? runs = sliced is null || ReferenceEquals(sliced, coarsest) ? null : new SynopsisPart(sliced);
        List<TimeSlice>? edges = null;
        Window window = new Window(
            held?.Least ?? long.MinValue, held is { Greatest: < long.MaxValue } bounds ? bounds.Greatest + 1 : long.MaxValue, low, high, question.Times);
        for (int s = 0; s < slices.Count;)
        {
            (long bucket, long start, long end, int first, int last) = BucketAt(coarsest.Bucket, slices, unit, s);
            s = last + 1;
            switch (window.FateOf(start, end))
            {
                case BucketFate.Kept:
                    whole.Fold(bucket, window.Carried(bucket, Math.Max(start, window.Least)));
                    break;
                case BucketFate.Edge:
                    for (int e = first; e <= last; e++)
                    {
                        BucketFate fate = coarsest.Bucket == SynopsisBucket.Slice ? BucketFate.Edge : window.FateOf(slices[e].Start, slices[e].End);
                        if (fate == BucketFate.Kept && runs is not null)
                        {
                            runs.Fold(slices[e].Start, window.Carried(slices[e].Start, Math.Max(slices[e].Start, window.Least)));
                        }
                        else if (fate != BucketFate.Out)
                        {
                            (edges ??= []).Add(slices[e]);
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        // An object whose every row the edges hold is read as rows, with no table.
        int count = (whole.KeptCount > 0 ? 1 : 0) + (runs is { KeptCount: > 0 } ? 1 : 0);
        if (count == 0 && edges is not null)
        {
            return null;
        }

        SynopsisPart[] parts = count == 0 ? [] : new SynopsisPart[count];
        if (whole.KeptCount > 0)
        {
            parts[0] = whole;
        }

        if (runs is { KeptCount: > 0 })
        {
            parts[count - 1] = runs;
        }

        return new SynopsisAnswer(
            parts, (IReadOnlyList<TimeSlice>?)edges ?? [], onKeyCount == 0 ? null : question.KeyFilter(joined, onKeys[..onKeyCount]), request,
            (IReadOnlyList<(VortexExpr, VortexExpr)>?)excluded ?? [])
        {
            Constants = (IReadOnlyList<(string, FilterLiteral)>?)constants ?? [],
            Renames = renames,
        };
    }

    /// <summary>The keys, filters and measures an answer holds at most, the fields it reads each held once.</summary>
    private const int MaxFields = 64;

    /// <summary>A few fields, each held once, in a span its holder lends.</summary>
    private ref struct FieldSet(Span<int> held)
    {
        private readonly Span<int> _held = held;
        private int _count;

        /// <summary>The fields held.</summary>
        internal readonly ReadOnlySpan<int> Fields => _held[.._count];

        /// <summary>Holds <paramref name="field"/>; false when the span is full.</summary>
        internal bool Add(int field)
        {
            if (Fields.Contains(field))
            {
                return true;
            }

            if (_count == _held.Length)
            {
                return false;
            }

            _held[_count++] = field;
            return true;
        }
    }

    /// <summary>
    /// The window an object's buckets meet: the instants its rows hold, from <see cref="Least"/> to before
    /// <see cref="Past"/>, the filter's bounds, and the question's keys of time.
    /// </summary>
    private readonly record struct Window(long Least, long Past, long Low, long High, FunctionFieldExpr[] Times)
    {
        /// <summary>The fate of the bucket of instants [<paramref name="start"/>, <paramref name="end"/>), cut to the instants the object holds.</summary>
        internal BucketFate FateOf(long start, long end) =>
            Math.Max(start, Least) >= Math.Min(end, Past) ? BucketFate.Out : Fate(Math.Max(start, Least), Math.Min(end, Past), Low, High, Times);

        /// <summary>
        /// The instant a kept bucket's states carry: the bucket's own value where every key of time maps it as it maps
        /// <paramref name="first"/>, the first instant the object holds in it, as it does for an hour's object keyed
        /// by its day; <paramref name="first"/> where one does not, and the column of instants is made again.
        /// </summary>
        internal long Carried(long bucket, long first)
        {
            if (first == bucket)
            {
                return bucket;
            }

            foreach (FunctionFieldExpr time in Times)
            {
                if (Image(time, bucket) is not { } own || Image(time, first) is not { } held || own != held)
                {
                    return first;
                }
            }

            return bucket;
        }
    }

    /// <summary>
    /// Adds to <paramref name="keyed"/> the columns a predicate on keys reads, <paramref name="columns"/>, each a
    /// table's key; false when one is no column of the schema.
    /// </summary>
    private static bool KeyedBy(string[] columns, DType schema, ref FieldSet keyed)
    {
        foreach (string column in columns)
        {
            int field = schema.IndexOfField(column);
            if (field < 0 || !keyed.Add(field))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Adds to <paramref name="keyed"/> what a predicate on keys reads, each a table's key: the columns, and the paths
    /// of a variant a table counts, evaluated on the table's rows as columns are, through <c>and</c>, <c>or</c> and
    /// <c>not</c>; false when it reads a column or a path no table could key.
    /// </summary>
    private static bool KeyedBy(VortexExpr predicate, SynopsisContents contents, DType schema, ref FieldSet keyed, ref Dictionary<string, string>? renames)
    {
        switch (predicate)
        {
            case LogicalExpr logical:
                return KeyedBy(logical.Left, contents, schema, ref keyed, ref renames) && KeyedBy(logical.Right, contents, schema, ref keyed, ref renames);
            case NotExpr not:
                return KeyedBy(not.Operand, contents, schema, ref keyed, ref renames);
            case VariantFilterExpr:
                return PathOf(predicate) is { } path && Counted(contents, schema, path, ref renames) is { } entry && keyed.Add(entry);
            default:
                HashSet<string> columns = new(StringComparer.Ordinal);
                predicate.CollectFields(columns);
                return KeyedBy([.. columns], schema, ref keyed);
        }
    }

    /// <summary>The path of a variant a predicate behind a path's fence reads, alone; null for any other predicate.</summary>
    private static VariantFieldExpr? PathOf(VortexExpr conjunct) => conjunct is VariantFilterExpr { Inner: { } inner }
        ? inner switch
        {
            ComparisonExpr comparison => comparison.Field as VariantFieldExpr,
            NullCheckExpr check => check.Field as VariantFieldExpr,
            InExpr set => set.Field as VariantFieldExpr,
            TypedInExpr set => set.Field as VariantFieldExpr,
            StringMatchExpr match => match.Field as VariantFieldExpr,
            _ => null,
        }
        : null;

    /// <summary>
    /// The entry of the object's synopsis path <paramref name="path"/> reads: its column, its key and the class of
    /// what it reads, whose name in a table is renamed to the path's text in <paramref name="renames"/>; null when the
    /// object counts no such path, or the path is not one a synopsis counts. A path the object leaves in the residual
    /// is counted over the rows that hold it alone, so it answers as a measure, never as a key or in a filter
    /// (<paramref name="measure"/>): its tables have no group for the rows without it.
    /// </summary>
    private static int? Counted(SynopsisContents contents, DType schema, VariantFieldExpr path, ref Dictionary<string, string>? renames, bool measure = false)
    {
        if (path.CountedClass is not { } @class)
        {
            return null;
        }

        int field = schema.IndexOfField(path.Column.Path);
        string key = path.Steps[0].Key!;
        IReadOnlyList<SynopsisPath> paths = contents.Slices.Paths;
        for (int p = 0; p < paths.Count; p++)
        {
            if (paths[p].Field == field && paths[p].Class == @class && string.Equals(paths[p].Key, key, StringComparison.Ordinal))
            {
                // A path left in the residual counts only the rows that hold it, so it answers as a measure
                // alone; one of doubles leaves out the floats AsDouble reads, so it answers where it holds none.
                if ((paths[p].Residual && !measure) || paths[p].Floats)
                {
                    return null;
                }

                int entry = SynopsisPath.Entry(p);
                renames ??= new Dictionary<string, string>(StringComparer.Ordinal);
                renames[SynopsisTableInfo.NameOf(entry, schema, paths)] = path.Text;
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// A column of a table's rows by the name the question reads it by: a path's, or one of its states', renamed to
    /// the path's text (<see cref="Renames"/>), any other as it is.
    /// </summary>
    private string Renamed(string name)
    {
        if (Renames is null)
        {
            return name;
        }

        foreach ((string table, string text) in Renames)
        {
            if (name.StartsWith(table, StringComparison.Ordinal) && (name.Length == table.Length || name[table.Length] == '.'))
            {
                return text + name[table.Length..];
            }
        }

        return name;
    }

    /// <summary>
    /// The part a conjunct leaves out when it is the complement of one value of an identifier under
    /// three-valued logic, <c>id != v || id == null</c> in either order, or <c>id != v</c> alone on an identifier
    /// the schema holds no null in: the equality <c>id = v</c>, whose rows the identifier's index finds; null for
    /// any other conjunct.
    /// </summary>
    private static VortexExpr? Exclusion(VortexExpr conjunct, IReadOnlyList<string>? identifiers, DType schema)
    {
        if (identifiers is not { Count: > 0 })
        {
            return null;
        }

        ComparisonExpr? other;
        if (conjunct is LogicalExpr { IsAnd: false } or)
        {
            (VortexExpr a, VortexExpr b) = (or.Left, or.Right);
            if (a is NullCheckExpr)
            {
                (a, b) = (b, a);
            }

            other = a as ComparisonExpr;
            if (b is not NullCheckExpr { IsNull: true } missing || other is null
                || missing.Field.GetType() != typeof(FieldExpr) || !string.Equals(other.Field.Path, missing.Field.Path, StringComparison.Ordinal))
            {
                return null;
            }
        }
        else
        {
            // Alone, the comparison drops the nulls too: the complement only where there are none.
            other = conjunct as ComparisonExpr;
            int field = other is null ? -1 : schema.IndexOfField(other.Field.Path);
            if (field < 0 || schema.GetField(field).IsNullable)
            {
                return null;
            }
        }

        if (other is { Op: ComparisonOp.NotEqual } && other.Value.Kind != FilterLiteralKind.Null && other.Field.GetType() == typeof(FieldExpr))
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

    /// <summary>
    /// <paramref name="table"/>'s row type and the names of its fields as its contents keep them, read once; a default
    /// type and null when the contents hold no such table, or names of another width.
    /// </summary>
    private static (DType Row, string[]? Names) RowOf(SynopsisContents contents, SynopsisTableInfo table)
    {
        for (int t = 0; t < contents.Tables.Count; t++)
        {
            if (ReferenceEquals(contents.Tables[t].Table, table))
            {
                (_, DType[] rows, string[][] names) = contents.TableRowTypes;
                return names[t].Length == rows[t].FieldCount ? (rows[t], names[t]) : (rows[t], null);
            }
        }

        return (default, null);
    }

    /// <summary>Whether <paramref name="chunk"/> of <paramref name="table"/> holds a bucket the answer folds, which the read of the synopsis fetches.</summary>
    /// <remarks>
    /// By index: asked of every chunk of every table of an object, an enumerator of the parts through their interface
    /// was an allocation a chunk, a quarter of what a warm question from the tables allocated.
    /// </remarks>
    internal bool Reads(SynopsisTableInfo table, TableChunk chunk)
    {
        for (int p = 0; p < Parts.Count; p++)
        {
            SynopsisPart part = Parts[p];
            if (ReferenceEquals(part.Table, table) && part.Folds(chunk))
            {
                return true;
            }
        }

        return false;
    }

    bool ISynopsisReads.Reads(SynopsisTableInfo table, TableChunk chunk) => Reads(table, chunk);

    /// <summary>The fields of <paramref name="table"/>'s rows its part reads (<see cref="Project"/>); every one of a table no part reads.</summary>
    Layouts.FieldMask ISynopsisReads.Fields(SynopsisTableInfo table)
    {
        for (int p = 0; p < Parts.Count; p++)
        {
            if (ReferenceEquals(Parts[p].Table, table))
            {
                return Parts[p].Fields;
            }
        }

        return Layouts.FieldMask.All;
    }

    /// <summary>
    /// Has each part read only the fields of its table's rows the batches of states are read by: the bucket, the rows
    /// each row stands for, the columns of the keys and of the filter on them, and the states of each measure the request
    /// folds, where a table holds 16 columns for the 3 a count by one key reads. The rows of a table a deletion or an
    /// identifier corrects are read whole, every key and state of a row taken out; so are those of a table whose paths
    /// the question reads by their text (<see cref="Renames"/>).
    /// </summary>
    internal void Project(SynopsisContents contents)
    {
        if (Renames is not null)
        {
            return;
        }

        // The names the batches are read by, the question's: a key's column under its functions, a filter's. The set is
        // the process's one, taken for the answer and given back cleared; one another answer holds leaves this one its own.
        HashSet<string> read = System.Threading.Interlocked.Exchange(ref s_read, null) ?? new HashSet<string>(StringComparer.Ordinal);
        read.Add(StateRequest.RowsColumn);
        for (int k = 0; k < Request.Keys.Count; k++)
        {
            FieldExpr column = Request.Keys[k];
            while (column is FunctionFieldExpr function)
            {
                column = function.Column;
            }

            read.Add(column.Path);
        }

        KeyFilter?.CollectFields(read);
        Span<int> wanted = stackalloc int[ProjectedFields];
        for (int p = 0; p < Parts.Count; p++)
        {
            SynopsisPart part = Parts[p];
            if (RowOf(contents, part.Table) is not (_, { } names) || names.Length > ProjectedFields)
            {
                continue;
            }

            int count = 0;
            for (int f = 0; f < names.Length; f++)
            {
                if (f == 0 || read.Contains(names[f]) || Measured(names[f]))
                {
                    wanted[count++] = f;
                }
            }

            part.Fields = count < names.Length ? Layouts.FieldMasks.Of(wanted[..count]) : Layouts.FieldMask.All;
        }

        read.Clear();
        System.Threading.Volatile.Write(ref s_read, read);
    }

    /// <summary>The fields of a table past which its rows are read whole: a table's are its keys, a count and five to eight states a measure.</summary>
    private const int ProjectedFields = 256;

    /// <summary>The names <see cref="Project"/> reads a table's fields by, held between answers.</summary>
    private static HashSet<string>? s_read;

    /// <summary>Whether <paramref name="name"/> is a state of a measure the request folds: its name, a dot, then the state's.</summary>
    private bool Measured(string name)
    {
        for (int i = 0; i < Request.Inputs.Count; i++)
        {
            if (Request.Inputs[i] is { } input && name.Length > input.Length && name[input.Length] == '.' && name.StartsWith(input, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where the table's field <paramref name="field"/> lies among the <paramref name="width"/> fields <paramref name="fields"/> holds; -1 for none.</summary>
    private static int Position(Layouts.FieldMask fields, int field, int width)
    {
        if (fields.IsAll)
        {
            return field < width ? field : -1;
        }

        for (int s = 0; s < width; s++)
        {
            if (fields.SelectedField(s) == field)
            {
                return s;
            }
        }

        return -1;
    }

    /// <summary>
    /// The batches of states of <paramref name="part"/>'s table, one for each chunk read into <paramref name="tables"/>
    /// that holds a bucket the part folds: its bucket named as the instant, its keys and states as they lie, and
    /// selected the rows of the part's buckets the key filter holds for, but a row whose every row was taken out,
    /// whose group the rows would not make. Added to <paramref name="into"/>.
    /// </summary>
    /// <param name="tables">The tables, read.</param>
    /// <param name="part">The table and its buckets.</param>
    /// <param name="contents">The object's table of contents, which says the chunks the part folds.</param>
    /// <param name="instant">The dataset's instant, whose name the buckets take.</param>
    /// <param name="startRow">The object's first row in the dataset.</param>
    /// <param name="into">Where the batches go.</param>
    /// <param name="removed">
    /// Rows to take out of the table's states first, a delete's or those a question excludes (see
    /// <see cref="SynopsisCorrection"/>); null for none.
    /// </param>
    /// <param name="schema">The object's schema, which names the measures of the rows taken out.</param>
    /// <param name="slices">The object's slice table, which places a row taken out in its bucket.</param>
    /// <returns>False when a chunk the part folds was not read, or a row cannot come out exactly.</returns>
    internal bool TryBatches(
        SynopsisTableSet tables, SynopsisPart part, SynopsisContents contents, string instant, long startRow, List<RecordBatch> into,
        IReadOnlyList<RecordBatch>? removed = null, DType schema = default, SliceTable? slices = null)
    {
        if (tables.Types is not { } types)
        {
            return false;
        }

        // The chunks the part folds, each of which must have been read: one whose region failed its checksum is not.
        int table = -1;
        int wanted = 0;
        for (int t = 0; t < contents.Tables.Count; t++)
        {
            (SynopsisTableInfo info, IReadOnlyList<TableChunk> chunks) = contents.Tables[t];
            if (ReferenceEquals(info, part.Table))
            {
                table = t;
                for (int c = 0; c < chunks.Count; c++)
                {
                    wanted += part.Folds(chunks[c]) ? 1 : 0;
                }
            }
        }

        // Every chunk read of the table holds the same fields, those the read projected or every one.
        CanonicalArena arena = tables.Arena;
        int read = 0;
        DType rowType = default;
        Layouts.FieldMask projected = Layouts.FieldMask.All;
        for (int t = 0; t < tables.Tables.Count; t++)
        {
            ReadTable chunk = tables.Tables[t];
            if (!ReferenceEquals(chunk.Table, part.Table) || !part.Folds(chunk.Chunk))
            {
                continue;
            }

            CanonicalNode rows = arena.GetNode(chunk.Node);
            if (read > 0 && (!chunk.Fields.Is(projected) || rows.DType.FieldCount != rowType.FieldCount))
            {
                return false;
            }

            rowType = rows.DType;
            projected = chunk.Fields;
            read++;
        }

        if (read != wanted)
        {
            return false;
        }

        if (wanted == 0)
        {
            return true;
        }

        // A row comes out of a table by every key and state of it: the table's rows read whole, and the columns of
        // each chunk those the correction made in their place.
        List<int[]>? corrected = null;
        if (removed is { Count: > 0 })
        {
            corrected = ChunkFields(tables, part);
            if (!projected.IsAll || slices is null
                || !SynopsisCorrection.TryRemove(arena, part.Table, rowType, corrected, schema, removed, slices.Slices, slices.Unit, part, Request.Reads))
            {
                return false;
            }
        }

        int width = rowType.FieldCount;
        (DType full, string[]? fieldNames) = RowOf(contents, part.Table);
        int bucketAt = Position(projected, 0, width);
        int stoodAt = Position(projected, part.Table.Keys.Length + 1, width);
        if (bucketAt != 0 || stoodAt < 0 || (!projected.IsAll && (full.IsDefault || fieldNames is null)))
        {
            return false;
        }

        // The partition's values, a column each beside the table's, of the schema's types in the contents' arena.
        int extra = Constants.Count;
        Span<int> constantColumns = extra <= 16 ? stackalloc int[16] : new int[extra];
        DType[]? constantTypes = extra == 0 ? null : new DType[extra];
        for (int c = 0; c < extra; c++)
        {
            int column = schema.IsDefault ? -1 : schema.IndexOfField(Constants[c].Name);
            if (column < 0)
            {
                return false;
            }

            constantColumns[c] = column;
            lock (types)
            {
                constantTypes![c] = DTypeImport.Into(types, schema.GetField(column));
            }
        }

        // The batch's struct: the table's columns renamed as the question reads them, then the partition's values. A
        // field's name is the contents' own, read once with the table's row type, where a name read from a dtype is a
        // string made at each read; its type is the contents' too, where the struct is built, a projection's struct
        // lying in the arena of the context that decoded it. The types are the object's contents', which every
        // question over it shares: what adds to them, once for each shape a question asks, does so alone, and the
        // arena keeps a node it holds where it lies, so its readers take no lock. A struct of the table's columns
        // alone the contents keep for the next answer read under the same fields.
        bool keeps = extra == 0 && Renames is null && table >= 0;
        DType bucketType = projected.IsAll ? rowType.GetField(0) : full.GetField(0);
        if (!keeps || !contents.TryStatesStruct(table, projected, instant, out DType root))
        {
            string[] names = new string[width + extra];
            DType[] dtypes = new DType[names.Length];
            for (int f = 0; f < width; f++)
            {
                int field = projected.IsAll ? f : projected.SelectedField(f);
                names[f] = f == 0 ? instant : Renamed(fieldNames?[field] ?? rowType.GetFieldName(f));
                dtypes[f] = projected.IsAll ? rowType.GetField(f) : full.GetField(field);
            }

            for (int c = 0; c < extra; c++)
            {
                names[width + c] = Constants[c].Name;
                dtypes[width + c] = constantTypes![c];
            }

            lock (types)
            {
                root = types.Struct(names, dtypes, Nullability.NonNullable);
            }

            if (keeps)
            {
                contents.KeepStatesStruct(table, projected, instant, root);
            }
        }

        Span<int> fields = width + extra <= 64 ? stackalloc int[64] : new int[width + extra];
        fields = fields[..(width + extra)];
        int made = 0;
        for (int t = 0; t < tables.Tables.Count; t++)
        {
            ReadTable chunk = tables.Tables[t];
            if (!ReferenceEquals(chunk.Table, part.Table) || !part.Folds(chunk.Chunk))
            {
                continue;
            }

            if (corrected is not null)
            {
                corrected[made++].AsSpan().CopyTo(fields);
            }
            else
            {
                CanonicalNode rows = arena.GetNode(chunk.Node);
                for (int f = 0; f < width; f++)
                {
                    fields[f] = rows.GetFieldIndex(f);
                }
            }

            int count = arena.GetNode(fields[0]).Length;

            // The partition's one value, a column of the table's rows.
            for (int c = 0; c < extra; c++)
            {
                if (ConstantColumn(arena, constantTypes![c], count, Constants[c].Value) is not { } constant)
                {
                    return false;
                }

                fields[width + c] = constant;
            }

            int bucketed = fields[0];
            ReadOnlySpan<long> buckets = arena.GetNode(bucketed).Values.Cast<long>()[..count];
            if (count > 0)
            {
                fields[0] = InstantsOf(arena, bucketed, buckets, RunsOf(buckets), bucketType, part.Instants);
            }

            int node = arena.AddStruct(root, count, Validity.NonNullable, fields);

            // The rows of the buckets kept, then those the filter on the keys holds for, but a row whose
            // every row was taken out, whose group the rows would not make.
            ReadOnlySpan<long> stood = arena.GetNode(fields[stoodAt]).Values.Cast<long>();
            int words = Math.Max((count + 63) >> 6, 1);
            VortexBuffer selection = arena.Allocate(words * sizeof(ulong), sizeof(ulong), out Span<byte> bits);
            bits.Clear();
            Span<ulong> kept = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bits);
            Span<byte> states = default;
            if (KeyFilter is { } keyFilter)
            {
                _ = arena.Allocate(count, 1, out states);
                states.Clear();
                FilterEvaluator.Evaluate(keyFilter, arena, node, count, states);
            }

            // The rows lie in the order of their buckets: a bucket's verdict is taken once for all its rows, and a row
            // of a bucket kept is kept where it stands for a row and the filter holds for it, with no branch.
            int selected = 0;
            for (int start = 0; start < count;)
            {
                int end = start + 1;
                while (end < count && buckets[end] == buckets[start])
                {
                    end++;
                }

                if (part.Holds(buckets[start]))
                {
                    for (int r = start; r < end; r++)
                    {
                        ulong bit = (ulong)(-stood[r]) >> 63;
                        bit &= states.IsEmpty || states[r] == Trilean.True ? 1UL : 0UL;
                        kept[r >> 6] |= bit << (r & 63);
                        selected += (int)bit;
                    }
                }

                start = end;
            }

            RecordBatch batch = new RecordBatch(arena, node, startRow) { CarriesStates = true };
            batch.Select(selection, selected);
            into.Add(batch);
        }

        return true;
    }

    /// <summary>The fields of every chunk read of <paramref name="part"/>'s table that holds a bucket it folds, as their nodes, which a correction reads.</summary>
    private static List<int[]> ChunkFields(SynopsisTableSet tables, SynopsisPart part)
    {
        List<int[]> chunkFields = [];
        for (int t = 0; t < tables.Tables.Count; t++)
        {
            ReadTable chunk = tables.Tables[t];
            if (ReferenceEquals(chunk.Table, part.Table) && part.Folds(chunk.Chunk))
            {
                CanonicalNode rows = tables.Arena.GetNode(chunk.Node);
                int[] stated = new int[rows.DType.FieldCount];
                for (int f = 0; f < stated.Length; f++)
                {
                    stated[f] = rows.GetFieldIndex(f);
                }

                chunkFields.Add(stated);
            }
        }

        return chunkFields;
    }

    /// <summary>
    /// A column of <paramref name="rows"/> rows of one value, at <paramref name="type"/>: a text or a binary value's
    /// bytes, a primitive's in its width; null for any other type, which no partition column of this library has.
    /// </summary>
    private static int? ConstantColumn(CanonicalArena arena, DType type, int rows, FilterLiteral value)
    {
        Validity valid = type.IsNullable ? Validity.AllValid : Validity.NonNullable;
        if (type.Kind is DTypeKind.Utf8 or DTypeKind.Binary && value.Kind == FilterLiteralKind.Bytes)
        {
            return arena.AddConstant(type, rows, valid, value.BytesValue);
        }

        if (type.Kind == DTypeKind.Primitive && value.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned or FilterLiteralKind.Bool)
        {
            Span<byte> element = stackalloc byte[16];
            int width = type.PType.ByteWidth();
            Writing.ZoneMapWriter.Write(element[..width], type.PType, value);
            return arena.AddConstant(type, rows, valid, element[..width]);
        }

        return null;
    }

    /// <summary>
    /// The instants a table's rows carry as states, as the batch's column: each bucket's own value, or the first instant
    /// the object holds in it (<paramref name="instants"/>). The rows lie in the order of their buckets, a run each: a
    /// chunk of one bucket carries one value over its rows, and one of few a value a run, which a key of time maps and
    /// groups once a value, where a column of every row's had its function applied and its value looked up at each;
    /// <paramref name="runs"/> runs of equal buckets in all.
    /// </summary>
    private static int InstantsOf(CanonicalArena arena, int buckets, ReadOnlySpan<long> values, int runs, DType type, Dictionary<long, long>? instants)
    {
        int count = values.Length;
        if (runs == 1)
        {
            long carried = Carried(values[0], instants);
            return arena.AddConstant(type, count, Validity.NonNullable, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<long>(in carried)));
        }

        if (runs <= count / 4)
        {
            VortexBuffer ends = arena.AllocateUninitialized(runs * sizeof(uint), sizeof(uint), out Span<byte> endBytes);
            VortexBuffer carried = arena.AllocateUninitialized(runs * sizeof(long), sizeof(long), out Span<byte> valueBytes);
            Span<uint> end = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(endBytes);
            Span<long> value = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(valueBytes);
            int run = 0;
            for (int r = 1; r <= count; r++)
            {
                if (r == count || values[r] != values[r - 1])
                {
                    end[run] = (uint)r;
                    value[run] = Carried(values[r - 1], instants);
                    run++;
                }
            }

            return arena.AddRunEnd(type, count, Validity.NonNullable, ends, arena.AddPrimitive(type, runs, Validity.NonNullable, PType.I64, carried));
        }

        if (instants is not { Count: > 0 })
        {
            return buckets;
        }

        VortexBuffer buffer = arena.AllocateUninitialized(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> rows = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int r = 0; r < count; r++)
        {
            rows[r] = r > 0 && values[r] == values[r - 1] ? rows[r - 1] : Carried(values[r], instants);
        }

        return arena.AddPrimitive(type, count, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>The instant a bucket's states carry: the first the object holds in it where <paramref name="instants"/> says, its own otherwise.</summary>
    private static long Carried(long bucket, Dictionary<long, long>? instants) =>
        instants is not null && instants.TryGetValue(bucket, out long first) ? first : bucket;

    /// <summary>How many runs of equal values <paramref name="values"/> holds.</summary>
    private static int RunsOf(ReadOnlySpan<long> values)
    {
        int runs = values.IsEmpty ? 0 : 1;
        for (int r = 1; r < values.Length; r++)
        {
            runs += values[r] != values[r - 1] ? 1 : 0;
        }

        return runs;
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
    private static BucketFate Fate(long start, long end, long low, long high, FunctionFieldExpr[] times)
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
    /// The bucket of a table over <paramref name="slices"/> that starts at slice <paramref name="first"/>: its value in
    /// the table's rows, the instants its slices span, and the first and last of them, the next bucket starting past
    /// the last. A slice is its own bucket; an hour holds the slices that start in it, which a slice wider than an hour
    /// carries past it; the object holds every slice.
    /// </summary>
    private static (long Bucket, long Start, long End, int First, int Last) BucketAt(SynopsisBucket kind, IReadOnlyList<TimeSlice> slices, TimeUnit unit, int first)
    {
        switch (kind)
        {
            case SynopsisBucket.Slice:
                return (slices[first].Start, slices[first].Start, slices[first].End, first, first);
            case SynopsisBucket.Hour:
                long hour = TimeSlicer.Floor(slices[first].Start, unit, SliceWidth.Hour);
                int last = first;
                long end = slices[first].End;
                while (last + 1 < slices.Count && TimeSlicer.Floor(slices[last + 1].Start, unit, SliceWidth.Hour) == hour)
                {
                    last++;
                    end = Math.Max(end, slices[last].End);
                }

                return (hour, slices[first].Start, end, first, last);
            default:
                return (0, slices[0].Start, slices[^1].End, 0, slices.Count - 1);
        }
    }


    /// <summary>Whether <paramref name="have"/> holds every field of <paramref name="need"/>.</summary>
    private static bool Holds(int[] have, ReadOnlySpan<int> need)
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
    internal static bool Bound(VortexExpr conjunct, string instant, ref long low, ref long high)
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
    internal static IEnumerable<VortexExpr> Conjuncts(VortexExpr? filter)
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
