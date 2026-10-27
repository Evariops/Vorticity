using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        Text = TextOf(plan, Filter);
        Identity = System.IO.Hashing.XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(Text));
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

    /// <summary>What tells the family from every other: the XXH3-128 of its <see cref="Text"/>.</summary>
    internal UInt128 Identity { get; }

    /// <summary>
    /// The family's keys, aggregates and filter as a text, written alike for alike: a line a key, its path and type, a line an
    /// aggregate, its kind and input, then the filter; what a store keeps beside its partials, for the families of other nodes
    /// to be found by (spec H, 3.5).
    /// </summary>
    internal string Text { get; }

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

    // What starts each line of a family's text, and what ends the line of a sketch's aggregate before its grid.
    private const string KeyLine = "key ";
    private const string AggregateLine = "aggregate ";
    private const string FilterLine = "filter ";
    private const string BitsMark = " bits=";

    /// <summary>
    /// The family's keys, its aggregates and its filter, each as its canonical text: a distribution's or a quantile's with the
    /// bits of its grid, which its partial's sketches are made on.
    /// </summary>
    private static string TextOf(AggregationPlan plan, VortexExpr? filter)
    {
        StringBuilder text = new StringBuilder();
        foreach (ColumnShape key in plan.Keys)
        {
            text.Append(KeyLine).Append(key.Key).Append(' ').Append(key.Type).Append('\n');
        }

        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            text.Append(AggregateLine).Append(aggregate.Kind).Append(' ').Append(aggregate.Input?.Key ?? "*");
            if (Aggregators.SketchBits(aggregate.Identity) is > 0 and int bits)
            {
                text.Append(BitsMark).Append(bits.ToString(CultureInfo.InvariantCulture));
            }

            text.Append('\n');
        }

        text.Append(FilterLine).Append(filter is null ? string.Empty : ExprText.Format(filter));
        return text.ToString();
    }

    /// <summary>
    /// How the family of text <paramref name="wider"/> serves the family of text <paramref name="narrower"/> (spec H, 3.5):
    /// for each key of the narrower, the place of the same key among the wider's; null unless their filters are the same,
    /// and each key and each aggregate of the narrower is among the wider's, a sketch on as fine a grid or finer. A partial of
    /// the wider, its columns brought to the narrower's, its extra keys and measures left out, folds into one of the narrower.
    /// </summary>
    internal static int[]? Covers(string wider, string narrower)
    {
        List<Range> wideKeys = [];
        List<Range> wideAggregates = [];
        List<Range> keys = [];
        List<Range> aggregates = [];
        if (!Lines(wider, wideKeys, wideAggregates, out int wideFilter) || !Lines(narrower, keys, aggregates, out int filter)
            || !wider.AsSpan(wideFilter).SequenceEqual(narrower.AsSpan(filter)))
        {
            return null;
        }

        foreach (Range aggregate in aggregates)
        {
            bool served = false;
            for (int w = 0; w < wideAggregates.Count && !served; w++)
            {
                served = Serves(wider.AsSpan(wideAggregates[w]), narrower.AsSpan(aggregate));
            }

            if (!served)
            {
                return null;
            }
        }

        int[] keyOf = new int[keys.Count];
        for (int k = 0; k < keyOf.Length; k++)
        {
            keyOf[k] = -1;
            for (int w = 0; w < wideKeys.Count && keyOf[k] < 0; w++)
            {
                if (wider.AsSpan(wideKeys[w]).SequenceEqual(narrower.AsSpan(keys[k])))
                {
                    keyOf[k] = w;
                }
            }

            if (keyOf[k] < 0)
            {
                return null;
            }
        }

        return keyOf;
    }

    /// <summary>
    /// The lines of a family's <paramref name="text"/>: what follows the word of each key's and each aggregate's, and where its
    /// filter starts, the rest of the text; false for a text of another form.
    /// </summary>
    private static bool Lines(string text, List<Range> keys, List<Range> aggregates, out int filter)
    {
        filter = -1;
        int at = 0;
        while (at < text.Length)
        {
            ReadOnlySpan<char> rest = text.AsSpan(at);
            if (rest.StartsWith(FilterLine, StringComparison.Ordinal))
            {
                filter = at + FilterLine.Length;
                return true;
            }

            int end = rest.IndexOf('\n');
            if (end < 0)
            {
                return false;
            }

            if (rest.StartsWith(KeyLine, StringComparison.Ordinal))
            {
                keys.Add(new Range(at + KeyLine.Length, at + end));
            }
            else if (rest.StartsWith(AggregateLine, StringComparison.Ordinal))
            {
                aggregates.Add(new Range(at + AggregateLine.Length, at + end));
            }
            else
            {
                return false;
            }

            at += end + 1;
        }

        return false;
    }

    /// <summary>Whether the aggregate of line <paramref name="wide"/> serves the one of <paramref name="narrow"/>: the same, or a sketch of the same on a grid as fine or finer.</summary>
    private static bool Serves(ReadOnlySpan<char> wide, ReadOnlySpan<char> narrow)
    {
        if (wide.SequenceEqual(narrow))
        {
            return true;
        }

        int w = wide.LastIndexOf(BitsMark, StringComparison.Ordinal);
        int n = narrow.LastIndexOf(BitsMark, StringComparison.Ordinal);
        return w > 0 && n > 0 && wide[..w].SequenceEqual(narrow[..n])
            && int.TryParse(wide[(w + BitsMark.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out int fine)
            && int.TryParse(narrow[(n + BitsMark.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out int coarse)
            && fine >= coarse;
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
    /// no row read. With <paramref name="keyOf"/>, they are partials of a family that serves this one (<see cref="Covers"/>),
    /// each key of this family at its place there, their other keys and measures left out, their groups of the same keys
    /// folded. <paramref name="source"/> is the source they were learned over.
    /// </summary>
    internal ValueTask<AggregationPartial> MergeAsync(
        AggregationPartial[] partials, int[]? keyOf, ScanSource source, ScanOptions options, CancellationToken cancellationToken)
    {
        VortexSchema expected = AggregationQuery.StatesOf(new FamilyHost(source, new ScanSpec()), Plan, Keys).Schema;
        PartialsSource merged = new PartialsSource(source, partials.ToAsyncEnumerable(), expected, keyOf);
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
            await foreach (RecordBatch batch in new FileScanSource(file).Rows.BatchesAsync(new ScanSpec(), new ScanCounters()).WithCancellation(cancellationToken).ConfigureAwait(false))
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

        internal override ScanSpec Spec() => spec;

        internal override void Begin()
        {
        }

        internal override void End()
        {
        }
    }
}
