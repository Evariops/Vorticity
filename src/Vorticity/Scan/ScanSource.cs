using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>What one scan asks of its source: the columns, the filter, the rows, the order, the options.</summary>
internal sealed record ScanSpec
{
    /// <summary>The columns to deliver, or null for every column.</summary>
    internal FieldMask? Projection { get; init; }

    /// <summary>The filter, or null for every row.</summary>
    internal VortexExpr? Filter { get; init; }

    /// <summary>Whether the filter is known to reject every row, so that nothing is read.</summary>
    internal bool MatchesNothing { get; init; }

    internal RowRange? Rows { get; init; }

    internal long[]? Take { get; init; }

    internal string? OrderPath { get; init; }

    internal bool Descending { get; init; }

    internal ScanOptions Options { get; init; } = ScanOptions.Default;

    /// <summary>Whether the decoders may deliver dictionary and run-end columns in their encoded form, for a consumer that reads it.</summary>
    internal bool KeepEncodings { get; init; }

    /// <summary>
    /// Whether the consumer reads the encoded forms itself, as an aggregation does, so that a block
    /// counts as decoded only when a column of it reached canonical form.
    /// </summary>
    internal bool SinkDecodes { get; init; }

    /// <summary>
    /// Whether <see cref="Live"/> is the filter's mask of live blocks, refined already: the ranges of
    /// one aggregation share one read of the structures instead of each reading them again.
    /// </summary>
    internal bool Pruned { get; init; }

    /// <summary>The mask of live blocks when <see cref="Pruned"/>, or null when no structure prunes anything.</summary>
    internal BlockMask? Live { get; init; }
}

/// <summary>
/// Where a scan's batches and answers come from: a file, or every object of a dataset's version.
/// The typed and the tool scans compile to a <see cref="ScanSpec"/> and ask one of these.
/// </summary>
internal abstract class ScanSource
{
    /// <summary>The columns the source holds.</summary>
    internal abstract VortexSchema Schema { get; }

    /// <summary>The session whose pool, cache and parallelism the scan uses.</summary>
    internal abstract VortexSession Session { get; }

    /// <summary>The batches, borrowed: each is valid until the next is asked for.</summary>
    internal abstract IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanMetrics metrics);

    internal abstract ValueTask<long> CountAsync(ScanSpec spec, ScanMetrics metrics, CancellationToken cancellationToken);

    internal abstract ValueTask<bool> AnyAsync(ScanSpec spec, ScanMetrics metrics, CancellationToken cancellationToken);

    /// <summary>The smallest or largest non-null value of a column among the rows the scan keeps, or a null literal.</summary>
    internal abstract ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanMetrics metrics, CancellationToken cancellationToken);

    internal abstract ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken);

    /// <summary>Whether the statistics alone rule the filter out; never false for a filter that may match.</summary>
    internal abstract bool MayMatch(VortexExpr filter);

    /// <summary>A walker over the keys of the column at <paramref name="path"/>, from the source's key structures.</summary>
    internal abstract ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken);

    /// <summary>Which key source <see cref="OpenKeysAsync"/> would walk.</summary>
    internal abstract ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken);
}

/// <summary>A scan over one open file, compiled to the engine's builder.</summary>
internal sealed class FileScanSource : ScanSource
{
    private readonly VortexFile _file;

    internal FileScanSource(VortexFile file) => _file = file;

    internal VortexFile File => _file;

    internal override VortexSchema Schema => _file.Schema;

    internal override VortexSession Session => _file.Session;

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanMetrics metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>() : Builder(spec, metrics).ExecuteAsync();

    internal override ValueTask<long> CountAsync(ScanSpec spec, ScanMetrics metrics, CancellationToken cancellationToken) =>
        spec.MatchesNothing ? ValueTask.FromResult(0L) : Builder(spec, metrics).CountAsync(cancellationToken);

    internal override ValueTask<bool> AnyAsync(ScanSpec spec, ScanMetrics metrics, CancellationToken cancellationToken) =>
        spec.MatchesNothing ? ValueTask.FromResult(false) : Builder(spec, metrics).AnyAsync(cancellationToken);

    internal override ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanMetrics metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return ValueTask.FromResult(FilterLiteral.Null);
        }

        ScanBuilder builder = Builder(spec, metrics);
        return min ? builder.MinAsync(column.Path, cancellationToken) : builder.MaxAsync(column.Path, cancellationToken);
    }

    internal override async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        ScanExplanation plan = await Builder(spec, new ScanMetrics()).ExplainAsync(cancellationToken).ConfigureAwait(false);
        ScanPlan result = ScanPlan.From(plan);

        // A filter known to match nothing runs no scan at all: it reads nothing and counts zero.
        return spec.MatchesNothing
            ? result with { MayMatch = false, LiveBlocks = 0, Segments = 0, BytesToRead = 0, Pruning = [], Count = new CountPlan(true, 0, 0, 0, 0) }
            : result;
    }

    internal override bool MayMatch(VortexExpr filter) => Compute.FileStatisticsPruner.MayMatch(_file, filter);

    internal override async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        await KeysOf(path, distinct, indexes).OpenAsync(cancellationToken).ConfigureAwait(false);

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        KeysOf(path, distinct, indexes).ExplainAsync(cancellationToken);

    private KeyCursorBuilder KeysOf(string path, bool distinct, bool indexes)
    {
        KeyCursorBuilder builder = _file.Keys(path);
        if (distinct)
        {
            builder.Distinct();
        }

        return indexes ? builder : builder.WithSource(KeySourceKind.SortedColumn);
    }

    /// <summary>The engine's builder for <paramref name="spec"/>.</summary>
    internal ScanBuilder Builder(ScanSpec spec, ScanMetrics metrics)
    {
        ScanBuilder builder = _file.ScanBuilder().WithMetrics(metrics);
        if (spec.Projection is { } mask)
        {
            builder.ProjectMask(in mask);
        }

        if (spec.Filter is { } filter)
        {
            builder.Where(filter);
        }

        if (spec.Rows is { } rows)
        {
            builder.Rows(rows);
        }

        if (spec.Take is { } take)
        {
            builder.Take(take);
        }

        if (spec.OrderPath is { } order)
        {
            builder.InKeyOrder(order, spec.Descending);
        }

        ScanOptions options = spec.Options;
        if (options.BatchRows > 0)
        {
            builder.WithMaxBatchRows(options.BatchRows);
        }

        builder.WithPruning(options.Pruning).WithIndexes(options.UseIndexes);
        int degree = options.DegreeOfParallelism > 0 ? options.DegreeOfParallelism : Session.Options.MaxDegreeOfParallelism;
        builder.WithDegreeOfParallelism(Math.Max(degree, 1));
        builder.WithPrefetch(options.Prefetch).WithCompaction(options.Compact).WithEncodings(spec.KeepEncodings, spec.SinkDecodes);
        if (spec.Pruned)
        {
            builder.WithPruned(spec.Live);
        }

        return builder;
    }
}
