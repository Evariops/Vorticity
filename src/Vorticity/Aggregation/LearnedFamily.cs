using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity.Aggregating;

/// <summary>
/// The family of an aggregation, whose partial a source learns from the rows it reads, so that the question asked again,
/// or with other values of its filter, reads no row of them: its plan, with each conjunct of its filter that asks a column
/// for a value or a few made a key of its own, after the question's; its filter without them; and its identity, which
/// keys a partial apart from every other family's.
/// </summary>
/// <remarks>
/// <para>
/// A partial of the family is a part's (<see cref="AggregationPartials"/>): its groups' states over some rows, read as states
/// by the question, whose engine finds its keys by their places, before the ones the family added. The groups of the
/// values the question asks are kept, and those of the others left out by a selection (<see cref="ServeAsync"/>).
/// </para>
/// <para>
/// Only a plan that takes parts has a family: a count of rows, a sum, a least, a greatest or a mean of a number column, a
/// distribution or a quantile of one, an approximate distinct count; no exact distinct count, nothing filtered.
/// </para>
/// </remarks>
internal sealed class LearnedFamily
{
    private LearnedFamily(AggregationPlan plan, ColumnShape[] keys, VortexExpr[] conjuncts, VortexExpr? selection, int added)
    {
        Plan = plan;
        Keys = keys;
        Conjuncts = conjuncts;
        Filter = GroupPredicates.And(null, [.. conjuncts]);
        Selection = selection;
        Identity = IdentityOf(plan, Filter);
        Added = added;
    }

    /// <summary>The conjuncts of <see cref="Filter"/>, in the question's order.</summary>
    internal VortexExpr[] Conjuncts { get; }

    /// <summary>
    /// The family of an object whose summaries prove the conjuncts <paramref name="proven"/> sets, by their places among
    /// <see cref="Conjuncts"/>, true over its every row: the same, those conjuncts left out, under an identity of its own,
    /// which every question whose other conjuncts are the same shares, whatever the proven ones were. A window that moves
    /// keeps the partials of the objects it holds whole.
    /// </summary>
    internal LearnedFamily Without(ulong proven)
    {
        if (proven == 0)
        {
            return this;
        }

        List<VortexExpr> kept = [];
        for (int i = 0; i < Conjuncts.Length; i++)
        {
            if (i >= 64 || ((proven >> i) & 1) == 0)
            {
                kept.Add(Conjuncts[i]);
            }
        }

        return new LearnedFamily(Plan, Keys, [.. kept], Selection, Added);
    }

    /// <summary>The family's plan: the question's aggregates, by the question's keys then the columns added.</summary>
    internal AggregationPlan Plan { get; }

    /// <summary>Its keys: the question's, then one a column its filter asked values of.</summary>
    internal ColumnShape[] Keys { get; }

    /// <summary>The question's filter without the conjuncts made keys, which a partial is learned under; null for none.</summary>
    internal VortexExpr? Filter { get; }

    /// <summary>The groups of the values the question asks, on a partial's keys by their places; null when the family adds no key.</summary>
    internal VortexExpr? Selection { get; }

    /// <summary>What tells the family from every other: the XXH3-128 of its keys, aggregates and filter, written alike for alike.</summary>
    internal UInt128 Identity { get; }

    /// <summary>The keys the family adds to the question's.</summary>
    internal int Added { get; }

    /// <summary>
    /// The family of <paramref name="plan"/> under <paramref name="filter"/> over <paramref name="source"/>'s columns; null for a
    /// plan that takes no parts.
    /// </summary>
    internal static LearnedFamily? Of(AggregationPlan plan, VortexExpr? filter, ScanSource source)
    {
        if (StateRequest.Of(plan, new AggregateSlot?[plan.Aggregates.Length], top: null) is null || plan.Chosen.Length > 0)
        {
            return null;
        }

        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            if (aggregate.Kind == AggregateKind.CountDistinct)
            {
                return null;
            }
        }

        // The conjuncts that ask a top-level column, not a key already, for a value or a few, none null: each a key added, the
        // values it asks the groups kept. A column asked twice keeps its second conjunct in the filter.
        VortexSchema schema = source.Schema;
        List<ColumnShape> keys = [.. plan.Keys];
        List<VortexExpr> rest = [];
        List<VortexExpr> kept = [];
        if (filter is not null)
        {
            List<VortexExpr> conjuncts = [];
            GroupPredicates.Conjuncts(filter, conjuncts);
            foreach (VortexExpr conjunct in conjuncts)
            {
                (FieldExpr? field, FilterLiteral[]? values) = conjunct switch
                {
                    ComparisonExpr { Op: ComparisonOp.Equal } equal when equal.Value.Kind != FilterLiteralKind.Null => (equal.Field, (FilterLiteral[]?)[equal.Value]),
                    InExpr asked when asked.Values.Count > 0 && Array.TrueForAll(asked.Literals, static v => v.Kind != FilterLiteralKind.Null) => (asked.Field, asked.Literals),
                    _ => (null, null),
                };
                int index = field is ColumnFieldExpr && !field.Path.Contains('.', StringComparison.Ordinal) ? schema.IndexOfName(field.Path) : -1;
                if (index < 0 || keys.Exists(key => string.Equals(key.Key, field!.Key, StringComparison.Ordinal)))
                {
                    rest.Add(conjunct);
                    continue;
                }

                ColumnSym column = new ColumnSym(field!, schema[index].Type, source.Session.Options.Extensions, null, -1, [index]);
                FieldExpr place = StateRequest.PartialField(keys.Count);
                kept.Add(values!.Length == 1 ? new ComparisonExpr(place, ComparisonOp.Equal, values[0]) : new InExpr(place, values));
                keys.Add(new ColumnShape(column));
            }
        }

        SymNode[] results = new SymNode[plan.Aggregates.Length];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = (SymNode)plan.Aggregates[i];
        }

        ColumnShape[] all = [.. keys];
        AggregationPlan family = new AggregationPlan(results, all);
        return new LearnedFamily(family, all, [.. rest], GroupPredicates.And(null, kept), all.Length - plan.Keys.Length);
    }

    /// <summary>The XXH3-128 of the family's keys, its aggregates and its filter, each as its canonical text.</summary>
    private static UInt128 IdentityOf(AggregationPlan plan, VortexExpr? filter)
    {
        StringBuilder text = new StringBuilder();
        foreach (ColumnShape key in plan.Keys)
        {
            text.Append("key ").Append(key.Key).Append(' ').Append(key.Type).Append('\n');
        }

        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            text.Append("aggregate ").Append(aggregate.Kind).Append(' ').Append(aggregate.Input?.Key ?? "*").Append('\n');
        }

        text.Append("filter ").Append(filter is null ? string.Empty : ExprText.Format(filter));
        return System.IO.Hashing.XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    /// Learns the family's partial over <paramref name="rows"/> of <paramref name="source"/>: the family run over them as a
    /// part, its filter applied, under <paramref name="options"/> with the synopses off, so that the run learns nothing of its
    /// own and reads the rows.
    /// </summary>
    internal ValueTask<AggregationPartial> LearnAsync(ScanSource source, RowRange rows, ScanOptions options, CancellationToken cancellationToken)
    {
        ScanSpec spec = new ScanSpec { Filter = Filter, Rows = rows, Options = options with { UseSynopses = false } };
        AggregationQuery query = AggregationQuery.StatesOf(new FamilyHost(source, spec), Plan, Keys);
        return AggregationPartials.ExportAsync(query, cancellationToken);
    }

    /// <summary>
    /// The family's partial of the rows of <paramref name="partials"/> together, partials of it over rows apart: their
    /// groups merged by the family's plan, as a merge of parts folds them, to the bits the rows together would give, with
    /// no row read. <paramref name="source"/> is the source they were learned over.
    /// </summary>
    internal ValueTask<AggregationPartial> MergeAsync(AggregationPartial[] partials, ScanSource source, ScanOptions options, CancellationToken cancellationToken)
    {
        VortexSchema expected = AggregationQuery.StatesOf(new FamilyHost(source, new ScanSpec()), Plan, Keys).Schema;
        PartialsSource merged = new PartialsSource(source, partials, expected);
        ScanSpec spec = new ScanSpec { Options = options with { UseSynopses = true, AlignWindow = false } };
        return AggregationPartials.ExportAsync(AggregationQuery.StatesOf(new FamilyHost(merged, spec), Plan, Keys), cancellationToken);
    }

    /// <summary>
    /// The states of <paramref name="partial"/>, a partial of this family, as a question of it reads them: each batch of its
    /// file a batch of a partial's states (<see cref="RecordBatch.OfPartial"/>), its rows numbered from
    /// <paramref name="firstRow"/>, the groups of the values the question asks selected.
    /// </summary>
    internal async IAsyncEnumerable<RecordBatch> ServeAsync(
        AggregationPartial partial, VortexSession session, long firstRow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        VortexFile file = await partial.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            long row = firstRow;
            await foreach (RecordBatch batch in new FileScanSource(file).BatchesAsync(new ScanSpec(), new ScanCounters()).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                RecordBatch states = PartialsSource.Canonical(batch, row);
                row += batch.RowCount;
                if (Selection is { } selection)
                {
                    FilterEvaluator.Select(states, selection);
                }

                yield return states;
            }
        }
    }

    /// <summary>A family's run over some rows of a source, as the engine sees it: the source, and the spec of those rows.</summary>
    private sealed class FamilyHost(ScanSource source, ScanSpec spec) : AggregationHost
    {
        private readonly ScanCounters _counters = new ScanCounters();

        internal override ScanSource Source => source;

        internal override ScanCounters Counters => _counters;

        internal override ScanMetrics Metrics => ScanMetrics.From(_counters);

        internal override ScanSpec Spec() => spec;

        internal override void Begin()
        {
        }

        internal override void End()
        {
        }
    }
}
