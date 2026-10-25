using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// An aggregation cut into parts, run apart and merged: the parts of its rows (<see cref="PartsAsync"/>), the states of
/// a part's groups as rows of states (<see cref="ExportAsync"/>), and the question answered from them
/// (<see cref="Merged"/>), which folds them as it folds a synopsis's states, to the bits the rows would give.
/// </summary>
/// <remarks>
/// A part's states are those a dataset's counts keep: integer sums exact in 128 bits, float sums
/// <see cref="WideIndexedSum"/>s, extremes, sketches. Only a question whose every aggregate a state answers whole takes
/// parts: a count of rows, a sum, a least, a greatest or a mean of a number column, a distribution or a quantile of one,
/// an approximate distinct count of a column; no aggregate filtered, no chosen row's column. Its keys come as its
/// functions gave them, so any key a state carries does. What follows the group by, its filters on aggregates, its
/// order and its window, applies to the merge alone.
/// </remarks>
internal static class AggregationPartials
{
    /// <summary>The query of an aggregation, which a projection's distinct values are not.</summary>
    internal static AggregationQuery Of(ResultQuery query) =>
        query as AggregationQuery ?? throw new NotSupportedException("Only an aggregation of a group by or of a whole scan takes parts.");

    /// <summary>Throws unless a state answers every aggregate of <paramref name="plan"/> whole, and the states of each have a row's form.</summary>
    internal static void Check(AggregationPlan plan)
    {
        if (StateRequest.Of(plan, new AggregateSlot?[plan.Aggregates.Length], top: null) is null)
        {
            throw new NotSupportedException(
                "An aggregation takes parts when a state answers its every aggregate whole: a count of rows, a sum, a least, a greatest or a mean "
                + "of a number column, a distribution or a quantile of one, or an approximate distinct count; none filtered, and no chosen row's column.");
        }

        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            if (aggregate.Kind == AggregateKind.CountDistinct)
            {
                throw new NotSupportedException("An exact distinct count takes no parts: its states are the values themselves. An approximate one does.");
            }
        }
    }

    /// <summary>
    /// The rows of <paramref name="query"/> cut into <paramref name="count"/> parts at most, which read apart: a file's at its
    /// chunks, a dataset's at its objects and inside a large one at its chunks, gathered into ranges of about as many rows
    /// each that follow one another. One part for a source that has no cut of its own.
    /// </summary>
    internal static async ValueTask<RowRange[]> PartsAsync(AggregationQuery query, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Check(query.Plan);
        ScanSource source = query.Host.Source;
        ScanSpec spec = query.Host.Spec(query.RowFilter);
        RowRange whole = spec.Rows ?? new RowRange(0, source.RowBound >= 0 ? source.RowBound : long.MaxValue);
        RowRange[]? pieces = count == 1 || spec.MatchesNothing
            ? null
            : AggregationEngine.Ranges(source, spec, count)
                ?? (source is FileScanSource ? null : await source.PiecesAsync(spec, count, cancellationToken).ConfigureAwait(false));
        return pieces is null || pieces.Length <= 1 ? [whole] : Gathered(whole, pieces, count);
    }

    /// <summary>
    /// <paramref name="pieces"/>, in the order of their rows, gathered into <paramref name="count"/> ranges at most of about
    /// as many of their rows, the first from the start of <paramref name="whole"/>, each to the start of the next, the last
    /// to its end: the rows no piece holds, which the question keeps none of, lie in a part all the same.
    /// </summary>
    private static RowRange[] Gathered(RowRange whole, RowRange[] pieces, int count)
    {
        RowRange[] sorted = [.. pieces];
        Array.Sort(sorted, static (a, b) => a.Start.CompareTo(b.Start));
        long total = 0;
        foreach (RowRange piece in sorted)
        {
            total += piece.Length;
        }

        List<long> starts = [whole.Start];
        long held = 0;
        for (int p = 0; p < sorted.Length; p++)
        {
            // A part closes before the piece that would take its rows past their share.
            if (p > 0 && starts.Count < count && held >= total * starts.Count / count)
            {
                starts.Add(sorted[p].Start);
            }

            held += sorted[p].Length;
        }

        RowRange[] parts = new RowRange[starts.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = new RowRange(starts[i], i + 1 < parts.Length ? starts[i + 1] : whole.End);
        }

        return parts;
    }

    /// <summary>Runs <paramref name="query"/>, the partial's (<see cref="AggregationQuery.Partial"/>), and writes its rows of states to a file in memory.</summary>
    internal static async ValueTask<AggregationPartial> ExportAsync(AggregationQuery query, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new MemoryStream();
        VortexFileWriter writer = query.Session.CreateWriter(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)), query.Schema);
        await using (writer.ConfigureAwait(false))
        {
            IAsyncEnumerator<RecordBatch> groups = query.Groups(cancellationToken);
            await using (groups.ConfigureAwait(false))
            {
                while (await groups.MoveNextAsync().ConfigureAwait(false))
                {
                    await writer.WriteAsync(groups.Current, cancellationToken).ConfigureAwait(false);
                }
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return AggregationPartial.Of(stream.ToArray());
    }

    /// <summary>
    /// <paramref name="query"/> answered from <paramref name="partials"/>: the same plan, its operators and its window, over
    /// a source of their states.
    /// </summary>
    internal static AggregationQuery Merged(AggregationQuery query, IEnumerable<AggregationPartial> partials)
    {
        ArgumentNullException.ThrowIfNull(partials);
        Check(query.Plan);
        PartialsSource source = new PartialsSource(query.Host.Source, [.. partials], query.Partial().Schema);
        return query.Merged(new PartialsHost(query.Host, source));
    }

    /// <summary>
    /// The columns of a part's rows of states: its keys under the names of their places, the rows each group counts, each
    /// measure's words, then each sketch.
    /// </summary>
    internal static ResultColumn[] Columns(AggregationPlan plan, ColumnShape[] keys)
    {
        List<ResultColumn> columns = [];
        for (int k = 0; k < keys.Length; k++)
        {
            columns.Add(new KeyResultColumn(StateRequest.PartialKey(k), keys[k].Type, k));
        }

        IAggregateNode[] aggregates = plan.Aggregates;
        int rows = Array.FindIndex(aggregates, static a => a.Kind == AggregateKind.Count && a.Input is null);
        columns.Add(new RowsColumn(plan, rows));

        // Each measure, in the order its first aggregate comes: the slots of its sums and extremes, which write its words.
        List<(string Name, bool Floats, List<int> Writers)> measures = [];
        List<(string Name, int Aggregate)> sketches = [];
        for (int i = 0; i < aggregates.Length; i++)
        {
            IAggregateNode aggregate = aggregates[i];
            if (aggregate.Input is not { } input || aggregate.Kind == AggregateKind.Count)
            {
                continue;
            }

            string name = StateRequest.NameOf(input.Field);
            if (aggregate.Kind == AggregateKind.ApproximateCountDistinct)
            {
                Sketch(sketches, StateRequest.DistinctOf(name), i, aggregates);
                continue;
            }

            int at = measures.FindIndex(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (at < 0)
            {
                measures.Add((name, input.PType is PType.F32 or PType.F64, []));
                at = measures.Count - 1;
            }

            if (aggregate.Kind is AggregateKind.Distribution or AggregateKind.Quantile)
            {
                Sketch(sketches, StateRequest.SketchOf(name), i, aggregates);
            }
            else
            {
                measures[at].Writers.Add(i);
            }
        }

        foreach ((string name, bool floats, List<int> writers) in measures)
        {
            MeasureWords words = new MeasureWords(plan, [.. writers], floats);
            string[] names = MeasureLayout.Names(name, floats);
            for (int w = 0; w < names.Length; w++)
            {
                columns.Add(new WordColumn(names[w], words, w));
            }
        }

        foreach ((string name, int aggregate) in sketches)
        {
            columns.Add(new SketchColumn(name, plan, aggregate));
        }

        return [.. columns];
    }

    /// <summary>The sketch column <paramref name="name"/> written by aggregate <paramref name="aggregate"/>, or by the one of the finest grid already written to it.</summary>
    private static void Sketch(List<(string Name, int Aggregate)> sketches, string name, int aggregate, IAggregateNode[] aggregates)
    {
        int at = sketches.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (at < 0)
        {
            sketches.Add((name, aggregate));
        }
        else if (Aggregators.SketchBits(aggregates[aggregate].Identity) > Aggregators.SketchBits(aggregates[sketches[at].Aggregate].Identity))
        {
            // A sketch merges into a grid as fine or coarser: the finest one answers every distribution of the column.
            sketches[at] = (name, aggregate);
        }
    }

    /// <summary>A scratch of at least <paramref name="length"/> values, cleared.</summary>
    private static Span<T> Cleared<T>(ref T[] scratch, int length)
    {
        if (scratch.Length < length)
        {
            scratch = new T[Scratch.Capacity(length, scratch.Length)];
        }

        Span<T> span = scratch.AsSpan(0, length);
        span.Clear();
        return span;
    }

    /// <summary>The rows each group counts: its count of rows's, or none where no aggregate counts them, which no other reads.</summary>
    private sealed class RowsColumn(AggregationPlan plan, int count) : ResultColumn(StateRequest.RowsColumn, VortexType.Int64)
    {
        private long[] _rows = [];

        internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups)
        {
            Span<long> rows = Cleared(ref _rows, groups.Length);
            if (count >= 0)
            {
                outcome.SlotOf(plan.Aggregates[count]).ExportStates(groups, new StateExport { Rows = rows });
            }

            ((FixedStore)store).Append<long>(rows);
        }
    }

    /// <summary>
    /// The words of a measure's states for a batch of groups, written by the slots of its sums and extremes when its first
    /// word's column is appended, and read by every one of its columns, which a batch appends in their order.
    /// </summary>
    private sealed class MeasureWords(AggregationPlan plan, int[] writers, bool floats)
    {
        private static readonly long NoFloat = BitConverter.DoubleToInt64Bits(double.NaN);
        private long[] _words = [];

        internal int Stride => MeasureLayout.Words(floats);

        internal void Write(AggregationOutcome outcome, ReadOnlySpan<int> groups)
        {
            int stride = Stride;
            Span<long> words = Cleared(ref _words, groups.Length * stride);
            if (floats)
            {
                // A float's extremes are NaN while they met no value, as a table of states keeps them.
                for (int at = 0; at < words.Length; at += stride)
                {
                    words[at + MeasureLayout.FloatLeast] = NoFloat;
                    words[at + MeasureLayout.FloatGreatest] = NoFloat;
                }
            }

            foreach (int writer in writers)
            {
                outcome.SlotOf(plan.Aggregates[writer]).ExportStates(groups, new StateExport { Words = words, Floats = floats });
            }
        }

        internal long Word(int group, int word) => _words[(group * Stride) + word];
    }

    /// <summary>One word of a measure's states (<see cref="MeasureLayout.Names"/>).</summary>
    private sealed class WordColumn(string name, MeasureWords words, int word) : ResultColumn(name, VortexType.Int64)
    {
        internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups)
        {
            if (word == 0)
            {
                words.Write(outcome, groups);
            }

            FixedStore into = (FixedStore)store;
            for (int g = 0; g < groups.Length; g++)
            {
                into.Append(words.Word(g, word));
            }
        }
    }

    /// <summary>A sketch of each group's values, as a table of sketches holds it.</summary>
    private sealed class SketchColumn(string name, AggregationPlan plan, int aggregate) : ResultColumn(name, VortexType.Binary)
    {
        private byte[]?[] _sketches = [];

        internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups)
        {
            Span<byte[]?> sketches = Cleared(ref _sketches, groups.Length);
            outcome.SlotOf(plan.Aggregates[aggregate]).ExportStates(groups, new StateExport { Sketches = sketches });
            VarBinStore into = (VarBinStore)store;
            foreach (byte[]? sketch in sketches)
            {
                into.Append(sketch);
            }
        }
    }
}

/// <summary>
/// The partials of an aggregation's parts, as the source of its merge: their rows of states, which the pass asks for, a
/// batch of states each batch of their files, their keys as the functions gave them (<see cref="RecordBatch.OfPartial"/>).
/// The source has no rows: it answers a count of them from the states, and nothing else but states.
/// </summary>
internal sealed class PartialsSource(ScanSource question, AggregationPartial[] partials, VortexSchema expected) : ScanSource
{
    /// <summary>The question's columns, which its plan reads by.</summary>
    internal override VortexSchema Schema => question.Schema;

    internal override VortexSession Session => question.Session;

    internal override bool KeepsStates => true;

    internal override ValueTask<bool> TakesStatesAsync(ScanSpec spec, StateRequest states, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.States is null
            ? throw new InvalidOperationException("A merge of partials reads them as states, which its pass did not ask for.")
            : StatesAsync(metrics);

    /// <summary>Each partial's batches, their rows numbered on from one partial to the next.</summary>
    private async IAsyncEnumerable<RecordBatch> StatesAsync(ScanCounters metrics, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long row = 0;
        foreach (AggregationPartial partial in partials)
        {
            VortexFile file = await OpenAsync(partial, cancellationToken).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in new FileScanSource(file).BatchesAsync(new ScanSpec(), metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return Canonical(batch, row);
                    row += batch.RowCount;
                }
            }
        }
    }

    /// <summary>
    /// A batch of a partial's file as a batch of its states: each column as its values lie, a constant, a dictionary or runs
    /// made whole, as the words of states are read.
    /// </summary>
    private static RecordBatch Canonical(RecordBatch batch, long row)
    {
        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        int[] columns = new int[root.DType.FieldCount];
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c] = EncodedForms.Canonical(arena, root.GetFieldIndex(c));
        }

        return RecordBatch.OfPartial(arena, arena.AddStruct(root.DType, batch.RowCount, Validity.NonNullable, columns), row);
    }

    /// <summary>A partial's file, refused when its columns are not those of this question's partials.</summary>
    private async ValueTask<VortexFile> OpenAsync(AggregationPartial partial, CancellationToken cancellationToken)
    {
        VortexFile file = await partial.OpenAsync(Session, cancellationToken).ConfigureAwait(false);
        if (!file.Schema.Equals(expected))
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("A partial of another question: its keys or its measures are not this question's.");
        }

        return file;
    }

    /// <summary>The rows the partials' groups count, for a count of rows alone, which the engine asks of a source.</summary>
    internal override async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        long rows = 0;
        await foreach (RecordBatch batch in StatesAsync(metrics, cancellationToken).ConfigureAwait(false))
        {
            CanonicalNode root = batch.Arena.GetNode(batch.RootIndex);
            foreach (long counted in batch.Arena.GetNode(root.GetFieldIndex(root.DType.IndexOfField(StateRequest.RowsColumn))).Values.Cast<long>())
            {
                rows += counted;
            }
        }

        return rows;
    }

    internal override ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A merge of partials holds states, not rows.");

    internal override ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A merge of partials holds states, not rows.");

    /// <summary>A plan of no block: the partials are in memory, and every row of them is read.</summary>
    internal override ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ScanPlan(0, 0, 0, 0, 0, MayMatch: true, [], new CountPlan(false, 0, 0, 0, 0), null));

    internal override bool MayMatch(VortexExpr filter) => true;

    internal override ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A merge of partials holds states, not rows.");

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new KeyPlan(path, KeySourceKind.None, 0, null, HasRows: false, [new KeySourceRejection(KeySourceKind.InMemory, "a merge of partials holds states, not rows")]));
}

/// <summary>
/// The merge of an aggregation's partials, as the engine sees it: their source, and the question's own spec without its
/// filter and its range, which its parts applied, its states asked for.
/// </summary>
internal sealed class PartialsHost(AggregationHost question, PartialsSource source) : AggregationHost
{
    private readonly ScanCounters _counters = new ScanCounters();
    private int _used;

    internal override ScanSource Source => source;

    internal override ScanCounters Counters => _counters;

    internal override ScanMetrics Metrics => ScanMetrics.From(_counters);

    internal override ScanSpec Spec()
    {
        ScanSpec asked = question.Spec();
        return new ScanSpec { Projection = asked.Projection, Options = asked.Options with { UseSynopses = true, AlignWindow = false } };
    }

    internal override void Begin()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new InvalidOperationException("A merge is single-use: merge the partials through another one.");
        }
    }

    internal override void End()
    {
    }
}
