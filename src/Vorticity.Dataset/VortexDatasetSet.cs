using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// Several datasets of one schema read as one: a question over the set answers what it would over a dataset holding
/// the rows of them all, the first dataset's first.
/// </summary>
/// <remarks>
/// <para>
/// The set is a list of handles and nothing more. It writes nothing to a store, owns none of its datasets and closes none:
/// an application that keeps rows in a dataset per length of time, per tenant or per source declares them once and asks
/// its questions of the set.
/// </para>
/// <para>
/// Each dataset keeps what makes its own questions cheap: its objects are pruned by its own summaries and its own key,
/// and answered from its own counts, statistics and rollups. The set runs one question over them, under one session's
/// lanes and memory, so an aggregate that does not add up across datasets, a quantile, a distinct count or a chosen row,
/// is exact all the same.
/// </para>
/// <para>
/// No version holds across the datasets: a scan reads each at the version its handle holds when the scan is made. The
/// datasets may lie in different stores and be laid out differently.
/// </para>
/// </remarks>
public sealed class VortexDatasetSet
{
    private readonly VortexDataset[] _datasets;

    /// <summary>A set of <paramref name="datasets"/>, read in that order.</summary>
    /// <param name="datasets">The datasets: one or more, opened under one session, of one schema.</param>
    /// <exception cref="ArgumentException">No dataset, a dataset given twice, or datasets of two sessions or two schemas.</exception>
    public VortexDatasetSet(params ReadOnlySpan<VortexDataset> datasets)
    {
        if (datasets.IsEmpty)
        {
            throw new ArgumentException("A set reads one dataset or more.", nameof(datasets));
        }

        _datasets = datasets.ToArray();
        for (int d = 0; d < _datasets.Length; d++)
        {
            ArgumentNullException.ThrowIfNull(_datasets[d], nameof(datasets));
            if (Array.IndexOf(_datasets, _datasets[d]) != d)
            {
                throw new ArgumentException($"Dataset {d + 1} of the set is given twice: its rows would be read twice.", nameof(datasets));
            }
        }

        // The checks a scan makes, made once here so that a set that cannot be read is refused where it is declared.
        _ = Source();
    }

    /// <summary>The datasets, in the order their rows are read.</summary>
    public IReadOnlyList<VortexDataset> Datasets => _datasets;

    /// <summary>The columns of the datasets, as the first holds them now.</summary>
    public VortexSchema Schema => _datasets[0].Schema;

    /// <summary>The session the datasets were opened under, whose lanes and memory a question of the set runs under.</summary>
    public VortexSession Session => _datasets[0].Session;

    /// <summary>The rows of every dataset at the version its handle holds, deleted ones counted.</summary>
    public long RowCount
    {
        get
        {
            long rows = 0;
            foreach (VortexDataset dataset in _datasets)
            {
                rows += dataset.RowCount;
            }

            return rows;
        }
    }

    /// <summary>A scan of the versions the handles hold, typed by <typeparamref name="TRecord"/>, over every object of every dataset.</summary>
    /// <typeparam name="TRecord">The record; its members bind to the datasets' columns by name.</typeparam>
    /// <returns>A fresh scan of the versions the handles hold now, whatever the handles do next.</returns>
    /// <remarks>
    /// A batch's <c>StartRow</c> counts the set's rows, the first dataset's then the next one's, and <c>Rows</c> takes the
    /// same positions. A key cursor walks one dataset: a set has none.
    /// </remarks>
    /// <exception cref="ArgumentException">The datasets no longer have one schema, one of them having changed its own since.</exception>
    public Scan<TRecord> Scan<TRecord>()
        where TRecord : IVortexRecord<TRecord> => new Scan<TRecord>(Source());

    /// <summary>A scan of the versions the handles hold for a caller without a record type: columns by name, filters as text.</summary>
    /// <param name="columns">The columns to read, by top-level name or <c>.</c>-separated path; none reads every column.</param>
    /// <returns>A fresh scan of the versions the handles hold now.</returns>
    /// <exception cref="ArgumentException">The datasets no longer have one schema, one of them having changed its own since.</exception>
    public Scan Scan(params ReadOnlySpan<string> columns) => new Scan(Source(), columns);

    /// <summary>Moves every handle to its dataset's latest version, the datasets asked side by side.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <remarks>
    /// The refreshes overlap, so the set is refreshed in the time the slowest dataset takes. Each handle moves on its own:
    /// when one fails, the others have moved all the same, and the first failure is thrown once all have ended.
    /// </remarks>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_datasets.Length == 1)
        {
            await _datasets[0].RefreshAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Task<ulong>[] refreshes = new Task<ulong>[_datasets.Length];
        for (int d = 0; d < refreshes.Length; d++)
        {
            refreshes[d] = _datasets[d].RefreshAsync(cancellationToken).AsTask();
        }

        Exception? failed = null;
        foreach (Task<ulong> refresh in refreshes)
        {
            try
            {
                await refresh.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failed ??= exception;
            }
        }

        if (failed is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }
    }

    /// <summary>The union of the datasets' sources, each at the version its handle holds now.</summary>
    private UnionScanSource Source()
    {
        ScanSource[] members = new ScanSource[_datasets.Length];
        for (int d = 0; d < members.Length; d++)
        {
            members[d] = new DatasetScanSource(_datasets[d], _datasets[d].Snapshot);
        }

        return new UnionScanSource(members);
    }
}
