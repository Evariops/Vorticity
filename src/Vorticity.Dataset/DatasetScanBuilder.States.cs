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
    /// How one lane's plan answers the object <paramref name="held"/>, which the scan covers whole (<see cref="Choose"/>), the
    /// <paramref name="kept"/> share of its rows its key's sparse index keeps known, or 1, the value the filter leaves out
    /// placed in it or not as <see cref="PlacedAsync"/> noted; and, that share unknown, whether it could change the choice,
    /// which then reads the index first, as <see cref="UnitsAsync"/> does. The lanes that cut an object answer through its
    /// granules one that one lane answers so.
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
        if (GranuleAnswer()?.Exclusion is not { } excluded
            || (excluded.Key is null && !CountsAnswer.NoNull(held.Summaries(_version.Schema), excluded.Path, _version.Schema.DType.GetField(excluded.Field))))
        {
            return false;
        }

        // Another column's value: where its locate, or the Bloom filters or postings its table of contents names, place it; a
        // shredded key's, where those of its variant column do, its rows that hold no value of the key judged by the granules.
        if (!excluded.Identifier)
        {
            bool located = held.Entry.Contents is { } contents
                && ((excluded.Key is null && contents.LocateOf(excluded.Field) is not null) || Names(contents, excluded));
            if (located)
            {
                excluded.Place(held.Entry.Uid);
            }

            return located;
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
    /// leaves out (<see cref="Dataset.GranuleAnswer.Exclusion"/>), with no row read: of an identifier, the places the index
    /// of its partition gives it, else those its own postings give, two round trips cold; of another column, the granules
    /// its locate gives it, one round trip, else the blocks its own Bloom filters or postings leave; null when none says.
    /// </summary>
    private async Task<List<RowRange>?> PlacesOfAsync(PositionedObject held, ExcludedValue excluded, CancellationToken cancellationToken)
    {
        VortexExpr found = excluded.Found;
        if (!excluded.Identifier)
        {
            return (excluded.Key is null ? await LocatedAsync(held, excluded, cancellationToken).ConfigureAwait(false) : null)
                ?? await _dataset.PlacesAsync(held.Entry, found, cancellationToken).ConfigureAwait(false);
        }

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
    /// The runs of the rows of the object <paramref name="held"/> whose granules its locate of the column of the value
    /// <paramref name="excluded"/> places the value in, one round trip cold; null when it keeps none, or one of other granules.
    /// </summary>
    private async Task<List<RowRange>?> LocatedAsync(PositionedObject held, ExcludedValue excluded, CancellationToken cancellationToken)
    {
        long granuleRows = _dataset.Options.Write.BlockRows;
        long rows = held.Entry.PhysicalRows;
        if (held.Entry.Contents?.LocateOf(excluded.Field) is null || granuleRows <= 0
            || (await _dataset.ReadLocatesAsync(held.Entry, [excluded.Field], cancellationToken).ConfigureAwait(false))[0] is not { } locate
            || locate.Granules != (rows + granuleRows - 1) / granuleRows
            || GranuleAlgebra.May(excluded.Found, _version.Schema.DType, field => field == excluded.Field ? locate : null, locate.Granules) is not { } granules)
        {
            return null;
        }

        List<RowRange> runs = [];
        for (int granule = 0; granule < locate.Granules; granule++)
        {
            if (((granules[granule >> 6] >> (granule & 63)) & 1) == 0)
            {
                continue;
            }

            long start = granule * granuleRows;
            long end = Math.Min(start + granuleRows, rows);
            if (runs.Count > 0 && runs[^1].End == start)
            {
                runs[^1] = new RowRange(runs[^1].Start, end);
            }
            else
            {
                runs.Add(new RowRange(start, end));
            }
        }

        return runs;
    }

    /// <summary>
    /// Whether the table of contents <paramref name="contents"/> names an index of the column of <paramref name="excluded"/>, or
    /// of its key, that places a value by its blocks with no open (<see cref="VortexDataset.PlacesAsync"/>): sorted runs,
    /// postings, or Bloom filters (<see cref="Dataset.GranuleAnswer.Places"/>).
    /// </summary>
    private static bool Names(Indexes.ObjectContents contents, ExcludedValue excluded)
    {
        foreach (Indexes.LocatingEntry located in contents.Locating ?? [])
        {
            if (Dataset.GranuleAnswer.Places(located, excluded))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The lookup of the value the filter leaves out (<see cref="CountsAnswer.Exclusion"/>) over the partitions' indexes, made
    /// once; null for none, or a version that keeps none.
    /// </summary>
    private PartitionLookup? ExclusionLookup() => (GranuleAnswer()?.Exclusion ?? Answer()?.Exclusion)?.Lookup(_dataset, _version);

    /// <summary>
    /// The statistics of the granules of the object <paramref name="held"/> an answer through them reads, and beside them the
    /// places of the value <paramref name="excluded"/> the filter leaves out, noted where the settling of its granules takes
    /// them (<see cref="ExcludedValue.PlacesOf"/>).
    /// </summary>
    private async Task<(Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms, Indexes.GranuleKeyRuns?[] Runs)> StatisticsPlacedAsync(
        PositionedObject held, ExcludedValue excluded, CancellationToken cancellationToken)
    {
        Task<List<RowRange>?> placing = PlacesOfAsync(held, excluded, cancellationToken);
        (Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms, Indexes.GranuleKeyRuns?[] Runs) statistics;
        try
        {
            statistics = await _dataset.ReadGranuleStatsAsync(
                held.Entry, GranuleAnswer()!.Fields, GranuleAnswer()!.Histogrammed, GranuleAnswer()!.Components, cancellationToken, GranuleAnswer()!.PathsOfFields, GranuleAnswer()!.Unread(held))
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

    /// <summary>The rows of the object <paramref name="held"/> its granules' statistics are expected to leave, read as rows (<see cref="Dataset.GranuleAnswer.Unanswered(in PositionedObject, bool)"/>).</summary>
    internal long GranulesLeave(in PositionedObject held) => (long)Math.Ceiling(GranuleAnswer()!.Unanswered(held, edges: true) * held.Entry.Rows);

    /// <summary>
    /// Reads the statistics of the granules of the object <paramref name="held"/> an answer through them reads into the
    /// handle's memo, where each lane's piece of the object finds them: begun as its pieces, each read them.
    /// </summary>
    internal Task GranuleStatsAheadAsync(in PositionedObject held, CancellationToken cancellationToken) =>
        _dataset.ReadGranuleStatsAsync(
            held.Entry, GranuleAnswer()!.Fields, GranuleAnswer()!.Histogrammed, GranuleAnswer()!.Components, cancellationToken, GranuleAnswer()!.PathsOfFields, GranuleAnswer()!.Unread(held))
            .AsTask();

    /// <summary>How the objects' counts answer the states the scan asks, worked out once; null when they answer none of them.</summary>
    private CountsAnswer? Answer()
    {
        if (!_answerKnown)
        {
            string? declined = "the question asks no states";
            _answer = _states is { } request ? CountsAnswer.Of(request, _filter, _version, out declined) : null;
            _answerDeclined = declined;
            _answerKnown = true;
        }

        return _answer;
    }

    /// <summary>How the statistics of the objects' blocks answer the states the scan asks, worked out once; null when they answer none.</summary>
    private GranuleAnswer? GranuleAnswer()
    {
        if (!_granuleAnswerKnown)
        {
            string? declined = _answerDeclined ?? "the question asks no states";
            _granuleAnswer = _states is { } request && Answer() is { } counts
                ? Dataset.GranuleAnswer.Of(request, _filter, _version, counts, _dataset.Options.Write.BlockRows, out declined)
                : null;
            _granuleDeclined = declined;
            _granuleAnswerKnown = true;
        }

        return _granuleAnswer;
    }

    /// <summary>The column whose counts answer the object <paramref name="held"/> for <paramref name="answer"/>: one the scan covers whole; null for its rows.</summary>
    private int? AnsweredBy(CountsAnswer answer, in PositionedObject held) =>
        Covered(held) == held.Entry.Rows ? answer.FieldOf(held) : null;

    /// <summary>
    /// Whether the synopses of an object the scan covers answer what it asks as states, read from the entries alone: an object
    /// whose weight chooses its counts or its blocks over its rows.
    /// </summary>
    internal async ValueTask<bool> AnswersAnyAsync(CancellationToken cancellationToken)
    {
        if (Answer() is not { } answer || !await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await foreach (PositionedObject held in _version.WalkAsync(Pruner(), _from, _to, null, cancellationToken).ConfigureAwait(false))
        {
            if (GranuleAnswer()?.Exclusion is not null)
            {
                await PlacedAsync(held, cancellationToken).ConfigureAwait(false);
            }

            Planned unit = new Planned(held, AnsweredBy(answer, held));
            Choose(unit, explain: false);
            if (unit.Chosen.Path is not (AnswerPath.Rows or AnswerPath.Zones))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The scan as states where the objects' synopses answer: each unit as its weight chose it (<see cref="UnitsAsync"/>), a
    /// rollup's totals or an object's counts as batches of states, an object's blocks through their statistics and the rows
    /// of the blocks they do not answer, or its rows, the objects read as rows side by side a part of the scan, which reads
    /// them as it would (<see cref="Part"/>). The counts and statistics of the units that read them are read ahead, as many
    /// at once as the session reads, and none of their sheets for counts, which say which values the object holds; the
    /// objects that read rows next are opened ahead, a few at a time.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> StatesAsync(CountsAnswer answer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (RollupChoice? rollups, List<Planned> units) = await UnitsAsync(answer, explain: false, read: true, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int width = Math.Max(1, _dataset.Session.Options.MaxConcurrentReads);
        CanonicalArena arena = new CanonicalArena(answer.Nodes);

        // The objects that read their rows next, opened ahead, a few at a time, so that each one's open is not a round trip
        // after the reads of the one before; left open in the handle's cache for the read that comes to them.
        int opensAhead = Math.Clamp(_dataset.Options.MaxOpenObjects / 4, 1, 8);
        int openSeen = 0;
        int begunUpTo = 0;

        // The rows of states of one object's counts after another, and the batch of states the stream gave last, bound
        // again into the next: the consumer is done with a batch when it asks for the next, as the arena's reset says.
        StateRows? objectRows = null;
        RecordBatch? spare = null;
        try
        {
            for (int o = 0; o < units.Count; o++)
            {
                // The counts and statistics of the next units read ahead, as many ahead as the session reads at once: from the
                // last begun on, where each unit asked again every unit of its window whether it had begun.
                for (; begunUpTo < units.Count && begunUpTo < o + width; begunUpTo++)
                {
                    Begin(units[begunUpTo], answer, stopped.Token);
                }

                // Each object that reads rows looked at once, as the window of those ahead reaches it.
                for (; openSeen < units.Count && openSeen < o + opensAhead; openSeen++)
                {
                    Planned next = units[openSeen];
                    if (next.Chosen.Path is AnswerPath.Rows or AnswerPath.Zones && next.Opened is null && !_version.HoldsOpen(next.Held.Entry))
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
                    ValueTask<(Indexes.ColumnCounts?, Indexes.ColumnSketches?)> read = unit.Counts!.Value;
                    unit.Counts = null;
                    if (await read.ConfigureAwait(false) is ({ } counts, var sketched))
                    {
                        if (unit.Buckets is null)
                        {
                            objectRows ??= new StateRows(answer);
                            rows = answer.Fill(objectRows, counts, sketched, unit.Run is { } run ? run.Summaries : answer.SummariesOf(held)) ? objectRows : null;
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
                            spare?.Dispose();
                            arena.Reset();
                            yield return spare = rows.Batch(arena, at, Math.Min(StateRows.BatchRows, rows.Count - at), held.FirstRow, spare);
                        }

                        for (int b = 0; b < (byBucket?.Count ?? 0); b++)
                        {
                            for (int at = 0; at < byBucket![b].Count; at += StateRows.BatchRows)
                            {
                                spare?.Dispose();
                                arena.Reset();
                                yield return spare = byBucket[b].Batch(arena, at, Math.Min(StateRows.BatchRows, byBucket[b].Count - at), held.FirstRow, spare);
                            }

                            byBucket[b].Release();
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
                    Task<(Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms, Indexes.GranuleKeyRuns?[] Runs)> statistics = unit.Statistics!;
                    unit.Statistics = null;
                    (Indexes.GranuleStats?[] stats, Indexes.GranuleHistograms?[] histograms, Indexes.GranuleKeyRuns?[] runs) = await statistics.ConfigureAwait(false);
                    await held.Entry.ResolveAsync(_version.Pages, cancellationToken).ConfigureAwait(false);
                    (long lower, long upper) = Whole(held) ? (0, held.Entry.PhysicalRows) : PhysicalRange(held);
                    (GranuleStateRows? settled, List<RowRange> unsettled, int answered) = GranuleAnswer()!.Settle(
                        held, stats, histograms, runs, held.Entry.Deletions, new RowRange(lower, upper), GranuleAnswer()!.Exclusion?.PlacesOf(held.Entry.Uid));
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
                            spare?.Dispose();
                            arena.Reset();
                            yield return spare = settled.Batch(arena, at, Math.Min(StateTable.BatchRows, settled.Count - at), Math.Max(_from, held.FirstRow), spare);
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

                // This object's rows and those of the objects after it that read theirs, up to the next answered otherwise: a
                // part of the scan, which reads them as it would, ahead and past their sheets. Under rollups, a part stops
                // where the rows the walk passed over begin, which a rollup answers.
                int last = o;
                while (last + 1 < units.Count && units[last + 1].Run is null && units[last + 1].Chosen.Path is AnswerPath.Rows or AnswerPath.Zones
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
            // The counts, statistics and opens begun ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            List<Task> begun = [];
            foreach (Planned left in units)
            {
                foreach (Task? task in (ReadOnlySpan<Task?>)[left.Opened, left.Statistics])
                {
                    if (task is not null)
                    {
                        begun.Add(task);
                    }
                }

                if (left.Counts is { } counting)
                {
                    left.Counts = null;
                    begun.Add(counting.AsTask());
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

            spare?.Dispose();
            objectRows?.Release();
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

        // A unit's counts are kept to be awaited once, by the loop that comes to it or by the cleanup that follows a stop.
#pragma warning disable CA2012
        switch (unit.Chosen.Path)
        {
            case AnswerPath.Rollup:
                unit.Counts = _dataset.ReadRollupCountsAsync(unit.Run!, unit.Field!.Value, answer.AsksSketches, answer.Values, unit.Buckets is not null, cancellationToken);
                break;
            case AnswerPath.Counts:
                ObjectEntry entry = unit.Held.Entry;
                int field = unit.Field!.Value;
                unit.Counts = entry.HasDeletions ? new(LiveCountsAsync(entry, field, cancellationToken))
                    : answer.Exclusion is { } exclusion ? new(KeptCountsAsync(answer, unit.Held, field, exclusion.Field, exclusion.Found, cancellationToken))
                    : _dataset.ReadCountsAsync(entry, field, answer.AsksSketches, cancellationToken);
                break;
            case AnswerPath.Granules:
                unit.Statistics = GranuleAnswer()!.Exclusion is { } excluded && excluded.Placed(unit.Held.Entry.Uid)
                    ? StatisticsPlacedAsync(unit.Held, excluded, cancellationToken)
                    : _dataset.ReadGranuleStatsAsync(
                        unit.Held.Entry, GranuleAnswer()!.Fields, GranuleAnswer()!.Histogrammed, GranuleAnswer()!.Components, cancellationToken, GranuleAnswer()!.PathsOfFields,
                        GranuleAnswer()!.Unread(unit.Held)).AsTask();
                break;
        }
#pragma warning restore CA2012
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
