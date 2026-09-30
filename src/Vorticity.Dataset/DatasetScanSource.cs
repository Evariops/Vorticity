using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// A scan over every object of one version of a dataset, compiled to the dataset's engine: the
/// core's typed and tool scans ask this what they ask a file. Each object is read by its own file
/// scan, its batches count the dataset's rows, and an answer takes the cheapest proof each object
/// offers: its entry, its statistics, then its blocks.
/// </summary>
internal sealed class DatasetScanSource : ScanSource
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;

    internal DatasetScanSource(VortexDataset dataset, DatasetSnapshot version)
    {
        _dataset = dataset;
        _version = version;
    }

    internal override VortexSchema Schema => _version.Schema.Columns;

    internal override VortexSession Session => _dataset.Session;

    /// <summary>The version every sink of the scan reads.</summary>
    internal ulong Version => _version.Version;

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

        DatasetScanBuilder builder = Builder(spec, metrics);
        return min ? builder.MinAsync(column.Path, cancellationToken) : builder.MaxAsync(column.Path, cancellationToken);
    }

    internal override async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        ScanPlan plan = await Builder(spec, null).PlanAsync(cancellationToken).ConfigureAwait(false);
        return spec.MatchesNothing ? plan with { MayMatch = false } : plan;
    }

    internal override bool MayMatch(VortexExpr filter) => _version.MayMatch(filter);

    internal override async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        await DatasetKeyCursor.OpenAsync(_dataset, _version, path, distinct, indexes, cancellationToken).ConfigureAwait(false);

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        DatasetKeyCursor.ExplainAsync(_version, path, indexes, cancellationToken);

    /// <summary>The engine's scan for <paramref name="spec"/>, adding what the objects' scans count to <paramref name="counters"/>.</summary>
    internal DatasetScanBuilder Builder(ScanSpec spec, ScanMetrics? counters)
    {
        DatasetScanBuilder builder = new DatasetScanBuilder(_dataset, _version);
        if (spec.Projection is { } mask)
        {
            builder.Project(in mask);
        }

        if (spec.Filter is { } filter)
        {
            builder.Where(filter);
        }

        if (spec.Rows is { } rows)
        {
            builder.Rows(rows.Start, rows.End);
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
        builder.WithOptions(options, spec.KeepEncodings, spec.SinkDecodes, spec.PositionsUnread).WithIndexes(options.UseIndexes).WithSummaries(options.Pruning);
        return counters is null ? builder : builder.WithCounters(counters);
    }
}
