using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// Whether the object <paramref name="held"/> learns its partial of the scan's family, or answers from it: one the scan
    /// covers whole, with no row marked, of the version's columns, whose added keys widen no read. A column the family adds
    /// that is the key's first, which the object holds more than one value of, would read every value's rows where the
    /// question reads its own, and the object is read as the question reads it.
    /// </summary>
    private bool Learns(in PositionedObject held)
    {
        if (_family is not { } family || Covered(held) != held.Entry.Rows || held.Entry.HasDeletions
            || FamilyOf(held) is not { } own || _dataset.Partials.TooLarge(held.Entry.Uid, own.Key))
        {
            return false;
        }

        // An object of an earlier schema, or one an append wrote, which keeps no table of contents, answers from a partial
        // it holds, one written as it was appended (VortexDataset.KeptUpAsync), and learns none from its rows.
        if (!_version.Schema.Holds(held.Entry) && !_dataset.Partials.Holds(held.Entry.Uid, own.Key, _dataset.Session.Options.SynopsisCache))
        {
            return false;
        }

        if (_version.Schema.Key is { } key)
        {
            string leading = key.InstantPath;
            ObjectSummaries summaries = held.Summaries(key);
            for (int i = family.Keys.Length - family.Added; i < family.Keys.Length; i++)
            {
                if (string.Equals(family.Keys[i].Path, leading, StringComparison.Ordinal)
                    && CountsAnswer.ConstantOf(summaries, leading, family.Keys[i].Field) is null)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The scan's family as the object <paramref name="held"/> makes it, and the key its partial is kept under (<see cref="FamilyProof"/>); null for an edge.</summary>
    private (LearnedFamily Family, UInt128 Key)? FamilyOf(in PositionedObject held) =>
        (_proof ??= new FamilyProof(_family!, _version.Schema)).Of(held.Summaries(_version.Schema.Key), held.Entry.PhysicalRows);

    private FamilyProof? _proof;

    /// <summary>The top-level columns <paramref name="family"/> reads, by their places: its keys', its aggregates' and its filter's.</summary>
    private int[] FieldsOf(LearnedFamily family)
    {
        HashSet<string> paths = [];
        foreach (ColumnShape key in family.Keys)
        {
            paths.Add(key.Path);
        }

        foreach (IAggregateNode aggregate in family.Plan.Aggregates)
        {
            if (aggregate.Input is { } input)
            {
                paths.Add(input.Path);
            }
        }

        family.Filter?.CollectFields(paths);
        SortedSet<int> fields = [];
        foreach (string path in paths)
        {
            int dot = path.IndexOf('.', StringComparison.Ordinal);
            if (_version.Schema.DType.IndexOfField(dot < 0 ? path : path[..dot]) is int field and >= 0)
            {
                fields.Add(field);
            }
        }

        return [.. fields];
    }

    /// <summary>
    /// The states of the object <paramref name="held"/> under the scan's family: its partial, from the handle or the
    /// session's cache, or learned now from its rows and kept; the groups of the values the question asks.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> LearnedAsync(
        PositionedObject held, Task<AggregationPartial>? learning, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AggregationPartial partial = await (learning ?? PartialAsync(held, cancellationToken)).ConfigureAwait(false);
        await foreach (RecordBatch batch in FamilyOf(held)!.Value.Family.ServeAsync(partial, _dataset.Session, held.FirstRow, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>The partial of the object <paramref name="held"/> under the scan's family: from the handle or the session's cache, else learned now from its rows and kept.</summary>
    private async Task<AggregationPartial> PartialAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        (LearnedFamily family, UInt128 key) = FamilyOf(held)!.Value;
        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        if (!_dataset.Partials.TryGet(held.Entry.Uid, key, cache, out AggregationPartial partial))
        {
            partial = await InheritedAsync(held.Entry.Uid, family, key, 0, cancellationToken).ConfigureAwait(false)
                ?? await family.LearnAsync(_familySource!, new RowRange(held.FirstRow, held.FirstRow + held.Entry.Rows), _options, cancellationToken)
                    .ConfigureAwait(false);
            _dataset.Partials.Keep(held.Entry.Uid, key, partial, cache);
        }

        return partial;
    }

    /// <summary>
    /// The partial of <paramref name="family"/> of the object <paramref name="uid"/> with no row read: the one learned of it,
    /// or the sum of those of the objects a compaction merged into it alone (<see cref="LearnedPartials.Inherit"/>), each found
    /// so in turn, a few merges deep; null when one of them has none.
    /// </summary>
    private async ValueTask<AggregationPartial?> InheritedAsync(UInt128 uid, LearnedFamily family, UInt128 key, int depth, CancellationToken cancellationToken)
    {
        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        if (_dataset.Partials.TryGet(uid, key, cache, out AggregationPartial held))
        {
            return held;
        }

        if (depth >= MergesDeep || _dataset.Partials.MergedInto(uid, cache) is not { } inputs)
        {
            return null;
        }

        AggregationPartial[] parts = new AggregationPartial[inputs.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (await InheritedAsync(inputs[i], family, key, depth + 1, cancellationToken).ConfigureAwait(false) is not { } part)
            {
                return null;
            }

            parts[i] = part;
        }

        return await family.MergeAsync(parts, _familySource!, _options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The merges a partial is summed through at most, an object's of objects merged into it before.</summary>
    private const int MergesDeep = 8;

    /// <summary>How the objects' counts answer the states the scan asks, worked out once; null when they answer none of them.</summary>
    private CountsAnswer? Answer()
    {
        if (!_answerKnown)
        {
            _answer = _states is { } request ? CountsAnswer.Of(request, _filter, _version) : null;
            _answerKnown = true;
        }

        return _answer;
    }

    /// <summary>How the statistics of the objects' blocks answer the states the scan asks, worked out once; null when they answer none.</summary>
    private BlockAnswer? BlockAnswer()
    {
        if (!_blockAnswerKnown)
        {
            _blockAnswer = _states is { } request && Answer() is { } counts ? Dataset.BlockAnswer.Of(request, _filter, _version, counts) : null;
            _blockAnswerKnown = true;
        }

        return _blockAnswer;
    }

    /// <summary>
    /// Whether the statistics of the blocks of the object <paramref name="held"/>, which the scan covers whole, may answer it;
    /// the answer worked out at the first object that keeps them.
    /// </summary>
    private bool BlocksAnswer(in PositionedObject held) =>
        held.Entry.Contents is { BlockStats.Count: > 0 } && Covered(held) == held.Entry.Rows && BlockAnswer() is { } blocks && blocks.MayAnswer(held);

    /// <summary>The column whose counts answer the object <paramref name="held"/> for <paramref name="answer"/>: one the scan covers whole; null for its rows.</summary>
    private int? AnsweredBy(CountsAnswer answer, in PositionedObject held) =>
        Covered(held) == held.Entry.Rows ? answer.FieldOf(held) : null;

    /// <summary>Whether the counts of an object the scan covers whole answer what it asks as states: read from the entries alone.</summary>
    internal async ValueTask<bool> AnswersAnyAsync(CancellationToken cancellationToken)
    {
        if (Answer() is not { } answer || !await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await foreach (PositionedObject held in _version.WalkAsync(Pruner(), _from, _to, null, cancellationToken).ConfigureAwait(false))
        {
            if (AnsweredBy(answer, held) is not null || BlocksAnswer(held))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The scan as states where the objects' counts answer: each such object's entries as batches of states; the objects
    /// between them read as rows, those side by side a part of the scan, which
    /// reads them as it would (<see cref="Part"/>). The counts of the objects that answer are read side by side, as many at
    /// once as the session reads, and none of their sheets: their counts say which values they hold.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> StatesAsync(CountsAnswer answer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The question's family is kept up by the handle a while: the objects it appends learn its partials as they are written.
        if (_family is { } asked)
        {
            _dataset.Partials.Asked(asked, _dataset.Options.TimeProvider.GetUtcNow().UtcTicks);
        }

        // The rollups a scan of the whole version takes, each standing for the objects it covers, which the walk passes
        // over; then the objects the walk meets that no rollup covers. The rollups come first, as many as rolled holds.
        RollupChoice? rollups = await RollupsAsync(answer, cancellationToken).ConfigureAwait(false);
        List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)>? rolled = rollups is null ? null : Rolled(rollups);
        List<(PositionedObject Held, int? Field)> objects = [];
        if (rollups is not null)
        {
            foreach ((RollupEntry _, int field, ObjectSummaries?[]? _) in rollups.Chosen)
            {
                objects.Add((default, field));
            }
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken, answer, rollups).ConfigureAwait(false))
        {
            if (Covered(held) > 0 && rollups?.CoverOf(held) is not >= 0)
            {
                objects.Add((held, AnsweredBy(answer, held)));
            }
        }

        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[] reads = new Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[objects.Count];
        int width = Math.Max(1, _dataset.Session.Options.MaxConcurrentReads);
        CanonicalArena arena = new CanonicalArena(answer.Nodes);

        // The statistics over the blocks of the objects the counts do not answer and whose blocks may, by the place of the
        // object, read ahead as the counts are; made at the first such object.
        Dictionary<UInt128, Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]>>? blockReads = null;

        // The objects that read their rows next, opened ahead, a few at a time, so that each one's open is not a round trip
        // after the reads of the one before; left open in the handle's cache for the read that comes to them.
        Dictionary<UInt128, Task>? opened = null;
        int opensAhead = Math.Clamp(_dataset.Options.MaxOpenObjects / 4, 1, 8);
        int openSeen = 0;

        // The partials of the objects that learn theirs next, learned ahead of the one served as the scan's batches are
        // decoded ahead of its consumer (ScanOptions.Prefetch): each one's reads then wait on no decode before them.
        Dictionary<UInt128, Task<AggregationPartial>>? learning = null;
        int learnSeen = 0;
        try
        {
            for (int o = 0; o < objects.Count; o++)
            {
                // The counts of the next objects and rollups read ahead, as many ahead as the session reads at once; a read
                // begun, or none to begin, is no longer null.
                for (int ahead = o; ahead < objects.Count && ahead < o + width; ahead++)
                {
                    reads[ahead] ??= objects[ahead].Field is not { } next ? NoCounts
                        : ahead < rolled?.Count
                            ? _dataset.ReadRollupCountsAsync(rolled[ahead].Rollup, next, answer.AsksSketches, answer.Values, rolled[ahead].Buckets is not null, stopped.Token).AsTask()
                        : objects[ahead].Held.Entry.HasDeletions
                            ? LiveCountsAsync(objects[ahead].Held.Entry, next, stopped.Token)
                        : answer.Exclusion is { } exclusion
                            ? KeptCountsAsync(answer, objects[ahead].Held.Entry, next, exclusion.Field, exclusion.Found, stopped.Token)
                        : _dataset.ReadCountsAsync(objects[ahead].Held.Entry, next, answer.AsksSketches, stopped.Token).AsTask();
                    if (objects[ahead].Field is null && ahead >= (rolled?.Count ?? 0) && (blockReads is null || !blockReads.ContainsKey(objects[ahead].Held.Entry.Uid))
                        && BlocksAnswer(objects[ahead].Held))
                    {
                        (blockReads ??= [])[objects[ahead].Held.Entry.Uid] = _dataset.ReadBlockColumnsAsync(objects[ahead].Held.Entry, BlockAnswer()!.Fields, stats: true, stopped.Token).AsTask();
                    }
                }

                // Each object looked at once, as the window of those ahead reaches it.
                for (; openSeen < objects.Count && openSeen < o + opensAhead; openSeen++)
                {
                    if (openSeen >= (rolled?.Count ?? 0) && objects[openSeen].Field is null && !_version.HoldsOpen(objects[openSeen].Held.Entry)
                        && ReadsRows(objects[openSeen].Held))
                    {
                        (opened ??= [])[objects[openSeen].Held.Entry.Uid] = OpenAheadAsync(objects[openSeen].Held, stopped.Token);
                    }
                }

                // A rollup taken by bucket gives the states of each bucket the filter does not refute.
                PositionedObject held = objects[o].Held;
                RollupEntry? taken = o < rolled?.Count ? rolled[o].Rollup : null;
                ObjectSummaries?[]? buckets = o < rolled?.Count ? rolled[o].Buckets : null;
                StateRows? rows = null;
                List<StateRows>? byBucket = null;
                if (reads[o] is { } read && read != NoCounts)
                {
                    reads[o] = NoCounts;
                    if (await read.ConfigureAwait(false) is ({ } counts, var sketched))
                    {
                        if (buckets is null)
                        {
                            rows = answer.Rows(counts, sketched, taken is not null ? taken.Summaries : answer.SummariesOf(held));
                        }
                        else
                        {
                            byBucket = BucketRows(answer, counts, sketched, buckets);
                        }
                    }
                }

                if (rows is null && byBucket is null && taken is not null)
                {
                    // A rollup whose totals did not answer after all: its objects, one by one, after the others, the rollups
                    // left first; their reads begin as the loop meets them, the reads begun before them kept.
                    List<(PositionedObject Held, int? Field)> spread = await SpreadAsync(taken, answer, cancellationToken).ConfigureAwait(false);
                    objects.RemoveAt(o);
                    rolled!.RemoveAt(o);
                    objects.AddRange(spread);
                    Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[] grown = new Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>?[objects.Count];
                    Array.Copy(reads, grown, o);
                    Array.Copy(reads, o + 1, grown, o, reads.Length - o - 1);
                    reads = grown;
                    o--;
                    continue;
                }

                if (rows is null && byBucket is null && taken is null && blockReads is not null && blockReads.Remove(held.Entry.Uid, out Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]>? blockRead))
                {
                    // An object whose blocks answer where its counts do not: the states of the blocks its statistics answer,
                    // then the rows of the others, each run of them a part of the scan.
                    // The blocks that hold a row a delete marked are read as rows too.
                    (Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[] blockColumns = await blockRead.ConfigureAwait(false);
                    await held.Entry.ResolveAsync(_version.Pages, cancellationToken).ConfigureAwait(false);
                    (BlockStateRows? settled, List<RowRange> unsettled, int answered) = BlockAnswer()!.Settle(held, blockColumns, held.Entry.Deletions);
                    if (_metrics is { } counted)
                    {
                        counted.BlocksAnswered += answered;
                        counted.StateRows += settled?.Count ?? 0;
                    }

                    for (int at = 0; settled is not null && at < settled.Count; at += StateTable.BatchRows)
                    {
                        arena.Reset();
                        yield return settled.Batch(arena, at, Math.Min(StateTable.BatchRows, settled.Count - at), held.FirstRow);
                    }

                    foreach (RowRange range in unsettled)
                    {
                        DatasetScanBuilder part = Part(_filter, held.FirstRow + range.Start, held.FirstRow + range.End);
                        await foreach (RecordBatch batch in part.ExecuteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            yield return batch;
                        }
                    }

                    continue;
                }

                if (rows is null && byBucket is null)
                {
                    // An object of the question's family answers from the partial it learned, or learns it from its rows.
                    if (Learns(held))
                    {
                        Task<AggregationPartial>? learned = null;
                        learning?.Remove(held.Entry.Uid, out learned);
                        int started = learning?.Count ?? 0;
                        for (learnSeen = Math.Max(learnSeen, o + 1); learnSeen < objects.Count && started < _options.Prefetch; learnSeen++)
                        {
                            PositionedObject next = objects[learnSeen].Held;
                            if (learnSeen >= (rolled?.Count ?? 0) && objects[learnSeen].Field is null && !BlocksAnswer(next) && Learns(next) && ReadsRows(next))
                            {
                                started++;
                                (learning ??= [])[next.Entry.Uid] = PartialAsync(next, stopped.Token);
                            }
                        }

                        await foreach (RecordBatch batch in LearnedAsync(held, learned, cancellationToken).ConfigureAwait(false))
                        {
                            yield return batch;
                        }

                        continue;
                    }

                    // This object's rows and those of the objects after it the counts do not answer, up to the next they may:
                    // a part of the scan, which reads them as it would, ahead and past their sheets. Under rollups, a part
                    // stops where the rows the walk passed over begin, which a rollup answers.
                    int last = o;
                    while (last + 1 < objects.Count && objects[last + 1].Field is null && !BlocksAnswer(objects[last + 1].Held) && !Learns(objects[last + 1].Held)
                        && (rollups is null || objects[last + 1].Held.FirstRow == objects[last].Held.FirstRow + objects[last].Held.Entry.Rows))
                    {
                        last++;
                    }

                    PositionedObject end = objects[last].Held;
                    DatasetScanBuilder part = Part(_filter, Math.Max(_from, held.FirstRow), Math.Min(_to, end.FirstRow + end.Entry.Rows));
                    await foreach (RecordBatch batch in part.ExecuteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    o = last;
                    continue;
                }

                if (_metrics is { } metrics)
                {
                    metrics.ObjectsAnswered += taken?.Objects ?? 1;
                    metrics.RollupsAnswered += taken is null ? 0 : 1;
                    metrics.StateRows += rows?.Count ?? 0;
                    for (int b = 0; b < (byBucket?.Count ?? 0); b++)
                    {
                        metrics.StateRows += byBucket![b].Count;
                    }
                }

                if (rows is not null)
                {
                    for (int at = 0; at < rows.Count; at += StateRows.BatchRows)
                    {
                        arena.Reset();
                        yield return rows.Batch(arena, at, Math.Min(StateRows.BatchRows, rows.Count - at), held.FirstRow);
                    }

                    continue;
                }

                for (int b = 0; b < byBucket!.Count; b++)
                {
                    for (int at = 0; at < byBucket[b].Count; at += StateRows.BatchRows)
                    {
                        arena.Reset();
                        yield return byBucket[b].Batch(arena, at, Math.Min(StateRows.BatchRows, byBucket[b].Count - at), held.FirstRow);
                    }
                }
            }
        }
        finally
        {
            // The counts, statistics and opens begun ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach (Task open in (IEnumerable<Task>?)opened?.Values ?? [])
            {
                await open.ConfigureAwait(false);
            }

            foreach (Task<AggregationPartial> left in (IEnumerable<Task<AggregationPartial>>?)learning?.Values ?? [])
            {
                try
                {
                    await left.ConfigureAwait(false);
                }
                catch (Exception) when (left.IsFaulted || left.IsCanceled)
                {
                }
            }

            foreach (Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]> left in (IEnumerable<Task<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]>>?)blockReads?.Values ?? [])
            {
                try
                {
                    await left.ConfigureAwait(false);
                }
                catch (Exception) when (left.IsFaulted || left.IsCanceled)
                {
                }
            }

            foreach (Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>? left in reads)
            {
                if (left is not null && left != NoCounts)
                {
                    try
                    {
                        await left.ConfigureAwait(false);
                    }
                    catch (Exception) when (left.IsFaulted || left.IsCanceled)
                    {
                    }
                }
            }

            arena.Reset();
        }
    }

    /// <summary>
    /// Whether the object <paramref name="held"/>, which its counts do not answer, reads its rows: not one whose blocks may
    /// answer, which reads their statistics first, nor one whose family's partial the handle holds or sums.
    /// </summary>
    private bool ReadsRows(in PositionedObject held)
    {
        if (BlocksAnswer(held))
        {
            return false;
        }

        if (!Learns(held))
        {
            return true;
        }

        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        return !_dataset.Partials.Holds(held.Entry.Uid, FamilyOf(held)!.Value.Key, cache) && _dataset.Partials.MergedInto(held.Entry.Uid, cache) is null;
    }

    /// <summary>
    /// Opens the object <paramref name="held"/> ahead of its read and gives the lease back, the object left open and idle in
    /// the handle's cache, its copy shredded in memory, if any, left to the read; what the open meets is the read's to
    /// meet again.
    /// </summary>
    private async Task OpenAheadAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        try
        {
            ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The read that comes to the object opens it again, and reports what fails then.
        }
    }

    /// <summary>
    /// The bytes a reader of <paramref name="values"/> values reads of <paramref name="region"/>, about: its header of
    /// <paramref name="head"/> bytes, then a chunk a value at most.
    /// </summary>
    private static long InPart(Indexes.IndexSegment region, int head, int values) =>
        head + Math.Min(region.Length - (long)head, (long)values * Indexes.ColumnCounts.ChunkBytes);

    /// <summary>The rollups <paramref name="rollups"/> takes, in their order, each with its buckets' summaries when taken by bucket.</summary>
    private static List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)> Rolled(RollupChoice rollups)
    {
        List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)> rolled = new List<(RollupEntry Rollup, ObjectSummaries?[]? Buckets)>(rollups.Chosen.Count);
        foreach ((RollupEntry rollup, int _, ObjectSummaries?[]? buckets) in rollups.Chosen)
        {
            rolled.Add((rollup, buckets));
        }

        return rolled;
    }

    /// <summary>
    /// The states of each bucket of <paramref name="counts"/>, kept by bucket, with <paramref name="sketches"/> their
    /// sketches, each bucket with its summaries of <paramref name="buckets"/>, none for a bucket the filter refutes; null
    /// when a bucket's counts do not answer, or the counts are not of those buckets.
    /// </summary>
    private static List<StateRows>? BucketRows(CountsAnswer answer, Indexes.ColumnCounts counts, Indexes.ColumnSketches? sketches, ObjectSummaries?[] buckets)
    {
        if (counts.Buckets != buckets.Length)
        {
            return null;
        }

        List<StateRows> rows = [];
        for (int b = 0; b < buckets.Length; b++)
        {
            if (buckets[b] is not { } summaries || Indexes.CountsBuckets.Apart(counts, sketches, b) is not ({ } apart, var apartSketches))
            {
                continue;
            }

            if (answer.Rows(apart, apartSketches, summaries) is not { } held)
            {
                return null;
            }

            rows.Add(held);
        }

        return rows;
    }

    /// <summary>The objects <paramref name="rollup"/> covers, each with the column whose counts answer for it, in key order.</summary>
    private async Task<List<(PositionedObject Held, int? Field)>> SpreadAsync(RollupEntry rollup, CountsAnswer answer, CancellationToken cancellationToken)
    {
        List<(PositionedObject Held, int? Field)> spread = [];
        await foreach (PositionedObject under in _version.WalkAsync(Pruner(), 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            if (under.Level == rollup.Level && rollup.Covers(under.TreeKey.Span))
            {
                spread.Add((under, AnsweredBy(answer, under)));
            }
        }

        return spread;
    }

    /// <summary>
    /// The counts of column <paramref name="field"/> of an object a delete marked, less those of its deleted rows
    /// (<see cref="DeletedCounts.Less"/>), read beside them from the commit object they lie in; none, and the object read as
    /// rows, when they do not give the live rows' counts exactly.
    /// </summary>
    private async Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> LiveCountsAsync(ObjectEntry entry, int field, CancellationToken cancellationToken)
    {
        ValueTask<DeletedCounts?> negatives = entry.NegativesAsync(_version.Pages, cancellationToken);
        (Indexes.ColumnCounts? counts, _) = await _dataset.ReadCountsAsync(entry, field, sketches: false, cancellationToken).ConfigureAwait(false);
        return (await negatives.ConfigureAwait(false))?.CountsOf(field) is { } taken && counts is not null ? (DeletedCounts.Less(counts, taken), null) : (null, null);
    }

    /// <summary>
    /// The counts of column <paramref name="field"/> of an object under a value of identifier <paramref name="identifier"/>
    /// left out, less those of the rows that hold it (<see cref="CountsAnswer.Exclusion"/>): the rows its postings find for
    /// <paramref name="found"/>, read beside the counts, counted and taken out (<see cref="DeletedCounts.Less"/>); none, and the
    /// object read as rows, when the postings cannot say or the counts do not give the rows kept exactly.
    /// </summary>
    private async Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> KeptCountsAsync(
        CountsAnswer answer, ObjectEntry entry, int field, int identifier, VortexExpr found, CancellationToken cancellationToken)
    {
        (Indexes.ColumnCounts? counts, _) = await _dataset.ReadCountsAsync(entry, field, sketches: false, cancellationToken).ConfigureAwait(false);
        if (counts is null)
        {
            return (null, null);
        }

        // The columns the rows' counts read: the counted one, the measures the question reads, and the identifier the rows
        // are kept by; the counts' other measures, which the question does not read, stay every row's.
        List<int> measures = [];
        List<MeasureKind> kinds = [];
        foreach ((_, int measured, _, _) in answer.Measures)
        {
            int place = Array.IndexOf(counts.MeasureFields, measured);
            if (place >= 0 && !measures.Contains(measured))
            {
                measures.Add(measured);
                kinds.Add(counts.Kinds[place]);
            }
        }

        Indexes.ColumnCounts shape = new Indexes.ColumnCounts([.. measures], [.. kinds], [], []);
        SortedSet<int> columns = [field, identifier, .. measures];
        int[] read = [.. columns];
        List<RecordBatch>? rows = await _dataset.LookupAsync(entry, found, read, cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return (null, null);
        }

        try
        {
            DeletedCounts.Builder taken = new DeletedCounts.Builder(_version.Schema.DType, [(field, shape)], read);
            long held = 0;
            foreach (RecordBatch batch in rows)
            {
                SelectBy(batch, found);
                if (batch.SelectedRows > 0)
                {
                    taken.Add(batch);
                    held += batch.SelectedRows;
                }
            }

            return held == 0 ? (counts, null) : (DeletedCounts.Less(counts, taken.Finish().CountsOf(field)!), null);
        }
        finally
        {
            foreach (RecordBatch batch in rows)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>The read of an object's counts <see cref="StatesAsync"/> does not begin, or has taken: none.</summary>
    private static readonly Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> NoCounts = Task.FromResult<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)>((null, null));

    /// <summary>
    /// The rollups the scan takes in place of the objects they cover (<see cref="RollupChoice"/>): a scan of the whole
    /// version's, for which the counts answer; null for another, which reads no rollup.
    /// </summary>
    private ValueTask<RollupChoice?> RollupsAsync(CountsAnswer answer, CancellationToken cancellationToken) =>
        _from == 0 && _to == long.MaxValue ? RollupChoice.OfAsync(_version, answer, Pruner(), cancellationToken) : default;
}
