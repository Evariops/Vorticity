using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// The key tuples of a dataset's object as its entries in key order (<see cref="ISortedEntries"/>): its writer sorted its
/// rows by them, nulls last, so every row is an entry and its rank its row. A key is the row encoding of a row's tuple, as
/// a composite sorted run held it. A bound bisects the granules by the first keys its sparse index holds, reading a
/// granule's first tuples only where a key shorter than the sought one leaves the order open, then the tuples of one
/// granule, read by its rows alone.
/// </summary>
internal sealed class ObjectKeyEntries : ISortedEntries
{
    /// <summary>The rows a part holds when the object keeps no sparse index to say its granules.</summary>
    private const int DefaultGranuleRows = 8192;

    private readonly VortexFile _file;
    private readonly string[] _paths;
    private readonly SortKey _key;
    private readonly GranuleKeys? _index;
    private readonly int _granuleRows;
    private readonly long _rows;

    // The granule loaded: its first row, its rows, their tuples end to end and where each ends.
    private long _loadedStart = -1;
    private int _loadedRows;
    private byte[] _keys = [];
    private int[] _ends = [];

    /// <summary>
    /// The entries of <paramref name="file"/>, whose rows are in the order of the tuple of <paramref name="paths"/>, as the
    /// object names them, nulls last; <paramref name="index"/> its sparse index with every page in hand, or null to bisect
    /// its granules by reading them.
    /// </summary>
    internal ObjectKeyEntries(VortexFile file, string[] paths, GranuleKeys? index)
    {
        _file = file;
        _paths = paths;
        _key = SortKey.OfColumns(file.DType, paths, descending: false);
        _index = index is { Rows: var rows } && rows == file.RowCount ? index : null;
        _granuleRows = _index?.GranuleRows ?? DefaultGranuleRows;
        _rows = file.RowCount;
        RowSortField[] fields = new RowSortField[paths.Length];
        Array.Fill(fields, KeyOrderedMerge.Field(descending: false));
        KeyFormat = new RowKeyEncoder(fields).Format;
    }

    public FilterLiteralKind KeyKind => FilterLiteralKind.Bytes;

    public string? KeyFormat { get; }

    public long EntryCount => _rows;

    public long RowOf(long entry) => entry;

    public int Compare(FilterLiteral left, FilterLiteral right) => Math.Sign(left.BytesValue.SequenceCompareTo(right.BytesValue));

    /// <summary>Its keys are read through its own scans: nothing to share.</summary>
    public void Share(ScanSegments held, Arrays.RetainedChunks retained)
    {
    }

    public ValueTask EnsureEntryAsync(long entry, CancellationToken cancellationToken) =>
        _loadedStart >= 0 && entry >= _loadedStart && entry < _loadedStart + _loadedRows
            ? ValueTask.CompletedTask
            : LoadAsync(entry / _granuleRows, cancellationToken);

    public FilterLiteral LoadedKey(long entry) => FilterLiteral.From(BytesAt(entry));

    public ReadOnlySpan<byte> BytesAt(long entry)
    {
        int at = (int)(entry - _loadedStart);
        return (uint)at < (uint)_loadedRows ? Loaded(at) : default;
    }

    public long PastKeyInZone(long entry, bool forward)
    {
        int at = (int)(entry - _loadedStart);
        ReadOnlySpan<byte> key = Loaded(at);
        if (forward)
        {
            int past = Bound(key, strict: true, at, _loadedRows);
            return past < _loadedRows ? _loadedStart + past : -1;
        }

        int first = Bound(key, strict: false, 0, at);
        return first > 0 ? _loadedStart + first - 1 : -1;
    }

    public ValueTask<long> LowerBoundAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(key.BytesValue.ToArray(), strict: false, cancellationToken);

    public ValueTask<long> UpperBoundAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(key.BytesValue.ToArray(), strict: true, cancellationToken);

    public ValueTask<long> BoundOfAsync(KeySource lender, bool strict, CancellationToken cancellationToken) =>
        BoundAsync(lender.KeyBytes.ToArray(), strict, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _loadedStart = -1;
        _loadedRows = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The first row whose tuple lies at or past <paramref name="key"/>, or strictly past it: the granules bisected for the
    /// first whose first tuple is, the bound lying in the one before, or at its start.
    /// </summary>
    private async ValueTask<long> BoundAsync(byte[] key, bool strict, CancellationToken cancellationToken)
    {
        if (_rows == 0)
        {
            return 0;
        }

        int granules = (int)((_rows + _granuleRows - 1) / _granuleRows);
        int low = 0;
        int high = granules;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (await FirstPastAsync(middle, key, strict, cancellationToken).ConfigureAwait(false))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        if (low == 0)
        {
            return 0;
        }

        await LoadAsync(low - 1, cancellationToken).ConfigureAwait(false);
        return _loadedStart + Bound(key, strict, 0, _loadedRows);
    }

    /// <summary>
    /// Whether granule <paramref name="granule"/>'s first tuple lies at or past <paramref name="key"/>, or strictly past it:
    /// by its key in the sparse index when that decides, a prefix of the tuple that is no prefix of the sought key, and by
    /// its first row otherwise.
    /// </summary>
    private async ValueTask<bool> FirstPastAsync(int granule, byte[] key, bool strict, CancellationToken cancellationToken)
    {
        if (_index is { } index)
        {
            ReadOnlySpan<byte> first = index.Key(granule);
            if (!key.AsSpan().StartsWith(first))
            {
                return first.SequenceCompareTo(key) > 0;
            }
        }

        await LoadAsync(granule, cancellationToken).ConfigureAwait(false);
        int order = Loaded(0).SequenceCompareTo(key);
        return strict ? order > 0 : order >= 0;
    }

    /// <summary>The first loaded row from <paramref name="from"/> to <paramref name="to"/> whose tuple lies at or past <paramref name="key"/>, or past it.</summary>
    private int Bound(ReadOnlySpan<byte> key, bool strict, int from, int to)
    {
        while (from < to)
        {
            int middle = from + ((to - from) >> 1);
            int order = Loaded(middle).SequenceCompareTo(key);
            if (strict ? order <= 0 : order < 0)
            {
                from = middle + 1;
            }
            else
            {
                to = middle;
            }
        }

        return from;
    }

    private ReadOnlySpan<byte> Loaded(int at) => _keys.AsSpan(at == 0 ? 0 : _ends[at - 1], _ends[at] - (at == 0 ? 0 : _ends[at - 1]));

    /// <summary>Reads granule <paramref name="granule"/>'s tuples, by its rows alone, unless it is loaded.</summary>
    private async ValueTask LoadAsync(long granule, CancellationToken cancellationToken)
    {
        long start = granule * _granuleRows;
        if (start == _loadedStart)
        {
            return;
        }

        long end = Math.Min(start + _granuleRows, _rows);
        int rows = (int)(end - start);
        if (_ends.Length < rows)
        {
            _ends = new int[Math.Max(rows, _granuleRows)];
        }

        _loadedStart = -1;
        int at = 0;
        int count = 0;
        ScanBuilder scan = _file.ScanBuilder().Project(_paths).Rows(new RowRange(start, end));
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using RowKeys keys = _key.Encode(batch);
            for (int row = 0; row < batch.RowCount; row++)
            {
                ReadOnlySpan<byte> tuple = keys.Row(row);
                if (at + tuple.Length > _keys.Length)
                {
                    Array.Resize(ref _keys, Math.Max(2 * _keys.Length, at + tuple.Length + 4096));
                }

                tuple.CopyTo(_keys.AsSpan(at));
                at += tuple.Length;
                _ends[count++] = at;
            }
        }

        _loadedStart = start;
        _loadedRows = count;
    }
}
