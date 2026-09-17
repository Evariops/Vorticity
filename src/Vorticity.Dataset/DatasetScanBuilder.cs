// A scan across a dataset's objects - docs/13-dataset.md §14's acceptance: "every answer it gives
// must equal the answer a single file would give".
//
// THE WHOLE SHAPE OF IT, and why it is this small. A data object is a plain Vortex file (§3), so
// the scan a dataset runs over one object is the scan the core already runs over a file, filter
// included: the pruning of 11 §6, the zone maps, the indexes, all of it. What a dataset adds is
// the ORDER -- the objects in key order -- and, later, the pruning that skips an object whole from
// its summaries (§4.2). So this builder holds the filter and the projection and hands them to each
// file's own builder, and the answer is the concatenation.
//
// WHAT IT DOES NOT DO YET: skip an object from its leaf entry's summaries, which needs the entry to
// carry them (§4.2); order across objects by key (`InKeyOrder`, §6.6); and answer `Rows(a, b)` from
// the insertion tree. Each is named in the plan's step 39 and none of them changes the shape here.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Dataset;

/// <summary>Builds a scan over every object of one version of a dataset.</summary>
public sealed class DatasetScanBuilder
{
    private readonly VortexDataset _dataset;
    private VortexExpr? _filter;
    private string[]? _projection;
    private bool _indexes = true;

    internal DatasetScanBuilder(VortexDataset dataset) => _dataset = dataset;

    /// <summary>Keeps the rows the predicate selects.</summary>
    /// <param name="filter">The predicate, in the core's expression model.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder Where(VortexExpr filter)
    {
        _filter = filter;
        return this;
    }

    /// <summary>Reads only these columns.</summary>
    /// <param name="paths">Their paths.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder Select(params string[] paths)
    {
        _projection = paths;
        return this;
    }

    /// <summary>Turns the per-file index chain on or off (08 §1's equivalence).</summary>
    /// <param name="indexes">Whether to consult them.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder WithIndexes(bool indexes)
    {
        _indexes = indexes;
        return this;
    }

    /// <summary>The batches of every object, in key order.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The batches.</returns>
    public async IAsyncEnumerable<RecordBatch> ExecuteAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ObjectEntry entry in _dataset.ObjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            (VortexFile file, ObjectSegmentSource source) =
                await _dataset.OpenObjectAsync(entry, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            await using (file.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in Of(file).ExecuteAsync()
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>The rows the scan selects, without materialising them.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        await foreach (ObjectEntry entry in _dataset.ObjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            (VortexFile file, ObjectSegmentSource source) =
                await _dataset.OpenObjectAsync(entry, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            await using (file.ConfigureAwait(false))
            {
                rows += await Of(file).CountAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>The file's own scan, carrying this builder's filter and projection.</summary>
    /// <param name="file">One data object.</param>
    private ScanBuilder Of(VortexFile file)
    {
        ScanBuilder scan = file.Scan();
        if (_filter is { } filter)
        {
            scan = scan.Where(filter);
        }

        if (_projection is { } projection)
        {
            scan = scan.Project(projection);
        }

        return _indexes ? scan : scan.WithIndexes(false);
    }
}
