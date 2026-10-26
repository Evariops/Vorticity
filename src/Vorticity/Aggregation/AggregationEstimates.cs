using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// An aggregation's estimates, tier after tier (<see cref="Aggregation.EstimateAsync"/>): the question's rows cut into
/// ranges, file by file and object by object (<see cref="ScanSource.StrataAsync"/>), runs of them gathered into strata,
/// each stratum's ranges drawn in an order at random; each tier reads the ranges its share adds, each as a part of the
/// question giving its groups' states (<see cref="AggregationQuery.Ranged"/>), merges them, and scales their totals.
/// </summary>
/// <remarks>
/// <para>
/// A stratum's ranges are its primary units, and a group's total in a range one draw, zero where the range holds none of
/// its rows. The ranges are not of one size, and the rows of a stratum are known: a total is estimated as the ratio of the
/// draws' totals to their rows, times the stratum's rows, <c>Ŷ_h = L_h Σ_i y_hi / Σ_i ℓ_hi</c>, its variance
/// <c>M_h² (1 − n_h / M_h) s_d² / n_h</c>, <c>s_d²</c> the spread of the residuals <c>y_hi − r̂_h ℓ_hi</c> among the
/// draws, scaled by the stratum's mean rows a range over the draws'; with ranges of one size, the plain expansion
/// <c>(M_h / n_h) Σ_i y_hi</c>. A mean is the ratio of two totals, its variance linearised. A margin is Student's 97.5 %
/// quantile, of as many degrees as the draws less one a stratum, times the standard error.
/// </para>
/// <para>
/// A stratum gathers objects that follow one another until it holds the ranges the first tier draws two of, so that
/// every stratum draws two at least: a stratum of one draw has no spread to tell. The states merged are exact, and the
/// last tier, every range read, is the question's own answer to the bits.
/// </para>
/// </remarks>
internal sealed class EstimateRun
{
    /// <summary>The ranges read at once over a source whose reads are round trips: an object store's latency, not its cores, bounds them.</summary>
    private const int RoundTripRanges = 16;

    /// <summary>The sums of a group's rows by stratum: the rows, their squares, their products with the ranges' rows.</summary>
    private const int Rows = 3;

    /// <summary>The sums of a measure by stratum and group: values, squares, sum, square, product, values and sum by the ranges' rows.</summary>
    private const int Measured = 7;

    private readonly AggregationQuery _question;
    private readonly EstimateOptions _options;
    private readonly ScanCounters _counters = new ScanCounters();
    private readonly ColumnShape[] _columns;
    private readonly int[] _inputs;

    // The sample's states, merged exactly, and its groups; the count of rows among the aggregates, -1 for none; the measures
    // a sum or a mean reads, whose totals are estimated.
    private readonly AggregationPartition _sample;
    private readonly int _rows;
    private readonly Measure[] _measures;

    // By stratum and group, what the draws summed (Rows, Measures): the rows, their squares and their products with the
    // ranges' rows, then for each measure the values, their squares, the sum, its square, the product of the two, and the
    // products of the values and of the sum with the ranges' rows.
    private readonly Dictionary<long, double[]> _sums = [];
    private readonly object _gate = new object();
    private Stratum[] _strata = [];
    private long _total;
    private long _read;
    private int _ranges;

    // What the exact answer costs, as the source prices it before reading anything; null when it cannot say.
    private TimeSpan? _exactCost;

    internal EstimateRun(AggregationQuery question, EstimateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (question.Operators.Length > 0 || question.Skip > 0 || question.Take != long.MaxValue)
        {
            throw new NotSupportedException("An estimate answers every group it meets: a filter, an order or a window on groups waits for the exact answer.");
        }

        AggregationPartials.Check(question.Plan);
        options.Validate();
        _question = question;
        _options = options;
        AggregationPlan plan = question.Plan;
        AggregateSlot?[] settled = new AggregateSlot?[plan.Aggregates.Length];
        (_columns, _inputs) = AggregationEngine.Columns(plan, settled);
        _sample = new AggregationPartition(plan, settled, _columns, _inputs, sorted: false);
        _rows = Array.FindIndex(plan.Aggregates, static a => a.Kind == AggregateKind.Count && a.Input is null);
        List<Measure> measures = [];
        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            if (aggregate.Kind is AggregateKind.Sum or AggregateKind.Average && aggregate.Input is { } input)
            {
                string name = StateRequest.NameOf(input.Field);
                if (!measures.Exists(m => string.Equals(m.Name, name, StringComparison.Ordinal)))
                {
                    measures.Add(new Measure(name, input.PType is PType.F32 or PType.F64));
                }
            }
        }

        _measures = [.. measures];
    }

    /// <summary>
    /// What the exact answer to <paramref name="question"/> costs, its requests and bytes as its source's planner prices its
    /// paths before reading anything, at the price of a request and a byte (<see cref="ReadCost"/>); null for a source that
    /// cannot say.
    /// </summary>
    internal static async ValueTask<TimeSpan?> CostOfAsync(AggregationQuery question, CancellationToken cancellationToken) =>
        await ReadsOfAsync(question, cancellationToken).ConfigureAwait(false) is (long requests, long bytes)
            ? ReadCost.Time(requests, bytes)
            : null;

    /// <summary>The requests and bytes the exact answer to <paramref name="question"/> reads, as its source's planner prices them; null for a source that cannot say.</summary>
    internal static ValueTask<(long Requests, long Bytes)?> ReadsOfAsync(AggregationQuery question, CancellationToken cancellationToken)
    {
        AggregationHost host = question.Host;
        AggregationPlan plan = question.Plan;
        AggregateSlot?[] settled = new AggregateSlot?[plan.Aggregates.Length];
        (ColumnShape[] columns, _) = AggregationEngine.Columns(plan, settled);
        ScanSpec pass = AggregationEngine.PassSpec(host.Spec(question.RowFilter), columns, plan, host.Source.Schema);
        StateRequest? states = host.Source.KeepsStates && pass.Options.UseSynopses ? StateRequest.Of(plan, settled, null) : null;
        LearnedFamily? family = states is not null && host.Source.LearnsPartials ? LearnedFamily.Of(plan, pass.Filter, host.Source) : null;
        return host.Source.CostAsync(pass with { States = states, Family = family }, cancellationToken);
    }

    /// <summary>The tiers: one a share of the ranges, each reading those the share adds, then the exact answer.</summary>
    internal async IAsyncEnumerable<AggregationEstimate> TiersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AggregationHost host = _question.Host;
        ScanSpec pass = AggregationEngine.PassSpec(host.Spec(_question.RowFilter), _columns, _question.Plan, host.Source.Schema);

        // The exact answer's cost, announced by every tier; under the threshold, the exact answer at once, the question read
        // whole as one part, through whatever its source's planner answers it by.
        _exactCost = await CostOfAsync(_question, cancellationToken).ConfigureAwait(false);
        if (_exactCost is { } exactCost)
        {
            if (exactCost <= _options.ExactUnder && _options.Exact && host.Source.RowBound >= 0)
            {
                await ReadWholeAsync(host.Source.RowBound, cancellationToken).ConfigureAwait(false);
                yield return Estimate(exact: true);
                yield break;
            }
        }

        RowRange[][] pieces = await host.Source.StrataAsync(pass, _options.RangeBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new NotSupportedException("A source with no ranges of its own, such as a query's result, takes no estimate.");
        Random random = _options.Seed is int seed ? new Random(seed) : new Random();
        int least = _options.Shares.Count == 0 ? 1 : (int)Math.Ceiling(2 / _options.Shares[0]);
        _strata = Strata(pieces, least, random);
        foreach (Stratum stratum in _strata)
        {
            _total += stratum.Rows;
        }

        foreach (double share in _options.Shares)
        {
            List<(int Stratum, RowRange Range)> drawn = Draw(share);
            if (drawn.Count == 0)
            {
                continue;
            }

            await ReadAsync(drawn, cancellationToken).ConfigureAwait(false);
            bool whole = Array.TrueForAll(_strata, static s => s.Drawn == s.Ranges.Length);
            yield return Estimate(whole);
            if (whole)
            {
                yield break;
            }
        }

        if (_options.Exact)
        {
            await ReadAsync(Draw(1), cancellationToken).ConfigureAwait(false);
            yield return Estimate(exact: true);
        }
    }

    /// <summary>
    /// <paramref name="pieces"/> gathered, in their order, into strata of <paramref name="least"/> ranges at least, the ranges
    /// left at the end joining the last; each stratum's ranges in an order drawn at random.
    /// </summary>
    private static Stratum[] Strata(RowRange[][] pieces, int least, Random random)
    {
        List<List<RowRange>> gathered = [];
        List<RowRange> current = [];
        foreach (RowRange[] piece in pieces)
        {
            current.AddRange(piece);
            if (current.Count >= least)
            {
                gathered.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            if (gathered.Count > 0)
            {
                gathered[^1].AddRange(current);
            }
            else
            {
                gathered.Add(current);
            }
        }

        Stratum[] strata = new Stratum[gathered.Count];
        for (int h = 0; h < strata.Length; h++)
        {
            RowRange[] ranges = [.. gathered[h]];
            int[] order = new int[ranges.Length];
            long rows = 0;
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
                rows += ranges[i].Length;
            }

            random.Shuffle(order);
            strata[h] = new Stratum(ranges, order, rows);
        }

        return strata;
    }

    /// <summary>The ranges a tier of <paramref name="share"/> adds: each stratum's next in its order, up to the share of its ranges, two at least.</summary>
    private List<(int Stratum, RowRange Range)> Draw(double share)
    {
        List<(int, RowRange)> drawn = [];
        for (int h = 0; h < _strata.Length; h++)
        {
            Stratum stratum = _strata[h];
            int count = stratum.Ranges.Length;
            int target = Math.Clamp((int)Math.Ceiling((share * count) - 1e-9), Math.Min(2, count), count);
            for (; stratum.Drawn < target; stratum.Drawn++)
            {
                RowRange range = stratum.Ranges[stratum.Order[stratum.Drawn]];
                stratum.DrawnRows += range.Length;
                stratum.DrawnSquares += (double)range.Length * range.Length;
                drawn.Add((h, range));
            }
        }

        return drawn;
    }

    /// <summary>Reads every one of the source's <paramref name="rows"/> as one part, its states folded into the sample's: the exact answer at once.</summary>
    private async Task ReadWholeAsync(long rows, CancellationToken cancellationToken)
    {
        AggregationQuery part = _question.Ranged(new RangeHost(_question.Host, new RowRange(0, rows), _counters));
        IAsyncEnumerator<RecordBatch> groups = part.Groups(cancellationToken);
        await using (groups.ConfigureAwait(false))
        {
            while (await groups.MoveNextAsync().ConfigureAwait(false))
            {
                if (groups.Current.RowCount > 0)
                {
                    _sample.Process(RecordBatch.OfPartial(groups.Current.Arena, groups.Current.RootIndex, 0));
                }
            }
        }

        _read = _total = rows;
        _ranges = 1;
    }

    /// <summary>Reads <paramref name="drawn"/>, as many at once as the session's lanes, or <see cref="RoundTripRanges"/> over an object store, each range's states folded as they come.</summary>
    private async Task ReadAsync(List<(int Stratum, RowRange Range)> drawn, CancellationToken cancellationToken)
    {
        ScanSource source = _question.Host.Source;
        int degree = Math.Max(1, source.Session.Options.MaxDegreeOfParallelism);
        ParallelOptions parallel = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = source.ReadsRoundTrips ? Math.Max(degree, RoundTripRanges) : degree,
        };

        await Parallel.ForEachAsync(drawn, parallel, async (draw, token) =>
        {
            AggregationQuery part = _question.Ranged(new RangeHost(_question.Host, draw.Range, _counters));
            IAsyncEnumerator<RecordBatch> groups = part.Groups(token);
            await using (groups.ConfigureAwait(false))
            {
                while (await groups.MoveNextAsync().ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        Fold(groups.Current, draw.Stratum, draw.Range.Length);
                    }
                }
            }

            lock (_gate)
            {
                _read += draw.Range.Length;
                _ranges++;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>A batch of the states of a range of <paramref name="length"/> rows: merged into the sample's, and summed by stratum and group.</summary>
    private void Fold(RecordBatch batch, int stratum, long length)
    {
        int rows = batch.RowCount;
        if (rows == 0)
        {
            return;
        }

        _sample.Process(RecordBatch.OfPartial(batch.Arena, batch.RootIndex, 0));
        ReadOnlySpan<int> groups = _sample.StateGroups(rows);
        double[][] sums = new double[rows][];
        for (int row = 0; row < rows; row++)
        {
            sums[row] = Sums(stratum, groups[row]);
        }

        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        if (_rows >= 0)
        {
            ReadOnlySpan<long> counts = Words(arena, root, StateRequest.RowsColumn);
            for (int row = 0; row < rows; row++)
            {
                double counted = counts[row];
                sums[row][0] += counted;
                sums[row][1] += counted * counted;
                sums[row][2] += counted * length;
            }
        }

        for (int m = 0; m < _measures.Length; m++)
        {
            Measure measure = _measures[m];
            int at = Rows + (Measured * m);
            ReadOnlySpan<long> values = Words(arena, root, measure.Values);
            ReadOnlySpan<long> word0 = Words(arena, root, measure.Sum[0]);
            ReadOnlySpan<long> word1 = Words(arena, root, measure.Sum[1]);
            ReadOnlySpan<long> word2 = measure.Floats ? Words(arena, root, measure.Sum[2]) : default;
            ReadOnlySpan<long> word3 = measure.Floats ? Words(arena, root, measure.Sum[3]) : default;
            ReadOnlySpan<long> word4 = measure.Floats ? Words(arena, root, measure.Sum[4]) : default;
            for (int row = 0; row < rows; row++)
            {
                double x = values[row];
                double s;
                if (measure.Floats)
                {
                    WideIndexedSum sum = default;
                    (sum.Sum.M0, sum.Sum.M1, sum.Sum.M2, sum.Sum.Meta, sum.High) = (word0[row], word1[row], word2[row], word3[row], word4[row]);
                    s = sum.Value;
                }
                else
                {
                    s = (double)new Int128(unchecked((ulong)word1[row]), unchecked((ulong)word0[row]));
                }

                double[] summed = sums[row];
                summed[at] += x;
                summed[at + 1] += x * x;
                summed[at + 2] += s;
                summed[at + 3] += s * s;
                summed[at + 4] += x * s;
                summed[at + 5] += x * length;
                summed[at + 6] += s * length;
            }
        }
    }

    /// <summary>What the draws of stratum <paramref name="stratum"/> summed for group <paramref name="group"/>.</summary>
    private double[] Sums(int stratum, int group)
    {
        long key = ((long)stratum << 32) | (uint)group;
        if (!_sums.TryGetValue(key, out double[]? sums))
        {
            sums = new double[Rows + (Measured * _measures.Length)];
            _sums.Add(key, sums);
        }

        return sums;
    }

    /// <summary>The 64-bit integers of the column <paramref name="name"/> of a batch of a range's states.</summary>
    private static ReadOnlySpan<long> Words(CanonicalArena arena, CanonicalNode root, string name) =>
        arena.GetNode(root.GetFieldIndex(root.DType.IndexOfField(name))).Values.Cast<long>();

    /// <summary>The tier's estimate: the sample's states, its totals scaled, or for the exact tier its states as they are.</summary>
    private AggregationEstimate Estimate(bool exact)
    {
        AggregationPlan plan = _question.Plan;
        int groups = _sample.Keys?.Count ?? 1;
        if (_sample.Keys is null)
        {
            // The one group of a whole scan, which no range read may have made.
            foreach (AggregateSlot slot in _sample.Slots)
            {
                slot.EnsureGroups(1);
            }
        }

        if (exact)
        {
            AggregationOutcome whole = new AggregationOutcome(plan, _sample.Slots, _sample.Keys, Order(groups));
            EstimateQuery answers = new EstimateQuery(_question, whole, groups, Margins(null, null, groups), _counters);
            return new AggregationEstimate(answers, 1, _ranges, groups, exact: true, _exactCost);
        }

        Totals totals = TotalsOf(groups);
        (AggregationOutcome scaled, int[] place) = Scaled(totals, groups);
        EstimateQuery estimate = new EstimateQuery(_question, scaled, groups, Margins(totals, place, groups), _counters);
        return new AggregationEstimate(estimate, _total == 0 ? 1 : (double)_read / _total, _ranges, groups, exact: false, _exactCost);
    }

    /// <summary>The estimates of every group's totals over every row, and their variances.</summary>
    private Totals TotalsOf(int groups)
    {
        Totals totals = new Totals(groups, _measures.Length);
        int degrees = 0;
        foreach (Stratum stratum in _strata)
        {
            degrees += stratum.Drawn >= 2 && stratum.Drawn < stratum.Ranges.Length ? stratum.Drawn - 1 : 0;
        }

        foreach ((long key, double[] sums) in _sums)
        {
            Stratum stratum = _strata[(int)(key >> 32)];
            int group = (int)(uint)key;
            double scale = stratum.Rows / stratum.DrawnRows;
            totals.Rows[group] += scale * sums[0];
            totals.RowsVariance[group] += stratum.Variance(sums[0], sums[1], sums[2]);
            for (int m = 0; m < _measures.Length; m++)
            {
                int at = Rows + (Measured * m);
                totals.Values[m][group] += scale * sums[at];
                totals.Sums[m][group] += scale * sums[at + 2];
                totals.SumsVariance[m][group] += stratum.Variance(sums[at + 2], sums[at + 3], sums[at + 6]);
            }
        }

        // A mean's residuals, the sum less the mean times the values, range by range: the ratio's error, linearised.
        foreach ((long key, double[] sums) in _sums)
        {
            Stratum stratum = _strata[(int)(key >> 32)];
            int group = (int)(uint)key;
            for (int m = 0; m < _measures.Length; m++)
            {
                int at = Rows + (Measured * m);
                double values = totals.Values[m][group];
                double mean = values > 0 ? totals.Sums[m][group] / values : 0;
                double residuals = sums[at + 2] - (mean * sums[at]);
                double squares = sums[at + 3] - (2 * mean * sums[at + 4]) + (mean * mean * sums[at + 1]);
                double byRows = sums[at + 6] - (mean * sums[at + 5]);
                totals.MeansVariance[m][group] += stratum.Variance(residuals, squares, byRows);
            }
        }

        totals.Quantile = StudentQuantile(degrees);
        return totals;
    }

    /// <summary>
    /// The sample's states with its totals scaled (<see cref="Scaling"/>), as rows of states folded into a partition of their
    /// own, which the question's columns read; and the group each of the sample's became there.
    /// </summary>
    private (AggregationOutcome Scaled, int[] Place) Scaled(Totals totals, int groups)
    {
        AggregationPlan plan = _question.Plan;
        int[] order = Order(groups);
        AggregationOutcome sample = new AggregationOutcome(plan, _sample.Slots, _sample.Keys, order);
        ResultColumn[] columns = AggregationPartials.Columns(plan, _question.Keys, new Scaling(totals, _measures));
        VortexField[] fields = new VortexField[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            fields[c] = new VortexField(columns[c].Name, columns[c].Type);
        }

        VortexSessionOptions options = _question.Session.Options;
        DType dtype = VortexTypes.ToDType(VortexSchema.Create(fields), new DTypeArena());
        StructStore store = (StructStore)ColumnStores.Create(dtype, options.EnginePool, options.Extensions);
        CanonicalArena arena = new CanonicalArena(GroupBatches.ResultNodes(dtype.FieldCount), options.EnginePool);
        AggregationPartition scaled = new AggregationPartition(plan, new AggregateSlot?[plan.Aggregates.Length], _columns, _inputs, sorted: false);
        int[] place = new int[groups];
        try
        {
            for (int start = 0; start < groups; start += GroupBatches.BatchRows)
            {
                int count = Math.Min(GroupBatches.BatchRows, groups - start);
                ReadOnlySpan<int> some = order.AsSpan(start, count);
                store.Truncate(0);
                for (int c = 0; c < columns.Length; c++)
                {
                    columns[c].Append(sample, store.Children[c], some);
                }

                arena.ResetKeepingBlocks();
                int root = store.Build(arena, count);
                scaled.Process(RecordBatch.OfPartial(arena, root, start));
                scaled.StateGroups(count).CopyTo(place.AsSpan(start, count));
            }
        }
        finally
        {
            store.Release();
            arena.Reset();
            foreach (ResultColumn column in columns)
            {
                column.Release();
            }
        }

        return (new AggregationOutcome(plan, scaled.Slots, scaled.Keys, Order(groups)), place);
    }

    /// <summary>
    /// The margin of each count of rows, sum and mean the selection holds, by element, each group's at its place in the
    /// estimate; every one 0 for the exact answer, which <paramref name="totals"/> is null for.
    /// </summary>
    private (int Element, double[] Margins)[] Margins(Totals? totals, int[]? place, int groups)
    {
        IResultNode[] nodes = _question.Nodes;
        List<(int, double[])> margins = [];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] is not IAggregateNode aggregate)
            {
                continue;
            }

            int measure = aggregate.Input is { } input ? Array.FindIndex(_measures, m => string.Equals(m.Name, StateRequest.NameOf(input.Field), StringComparison.Ordinal)) : -1;
            Func<Totals, int, double>? of = aggregate.Kind switch
            {
                AggregateKind.Count when aggregate.Input is null => static (t, g) => t.Quantile * Math.Sqrt(Math.Max(0, t.RowsVariance[g])),
                AggregateKind.Sum when measure >= 0 => (t, g) => t.Quantile * Math.Sqrt(Math.Max(0, t.SumsVariance[measure][g])),
                AggregateKind.Average when measure >= 0 => (t, g) => t.MeanMargin(measure, g),
                _ => null,
            };

            if (of is null)
            {
                continue;
            }

            double[] margin = new double[groups];
            if (totals is not null)
            {
                for (int g = 0; g < groups; g++)
                {
                    margin[place![g]] = of(totals, g);
                }
            }

            margins.Add((i, margin));
        }

        return [.. margins];
    }

    /// <summary>The groups <c>0</c> to <paramref name="groups"/> − 1, in order.</summary>
    private static int[] Order(int groups)
    {
        int[] order = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            order[g] = g;
        }

        return order;
    }

    /// <summary>
    /// Student's 97.5 % quantile of <paramref name="degrees"/> degrees of freedom, the half-width of a 95 % interval in
    /// standard errors: its value up to ten degrees, and Fisher's expansion past them, within 10⁻³; 0 with no degree, every
    /// stratum read whole.
    /// </summary>
    internal static double StudentQuantile(int degrees)
    {
        ReadOnlySpan<double> table = [12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228];
        if (degrees <= 0)
        {
            return 0;
        }

        if (degrees <= table.Length)
        {
            return table[degrees - 1];
        }

        const double Z = 1.959964;
        double z3 = Z * Z * Z;
        double z5 = z3 * Z * Z;
        double z7 = z5 * Z * Z;
        double n = degrees;
        return Z + ((z3 + Z) / (4 * n)) + (((5 * z5) + (16 * z3) + (3 * Z)) / (96 * n * n)) + (((3 * z7) + (19 * z5) + (17 * z3) - (15 * Z)) / (384 * n * n * n));
    }

    /// <summary>A measure a sum or a mean reads: its name in rows of states, whether its values are floats, and its words' columns.</summary>
    private sealed record Measure(string Name, bool Floats)
    {
        internal string Values { get; } = StateRequest.ValuesOf(Name);

        internal string[] Sum { get; } = MeasureLayout.Names(Name, Floats)[MeasureLayout.Sum..(MeasureLayout.Sum + MeasureLayout.SumLength(Floats))];
    }

    /// <summary>
    /// A run of objects whose ranges are drawn together: its ranges, the order they are drawn in, its rows, how many ranges
    /// are drawn, and their rows and those rows' squares.
    /// </summary>
    private sealed class Stratum(RowRange[] ranges, int[] order, long rows)
    {
        internal RowRange[] Ranges => ranges;

        internal int[] Order => order;

        internal long Rows => rows;

        internal int Drawn { get; set; }

        internal double DrawnRows { get; set; }

        internal double DrawnSquares { get; set; }

        /// <summary>
        /// The variance of the stratum's estimate of a total, the ratio of the draws' totals to their rows times its rows,
        /// from the draws' sums of the totals, of their squares and of their products with the ranges' rows: 0 for a stratum
        /// read whole, or one draw, which tells no spread.
        /// </summary>
        internal double Variance(double sum, double squares, double byRows)
        {
            int drawn = Drawn;
            int count = ranges.Length;
            if (drawn < 2 || drawn >= count)
            {
                return 0;
            }

            double ratio = sum / DrawnRows;
            double spread = Math.Max(0, squares - (2 * ratio * byRows) + (ratio * ratio * DrawnSquares)) / (drawn - 1);
            double scale = (double)rows / count / (DrawnRows / drawn);
            return (double)count * count * (1 - ((double)drawn / count)) / drawn * spread * scale * scale;
        }
    }

    /// <summary>Each group's estimated totals and their variances, by measure, and the quantile a margin takes.</summary>
    private sealed class Totals
    {
        internal Totals(int groups, int measures)
        {
            Rows = new double[groups];
            RowsVariance = new double[groups];
            Values = Arrays(groups, measures);
            Sums = Arrays(groups, measures);
            SumsVariance = Arrays(groups, measures);
            MeansVariance = Arrays(groups, measures);
        }

        internal double[] Rows { get; }

        internal double[] RowsVariance { get; }

        internal double[][] Values { get; }

        internal double[][] Sums { get; }

        internal double[][] SumsVariance { get; }

        /// <summary>The variance of the total of each mean's residuals, the sum less the mean times the values.</summary>
        internal double[][] MeansVariance { get; }

        internal double Quantile { get; set; }

        /// <summary>The margin of a mean, the ratio of the sum's total over the values', its variance linearised; none with no value.</summary>
        internal double MeanMargin(int measure, int group)
        {
            double values = Values[measure][group];
            if (!(values > 0))
            {
                return double.NaN;
            }

            return Quantile * Math.Sqrt(Math.Max(0, MeansVariance[measure][group])) / values;
        }

        private static double[][] Arrays(int groups, int measures)
        {
            double[][] arrays = new double[measures][];
            for (int m = 0; m < measures; m++)
            {
                arrays[m] = new double[groups];
            }

            return arrays;
        }
    }

    /// <summary>The sample's counts and its measures' sums and counts of values replaced by their totals' estimates; its extremes and sketches kept.</summary>
    private sealed class Scaling(Totals totals, Measure[] measures) : IStateOverlay
    {
        public void Rows(ReadOnlySpan<int> groups, Span<long> rows)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                rows[i] = (long)Math.Round(totals.Rows[groups[i]]);
            }
        }

        public void Words(string measure, bool floats, ReadOnlySpan<int> groups, Span<long> words)
        {
            int m = Array.FindIndex(measures, x => string.Equals(x.Name, measure, StringComparison.Ordinal));
            if (m < 0)
            {
                return;
            }

            int stride = MeasureLayout.Words(floats);
            for (int i = 0; i < groups.Length; i++)
            {
                int group = groups[i];
                Span<long> held = words.Slice(i * stride, stride);
                long values = (long)Math.Round(totals.Values[m][group]);
                double sum = totals.Sums[m][group];
                held[MeasureLayout.Values] = values;
                if (floats)
                {
                    WideIndexedSum scaled = default;
                    if (values > 0)
                    {
                        scaled.AddWeighted(sum / values, values);
                    }

                    StateWords.Write(in scaled, held, MeasureLayout.Sum);
                }
                else
                {
                    StateWords.Write(Int128.CreateSaturating(Math.Round(sum)), held, MeasureLayout.Sum);
                }
            }
        }
    }
}

/// <summary>A range an estimate draws, as the engine sees it: the question's scan over the range's rows, its reads counted with the estimate's.</summary>
internal sealed class RangeHost(AggregationHost question, RowRange rows, ScanCounters counters) : AggregationHost
{
    internal override ScanSource Source => question.Source;

    internal override ScanCounters Counters => counters;

    internal override ScanMetrics Metrics => ScanMetrics.From(counters);

    internal override ScanSpec Spec()
    {
        ScanSpec spec = question.Spec();
        return spec with { Rows = spec.Rows is { } asked ? asked.Intersect(rows) : rows };
    }

    internal override void Begin()
    {
    }

    internal override void End()
    {
    }
}

/// <summary>
/// An estimate's answers: the question's columns over the estimate's states, then a margin for each count of rows, sum and
/// mean among them, every group the estimate holds in the order of its numbers.
/// </summary>
internal sealed class EstimateQuery : ResultQuery
{
    private readonly AggregationQuery _question;
    private readonly AggregationOutcome _outcome;
    private readonly int _groups;
    private readonly (int Element, double[] Margins)[] _margins;
    private readonly ScanCounters _counters;
    private readonly (VortexSchema Record, Type Type)? _typed;
    private readonly long _skip;
    private readonly long _take;
    private VortexSchema? _schema;

    internal EstimateQuery(
        AggregationQuery question, AggregationOutcome outcome, int groups, (int Element, double[] Margins)[] margins, ScanCounters counters,
        (VortexSchema Record, Type Type)? typed = null, long skip = 0, long take = long.MaxValue)
    {
        _question = question;
        _outcome = outcome;
        _groups = groups;
        _margins = margins;
        _counters = counters;
        _typed = typed;
        _skip = skip;
        _take = take;
    }

    internal override VortexSchema Schema
    {
        get
        {
            if (_schema is null)
            {
                ResultColumn[] columns = NewColumns();
                VortexField[] fields = new VortexField[columns.Length];
                for (int c = 0; c < columns.Length; c++)
                {
                    fields[c] = new VortexField(columns[c].Name, columns[c].Type);
                }

                _schema = VortexSchema.Create(fields);
            }

            return _schema;
        }
    }

    internal override VortexSession Session => _question.Session;

    internal override ScanCounters Counters => _counters;

    internal override IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken) => new EstimateBatches(this, cancellationToken);

    internal override ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken) => _question.ExplainAsync(cancellationToken);

    internal override ResultQuery As(VortexSchema record, Type type)
    {
        int elements = _question.Nodes.Length;
        if (record.Count != elements + _margins.Length)
        {
            throw new VortexSchemaException(
                $"{type.Name} has {record.Count} members and the estimate {elements} elements and {_margins.Length} margins: a record takes the elements in order, then the margins.");
        }

        return new EstimateQuery(_question, _outcome, _groups, _margins, _counters, (record, type), _skip, _take);
    }

    internal override ResultQuery Limit(long skip, long take)
    {
        (long from, long count) = Within(_skip, _take, skip, take);
        return new EstimateQuery(_question, _outcome, _groups, _margins, _counters, _typed, from, count);
    }

    /// <summary>The columns, made anew for a stream: the elements', named <c>Item1</c> and on or by the record, then the margins'.</summary>
    private ResultColumn[] NewColumns()
    {
        IResultNode[] nodes = _question.Nodes;
        ResultColumn[] columns = new ResultColumn[nodes.Length + _margins.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            columns[i] = _typed is { } record ? nodes[i].Column(record.Record[i], _question.Keys, i, record.Type) : nodes[i].Column($"Item{i + 1}", _question.Keys);
        }

        for (int j = 0; j < _margins.Length; j++)
        {
            (int element, double[] margin) = _margins[j];
            if (_typed is { } record)
            {
                VortexField member = record.Record[nodes.Length + j];
                if (!member.Type.NonNullable.Equals(VortexType.Float64))
                {
                    throw new VortexSchemaException($"{record.Type.Name}.{member.Name} takes the margin of element {element + 1}, a double, and is {member.Type}.");
                }

                columns[nodes.Length + j] = new MarginColumn(member.Name, member.Type, margin);
            }
            else
            {
                columns[nodes.Length + j] = new MarginColumn($"Margin{element + 1}", VortexType.Float64, margin);
            }
        }

        return columns;
    }

    /// <summary>The estimate's batches, built a batch of groups at a time in stores reused from one to the next.</summary>
    private sealed class EstimateBatches(EstimateQuery query, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
    {
        private ResultColumn[]? _columns;
        private StructStore? _store;
        private CanonicalArena? _arena;
        private RecordBatch? _current;
        private int[] _order = [];
        private long _next;

        public RecordBatch Current => _current ?? throw new InvalidOperationException("No batch: MoveNextAsync first.");

        public ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_columns is null)
            {
                _columns = query.NewColumns();
                VortexSessionOptions options = query.Session.Options;
                DType dtype = VortexTypes.ToDType(query.Schema, new DTypeArena());
                _store = (StructStore)ColumnStores.Create(dtype, options.EnginePool, options.Extensions);
                _arena = new CanonicalArena(GroupBatches.ResultNodes(dtype.FieldCount), options.EnginePool);
                _order = new int[query._groups];
                for (int g = 0; g < _order.Length; g++)
                {
                    _order[g] = g;
                }

                _next = Math.Min(query._skip, query._groups);
            }

            long end = query._take == long.MaxValue ? query._groups : Math.Min(query._groups, query._skip + query._take);
            if (_next >= end)
            {
                return new ValueTask<bool>(false);
            }

            int count = (int)Math.Min(GroupBatches.BatchRows, end - _next);
            ReadOnlySpan<int> some = _order.AsSpan((int)_next, count);
            StructStore store = _store!;
            store.Truncate(0);
            for (int c = 0; c < _columns.Length; c++)
            {
                _columns[c].Append(query._outcome, store.Children[c], some);
            }

            _current?.Dispose();
            CanonicalArena arena = _arena!;
            arena.ResetKeepingBlocks();
            int root = store.Build(arena, count);
            _current = RecordBatch.Over(arena, root, _next - query._skip, _current);
            _next += count;
            return new ValueTask<bool>(true);
        }

        public ValueTask DisposeAsync()
        {
            _current?.Dispose();
            _store?.Release();
            _arena?.Reset();
            foreach (ResultColumn column in _columns ?? [])
            {
                column.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A margin of an estimate's element, a double a group.</summary>
internal sealed class MarginColumn(string name, VortexType type, double[] margins) : ResultColumn(name, type)
{
    internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups)
    {
        FixedStore into = (FixedStore)store;
        foreach (int group in groups)
        {
            into.Append(margins[group]);
        }
    }
}
