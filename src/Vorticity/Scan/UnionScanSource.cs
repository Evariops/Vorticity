using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// Several sources of one schema read as one: their rows one after the other, in the order the sources are given, so that
/// a question over them answers what it would over a source holding every row. The engine asks it what it asks any source
/// (spec B, 8), and it asks each member in turn, or side by side: each member keeps its own pruning, its own structures
/// and its own choice of the cheapest answer of each of its units, and the engine folds what they give in one run.
/// </summary>
/// <remarks>
/// <para>
/// The rows are numbered on from one member to the next: member <c>m</c>'s row <c>r</c> is row <c>start(m) + r</c> of
/// the whole. A range of rows and a take by position are cut at the members' bounds, and each batch a member gives is
/// seen at its place in the whole through a view bound again per batch (<see cref="RecordBatch.RebindRebased"/>). A
/// member a range covers whole is asked without a range, as a scan of its own would ask it, so that what it answers
/// only for the whole of itself, a dataset's rollups, still answers.
/// </para>
/// <para>
/// No snapshot holds across members: each is read at the version its source was made at. No order holds across them
/// either, so the rows asked in the order of a column are asked of each member in that order and merged here
/// (<see cref="ExternalSort"/>), and a window of them is read late (<see cref="ReadsTopLate"/>), from the places each
/// member finds its own best rows at (<see cref="TopPositionsAsync"/>). A walk of keys has no walker over several
/// sources: the union has no keys.
/// </para>
/// </remarks>
internal sealed class UnionScanSource : ScanSource, IRowsCapability, IStatisticsCapability, IStatesCapability, ITasksCapability, ICostCapability
{
    private readonly ScanSource[] _members;

    // The first row of each member in the whole, and past the last the rows of the whole.
    private readonly long[] _starts;
    private readonly bool _states;

    /// <param name="members">The sources, in the order of their rows: one or more, of one session and one schema, each knowing its rows before a read.</param>
    /// <exception cref="ArgumentException">No source, sources of two sessions or two schemas, or one that does not know its rows.</exception>
    internal UnionScanSource(ReadOnlySpan<ScanSource> members)
    {
        if (members.IsEmpty)
        {
            throw new ArgumentException("A set reads one source or more.", nameof(members));
        }

        _members = members.ToArray();
        _starts = new long[_members.Length + 1];
        ScanSource first = _members[0];
        for (int m = 0; m < _members.Length; m++)
        {
            ScanSource member = _members[m];
            if (!ReferenceEquals(member.Session, first.Session))
            {
                throw new ArgumentException($"Source {m + 1} of the set reads under another session than the first: one question runs under one session's memory and lanes.", nameof(members));
            }

            if (!member.Schema.Equals(first.Schema))
            {
                throw new ArgumentException($"Source {m + 1} of the set has other columns than the first: the sources of a set have one schema.", nameof(members));
            }

            if (member.Statistics is not { RowBound: >= 0 } known)
            {
                throw new ArgumentException($"Source {m + 1} of the set does not know its rows before it is read, which a set numbers its rows by.", nameof(members));
            }

            _starts[m + 1] = checked(_starts[m] + known.RowBound);
            _states |= member.States is not null;
            SettlesBlocks |= member.Rows.SettlesBlocks;
            ReadsRoundTrips |= member.Cost?.ReadsRoundTrips ?? false;
        }
    }

    internal override VortexSchema Schema => _members[0].Schema;

    internal override VortexSession Session => _members[0].Session;

    internal override LogicalSource Logical => new LogicalSource(LogicalSourceKind.Union, (ulong)_members.Length);

    internal override IRowsCapability Rows => this;

    internal override IStatisticsCapability Statistics => this;

    /// <summary>Its, when a member gives states: the others give their rows beside them.</summary>
    internal override IStatesCapability? States => _states ? this : null;

    internal override ITasksCapability Tasks => this;

    internal override ICostCapability Cost => this;

    /// <summary>Whether a member settles its blocks itself: the others read theirs as rows.</summary>
    public bool SettlesBlocks { get; }

    /// <summary>The first member's, which the members of a set share when they lie in one store.</summary>
    public CostProfile Profile => _members[0].Cost?.Profile ?? CostProfile.Remote;

    /// <summary>Whether a read of a member is a round trip.</summary>
    public bool ReadsRoundTrips { get; }

    /// <summary>The rows of every member.</summary>
    public long RowBound => _starts[^1];

    public IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>()
        : spec.OrderPath is not null ? SortedAsync(spec, metrics)
        : spec.Memory is null ? QuestionAsync(spec, metrics)
        : ConcatAsync(spec, metrics);

    /// <summary>
    /// The batches of a scan of the set that is a question of its own: every member's scan under one memory, made as it
    /// starts and given back as it ends, where each member's made its own.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> QuestionAsync(ScanSpec spec, ScanCounters metrics, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using QueryMemory.Lease lease = QueryMemory.For(spec, Session);
        await foreach (RecordBatch batch in ConcatAsync(spec.Under(lease.Memory), metrics, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The batches of each member <paramref name="spec"/> keeps rows of, the members in their order, the last first for a
    /// read from the end, each batch at its place among the rows of the whole.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> ConcatAsync(ScanSpec spec, ScanCounters metrics, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < _members.Length; i++)
        {
            int m = spec.Backward ? _members.Length - 1 - i : i;
            if (MemberSpec(m, spec) is not { } asked)
            {
                continue;
            }

            long start = _starts[m];
            RecordBatch? view = null;
            try
            {
                await foreach (RecordBatch batch in _members[m].Rows.BatchesAsync(asked, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return InWhole(batch, start, ref view);
                }
            }
            finally
            {
                view?.Dispose();
            }
        }
    }

    /// <summary>
    /// A member's batch seen as the whole's rows, after the rows of the members before it. One view serves a member's
    /// stream, bound again per batch: a batch is valid until the next is asked for.
    /// </summary>
    private static RecordBatch InWhole(RecordBatch batch, long start, ref RecordBatch? view)
    {
        if (start == 0)
        {
            return batch;
        }

        if (view is null)
        {
            view = batch.Rebased(start + batch.StartRow);
            return view;
        }

        view.Dispose();
        view.RebindRebased(batch, start + batch.StartRow);
        return view;
    }

    /// <summary>
    /// The rows <paramref name="spec"/> keeps, in the order of the column it asks, nulls last, ties in the order of the
    /// rows. Each member is asked for its own rows in that order, so that a member that orders by no key source refuses as
    /// it would alone, and a set orders what its members order and nothing else. One member's rows come as it gives them;
    /// those of several are merged under the session's memory budget, in memory while it holds them and by runs on the
    /// scratch otherwise: a blocking stage, the members sharing no order a merge could follow as they are read.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> SortedAsync(ScanSpec spec, ScanCounters metrics, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int asked = 0;
        for (int m = 0; m < _members.Length; m++)
        {
            asked += Asks(m, spec) ? 1 : 0;
        }

        if (asked <= 1)
        {
            await foreach (RecordBatch batch in ConcatAsync(spec, metrics, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        string path = spec.OrderPath!;
        FieldMask mask = new FieldMaskBuilder().Include(spec.Projection ?? FieldMask.All).Include(ToolPaths.Resolve(Schema, path)).Build();
        VortexSchema schema = mask.IsAll ? Schema : ToolPaths.Project(Schema, mask);

        // One question over every member: their rows and the sort under one memory.
        using QueryMemory.Lease lease = QueryMemory.For(spec, Session);
        QueryMemory memory = lease.Memory;
        ScanSpec kept = spec.Under(memory) with
        {
            Backward = false,
            Projection = mask,
            KeepEncodings = false,
            SinkDecodes = false,
            PositionsUnread = false,
            Analysis = null,
            Answering = null,
            Options = spec.Options with { Compact = true },
        };

        int[] delivered = new int[schema.Count];
        for (int c = 0; c < delivered.Length; c++)
        {
            delivered[c] = c;
        }

        SortKey key = new SortKey(new ColumnFieldExpr(path), TopRows.TypeOf(this, path), spec.Descending);
        ExternalSort sort = new ExternalSort(Session, schema, [key], delivered, memory, GroupBatches.BatchRows);
        await using (sort.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in ConcatAsync(kept, metrics, cancellationToken).ConfigureAwait(false))
            {
                await sort.AddAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            IAsyncEnumerator<RecordBatch> sorted = await sort.SortedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (await sorted.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return sorted.Current;
                }
            }
            finally
            {
                await sorted.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// What <paramref name="spec"/> asks of member <paramref name="m"/>: its range of rows and its places cut at the member's
    /// bounds and counted from the member's first row. Null when it asks none of the member's rows. A range that covers
    /// the member whole asks it without a range.
    /// </summary>
    private ScanSpec? MemberSpec(int m, ScanSpec spec)
    {
        long start = _starts[m];
        long end = _starts[m + 1];
        if (end == start)
        {
            return null;
        }

        ScanSpec asked = spec;
        if (spec.Rows is { } rows)
        {
            long from = Math.Max(rows.Start, start);
            long to = Math.Min(rows.End, end);
            if (from >= to)
            {
                return null;
            }

            asked = asked with { Rows = from == start && to == end ? null : new RowRange(from - start, to - start) };
        }

        if (spec.Take is { } take)
        {
            ReadOnlySpan<long> places = take.Span;
            int first = LowerBound(places, start);
            int last = LowerBound(places, end);
            if (first == last)
            {
                return null;
            }

            // The first member reads its places where they lie; another one a copy counted from its first row, held in the
            // question's memory until the question ends.
            ReadOnlyMemory<long> own = take.Slice(first, last - first);
            if (start != 0)
            {
                spec.Memory?.Hold((long)(last - first) * sizeof(long), "a union member's places");
                long[] moved = new long[last - first];
                for (int i = 0; i < moved.Length; i++)
                {
                    moved[i] = places[first + i] - start;
                }

                own = moved;
            }

            asked = asked with { Take = own };
        }

        return asked;
    }

    /// <summary>Whether <paramref name="spec"/> asks any of member <paramref name="m"/>'s rows, as <see cref="MemberSpec"/> says, with no copy made.</summary>
    private bool Asks(int m, ScanSpec spec)
    {
        long start = _starts[m];
        long end = _starts[m + 1];
        if (end == start || (spec.Rows is { } rows && Math.Max(rows.Start, start) >= Math.Min(rows.End, end)))
        {
            return false;
        }

        return spec.Take is not { } take || LowerBound(take.Span, start) != LowerBound(take.Span, end);
    }

    /// <summary>The first of <paramref name="places"/>, which ascend, at or past <paramref name="row"/>.</summary>
    private static int LowerBound(ReadOnlySpan<long> places, long row)
    {
        int low = 0;
        int high = places.Length;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            if (places[middle] < row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// What each member answers <paramref name="ask"/> for its part of <paramref name="spec"/>, the members asked side by
    /// side so that their reads overlap: <paramref name="absent"/> for a member the spec asks no row of.
    /// </summary>
    private async ValueTask<T[]> EachAsync<T>(ScanSpec spec, Func<ScanSource, ScanSpec, ValueTask<T>> ask, T absent)
    {
        T[] answers = new T[_members.Length];
        if (_members.Length == 1)
        {
            answers[0] = MemberSpec(0, spec) is { } only ? await ask(_members[0], only).ConfigureAwait(false) : absent;
            return answers;
        }

        Task<T>?[] asked = new Task<T>?[_members.Length];
        for (int m = 0; m < asked.Length; m++)
        {
            asked[m] = MemberSpec(m, spec) is { } own ? ask(_members[m], own).AsTask() : null;
        }

        // Every member is awaited, whatever one of them threw: none reads on once the question has ended.
        Exception? failed = null;
        for (int m = 0; m < asked.Length; m++)
        {
            if (asked[m] is not { } task)
            {
                answers[m] = absent;
                continue;
            }

            try
            {
                answers[m] = await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failed ??= exception;
            }
        }

        if (failed is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }

        return answers;
    }

    /// <summary>
    /// The sum of the members' counts, whether a member holds a row, the first asked first, or the best of their
    /// extremes: each member answers from the cheapest proof it holds.
    /// </summary>
    public async ValueTask<FormAnswer> AnswerAsync(ScanSpec spec, LogicalShape form, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return new FormAnswer(0, FilterLiteral.Null);
        }

        if (ReferenceEquals(form, LogicalShape.Any))
        {
            // One member after the other: the first that holds a row ends the question, and the others are not asked.
            for (int m = 0; m < _members.Length; m++)
            {
                if (MemberSpec(m, spec) is { } asked
                    && (await _members[m].Rows.AnswerAsync(asked, form, metrics, cancellationToken).ConfigureAwait(false)).Rows > 0)
                {
                    return new FormAnswer(1, FilterLiteral.Null);
                }
            }

            return new FormAnswer(0, FilterLiteral.Null);
        }

        FormAnswer[] answers = await EachAsync(
            spec, (member, asked) => member.Rows.AnswerAsync(asked, form, metrics, cancellationToken), new FormAnswer(0, FilterLiteral.Null)).ConfigureAwait(false);
        if (form is ExtremeShape extreme)
        {
            bool decimals = Scanning.ExtremeOrder.IsDecimal(Schema, extreme.Path);
            FilterLiteral best = FilterLiteral.Null;
            foreach (FormAnswer answer in answers)
            {
                if (Scanning.ExtremeOrder.Beats(answer.Value, best, extreme.Min, decimals))
                {
                    best = answer.Value;
                }
            }

            return new FormAnswer(0, best);
        }

        long rows = 0;
        foreach (FormAnswer answer in answers)
        {
            rows += answer.Rows;
        }

        return new FormAnswer(rows, FilterLiteral.Null);
    }

    /// <summary>The paths of each member's variant columns, tallied in the one question, each from its own directories first.</summary>
    public async ValueTask PathsAsync(ScanSpec spec, PathsQuestion question, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return;
        }

        for (int m = 0; m < _members.Length; m++)
        {
            if (MemberSpec(m, spec) is { } asked)
            {
                await _members[m].Rows.PathsAsync(asked, question, metrics, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The plans of the members, summed: their rows, their blocks, what their structures pruned, their units one member
    /// after the other, and the cost of them all at the union's profile.
    /// </summary>
    public async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, bool analyze, CancellationToken cancellationToken)
    {
        ScanPlan?[] plans = await EachAsync<ScanPlan?>(
            spec, async (member, asked) => await member.Rows.ExplainAsync(asked, analyze, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false);
        long rows = 0;
        int blocks = 0;
        int live = 0;
        int segments = 0;
        long bytes = 0;
        bool mayMatch = false;
        bool exact = true;
        long counted = 0;
        int pruned = 0;
        int proven = 0;
        int decoded = 0;
        bool priced = true;
        long requests = 0;
        long costBytes = 0;
        double nanoseconds = 0;
        ImmutableArray<PruningStep>.Builder pruning = ImmutableArray.CreateBuilder<PruningStep>();
        ImmutableArray<UnitStep>.Builder units = ImmutableArray.CreateBuilder<UnitStep>();
        foreach (ScanPlan? plan in plans)
        {
            if (plan is null)
            {
                continue;
            }

            rows += plan.Rows;
            blocks += plan.Blocks;
            live += plan.LiveBlocks;
            segments += plan.Segments;
            bytes += plan.BytesToRead;
            mayMatch |= plan.MayMatch;
            exact &= plan.Count.Exact;
            counted += plan.Count.Rows;
            pruned += plan.Count.Pruned;
            proven += plan.Count.Proven;
            decoded += plan.Count.Decoded;
            pruning.AddRange(plan.Pruning);
            units.AddRange(plan.Units);
            if (plan.Cost is { } cost)
            {
                requests += cost.Requests;
                costBytes += cost.Bytes;
                nanoseconds += cost.Nanoseconds;
            }
            else
            {
                priced = false;
            }
        }

        return new ScanPlan(rows, blocks, live, segments, bytes, mayMatch && !spec.MatchesNothing, pruning.ToImmutable(), new CountPlan(exact, counted, pruned, proven, decoded), null)
        {
            Units = units.ToImmutable(),
            Cost = priced ? new PlanCost(requests, costBytes, nanoseconds, Profile.Predict(requests, costBytes, nanoseconds)) : null,
        };
    }

    /// <summary>
    /// Any column: the members lie in no common order, so a window of the rows in a column's order reads the column and
    /// the filter first, keeps the places of its rows, then reads the other columns at those places (spec G, 2).
    /// </summary>
    public bool ReadsTopLate(FieldExpr column) => true;

    /// <summary>
    /// The places of the best <paramref name="keep"/> rows of the whole. Each member finds its own best
    /// <paramref name="keep"/>, by the path it would take alone, a dataset its objects and granules from the best, the
    /// members side by side: the best of the whole are among them, a row a member leaves out being beaten by
    /// <paramref name="keep"/> rows of that member. When they are more than <paramref name="keep"/>, the column is read
    /// at their places alone, and those rows settle which are kept, a tie to the earlier place.
    /// </summary>
    public async ValueTask<long[]?> TopPositionsAsync(ScanSpec spec, string path, bool descending, long keep, ScanCounters metrics, CancellationToken cancellationToken)
    {
        // One question over every member: their tops side by side, the candidates and the top among them under one memory.
        using QueryMemory.Lease lease = QueryMemory.For(spec, Session);
        QueryMemory memory = lease.Memory;
        spec = spec.Under(memory);
        long[]?[] found = await EachAsync<long[]?>(
            spec,
            async (member, asked) =>
                await member.Rows.TopPositionsAsync(asked, path, descending, keep, metrics, cancellationToken).ConfigureAwait(false)
                ?? await TopRows.PlacesAsync(member, asked, path, descending, keep, metrics, cancellationToken).ConfigureAwait(false),
            null).ConfigureAwait(false);
        int total = 0;
        foreach (long[]? places in found)
        {
            total += places?.Length ?? 0;
        }

        // The members' places at their places in the whole: each member's ascend, and the members follow one another. Held in
        // the question's memory, which bounds how many candidates the members may bring.
        memory.Hold((long)total * sizeof(long), "a union's candidate places");
        long[] candidates = new long[total];
        int at = 0;
        for (int m = 0; m < found.Length; m++)
        {
            if (found[m] is not { } places)
            {
                continue;
            }

            long start = _starts[m];
            foreach (long place in places)
            {
                candidates[at++] = place + start;
            }
        }

        if (total <= TopRows.Kept(keep))
        {
            return candidates;
        }

        ScanSpec take = spec with
        {
            Filter = null,
            MatchesNothing = false,
            Rows = null,
            Take = candidates,
            Projection = new FieldMaskBuilder().Include(ToolPaths.Resolve(Schema, path)).Build(),
            OrderPath = null,
            Descending = false,
            Backward = false,
            PositionsUnread = false,
            KeepEncodings = false,
            SinkDecodes = false,
            Analysis = null,
            Answering = null,
            Options = spec.Options with { Compact = true },
        };

        // The candidates' values, numbered as the candidates are: the top keeps the numbers of the best, a tie to the lower.
        using TopKeep top = new TopKeep(Session, TopRows.TypeOf(this, path), descending, TopRows.Kept(keep), memory);
        FieldExpr column = new ColumnFieldExpr(path);
        long read = 0;
        await foreach (RecordBatch batch in ConcatAsync(take, metrics, cancellationToken).ConfigureAwait(false))
        {
            int rows = batch.RowCount;
            if (read + rows > total)
            {
                throw new InvalidOperationException($"A take of {total} rows delivered more.");
            }

            top.Add(batch.Arena, FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, column, rows), rows, batch.SelectionWords, read, cancellationToken);
            read += rows;
        }

        if (read != total)
        {
            throw new InvalidOperationException($"A take of {total} rows delivered {read}.");
        }

        long[] best = top.Places(cancellationToken);
        for (int i = 0; i < best.Length; i++)
        {
            best[i] = candidates[best[i]];
        }

        return best;
    }

    /// <summary>The loosest bounds the members' statistics give the column; none when a member that holds rows gives none.</summary>
    public bool TryBounds(int[] path, out FilterLiteral min, out FilterLiteral max)
    {
        min = FilterLiteral.Null;
        max = FilterLiteral.Null;
        bool any = false;
        bool decimals = Scanning.ExtremeOrder.IsDecimal(Schema, path);
        for (int m = 0; m < _members.Length; m++)
        {
            if (_starts[m + 1] == _starts[m])
            {
                continue;
            }

            if (_members[m].Statistics is not { } known || !known.TryBounds(path, out FilterLiteral low, out FilterLiteral high)
                || low.Kind == FilterLiteralKind.Null || high.Kind == FilterLiteralKind.Null || (any && (low.Kind != min.Kind || high.Kind != max.Kind)))
            {
                min = FilterLiteral.Null;
                max = FilterLiteral.Null;
                return false;
            }

            if (!any)
            {
                min = low;
                max = high;
                any = true;
                continue;
            }

            // A decimal's bounds are numbers, an unscaled value past 64 bits read as its bytes; bounds that do not compare
            // as numbers bound nothing.
            if (Scanning.ExtremeOrder.Compare(low, min, decimals) is not { } below || Scanning.ExtremeOrder.Compare(high, max, decimals) is not { } above)
            {
                min = FilterLiteral.Null;
                max = FilterLiteral.Null;
                return false;
            }

            min = below < 0 ? low : min;
            max = above > 0 ? high : max;
        }

        return any;
    }

    /// <summary>The loosest bounds the members' statistics in hand give an integer key; null when a member that holds rows gives none.</summary>
    public KeyBounds? Bounds(ColumnShape key)
    {
        KeyBounds? bounds = null;
        for (int m = 0; m < _members.Length; m++)
        {
            if (_starts[m + 1] == _starts[m])
            {
                continue;
            }

            if (_members[m].Statistics?.Bounds(key) is not { } own)
            {
                return null;
            }

            bounds = bounds is { } held ? new KeyBounds(Math.Min(held.Min, own.Min), Math.Max(held.Max, own.Max)) : own;
        }

        return bounds;
    }

    /// <summary><see cref="Bounds"/>, once each member read what its scan reads first anyway, the members side by side.</summary>
    public async ValueTask<KeyBounds?> BoundsAsync(ColumnShape key, CancellationToken cancellationToken)
    {
        Task<KeyBounds?>?[] asked = new Task<KeyBounds?>?[_members.Length];
        for (int m = 0; m < asked.Length; m++)
        {
            asked[m] = _starts[m + 1] == _starts[m] ? null
                : _members[m].Statistics is { } known ? known.BoundsAsync(key, cancellationToken).AsTask()
                : Task.FromResult<KeyBounds?>(null);
        }

        KeyBounds? bounds = null;
        bool unknown = false;
        foreach (Task<KeyBounds?>? task in asked)
        {
            if (task is null)
            {
                continue;
            }

            if (await task.ConfigureAwait(false) is not { } own)
            {
                unknown = true;
                continue;
            }

            bounds = bounds is { } held ? new KeyBounds(Math.Min(held.Min, own.Min), Math.Max(held.Max, own.Max)) : own;
        }

        return unknown ? null : bounds;
    }

    /// <summary>Whether a member would take states in place of some of its rows: the others give theirs as rows, in the same pass.</summary>
    public async ValueTask<bool> TakesStatesAsync(ScanSpec spec, StateRequest states, CancellationToken cancellationToken)
    {
        for (int m = 0; m < _members.Length; m++)
        {
            if (_members[m].States is { } kept && MemberSpec(m, spec) is { } asked
                && await kept.TakesStatesAsync(asked, states, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pieces of each member, at their places in the whole, a member that cuts none of its own as one piece: the lanes
    /// of a pass take them from one queue, so that no lane waits on a member the others finished beside.
    /// </summary>
    public async ValueTask<RowRange[]?> PiecesAsync(ScanSpec spec, int degree, CancellationToken cancellationToken)
    {
        if (spec.OrderPath is not null || spec.Take is not null || spec.MatchesNothing)
        {
            return null;
        }

        RowRange[]?[] cut = await EachAsync<RowRange[]?>(
            spec,
            async (member, asked) => member.Tasks is { } tasks ? await tasks.PiecesAsync(asked, degree, cancellationToken).ConfigureAwait(false) : null,
            null).ConfigureAwait(false);
        List<RowRange> pieces = [];
        for (int m = 0; m < _members.Length; m++)
        {
            long start = _starts[m];
            if (cut[m] is { } own)
            {
                foreach (RowRange piece in own)
                {
                    pieces.Add(new RowRange(piece.Start + start, piece.End + start));
                }

                continue;
            }

            long from = Math.Max(spec.Rows?.Start ?? 0, start);
            long to = Math.Min(spec.Rows?.End ?? long.MaxValue, _starts[m + 1]);
            if (from < to)
            {
                pieces.Add(new RowRange(from, to));
            }
        }

        return pieces.Count > 1 ? [.. pieces] : null;
    }

    /// <summary>The ranges an estimate draws from, each member's lists at their places in the whole; null when a member has no cut of its own.</summary>
    public async ValueTask<RowRange[][]?> StrataAsync(ScanSpec pass, long rangeBytes, CancellationToken cancellationToken)
    {
        if (pass.MatchesNothing)
        {
            return [];
        }

        RowRange[][]?[] cut = await EachAsync<RowRange[][]?>(
            pass,
            async (member, asked) => member.Tasks is { } tasks ? await tasks.StrataAsync(asked, rangeBytes, cancellationToken).ConfigureAwait(false) : null,
            []).ConfigureAwait(false);
        List<RowRange[]> strata = [];
        for (int m = 0; m < _members.Length; m++)
        {
            if (cut[m] is not { } own)
            {
                return null;
            }

            long start = _starts[m];
            foreach (RowRange[] ranges in own)
            {
                if (start == 0)
                {
                    strata.Add(ranges);
                    continue;
                }

                RowRange[] moved = new RowRange[ranges.Length];
                for (int r = 0; r < moved.Length; r++)
                {
                    moved[r] = new RowRange(ranges[r].Start + start, ranges[r].End + start);
                }

                strata.Add(moved);
            }
        }

        return [.. strata];
    }

    /// <summary>The most a split read ahead holds in a member.</summary>
    public long ReadAheadBytes(ScanSpec spec)
    {
        long most = 0;
        foreach (ScanSource member in _members)
        {
            most = Math.Max(most, member.Cost?.ReadAheadBytes(spec) ?? 0);
        }

        return most;
    }

    /// <summary>The sum of what the members' planners price their parts of <paramref name="spec"/> at; null when a member cannot say.</summary>
    public async ValueTask<(long Requests, long Bytes)?> CostAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        (long Requests, long Bytes, bool Known)[] costs = await EachAsync(
            spec,
            async (member, asked) => member.Cost is { } cost && await cost.CostAsync(asked, cancellationToken).ConfigureAwait(false) is { } priced
                ? (priced.Requests, priced.Bytes, true)
                : (0L, 0L, false),
            (0L, 0L, true)).ConfigureAwait(false);
        long requests = 0;
        long bytes = 0;
        foreach ((long Requests, long Bytes, bool Known) cost in costs)
        {
            if (!cost.Known)
            {
                return null;
            }

            requests += cost.Requests;
            bytes += cost.Bytes;
        }

        return (requests, bytes);
    }
}
