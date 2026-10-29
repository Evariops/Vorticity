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
    /// Whether a partial of the scan's family answers the object <paramref name="held"/> whole, its own or those of the objects
    /// a compaction merged into it, as a plan covers it (<see cref="PartialAlternative"/>): the store's partials of the family
    /// listed first. A lane's piece cut over its rows would read them, where the partial answers the object at no read.
    /// </summary>
    internal async ValueTask<bool> PartialAnswersAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        if (!Learns(held) || FamilyOf(held) is not { } own)
        {
            return false;
        }

        if (_dataset.LearnedStore is { } store)
        {
            await _dataset.Partials.ListAsync(store, own.Key, cancellationToken).ConfigureAwait(false);
        }

        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        return _dataset.Partials.Holds(held.Entry.Uid, own.Key, cache)
            || (_dataset.Partials.MergedInto(held.Entry.Uid, cache) is { } inputs
                && Array.TrueForAll(inputs, input => _dataset.Partials.Holds(input, own.Key, cache)));
    }

    /// <summary>
    /// Whether the object <paramref name="held"/> learns its partial of the scan's family, or answers from it: one the scan
    /// covers whole that may learn one (<see cref="MayLearn"/>).
    /// </summary>
    internal bool Learns(in PositionedObject held) => Covered(held) == held.Entry.Rows && MayLearn(held);

    /// <summary>
    /// How one lane's plan answers the object <paramref name="held"/>, which the scan covers whole (<see cref="Choose"/>), the
    /// <paramref name="kept"/> share of its rows its key's sparse index keeps known, or 1, the value the filter leaves out
    /// placed in it or not as <see cref="PlacedAsync"/> noted; and, that share unknown, whether it could change the choice,
    /// which then reads the index first, as <see cref="UnitsAsync"/> does. The lanes that cut an object learn what one lane
    /// learns, and answer through its granules one that one lane answers so.
    /// </summary>
    internal (AnswerPath Path, bool AsksKept) ChosenOnOneLane(in PositionedObject held, double kept)
    {
        Planned unit = new Planned(held, Answer() is { } answer ? AnsweredBy(answer, held) : null) { Kept = kept };
        Choose(unit, explain: false);
        return (unit.Chosen.Path, kept >= 1 && unit.Weight > _version.Costs.Weight(RowsCost(held, double.Epsilon)));
    }

    /// <summary>
    /// Whether the postings of an identifier may place in the object <paramref name="held"/> the value the filter leaves out
    /// (<see cref="CountsAnswer.Exclusion"/>), as <see cref="PlacesOfAsync"/> will look, noted where they may
    /// (<see cref="ExcludedValue.Place"/>), which its granules then answer alone: the identifier holds no null in it, which
    /// the filter leaves out as well, and the index of its partition covers it, or its own directory, which a declared
    /// identifier's postings are in until a consolidation moves them to the partition's. False under none.
    /// </summary>
    internal async ValueTask<bool> PlacedAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        if (Answer()?.Exclusion is not { } excluded || GranuleAnswer() is null
            || !CountsAnswer.NoNull(held.Summaries(_version.Schema), excluded.Path, _version.Schema.DType.GetField(excluded.Field)))
        {
            return false;
        }

        bool placed = (ExclusionLookup() is not null && !held.Entry.HasDeletions
            && await _version.PartitionIndexAsync(PartitionLookup.PartitionOf(_version, held), cancellationToken).ConfigureAwait(false) is { } index
            && index.Covers(held.Entry.Uid))
            || held.Entry.Contents is { DirectoryRegion: not null };
        if (placed)
        {
            excluded.Place(held.Entry.Uid);
        }

        return placed;
    }

    /// <summary>
    /// The runs of the rows of the object <paramref name="held"/>, at its own positions, that may hold the value the filter
    /// leaves out (<see cref="Dataset.GranuleAnswer.Exclusion"/>), with no row read: the places the index of its partition
    /// gives it, else those its own postings give, two round trips cold; null when neither says.
    /// </summary>
    private async Task<List<RowRange>?> PlacesOfAsync(PositionedObject held, VortexExpr found, CancellationToken cancellationToken)
    {
        if (ExclusionLookup() is { } partitions && !held.Entry.HasDeletions
            && await partitions.BlocksAsync(held, cancellationToken).ConfigureAwait(false) is ({ } places, int placeRows))
        {
            List<RowRange> runs = new List<RowRange>(places.Length);
            foreach (uint place in places)
            {
                long start = (long)place * placeRows;
                long end = Math.Min(start + placeRows, held.Entry.PhysicalRows);
                if (runs.Count > 0 && runs[^1].End == start)
                {
                    runs[^1] = new RowRange(runs[^1].Start, end);
                }
                else if (end > start)
                {
                    runs.Add(new RowRange(start, end));
                }
            }

            return runs;
        }

        return await _dataset.PlacesAsync(held.Entry, found, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The lookup of the value the filter leaves out (<see cref="CountsAnswer.Exclusion"/>) over the partitions' indexes, made
    /// once; null for none, or a version that keeps none.
    /// </summary>
    private PartitionLookup? ExclusionLookup() => Answer()?.Exclusion?.Lookup(_dataset, _version);

    /// <summary>
    /// The statistics of the granules of the object <paramref name="held"/> an answer through them reads, and beside them the
    /// places of the value <paramref name="excluded"/> the filter leaves out, noted where the settling of its granules takes
    /// them (<see cref="ExcludedValue.PlacesOf"/>).
    /// </summary>
    private async Task<(Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms)> StatisticsPlacedAsync(
        PositionedObject held, ExcludedValue excluded, CancellationToken cancellationToken)
    {
        Task<List<RowRange>?> placing = PlacesOfAsync(held, excluded.Found, cancellationToken);
        (Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms) statistics;
        try
        {
            statistics = await _dataset.ReadGranuleStatsAsync(held.Entry, GranuleAnswer()!.Fields, GranuleAnswer()!.Histogrammed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The places are read beside: they end first.
            await ((Task)placing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        excluded.Hold(held.Entry.Uid, await placing.ConfigureAwait(false));
        return statistics;
    }

    /// <summary>The rows of the object <paramref name="held"/> its granules' statistics are expected to leave, read as rows (<see cref="Dataset.GranuleAnswer.Unanswered"/>).</summary>
    internal long GranulesLeave(in PositionedObject held) => (long)Math.Ceiling(GranuleAnswer()!.Unanswered(held, edges: true) * held.Entry.Rows);

    /// <summary>
    /// Reads the statistics of the granules of the object <paramref name="held"/> an answer through them reads into the
    /// handle's memo, where each lane's piece of the object finds them: begun as its pieces, each read them.
    /// </summary>
    internal Task GranuleStatsAheadAsync(in PositionedObject held, CancellationToken cancellationToken) =>
        _dataset.ReadGranuleStatsAsync(held.Entry, GranuleAnswer()!.Fields, GranuleAnswer()!.Histogrammed, cancellationToken).AsTask();

    /// <summary>
    /// How many questions before this one the handle saw ask the scan's family, in a row no hour apart, its own not counted
    /// (<see cref="LearnedPartials.AskedBefore"/>): found once.
    /// </summary>
    private int AskedBefore()
    {
        if (_askedBefore is not { } asked)
        {
            long now = _dataset.Options.TimeProvider.GetUtcNow().UtcTicks;
            asked = _family is { } family && _familySource is DatasetScanSource source
                ? _dataset.Partials.AskedBefore(family.Identity, source.Asking, now - VortexDataset.KeptUpFor.Ticks)
                : 0;
            _askedBefore = asked;
        }

        return asked;
    }

    private int? _askedBefore;

    /// <summary>
    /// Whether the scan's family may learn a partial of the object <paramref name="held"/>: one with no row marked, of the
    /// version's columns, whose added keys widen no read. A column the family adds that is the key's first, which the object
    /// holds more than one value of, would read every value's rows where the question reads its own, and the object is read
    /// as the question reads it.
    /// </summary>
    private bool MayLearn(in PositionedObject held)
    {
        if (_family is not { } family || held.Entry.HasDeletions
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

        if (_version.Schema.KeyColumns is [{ } leading, ..])
        {
            ObjectSummaries summaries = held.Summaries(_version.Schema);
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
        (_proof ??= new FamilyProof(_family!, _version.Schema)).Of(held.Summaries(_version.Schema), held.Entry.PhysicalRows);

    private FamilyProof? _proof;

    /// <summary>The top-level columns the scan's family reads (<see cref="FieldsOf"/>), found once.</summary>
    private int[] FamilyFields() => _familyFields ??= FieldsOf(_family!);

    private int[]? _familyFields;

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
    /// The states of the object <paramref name="held"/> under the scan's family, its rows numbered from
    /// <paramref name="firstRow"/>: its partial, from the handle or the session's cache, or learned now from its rows and
    /// kept, or the partial of the scan's piece of it; the groups of the values the question asks.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> LearnedAsync(
        PositionedObject held, Task<AggregationPartial>? learning, long firstRow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AggregationPartial partial = await (learning ?? PartialAsync(held, cancellationToken)).ConfigureAwait(false);
        await foreach (RecordBatch batch in FamilyOf(held)!.Value.Family.ServeAsync(partial, _dataset.Session, firstRow, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The rows of the object <paramref name="held"/> the scan covers when it is a lane's piece of it, which learns the partial
    /// of the scan's family over them (<see cref="PiecePartials"/>); null for a scan that covers the object whole or none of
    /// it, or for an object that may learn none.
    /// </summary>
    private RowRange? Piece(in PositionedObject held)
    {
        if (!_piece || _take is not null || _familySource is not DatasetScanSource || Whole(held))
        {
            return null;
        }

        long start = Math.Max(_from, held.FirstRow);
        long end = Math.Min(_to, held.FirstRow + held.Entry.Rows);
        return end > start && MayLearn(held) ? new RowRange(start, end) : null;
    }

    /// <summary>
    /// The partial of the scan's family over <paramref name="piece"/>, a lane's piece of the object <paramref name="held"/>:
    /// learned from its rows on the lane, and handed to the question's source, which has the object's partial merged once
    /// its pieces tile it.
    /// </summary>
    private async Task<AggregationPartial> PiecePartialAsync(PositionedObject held, RowRange piece, CancellationToken cancellationToken)
    {
        (LearnedFamily family, UInt128 key) = FamilyOf(held)!.Value;
        DatasetScanSource source = (DatasetScanSource)_familySource!;
        AggregationPartial partial = await family.LearnAsync(source, piece, _options with { DegreeOfParallelism = 1 }, cancellationToken)
            .ConfigureAwait(false);
        source.Pieces.Learned(_dataset, source, held, family, key, piece, partial, _options);
        return partial;
    }

    /// <summary>
    /// The partial of the object <paramref name="held"/> under the scan's family: from the handle, the session's cache or the
    /// dataset's store, else learned now from its rows and kept in all three.
    /// </summary>
    private async Task<AggregationPartial> PartialAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        (LearnedFamily family, UInt128 key) = FamilyOf(held)!.Value;
        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        IO.IObjectStore? store = _dataset.LearnedStore;
        if (await _dataset.Partials.GetAsync(held.Entry.Uid, key, cache, store, cancellationToken).ConfigureAwait(false) is { } kept)
        {
            return kept;
        }

        AggregationPartial partial = await InheritedAsync(held.Entry.Uid, family, key, 0, cancellationToken).ConfigureAwait(false)
            ?? await family.LearnAsync(_familySource!, new RowRange(held.FirstRow, held.FirstRow + held.Entry.Rows), _options, cancellationToken)
                .ConfigureAwait(false);
        _dataset.Partials.Keep(held.Entry.Uid, key, partial, cache, held.Entry.Bytes, store, family.Text);
        return partial;
    }

    /// <summary>
    /// The partial of <paramref name="family"/> of the object <paramref name="uid"/> with no row read: the one learned of it,
    /// the one of a family that serves it brought to its columns and folded (<see cref="LearnedPartials.CoveredAsync"/>), or
    /// the sum of those of the objects a compaction merged into it alone (<see cref="LearnedPartials.Inherit"/>), each found
    /// so in turn, a few merges deep; null when one of them has none.
    /// </summary>
    private async ValueTask<AggregationPartial?> InheritedAsync(UInt128 uid, LearnedFamily family, UInt128 key, int depth, CancellationToken cancellationToken)
    {
        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        if (await _dataset.Partials.GetAsync(uid, key, cache, _dataset.LearnedStore, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        if (await _dataset.Partials.CoveredAsync(uid, key, cache, _dataset.LearnedStore, cancellationToken).ConfigureAwait(false) is { } covered)
        {
            try
            {
                return await family.MergeAsync([covered.Partial], covered.KeyOf, _familySource!, _options, cancellationToken).ConfigureAwait(false);
            }
            catch (System.IO.InvalidDataException)
            {
                // A partial whose columns its family's text did not say: the object is summed or learned as if none served.
            }
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

        return await family.MergeAsync(parts, null, _familySource!, _options, cancellationToken).ConfigureAwait(false);
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
    private GranuleAnswer? GranuleAnswer()
    {
        if (!_granuleAnswerKnown)
        {
            _granuleAnswer = _states is { } request && Answer() is { } counts ? Dataset.GranuleAnswer.Of(request, _filter, _version, counts, _dataset.Options.Write.BlockRows) : null;
            _granuleAnswerKnown = true;
        }

        return _granuleAnswer;
    }

    /// <summary>The column whose counts answer the object <paramref name="held"/> for <paramref name="answer"/>: one the scan covers whole; null for its rows.</summary>
    private int? AnsweredBy(CountsAnswer answer, in PositionedObject held) =>
        Covered(held) == held.Entry.Rows ? answer.FieldOf(held) : null;

    /// <summary>
    /// Whether the synopses of an object the scan covers answer what it asks as states, read from the entries alone: an object
    /// whose weight chooses its counts, its blocks or a learned partial over its rows.
    /// </summary>
    internal async ValueTask<bool> AnswersAnyAsync(CancellationToken cancellationToken)
    {
        if (Answer() is not { } answer || !await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await foreach (PositionedObject held in _version.WalkAsync(Pruner(), _from, _to, null, cancellationToken).ConfigureAwait(false))
        {
            if (answer.Exclusion is not null)
            {
                await PlacedAsync(held, cancellationToken).ConfigureAwait(false);
            }

            Planned unit = new Planned(held, AnsweredBy(answer, held));
            Choose(unit, explain: false);
            if (unit.Chosen.Path is not (AnswerPath.Rows or AnswerPath.Zones or AnswerPath.Learn))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The scan as states where the objects' synopses answer: each unit as its weight chose it (<see cref="UnitsAsync"/>), a
    /// rollup's totals or an object's counts as batches of states, an object's blocks through their statistics and the rows
    /// of the blocks they do not answer, the partial its family learned, or its rows, the objects read as rows side by side
    /// a part of the scan, which reads them as it would (<see cref="Part"/>). The counts and statistics of the units that
    /// read them are read ahead, as many at once as the session reads, and none of their sheets for counts, which say which
    /// values the object holds; the objects that read rows next are opened ahead, a few at a time; the partials of the
    /// objects that learn theirs next are learned ahead of the one served, as the scan's batches are decoded ahead of its
    /// consumer (<see cref="ScanOptions.Prefetch"/>).
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> StatesAsync(CountsAnswer answer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The question's family is kept up by the handle a while: the objects it appends learn its partials as they are written.
        // And it counts once among the family's askings, which share the cost of learning an object (LearnAlternative).
        if (_family is { } asked)
        {
            long now = _dataset.Options.TimeProvider.GetUtcNow().UtcTicks;
            _dataset.Partials.Asked(asked, now, _familySource is DatasetScanSource source ? source.Asking : 0, now - VortexDataset.KeptUpFor.Ticks);
        }

        (RollupChoice? rollups, List<Planned> units) = await UnitsAsync(answer, explain: false, read: true, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int width = Math.Max(1, _dataset.Session.Options.MaxConcurrentReads);
        CanonicalArena arena = new CanonicalArena(answer.Nodes);

        // The objects that read their rows next, opened ahead, a few at a time, so that each one's open is not a round trip
        // after the reads of the one before; left open in the handle's cache for the read that comes to them.
        int opensAhead = Math.Clamp(_dataset.Options.MaxOpenObjects / 4, 1, 8);
        int openSeen = 0;
        int learnSeen = 0;
        int learningAhead = 0;
        try
        {
            for (int o = 0; o < units.Count; o++)
            {
                // The counts and statistics of the next units read ahead, as many ahead as the session reads at once.
                for (int ahead = o; ahead < units.Count && ahead < o + width; ahead++)
                {
                    Begin(units[ahead], answer, stopped.Token);
                }

                // Each object that reads rows looked at once, as the window of those ahead reaches it.
                for (; openSeen < units.Count && openSeen < o + opensAhead; openSeen++)
                {
                    Planned next = units[openSeen];
                    if (next.Chosen.Path is AnswerPath.Rows or AnswerPath.Zones or AnswerPath.Learn && next.Opened is null && !_version.HoldsOpen(next.Held.Entry))
                    {
                        next.Opened = OpenAheadAsync(next.Held, stopped.Token);
                    }
                }

                Planned unit = units[o];
                PositionedObject held = unit.Held;
                AnswerPath path = unit.Chosen.Path;
                if (path is AnswerPath.Rollup or AnswerPath.Counts)
                {
                    StateRows? rows = null;
                    List<StateRows>? byBucket = null;
                    Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> read = unit.Counts!;
                    unit.Counts = null;
                    if (await read.ConfigureAwait(false) is ({ } counts, var sketched))
                    {
                        if (unit.Buckets is null)
                        {
                            rows = answer.Rows(counts, sketched, unit.Run is { } run ? run.Summaries : answer.SummariesOf(held));
                        }
                        else
                        {
                            byBucket = BucketRows(answer, counts, sketched, unit.Buckets);
                        }
                    }

                    if (rows is not null || byBucket is not null)
                    {
                        if (_metrics is { } metrics)
                        {
                            metrics.ObjectsAnswered += unit.Run?.Objects ?? 1;
                            metrics.RollupsAnswered += unit.Run is null ? 0 : 1;
                            metrics.StateRows += rows?.Count ?? 0;
                            for (int b = 0; b < (byBucket?.Count ?? 0); b++)
                            {
                                metrics.StateRows += byBucket![b].Count;
                            }
                        }

                        for (int at = 0; rows is not null && at < rows.Count; at += StateRows.BatchRows)
                        {
                            arena.Reset();
                            yield return rows.Batch(arena, at, Math.Min(StateRows.BatchRows, rows.Count - at), held.FirstRow);
                        }

                        for (int b = 0; b < (byBucket?.Count ?? 0); b++)
                        {
                            for (int at = 0; at < byBucket![b].Count; at += StateRows.BatchRows)
                            {
                                arena.Reset();
                                yield return byBucket[b].Batch(arena, at, Math.Min(StateRows.BatchRows, byBucket[b].Count - at), held.FirstRow);
                            }
                        }

                        continue;
                    }

                    if (unit.Run is { } spread)
                    {
                        // A rollup whose totals did not answer after all: its objects, each as its weight chooses, after the
                        // others; their reads begin as the loop meets them, the reads begun before them kept.
                        units.AddRange(await SpreadAsync(spread, answer, cancellationToken).ConfigureAwait(false));
                        units.RemoveAt(o);
                        o--;
                        continue;
                    }

                    // An object whose counts did not answer after all: its rows.
                    path = AnswerPath.Rows;
                }

                if (path == AnswerPath.Granules)
                {
                    // An object whose granules answer: the states of the granules its statistics answer, then the rows of the
                    // others, each run of them a part of the scan. The granules that hold a row a delete marked are read as
                    // rows too. A lane's piece of the object settles the granules it holds, and reads the rows it holds of
                    // those it cuts.
                    Task<(Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms)> statistics = unit.Statistics!;
                    unit.Statistics = null;
                    (Indexes.GranuleStats?[] stats, Indexes.GranuleHistograms?[] histograms) = await statistics.ConfigureAwait(false);
                    await held.Entry.ResolveAsync(_version.Pages, cancellationToken).ConfigureAwait(false);
                    (long lower, long upper) = Whole(held) ? (0, held.Entry.PhysicalRows) : PhysicalRange(held);
                    (GranuleStateRows? settled, List<RowRange> unsettled, int answered) = GranuleAnswer()!.Settle(
                        held, stats, histograms, held.Entry.Deletions, new RowRange(lower, upper), answer.Exclusion?.PlacesOf(held.Entry.Uid));
                    if (_metrics is { } counted)
                    {
                        counted.GranulesAnswered += answered;
                        counted.StateRows += settled?.Count ?? 0;
                    }

                    // Each batch copies its rows of states into the arena: their arrays go back to the pools after the last.
                    try
                    {
                        for (int at = 0; settled is not null && at < settled.Count; at += StateTable.BatchRows)
                        {
                            arena.Reset();
                            yield return settled.Batch(arena, at, Math.Min(StateTable.BatchRows, settled.Count - at), Math.Max(_from, held.FirstRow));
                        }
                    }
                    finally
                    {
                        settled?.Release();
                    }

                    // The granules the statistics leave, read as the rows path reads an object: in one pass over them, where a
                    // part a run of them waited a round trip each, 155 for the routes of a service under 5xx sorted by it.
                    if (UnsettledPart(held, unsettled, GranuleRows(stats)) is { } rest)
                    {
                        await foreach (RecordBatch batch in rest.ExecuteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            yield return batch;
                        }

                        continue;
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

                RowRange? piece = path is AnswerPath.Partial or AnswerPath.Learn ? null : Piece(held);
                if (path is AnswerPath.Partial or AnswerPath.Learn || piece is not null)
                {
                    // An object of the question's family answers from the partial it learned, read ahead with the counts, or
                    // learns it from its rows, or a lane's piece of it the partial of its rows; the partials of the objects
                    // after it that learn theirs from their rows are learned ahead. Each partial read in turn cost a round
                    // trip an object at one lane: 15 for QP-3.
                    Task<AggregationPartial>? learned = unit.Learning ?? unit.Partial ?? (piece is { } rows ? PiecePartialAsync(held, rows, stopped.Token) : null);
                    if (unit.Learning is not null)
                    {
                        unit.Learning = null;
                        learningAhead--;
                    }

                    unit.Partial = null;

                    for (learnSeen = Math.Max(learnSeen, o + 1); learnSeen < units.Count && learningAhead < _options.Prefetch; learnSeen++)
                    {
                        Planned next = units[learnSeen];
                        if (next.Chosen.Path == AnswerPath.Learn && next.Learning is null)
                        {
                            learningAhead++;
                            next.Learning = PartialAsync(next.Held, stopped.Token);
                        }
                    }

                    await foreach (RecordBatch batch in LearnedAsync(held, learned, piece?.Start ?? held.FirstRow, cancellationToken).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    continue;
                }

                // This object's rows and those of the objects after it that read theirs, up to the next answered otherwise: a
                // part of the scan, which reads them as it would, ahead and past their sheets. Under rollups, a part stops
                // where the rows the walk passed over begin, which a rollup answers.
                int last = o;
                while (last + 1 < units.Count && units[last + 1].Run is null && units[last + 1].Chosen.Path is AnswerPath.Rows or AnswerPath.Zones
                    && Piece(units[last + 1].Held) is null
                    && (rollups is null || units[last + 1].Held.FirstRow == units[last].Held.FirstRow + units[last].Held.Entry.Rows))
                {
                    last++;
                }

                PositionedObject end = units[last].Held;
                DatasetScanBuilder rowsPart = Part(_filter, Math.Max(_from, held.FirstRow), Math.Min(_to, end.FirstRow + end.Entry.Rows));
                await foreach (RecordBatch batch in rowsPart.ExecuteAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }

                o = last;
            }
        }
        finally
        {
            // The counts, statistics, partials and opens begun ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            List<Task> begun = [];
            foreach (Planned left in units)
            {
                foreach (Task? task in (ReadOnlySpan<Task?>)[left.Opened, left.Learning, left.Partial, left.Statistics, left.Counts])
                {
                    if (task is not null)
                    {
                        begun.Add(task);
                    }
                }
            }

            foreach (Task task in begun)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception) when (task.IsFaulted || task.IsCanceled)
                {
                }
            }

            arena.Reset();
        }
    }

    /// <summary>
    /// Begins the reads <paramref name="unit"/> answers from, once: a rollup's totals or counts by bucket, an object's counts,
    /// less those of its deleted rows or of the rows an identifier's postings find, or its columns' statistics over its blocks.
    /// </summary>
    private void Begin(Planned unit, CountsAnswer answer, CancellationToken cancellationToken)
    {
        if (unit.Begun)
        {
            return;
        }

        unit.Begun = true;
        switch (unit.Chosen.Path)
        {
            case AnswerPath.Rollup:
                unit.Counts = _dataset.ReadRollupCountsAsync(unit.Run!, unit.Field!.Value, answer.AsksSketches, answer.Values, unit.Buckets is not null, cancellationToken).AsTask();
                break;
            case AnswerPath.Counts:
                ObjectEntry entry = unit.Held.Entry;
                int field = unit.Field!.Value;
                unit.Counts = entry.HasDeletions ? LiveCountsAsync(entry, field, cancellationToken)
                    : answer.Exclusion is { } exclusion ? KeptCountsAsync(answer, unit.Held, field, exclusion.Field, exclusion.Found, cancellationToken)
                    : _dataset.ReadCountsAsync(entry, field, answer.AsksSketches, cancellationToken).AsTask();
                break;
            case AnswerPath.Granules:
                unit.Statistics = GranuleAnswer()!.Exclusion is { } excluded
                    ? StatisticsPlacedAsync(unit.Held, excluded, cancellationToken)
                    : _dataset.ReadGranuleStatsAsync(unit.Held.Entry, GranuleAnswer()!.Fields, GranuleAnswer()!.Histogrammed, cancellationToken).AsTask();
                break;
            case AnswerPath.Partial:
                unit.Partial = PartialAsync(unit.Held, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// The rows of the granules of <paramref name="held"/> its statistics leave, <paramref name="unsettled"/>, as one part of
    /// the scan that reads them alone (<see cref="_unsettled"/>), its own settling of blocks left aside; null when there are
    /// none, the object has rows marked, its granules are not of <paramref name="granuleRows"/> rows each but the last, or a
    /// range is not whole granules, which a part a range reads. The first range may start inside a granule and the last end
    /// inside one, where a lane's piece of the object cuts it: the part's rows leave out the rest.
    /// </summary>
    private DatasetScanBuilder? UnsettledPart(in PositionedObject held, List<RowRange> unsettled, long granuleRows)
    {
        long rows = held.Entry.PhysicalRows;
        if (unsettled.Count == 0 || held.Entry.HasDeletions || granuleRows <= 0)
        {
            return null;
        }

        ulong[] granules = new ulong[(int)((((rows + granuleRows - 1) / granuleRows) + 63) / 64)];
        for (int r = 0; r < unsettled.Count; r++)
        {
            RowRange range = unsettled[r];
            if ((r > 0 && range.Start % granuleRows != 0) || (r < unsettled.Count - 1 && range.End % granuleRows != 0 && range.End != rows))
            {
                return null;
            }

            for (long granule = range.Start / granuleRows; granule * granuleRows < range.End; granule++)
            {
                granules[granule >> 6] |= 1UL << (int)(granule & 63);
            }
        }

        DatasetScanBuilder part = Part(_filter, held.FirstRow + unsettled[0].Start, held.FirstRow + unsettled[^1].End);
        part._settles = null;
        part._unsettled = (held.Entry.Uid, granules);
        return part;
    }

    /// <summary>
    /// The rows of each granule <paramref name="stats"/> describe, the object's blocks, but the last, which holds as many or
    /// fewer; 0 when they describe none, or granules of other sizes. A handle's own block rows need not be its objects'.
    /// </summary>
    private static long GranuleRows(Indexes.GranuleStats?[] stats)
    {
        foreach (Indexes.GranuleStats? column in stats)
        {
            if (column is not { Count: > 0 } described)
            {
                continue;
            }

            long rows = described.Rows[0];
            for (int b = 1; b < described.Count; b++)
            {
                if (described.Rows[b] > rows || (described.Rows[b] < rows && b < described.Count - 1))
                {
                    return 0;
                }
            }

            return rows;
        }

        return 0;
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

    /// <summary>The objects <paramref name="rollup"/> covers, in key order, each as its weight chooses.</summary>
    private async Task<List<Planned>> SpreadAsync(RollupEntry rollup, CountsAnswer answer, CancellationToken cancellationToken)
    {
        List<Planned> spread = [];
        await foreach (PositionedObject under in _version.WalkAsync(Pruner(), 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            if (under.Level == rollup.Level && rollup.Covers(under.TreeKey.Span))
            {
                Planned unit = new Planned(under, AnsweredBy(answer, under));
                Choose(unit, explain: false);
                spread.Add(unit);
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
    /// The counts of column <paramref name="field"/> of the object <paramref name="held"/> under a value of identifier
    /// <paramref name="identifier"/> left out, less those of the rows that hold it (<see cref="CountsAnswer.Exclusion"/>): the
    /// rows its postings find for <paramref name="found"/>, those of the index of its partition first, its own otherwise, read
    /// beside the counts, counted and taken out (<see cref="DeletedCounts.Less"/>); none, and the object read as rows, when the
    /// postings cannot say or the counts do not give the rows kept exactly. A consolidated object keeps no postings of its own:
    /// its counts never answered.
    /// </summary>
    private async Task<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> KeptCountsAsync(
        CountsAnswer answer, PositionedObject held, int field, int identifier, VortexExpr found, CancellationToken cancellationToken)
    {
        ObjectEntry entry = held.Entry;
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
        List<RecordBatch>? rows = ExclusionLookup() is { } partitions && !entry.HasDeletions
            && await partitions.BlocksAsync(held, cancellationToken).ConfigureAwait(false) is ({ } places, int placeRows)
                ? await _dataset.ReadBlocksAsync(entry, places, placeRows, found, read, cancellationToken).ConfigureAwait(false)
                : await _dataset.LookupAsync(entry, found, read, cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return (null, null);
        }

        try
        {
            DeletedCounts.Builder taken = new DeletedCounts.Builder(_version.Schema.DType, [(field, shape)], read);
            long holding = 0;
            foreach (RecordBatch batch in rows)
            {
                SelectBy(batch, found);
                if (batch.SelectedRows > 0)
                {
                    taken.Add(batch);
                    holding += batch.SelectedRows;
                }
            }

            return holding == 0 ? (counts, null) : (DeletedCounts.Less(counts, taken.Finish().CountsOf(field)!), null);
        }
        finally
        {
            foreach (RecordBatch batch in rows)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>
    /// The rollups the scan takes in place of the objects they cover (<see cref="RollupChoice"/>), each weighing no more than
    /// its objects (<see cref="KeepsAsync"/>): a scan of the whole version's, for which the counts answer; null for another,
    /// which reads no rollup.
    /// </summary>
    private ValueTask<RollupChoice?> RollupsAsync(CountsAnswer answer, CancellationToken cancellationToken) =>
        _from == 0 && _to == long.MaxValue ? RollupChoice.OfAsync(_version, answer, Pruner(), cancellationToken, KeepsAsync) : default;
}
