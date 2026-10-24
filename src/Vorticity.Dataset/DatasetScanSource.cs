using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
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

    /// <summary>The rows of every object of the version, deleted ones counted, as its levels hold them: what bounds a table of a key's groups by value.</summary>
    internal override long RowBound => _version.RowCount;

    /// <summary>Its objects are read from its store, a request each.</summary>
    internal override bool ReadsRoundTrips => true;

    /// <summary>The version every sink of the scan reads.</summary>
    internal ulong Version => _version.Version;

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>() : Builder(spec, metrics).ExecuteAsync();

    internal override async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        spec.MatchesNothing ? 0L : await Builder(spec, metrics).CountAsync(cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        !spec.MatchesNothing && await Builder(spec, metrics).AnyAsync(cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return FilterLiteral.Null;
        }

        DatasetScanBuilder builder = Builder(spec, metrics);
        return min
            ? await builder.MinAsync(column.Path, cancellationToken).ConfigureAwait(false)
            : await builder.MaxAsync(column.Path, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// On the first column of the clustering key, or a function of it: the key-ordered read opens an
    /// object once it could hold the next row, so a group of it is final once a greater key is read.
    /// Not in a dataset of periods, whose objects may order each slice by another column: read in key
    /// order, such a slice is sorted again, its instants read to order it and its columns read again in
    /// that order, which made a group by of time over a store 5 ms away 5.6 times slower than the pass
    /// that holds its groups at one lane, reading 14 times its bytes, and 35 times slower at sixteen.
    /// </summary>
    internal override bool OrdersOnAsking(FieldExpr column) =>
        !_version.Levels.ByPeriods && _version.Schema.Key is { } key && key.Paths.Count > 0 && string.Equals(key.Paths[0], column.Path, System.StringComparison.Ordinal);

    /// <summary>
    /// The loosest bounds the summaries of the version's root pages in hand give the column, over
    /// every object: a table of a key's groups by value over the objects,
    /// as over a file, with no read of its own.
    /// </summary>
    internal override Aggregating.KeyBounds? Bounds(Aggregating.ColumnShape key) => _version.Bounds(key.Path);

    /// <summary>The version's roots read first, as its walk reads them: then in hand.</summary>
    internal override async ValueTask<Aggregating.KeyBounds?> BoundsAsync(Aggregating.ColumnShape key, CancellationToken cancellationToken)
    {
        await _version.ReadRootsAsync(cancellationToken).ConfigureAwait(false);
        return _version.Bounds(key.Path);
    }

    /// <summary>
    /// The objects the summaries keep, each a range of the dataset's rows, an object of two shares of
    /// the rows or more cut between its chunks into ranges of a share or more: about four a lane, so
    /// that a lane on a slow core takes fewer and no lane waits on a large object the others finished
    /// beside.
    /// </summary>
    internal override async ValueTask<RowRange[]?> PiecesAsync(ScanSpec spec, int degree, CancellationToken cancellationToken)
    {
        if (spec.OrderPath is not null || spec.Take is not null)
        {
            return null;
        }

        SummaryPruner? pruner = spec.Options.UseStatistics && spec.Filter is { } filter ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(filter)) : null;
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;
        List<(PositionedObject Held, RowRange Rows)> objects = [];
        long total = 0;
        long low = long.MinValue;
        long high = long.MaxValue;
        bool windowed = spec.Filter is { } bounded && _dataset.Key is { } instant && DatasetScanBuilder.Bound(bounded, instant.InstantPath, ref low, ref high);
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);
            if (end <= start)
            {
                continue;
            }

            // An object reads the slices its window reaches alone, as its scan does (DatasetScanBuilder.Of): those are the
            // rows its lanes share, where a day's object cut at its every row left the last 15 minutes, 45 M rows, to two
            // of its 128 pieces, every other piece reading nothing.
            if (windowed && held.Entry.Deletions.IsEmpty && held.Entry.Contents is { } sliced
                && DatasetScanBuilder.WindowRows(sliced.Slices.Slices, low, high) is { } window)
            {
                start = System.Math.Max(start, held.FirstRow + window.Start);
                end = System.Math.Min(end, held.FirstRow + window.End);
            }

            if (end > start)
            {
                objects.Add((held, new RowRange(start, end)));
                total += end - start;
            }
        }

        // The objects to cut are opened side by side to read where their chunks end; the scan finds
        // them open after. Each cut gives its object back, whatever another one's open did. A lookup of an identifier
        // cuts none: it reads an object's rows by the identifier's index with no open of it, where a cut opens it, and
        // each piece would look the whole object up again.
        long share = System.Math.Max(_dataset.Options.LeastPieceRows, total / (degree * 4L));
        bool lookingUp = spec.Options.UseIndexes && DatasetScanBuilder.LookupOf(spec.Filter, _version.Header.Identifiers) is not null;
        Task<RowRange[]>?[] cuts = new Task<RowRange[]>?[objects.Count];
        List<Task> cutting = [];
        for (int o = 0; o < objects.Count; o++)
        {
            if (objects[o].Rows.Length >= 2 * share && !(lookingUp && DatasetScanBuilder.LooksUp(objects[o].Held.Entry, _dataset.Options)))
            {
                cuts[o] = CutAsync(objects[o].Held, objects[o].Rows, share, spec.Projection, cancellationToken);
                cutting.Add(cuts[o]!);
            }
        }

        await Task.WhenAll(cutting).ConfigureAwait(false);
        List<RowRange> pieces = [];
        for (int o = 0; o < objects.Count; o++)
        {
            if (cuts[o] is { } cut)
            {
                pieces.AddRange(await cut.ConfigureAwait(false));
            }
            else
            {
                pieces.Add(objects[o].Rows);
            }
        }

        return pieces.Count > 1 ? [.. pieces] : null;
    }

    /// <summary>
    /// The rows <paramref name="rows"/> of an object cut between its chunks, as a file's are, into
    /// ranges of <paramref name="share"/> rows or more: a cut inside a chunk would have both ranges
    /// decode it. A deleted row counts for none, and a chunk of deleted rows alone is in no range.
    /// </summary>
    private async Task<RowRange[]> CutAsync(PositionedObject held, RowRange rows, long share, FieldMask? projection, CancellationToken cancellationToken)
    {
        ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            // The projection names the version's columns, where an object of an earlier schema need
            // not hold them: its chunks are read whole for every column.
            FieldMask mask = projection is { } named && _version.Schema.ColumnsOf(lease.File.DType, held.Entry.Key) is null ? named : FieldMask.All;
            DeletionVector deletions = held.Entry.Deletions;
            long lower = rows.Start - held.FirstRow;
            long upper = rows.End - held.FirstRow;
            RowRange physical = deletions.IsEmpty ? new RowRange(lower, upper) : new RowRange(deletions.Physical(lower), deletions.Physical(upper - 1) + 1);
            long[] starts = CommonStarts(lease.File.LayoutTree, physical, in mask);
            long parts = rows.Length / share;
            List<RowRange> pieces = [];
            long start = rows.Start;
            for (int b = 0; b < starts.Length && pieces.Count < parts - 1; b++)
            {
                long at = held.FirstRow + deletions.Logical(starts[b]);
                if (at - start >= share && at < rows.End)
                {
                    pieces.Add(new RowRange(start, at));
                    start = at;
                }
            }

            pieces.Add(new RowRange(start, rows.End));
            return [.. pieces];
        }
    }

    /// <summary>
    /// The rows strictly inside <paramref name="physical"/> where a chunk of every top-level column of
    /// <paramref name="mask"/> starts, ascending: where an object whose columns are chunked apart is cut between lanes
    /// without a chunk two of them read. Its columns' chunks end together at its time slices and its groups of rows.
    /// </summary>
    private static long[] CommonStarts(LayoutTree tree, RowRange physical, in FieldMask mask)
    {
        long natural = SplitPlan.NaturalBatchRows(tree);
        int fields = tree.Root.Encoding == Arrays.LayoutEncodingId.Struct ? tree.Root.ChildCount : 0;
        int count = fields == 0 ? 0 : mask.IsAll ? fields : mask.NamedFieldCount;
        if (count == 0)
        {
            SplitPlan whole = SplitPlan.Compute(tree, physical, in mask, natural);
            return Interior(whole, physical);
        }

        long[]? common = null;
        for (int i = 0; i < count && common is not { Length: 0 }; i++)
        {
            int field = mask.IsAll ? i : mask.GetNamedField(i);
            FieldMask column = FieldMask.Single(field);
            long[] starts = Interior(SplitPlan.Compute(tree, physical, in column, natural), physical);
            common = common is null ? starts : Intersect(common, starts);
        }

        return common ?? [];

        static long[] Interior(SplitPlan plan, RowRange rows)
        {
            List<long> inside = new List<long>(plan.BoundaryCount);
            for (int b = 0; b < plan.BoundaryCount; b++)
            {
                long at = plan.BoundaryAt(b);
                if (at > rows.Start && at < rows.End)
                {
                    inside.Add(at);
                }
            }

            return [.. inside];
        }

        static long[] Intersect(long[] left, long[] right)
        {
            List<long> both = new List<long>(System.Math.Min(left.Length, right.Length));
            int l = 0;
            int r = 0;
            while (l < left.Length && r < right.Length)
            {
                if (left[l] == right[r])
                {
                    both.Add(left[l]);
                    l++;
                    r++;
                }
                else if (left[l] < right[r])
                {
                    l++;
                }
                else
                {
                    r++;
                }
            }

            return [.. both];
        }
    }

    /// <summary>The engine's scan for <paramref name="spec"/>, adding what the objects' scans count to <paramref name="counters"/>.</summary>
    internal DatasetScanBuilder Builder(ScanSpec spec, ScanCounters? counters)
    {
        DatasetScanBuilder builder = new DatasetScanBuilder(_dataset, _version)
            .WithVariantKeys(spec.VariantKeys).WithReadAhead(spec.ReadAhead);
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
        builder.WithOptions(options, spec.KeepEncodings, spec.SinkDecodes, spec.PositionsUnread).WithIndexes(options.UseIndexes).WithSummaries(options.UseStatistics);
        return counters is null ? builder : builder.WithCounters(counters);
    }
}
