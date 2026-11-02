using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Indexes;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>The units a dataset question's answer is chosen over (spec B, 4), outermost first.</summary>
internal enum UnitLevel
{
    /// <summary>The version.</summary>
    Version,

    /// <summary>A rollup's run, standing for the objects it covers.</summary>
    Run,

    /// <summary>A partition.</summary>
    Partition,

    /// <summary>An object.</summary>
    Object,

    /// <summary>A range of an object's granules.</summary>
    Granules,
}

/// <summary>How much of a unit an alternative answers, known from the entries alone.</summary>
internal enum Coverage
{
    /// <summary>None of it.</summary>
    None,

    /// <summary>Some of it, the rest by its rows.</summary>
    Part,

    /// <summary>All of it.</summary>
    Whole,
}

/// <summary>The alternatives that answer a unit of a dataset's question (spec B, 6.2), in the order that breaks a tie of weights.</summary>
internal enum AnswerPath
{
    /// <summary>The entry, which the walk holds: a count or an extreme its summaries give.</summary>
    Entry,

    /// <summary>A rollup's totals, for the objects it covers.</summary>
    Rollup,

    /// <summary>An object's counts of a column.</summary>
    Counts,

    /// <summary>The statistics of an object's columns over its granules, the granules they do not answer read as rows.</summary>
    Granules,

    /// <summary>An object's rows, the granules its zone maps settle taken from them.</summary>
    Zones,

    /// <summary>An object's rows.</summary>
    Rows,
}

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// A unit of an aggregation's states (spec B, 4) and how it is answered: a rollup taken in place of the objects it covers,
    /// or an object; the alternative its weight chose, what it costs, why the others lost, and the reads begun ahead of it.
    /// </summary>
    private sealed class Planned
    {
        /// <summary>An object of the walk.</summary>
        internal Planned(PositionedObject held, int? field)
        {
            Held = held;
            Field = field;
        }

        /// <summary>A rollup taken, its column <paramref name="field"/>, by bucket under <paramref name="buckets"/>.</summary>
        internal Planned(RollupEntry run, int field, ObjectSummaries?[]? buckets)
        {
            Run = run;
            Field = field;
            Buckets = buckets;
        }

        /// <summary>The object; default for a rollup.</summary>
        internal PositionedObject Held { get; }

        /// <summary>The rollup; null for an object.</summary>
        internal RollupEntry? Run { get; }

        /// <summary>The summaries of the buckets of a rollup taken by bucket; null otherwise.</summary>
        internal ObjectSummaries?[]? Buckets { get; }

        /// <summary>The column whose counts answer it; null when none does.</summary>
        internal int? Field { get; }

        internal UnitLevel Level => Run is null ? UnitLevel.Object : UnitLevel.Run;

        /// <summary>The unit's name: its key in the dataset.</summary>
        internal string Name => Run?.Key ?? Held.Entry.Key;

        /// <summary>The alternative its weight chose.</summary>
        internal Alternative Chosen { get; set; } = null!;

        /// <summary>What the alternative chosen costs.</summary>
        internal PathCosts.Cost Cost { get; set; }

        /// <summary>Its weight, at the dataset's cost profile.</summary>
        internal double Weight { get; set; }

        /// <summary>The share of its rows its key's sparse index keeps, which a read of its rows takes alone; 1 when unknown.</summary>
        internal double Kept { get; set; } = 1;

        /// <summary>Each other alternative and why it lost, for an explanation; null for a run.</summary>
        internal List<string>? Rejected { get; set; }

        /// <summary>Whether its reads began (<see cref="Begin"/>).</summary>
        internal bool Begun { get; set; }

        /// <summary>The read of its counts, begun ahead; null before or after.</summary>
        internal Task<(ColumnCounts?, ColumnSketches?)>? Counts { get; set; }

        /// <summary>The read of its columns' statistics over its granules and its measures' histograms, begun ahead; null before or after.</summary>
        internal Task<(GranuleStats?[] Stats, GranuleHistograms?[] Histograms, GranuleKeyRuns?[] Runs)>? Statistics { get; set; }

        /// <summary>Its open, begun ahead; null before or after.</summary>
        internal Task? Opened { get; set; }
    }

    /// <summary>
    /// One way of answering a unit (spec B, 6.1): whether it covers the unit from the entries alone, and what it costs at the
    /// dataset's profile, each region at the tier it lies in. What it gives, states or rows, the scan's loop reads through it.
    /// </summary>
    private abstract class Alternative(DatasetScanBuilder scan, AnswerPath path, UnitLevel level)
    {
        protected DatasetScanBuilder Scan { get; } = scan;

        internal AnswerPath Path { get; } = path;

        internal UnitLevel Level { get; } = level;

        /// <summary>The alternative's name in an explanation.</summary>
        internal string Name => Path.ToString().ToLowerInvariant();

        /// <summary>How much of <paramref name="unit"/> it answers, from the entries alone.</summary>
        internal abstract Coverage Covers(Planned unit);

        /// <summary>Why it covers none of <paramref name="unit"/>, for an explanation.</summary>
        internal virtual string Uncovered(Planned unit) => "does not cover it";

        /// <summary>What answering <paramref name="unit"/> through it costs.</summary>
        internal abstract PathCosts.Cost Cost(Planned unit);

        /// <summary>What it reads of <paramref name="unit"/>, in words.</summary>
        internal abstract string Describe(Planned unit, PathCosts.Cost cost);
    }

    /// <summary>
    /// The entry the walk holds (spec B, 6.2): the count of an object whose rows the filter keeps all, known from its
    /// summaries, or the extreme its exact summaries give, under a question of that shape (<see cref="WithShape"/>); weighs
    /// nothing. Count, any and extremes take it object by object (<see cref="EntryAnswers"/>).
    /// </summary>
    private sealed class EntryAlternative(DatasetScanBuilder scan) : Alternative(scan, AnswerPath.Entry, UnitLevel.Object)
    {
        internal override Coverage Covers(Planned unit) => Scan.EntryAnswers(unit.Held) ? Coverage.Whole : Coverage.None;

        internal override string Uncovered(Planned unit) => Scan._shape switch
        {
            ExtremeShape when !Scan._summaries => "summaries are off",
            ExtremeShape when Scan._filter is not null => "an extreme under a filter",
            ExtremeShape when !Scan.Whole(unit.Held) => "the scan covers part of it",
            ExtremeShape extreme => $"its summaries hold no exact extreme of {extreme.Path}",
            { ReadsColumns: false } and not AggregateShape and not KeysShape => "its summaries do not prove the filter keeps all its rows",
            _ => "the question asks more than a count of its rows or an extreme",
        };

        internal override PathCosts.Cost Cost(Planned unit) => default;

        internal override string Describe(Planned unit, PathCosts.Cost cost) =>
            Scan._shape is ExtremeShape extreme ? $"the {(extreme.Min ? "least" : "greatest")} value of {extreme.Path} its summaries give" : "the rows its entry counts";
    }

    /// <summary>
    /// Whether the entry of the object <paramref name="held"/> answers the question's shape: a count, or whether a row is,
    /// when the filter keeps every row the scan covers in it (<see cref="Settled"/>); an extreme of a column when the scan
    /// covers it whole with no filter and its summaries hold the column's exact extreme.
    /// </summary>
    private bool EntryAnswers(in PositionedObject held) => _shape switch
    {
        ExtremeShape extreme => _summaries && _filter is null && Whole(held) && Exact(held.Entry.Summaries, extreme.Path, extreme.Min, out _),
        { ReadsColumns: false } and not AggregateShape and not KeysShape => Settled(held, _countRange ??= CountRange()),
        _ => false,
    };

    // The range on the sort key the filter asks, for the entries that count their rows; found once.
    private KeyRange? _countRange;

    /// <summary>A rollup's totals, or its counts by bucket, for the objects it covers (<see cref="RollupChoice"/>).</summary>
    private sealed class RollupAlternative(DatasetScanBuilder scan) : Alternative(scan, AnswerPath.Rollup, UnitLevel.Run)
    {
        internal override Coverage Covers(Planned unit) => unit.Run is null ? Coverage.None : Coverage.Whole;

        internal override PathCosts.Cost Cost(Planned unit) => Scan.RollupCost(unit.Run!, unit.Field!.Value, unit.Buckets);

        internal override string Describe(Planned unit, PathCosts.Cost cost)
        {
            RollupEntry rollup = unit.Run!;
            CountsAnswer answer = Scan.Answer()!;
            RollupColumn kept = rollup.ColumnOf(unit.Field!.Value)!.Value;
            string column = Scan._version.Schema.Columns[unit.Field.Value].Name;
            bool sketched = answer.AsksSketches && kept.Regions(unit.Buckets is not null).Sketches is not null;
            string what = unit.Buckets is null ? $"totals of {column}" : $"counts by bucket of {column}";
            string read = VortexDataset.ReadsInPart(kept, answer.AsksSketches, answer.Values, unit.Buckets is not null)
                ? $"the header of the {what} and the chunks of {answer.Values!.Length} values"
                : $"the {what}";
            int answered = 0;
            foreach (ObjectSummaries? bucket in unit.Buckets ?? [])
            {
                answered += bucket is null ? 0 : 1;
            }

            return $"{read}{(sketched ? " and their sketches" : string.Empty)} over {rollup.Objects} objects, rank {rollup.Rank}"
                + (unit.Buckets is null ? string.Empty : $", {answered} of its {unit.Buckets.Length} buckets");
        }
    }

    /// <summary>An object's counts of the column that answers the question (<see cref="CountsAnswer"/>).</summary>
    private sealed class CountsAlternative(DatasetScanBuilder scan) : Alternative(scan, AnswerPath.Counts, UnitLevel.Object)
    {
        internal override Coverage Covers(Planned unit) => unit.Field is null ? Coverage.None : Coverage.Whole;

        internal override string Uncovered(Planned unit) =>
            Scan.Answer() is not { } answer ? Scan._answerDeclined ?? "does not cover it"
            : Scan.Covered(unit.Held) != unit.Held.Entry.Rows ? "the scan covers part of it"
            : answer.FieldOf(unit.Held, out string? declined) is null ? declined ?? "does not cover it"
            : "does not cover it";

        internal override PathCosts.Cost Cost(Planned unit)
        {
            ObjectEntry entry = unit.Held.Entry;
            CountsAnswer answer = Scan.Answer()!;
            int field = unit.Field!.Value;
            PathCosts.Cost counts = Scan._version.Costs.Regions(entry.Uid, [entry.Contents!.CountsOf(field), answer.AsksSketches ? entry.Contents.SketchesOf(field) : null]);

            // A delete's counts read beside, from the commit object they lie in; under a value of an identifier left out,
            // the rows its postings find, in three round trips cold: the index entries and chunk directories, the runs, the
            // chunks.
            int less = (entry.HasDeletions ? 1 : 0) + (answer.Exclusion is null ? 0 : 3);
            return less == 0 ? counts : counts + new PathCosts.Cost(less, entry.HasDeletions ? entry.NegativesAt.Length : 0);
        }

        internal override string Describe(Planned unit, PathCosts.Cost cost)
        {
            CountsAnswer answer = Scan.Answer()!;
            ObjectEntry entry = unit.Held.Entry;
            string column = Scan._version.Schema.Columns[unit.Field!.Value].Name;
            bool sketched = answer.AsksSketches && entry.Contents!.SketchesOf(unit.Field.Value) is not null;
            return (sketched ? $"the counts of {column} and their sketches" : $"the counts of {column}")
                + (entry.HasDeletions ? ", less those of its deleted rows" : string.Empty)
                + (answer.Exclusion is { } exclusion ? $", less those of the rows of {exclusion.Path} its postings find" : string.Empty);
        }
    }

    /// <summary>
    /// The statistics of an object's columns over its granules, the granules they do not answer read as rows
    /// (<see cref="GranuleAnswer"/>); of a lane's piece of it, the granules the piece holds. Under a value of an identifier
    /// left out, an object its postings may place the value in (<see cref="PlacedAsync"/>).
    /// </summary>
    private sealed class GranulesAlternative(DatasetScanBuilder scan) : Alternative(scan, AnswerPath.Granules, UnitLevel.Object)
    {
        internal override Coverage Covers(Planned unit) =>
            Scan.Answer() is not null && unit.Held.Entry.Contents is { GranuleStats.Count: > 0 } && Scan.Covered(unit.Held) > 0
            && Scan.GranuleAnswer() is { } granules && (granules.Exclusion is not { } excluded || !excluded.Identifier || excluded.Placed(unit.Held.Entry.Uid))
            && granules.Covers(unit.Held)
                ? Coverage.Part
                : Coverage.None;

        internal override string Uncovered(Planned unit) =>
            Scan.Answer() is null ? Scan._answerDeclined ?? "does not cover it"
            : unit.Held.Entry.Contents is not { GranuleStats.Count: > 0 } ? "it keeps no statistics over its granules"
            : Scan.Covered(unit.Held) <= 0 ? "the scan covers none of its rows"
            : Scan.GranuleAnswer() is not { } granules ? Scan._granuleDeclined ?? "does not cover it"
            : granules.Exclusion is { Identifier: true } excluded && !excluded.Placed(unit.Held.Entry.Uid) ? $"the value of {excluded.Path} left out is not placed in it"
            : granules.Declines(unit.Held) ?? "does not cover it";

        internal override PathCosts.Cost Cost(Planned unit)
        {
            // Under a value left out, the places its indexes give it (GranuleAnswer.Placing).
            (PathCosts.Cost statistics, PathCosts.Cost unanswered, _) = Scan.GranuleAnswer()!.Costs(unit.Held, unit.Kept);
            PathCosts.Cost placing = Scan.GranuleAnswer()!.Placing(unit.Held);
            return statistics + unanswered + placing + Scan.SheetsCost(unit.Held);
        }

        internal override string Describe(Planned unit, PathCosts.Cost cost)
        {
            (PathCosts.Cost statistics, PathCosts.Cost unanswered, _) = Scan.GranuleAnswer()!.Costs(unit.Held, unit.Kept);
            return $"the statistics over the granules of {Scan.GranuleAnswer()!.Fields.Length} columns, {statistics.Bytes} bytes in {statistics.Requests} requests, "
                + $"and the rows of the granules they do not answer, about {unanswered.Bytes} bytes in {unanswered.Requests}"
                + (Scan.GranuleAnswer()!.Exclusion is { } exclusion && exclusion.Placed(unit.Held.Entry.Uid)
                    ? $", those {Scan.GranuleAnswer()!.PlacingSource(unit.Held)} place a value of {exclusion.Path} in among them, for {Scan.GranuleAnswer()!.Placing(unit.Held).Bytes} bytes"
                    : string.Empty);
        }
    }

    /// <summary>
    /// An object's rows, the blocks its zone maps settle taken from them (<see cref="ZoneSettling"/>): an aggregation's whose
    /// filter, if any, the object's summaries prove true over every row. What its zone maps settle is known once it is
    /// open, which its rows need anyway: it weighs its rows, no more.
    /// </summary>
    private sealed class ZonesAlternative(DatasetScanBuilder scan) : Alternative(scan, AnswerPath.Zones, UnitLevel.Object)
    {
        internal override Coverage Covers(Planned unit) =>
            Scan._settles is not null && Scan._take is null && Scan._version.Schema.Holds(unit.Held.Entry) && Scan.MaySettle(unit.Held)
                ? Coverage.Part
                : Coverage.None;

        internal override string Uncovered(Planned unit) =>
            Scan._settles is null ? "the question asks no aggregation its zone maps settle"
            : Scan._take is not null ? "the scan takes rows by index"
            : !Scan._version.Schema.Holds(unit.Held.Entry) ? "its columns are an earlier schema's"
            : "its summaries do not prove the filter true over its rows, or the filter reads another column than the key's first";

        internal override PathCosts.Cost Cost(Planned unit) => Scan.RowsCost(unit.Held, unit.Kept);

        internal override string Describe(Planned unit, PathCosts.Cost cost) =>
            $"{RowsOf(unit)}, {cost.Bytes} bytes in {cost.Requests} requests, the blocks its zone maps settle taken from them";
    }

    /// <summary>An object's rows of the question's columns, the granules its key's index keeps of them.</summary>
    private sealed class RowsAlternative(DatasetScanBuilder scan) : Alternative(scan, AnswerPath.Rows, UnitLevel.Object)
    {
        internal override Coverage Covers(Planned unit) => Coverage.Whole;

        internal override PathCosts.Cost Cost(Planned unit) => Scan.RowsCost(unit.Held, unit.Kept);

        internal override string Describe(Planned unit, PathCosts.Cost cost) => $"{RowsOf(unit)}, {cost.Bytes} bytes in {cost.Requests} requests";
    }

    /// <summary>What a unit read as rows reads of them: all, or the granules its key's sparse index keeps.</summary>
    private static string RowsOf(Planned unit) =>
        unit.Kept < 1 ? string.Create(CultureInfo.InvariantCulture, $"the {unit.Kept:P1} of its rows its key's index keeps") : "its rows";

    /// <summary>
    /// The alternatives of a unit, in the order that breaks a tie: today's cascade (spec B, 6.3).
    /// </summary>
    private Alternative[] Alternatives => _alternatives ??=
    [
        new EntryAlternative(this), new RollupAlternative(this), new CountsAlternative(this), new GranulesAlternative(this),
        new ZonesAlternative(this), new RowsAlternative(this),
    ];

    private Alternative[]? _alternatives;

    /// <summary>
    /// Chooses how <paramref name="unit"/> is answered: of the alternatives that cover it, the one of least weight at the
    /// dataset's profile, the first in their order at a tie; with <paramref name="explain"/>, why each other lost.
    /// </summary>
    private void Choose(Planned unit, bool explain)
    {
        Alternative? best = null;
        PathCosts.Cost cost = default;
        double least = double.PositiveInfinity;
        List<string>? rejected = explain ? [] : null;
        foreach (Alternative alternative in Alternatives)
        {
            if (alternative.Level != unit.Level)
            {
                continue;
            }

            if (alternative.Covers(unit) == Coverage.None)
            {
                rejected?.Add($"{alternative.Name}: {alternative.Uncovered(unit)}");
                continue;
            }

            PathCosts.Cost its = alternative.Cost(unit);
            double weight = _version.Costs.Weight(its);
            if (weight < least)
            {
                if (best is not null)
                {
                    rejected?.Add(Weighs(best.Name, least));
                }

                (best, cost, least) = (alternative, its, weight);
            }
            else
            {
                rejected?.Add(Weighs(alternative.Name, weight));
            }
        }

        unit.Chosen = best!;
        unit.Cost = cost;
        unit.Weight = least;
        unit.Rejected = rejected;

        static string Weighs(string name, double weight) => string.Create(CultureInfo.InvariantCulture, $"{name}: weighs {weight:F0}");
    }

    /// <summary>
    /// What reading the column <paramref name="field"/> of <paramref name="rollup"/> costs: its totals, or its counts by bucket
    /// under <paramref name="buckets"/>, whole, or under the values the question asks their header and a chunk a value; and
    /// their sketches beside them for a quantile.
    /// </summary>
    private PathCosts.Cost RollupCost(RollupEntry rollup, int field, ObjectSummaries?[]? buckets)
    {
        CountsAnswer answer = Answer()!;
        RollupColumn kept = rollup.ColumnOf(field)!.Value;
        (IndexSegment? counted, IndexSegment? sketches, int countsHead, int sketchesHead) = kept.Regions(buckets is not null);
        IndexSegment? sketched = answer.AsksSketches ? sketches : null;
        if (!VortexDataset.ReadsInPart(kept, answer.AsksSketches, answer.Values, buckets is not null))
        {
            return _version.Costs.Regions(rollup.Uid, [counted, sketched]);
        }

        long bytes = InPart(counted!.Value, countsHead, answer.Values!.Length) + (sketched is { } some ? InPart(some, sketchesHead, answer.Values.Length) : 0);
        return new PathCosts.Cost(sketched is null ? 2 : 4, bytes);
    }

    /// <summary>
    /// What reading the rows of the object <paramref name="held"/> costs the scan: the question's columns, each value decoded,
    /// the filter's columns judged, and, under an aggregation, each value of the columns read folded, of the
    /// <paramref name="kept"/> share of its rows its key's sparse index keeps alone; and its sheets, read first when the
    /// filter asks values of a column.
    /// </summary>
    private PathCosts.Cost RowsCost(in PositionedObject held, double kept = 1)
    {
        PathCosts costs = _version.Costs;
        int[] fields = FieldsRead();
        double share = Math.Clamp(kept, 0, 1);
        PathCosts.Cost rows = share < 1 ? costs.Share(held.Entry, fields, share) : costs.Rows(held.Entry, fields);
        double processing = share * costs.Processing(held.Entry, default, FieldsJudged(), _states is not null || _settles is not null ? fields.Length : 0);
        return rows with { Nanoseconds = rows.Nanoseconds + processing } + SheetsCost(held);
    }

    /// <summary>What reading the sheets of the object <paramref name="held"/> costs, when the scan reads them (<see cref="ReadsSheets"/>); nothing otherwise.</summary>
    private PathCosts.Cost SheetsCost(in PositionedObject held) =>
        ReadsSheets(held) ? SheetPruning.Cost(held.Entry, _sheetColumns!, _version.Costs) : default;

    /// <summary>
    /// Whether the scan reads the sheets of the object <paramref name="held"/> before it (spec B, 5, the rule of
    /// consultation): the filter asks values of a column, the object keeps sheets, and reading them weighs less than the
    /// rows of the question's columns they may spare.
    /// </summary>
    internal bool ReadsSheets(in PositionedObject held) =>
        (SheetColumns() ?? (SheetPaths() is { Count: > 0 } ? [] : null)) is { } columns && SheetPruning.Keeps(held.Entry, _version.Schema)
        && _version.Costs.Weight(SheetPruning.Cost(held.Entry, columns, _version.Costs, SheetPaths()))
            < _version.Costs.Weight(_version.Costs.Rows(held.Entry, FieldsRead()));

    /// <summary>The shredded keys whose values the filter asks, which the walk reads where they lie (spec E, 3.3); empty for none. Found once.</summary>
    private List<(int Field, string Key, Types.Variants.PathClass Class)> SheetPaths() =>
        _sheetPaths ??= _summaries && _filter is { } filter ? SheetPruning.Paths(filter, _version.Schema.DType) : [];

    private List<(int Field, string Key, Types.Variants.PathClass Class)>? _sheetPaths;

    /// <summary>
    /// Whether the scan reads the sparse index of the object <paramref name="held"/> to pass over the granules its filter
    /// refutes (spec B, 5): it keeps one, and reading it weighs less than the rows of the question's columns it may spare,
    /// all but what a read of any of its granules pays, the object's open and a granule of each column, or a chunk where
    /// the object keeps no marks: nothing of an object its open reads whole, nor of one of a granule. An index in the tail
    /// the open reads costs nothing past that open, which the read of its granules pays.
    /// </summary>
    internal bool ReadsKeyIndex(in PositionedObject held)
    {
        if (held.Entry.Contents is not { KeyIndex: { } index } || held.Entry.PhysicalRows <= _dataset.Options.Write.BlockRows)
        {
            return false;
        }

        // What it may spare the store, the bytes and requests of the rows its granules leave out, their decoding aside.
        PathCosts costs = _version.Costs;
        int[] fields = FieldsRead();
        double spared = costs.Weight(costs.Rows(held.Entry, fields) with { Nanoseconds = 0 })
            - costs.Weight(costs.Share(held.Entry, fields, double.Epsilon) with { Nanoseconds = 0 });
        return (PathCosts.InOpen(held.Entry, index) ? 0 : costs.Weight(costs.Regions(held.Entry.Uid, [index]))) < spared;
    }

    /// <summary>The columns whose sheets the walk reads, when the filter asks values of a column; null otherwise. Found once.</summary>
    private List<int>? SheetColumns()
    {
        if (!_sheetColumnsKnown)
        {
            _sheetColumns = _summaries && _filter is { } filter && SheetPruning.Asks(filter, _version.Schema.DType)
                ? SheetPruning.Columns(filter, _version.Schema.DType)
                : null;
            _sheetColumnsKnown = true;
        }

        return _sheetColumns;
    }

    private List<int>? _sheetColumns;
    private bool _sheetColumnsKnown;

    /// <summary>The top-level columns the filter reads, by their places, each once, ascending: those a pushed predicate judges. Found once.</summary>
    private int[] FieldsJudged()
    {
        if (_fieldsJudged is null)
        {
            SortedSet<int> judged = [];
            foreach (string path in _filter is null ? [] : FilterPaths())
            {
                int dot = path.IndexOf('.', StringComparison.Ordinal);
                if (_version.Schema.DType.IndexOfField(dot < 0 ? path : path[..dot]) is int field and >= 0)
                {
                    judged.Add(field);
                }
            }

            _fieldsJudged = [.. judged];
        }

        return _fieldsJudged;
    }

    private int[]? _fieldsJudged;

    /// <summary>
    /// The units of a question, each chosen (<see cref="Choose"/>): the rollups the scan takes, each weighing no more than
    /// its objects (<see cref="KeepsAsync"/>), then the objects the walk meets that no rollup covers. With
    /// <paramref name="read"/>, the units whose choice reads rows are passed through their sheets, as a read does
    /// (<see cref="SheetsKeptAsync"/>); without, nothing but the entries is read, and the sheets weigh in each object's cost.
    /// </summary>
    private async ValueTask<(RollupChoice? Rollups, List<Planned> Units)> UnitsAsync(CountsAnswer? answer, bool explain, bool read, CancellationToken cancellationToken)
    {
        RollupChoice? rollups = answer is null ? null : await RollupsAsync(answer, cancellationToken).ConfigureAwait(false);
        List<Planned> units = [];
        foreach ((RollupEntry rollup, int field, ObjectSummaries?[]? buckets) in rollups?.Chosen ?? [])
        {
            Planned unit = new Planned(rollup, field, buckets);
            Choose(unit, explain);
            unit.Rejected = explain && _objectsWeighed?.TryGetValue(rollup.Key, out string? why) == true ? [why!] : unit.Rejected;
            units.Add(unit);
        }

        // The objects the walk keeps, less those whose keys the filter refutes, as a read of rows passes them over.
        KeyPruning? keys = _summaries && _indexes && _take is null ? KeyPruning.Of(_version.Schema, _filter) : null;
        Queue<(Planned Unit, Task<(List<(int First, int End)> Ranges, Indexes.GranuleKeys Keys)?> Reading)>? reading = null;
        await foreach (PositionedObject held in _version.WalkAsync(
            Pruner(), _from, _to, read && _considers ? _metrics : null, cancellationToken, rollups is null ? null : rollups.Covers).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> first = default;
            if (keys is not null && KeyRefutes(keys, held, out first))
            {
                if (read && _considers && _metrics is { } skipped)
                {
                    skipped.ObjectsPrunedByKey++;
                }

                continue;
            }

            if (Covered(held) > 0 && rollups?.CoverOf(held) is not >= 0)
            {
                if (answer is not null && GranuleAnswer()?.Exclusion is not null)
                {
                    await PlacedAsync(held, cancellationToken).ConfigureAwait(false);
                }

                Planned unit = new Planned(held, answer is null ? null : AnsweredBy(answer, held));
                Choose(unit, explain);
                units.Add(unit);

                // The share of its rows its key's sparse index keeps, which a read of them takes alone: found when so narrow
                // a read could beat the choice, through the open an analysis makes anyway, or as the handle holds it. The
                // indexes of the next objects are asked while the walk goes on, as the rows' walk asks them (KeysAhead), the
                // oldest awaited when as many wait: each awaited in turn cost QP-1 a round trip an object on one lane.
                if (keys is not null && unit.Weight > _version.Costs.Weight(RowsCost(held, double.Epsilon)))
                {
                    ValueTask<(List<(int First, int End)> Ranges, Indexes.GranuleKeys Keys)?> asked = KeptGranulesAsync(held, keys, first, read, cancellationToken);
                    if (asked.IsCompletedSuccessfully)
                    {
                        Refine(unit, await asked.ConfigureAwait(false), explain);
                        continue;
                    }

                    (reading ??= new Queue<(Planned, Task<(List<(int First, int End)> Ranges, Indexes.GranuleKeys Keys)?>)>(KeysAhead)).Enqueue((unit, asked.AsTask()));
                    if (reading.Count >= KeysAhead)
                    {
                        (Planned waited, Task<(List<(int First, int End)> Ranges, Indexes.GranuleKeys Keys)?> its) = reading.Dequeue();

                        // Each read was asked by this walk, a few objects before.
#pragma warning disable VSTHRD003
                        Refine(waited, await its.ConfigureAwait(false), explain);
#pragma warning restore VSTHRD003
                    }
                }
            }
        }

        while (reading is not null && reading.TryDequeue(out (Planned Unit, Task<(List<(int First, int End)> Ranges, Indexes.GranuleKeys Keys)?> Reading) last))
        {
#pragma warning disable VSTHRD003
            Refine(last.Unit, await last.Reading.ConfigureAwait(false), explain);
#pragma warning restore VSTHRD003
        }

        return (rollups, read ? await SheetsKeptAsync(units, cancellationToken).ConfigureAwait(false) : units);
    }

    /// <summary>
    /// Chooses <paramref name="unit"/> again with the share of its rows its key's sparse index keeps, as <paramref name="kept"/>
    /// says, when it said one.
    /// </summary>
    private void Refine(Planned unit, (List<(int First, int End)> Ranges, Indexes.GranuleKeys Keys)? kept, bool explain)
    {
        if (kept is not ({ } ranges, { } keys))
        {
            return;
        }

        long rows = 0;
        foreach ((int from, int end) in ranges)
        {
            rows += Math.Min((long)end * keys.GranuleRows, keys.Rows) - ((long)from * keys.GranuleRows);
        }

        unit.Kept = (double)rows / Math.Max(keys.Rows, 1);
        Choose(unit, explain);
    }

    /// <summary>
    /// <paramref name="units"/> less the objects whose column sheets refute the filter (<see cref="SheetPruning"/>), in their
    /// order: the sheets of the objects whose choice reads rows are read side by side, <see cref="SheetBatch"/> objects at a
    /// time, through their partitions' indexes as the walk reads them; an object its counts or its entry answer reads none,
    /// its synopses saying which values it holds, nor does a rollup. Nothing is read when the filter asks no value of a
    /// column.
    /// </summary>
    private async ValueTask<List<Planned>> SheetsKeptAsync(List<Planned> units, CancellationToken cancellationToken)
    {
        if (!_summaries || _filter is not { } filter || SheetColumns() is not { } columns)
        {
            return units;
        }

        HashSet<long> answered = [];
        foreach (Planned unit in units)
        {
            if (unit.Run is null && unit.Chosen.Path is AnswerPath.Counts or AnswerPath.Entry)
            {
                answered.Add(unit.Held.FirstRow);
            }
        }

        List<Planned> kept = new List<Planned>(units.Count);
        int first = 0;
        for (; first < units.Count && units[first].Run is not null; first++)
        {
            kept.Add(units[first]);
        }

        List<PositionedObject> batch = new List<PositionedObject>(SheetBatch);
        DatasetScanCounters? metrics = _considers ? _metrics : null;
        PartitionLocates? partitions = LocatesOf(filter, columns);
        Func<PositionedObject, bool> consulted = held => !answered.Contains(held.FirstRow) && ReadsSheets(held);
        for (int from = first; from < units.Count; from += SheetBatch)
        {
            int to = Math.Min(units.Count, from + SheetBatch);
            batch.Clear();
            for (int u = from; u < to; u++)
            {
                batch.Add(units[u].Held);
            }

            List<PositionedObject> passed = await SheetPruning.KeptAsync(
                _dataset, _version.Schema, batch, filter, columns, metrics, consulted, cancellationToken, _narrowing ??= Narrow, partitions, SheetPaths()).ConfigureAwait(false);
            for (int u = from, p = 0; u < to; u++)
            {
                if (p < passed.Count && passed[p].FirstRow == units[u].Held.FirstRow)
                {
                    kept.Add(units[u]);
                    p++;
                }
            }
        }

        return kept;
    }

    /// <summary>
    /// Whether the rollup <paramref name="rollup"/>, read through its column <paramref name="field"/>, by bucket under
    /// <paramref name="buckets"/>, weighs no more than the objects it covers at their cheapest (spec B, 6.3): each reads a
    /// request at least, which bounds most rollups at once; past that bound, its objects are weighed each, as the walk
    /// would meet them, once for every rollup.
    /// </summary>
    private async ValueTask<bool> KeepsAsync(RollupEntry rollup, int field, ObjectSummaries?[]? buckets, CancellationToken cancellationToken)
    {
        double rolled = _version.Costs.Weight(RollupCost(rollup, field, buckets));
        double floor = rollup.Objects * (double)_version.Costs.Profile.RequestBytes;
        if (rolled <= floor)
        {
            (_objectsWeighed ??= [])[rollup.Key] = string.Create(CultureInfo.InvariantCulture, $"its {rollup.Objects} objects: weigh {floor:F0} at least, a request each");
            return true;
        }

        if (_weights is null)
        {
            // Each object of the version the summaries keep, at its cheapest, in the order of the levels and the tree keys.
            List<(int Level, ReadOnlyMemory<byte> Key, double Weight)> weights = [];
            await foreach (PositionedObject held in _version.WalkAsync(Pruner(), 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
            {
                Planned unit = new Planned(held, AnsweredBy(Answer()!, held));
                Choose(unit, explain: false);
                weights.Add((held.Level, held.TreeKey, unit.Weight));
            }

            weights.Sort(static (left, right) => left.Level != right.Level ? left.Level.CompareTo(right.Level) : left.Key.Span.SequenceCompareTo(right.Key.Span));
            _weights = weights;
        }

        double objects = 0;
        int at = _weights.BinarySearch((rollup.Level, rollup.FirstKey, 0), WeightOrder.Instance);
        for (at = at < 0 ? ~at : at; at < _weights.Count && _weights[at].Level == rollup.Level && rollup.Covers(_weights[at].Key.Span); at++)
        {
            objects += _weights[at].Weight;
        }

        (_objectsWeighed ??= [])[rollup.Key] = string.Create(CultureInfo.InvariantCulture, $"its {rollup.Objects} objects: weigh {objects:F0}");
        return rolled <= objects;
    }

    // The objects of the version at their cheapest, for the rollups whose bound did not decide; null until one did not.
    private List<(int Level, ReadOnlyMemory<byte> Key, double Weight)>? _weights;

    // Why each rollup taken beat its objects, by its key, for an explanation.
    private Dictionary<string, string>? _objectsWeighed;

    /// <summary>The order of the objects' weights: by level, then by tree key.</summary>
    private sealed class WeightOrder : IComparer<(int Level, ReadOnlyMemory<byte> Key, double Weight)>
    {
        internal static WeightOrder Instance { get; } = new WeightOrder();

        public int Compare((int Level, ReadOnlyMemory<byte> Key, double Weight) x, (int Level, ReadOnlyMemory<byte> Key, double Weight) y) =>
            x.Level != y.Level ? x.Level.CompareTo(y.Level) : x.Key.Span.SequenceCompareTo(y.Key.Span);
    }
}
