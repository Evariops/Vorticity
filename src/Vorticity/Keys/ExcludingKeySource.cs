using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity.Keys;

/// <summary>One entry a cursor leaves out: its key, and the row it came from.</summary>
/// <param name="Key">The entry's key, in the column's domain; a composite key's row encoding as bytes.</param>
/// <param name="Row">The file row.</param>
internal readonly record struct ExcludedKey(FilterLiteral Key, long Row);

/// <summary>
/// A key source without the entries of some rows: the rows a dataset deleted from a file without
/// rewriting it. Every step lands on a kept entry, and every rank counts kept entries only.
/// </summary>
/// <remarks>
/// <para>
/// A step or a seek lands where the source does, then steps on in the same direction past the
/// rows left out. A rank is the source's, less the entries left out below it, which is where the
/// two ways of knowing them part.
/// </para>
/// <para>
/// When the entries are rows <c>[offset, offset + entries)</c> in rank order -- a sorted column's,
/// or a key the file's rows are sorted by -- an entry's rank is its row less the offset, and every
/// rank is arithmetic on the rows left out: no key of theirs is needed. Otherwise the caller hands
/// the keys of the rows left out, sorted in <c>(key, row)</c> order, which the ranks are counted
/// against.
/// </para>
/// </remarks>
internal sealed class ExcludingKeySource : KeySource
{
    private readonly KeySource _inner;
    private readonly IRowExclusion _rows;
    private readonly long _offset = -1;
    private readonly ExcludedKey[] _keys = [];
    private readonly long _entries;

    /// <summary>A source whose entries are rows <c>[offset, offset + entries)</c> in rank order.</summary>
    internal ExcludingKeySource(KeySource inner, IRowExclusion rows, long offset)
    {
        _inner = inner;
        _rows = rows;
        _offset = offset;
        long? entries = inner.EntryCount;
        _entries = entries is { } count
            ? count - (rows.ExcludedBefore(offset + count) - rows.ExcludedBefore(offset))
            : throw new ArgumentException("A source whose ranks are its rows counts its entries.", nameof(inner));
    }

    /// <summary>A source whose left-out entries are <paramref name="keys"/>, in <c>(key, row)</c> order.</summary>
    internal ExcludingKeySource(KeySource inner, IRowExclusion rows, ExcludedKey[] keys)
    {
        _inner = inner;
        _rows = rows;
        _keys = keys;
        _entries = inner.EntryCount is { } count ? count - keys.Length : 0;
    }

    /// <summary>Whether ranks are rows less an offset rather than counted against keys.</summary>
    private bool ByRow => _offset >= 0;

    internal override FilterLiteralKind KeyKind => _inner.KeyKind;

    internal override long? EntryCount => _inner.EntryCount is null ? null : _entries;

    internal override int Runs => _inner.Runs;

    internal override bool HasRows => _inner.HasRows;

    internal override string? KeyFormat => _inner.KeyFormat;

    internal override bool IsValid => _inner.IsValid;

    internal override FilterLiteral Key => _inner.Key;

    internal override ReadOnlySpan<byte> KeyBytes => _inner.KeyBytes;

    internal override long Row => _inner.Row;

    internal override void Share(ScanSegments held, Arrays.RetainedChunks retained) => _inner.Share(held, retained);

    internal override async ValueTask<bool> SeekAsync(FilterLiteral key, SeekOp op, CancellationToken cancellationToken)
    {
        bool forward = op is not (SeekOp.AtOrBefore or SeekOp.Before);
        if (!await _inner.SeekAsync(key, op, cancellationToken).ConfigureAwait(false)
            || !await KeptAsync(forward, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // An exact seek landed on the key's first kept entry, or past the key when every one of
        // its entries is left out, in which case the key is not there.
        if (op == SeekOp.Exact && KeyOrder.Total(_inner.Key, key) != 0)
        {
            _inner.Invalidate();
            return false;
        }

        return true;
    }

    internal override async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken) =>
        await _inner.SeekFirstAsync(cancellationToken).ConfigureAwait(false)
        && await KeptAsync(forward: true, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken) =>
        await _inner.SeekLastAsync(cancellationToken).ConfigureAwait(false)
        && await KeptAsync(forward: false, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> NextAsync(CancellationToken cancellationToken) =>
        await _inner.NextAsync(cancellationToken).ConfigureAwait(false)
        && await KeptAsync(forward: true, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> PrevAsync(CancellationToken cancellationToken) =>
        await _inner.PrevAsync(cancellationToken).ConfigureAwait(false)
        && await KeptAsync(forward: false, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken) =>
        await _inner.NextKeyAsync(cancellationToken).ConfigureAwait(false)
        && await KeptAsync(forward: true, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken) =>
        await _inner.PrevKeyAsync(cancellationToken).ConfigureAwait(false)
        && await KeptAsync(forward: false, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken)
    {
        long rank = await _inner.RankAsync(key, cancellationToken).ConfigureAwait(false);
        return rank - (ByRow ? ExcludedBelowRank(rank) : Below(key, upper: false));
    }

    internal override async ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken)
    {
        long rank = await _inner.UpperRankAsync(key, cancellationToken).ConfigureAwait(false);
        return rank - (ByRow ? ExcludedBelowRank(rank) : Below(key, upper: true));
    }

    internal override async ValueTask<long> RankOfAsync(KeySource other, bool upper, CancellationToken cancellationToken)
    {
        // Read before the inner source ranks, which may reposition a source the other shares nothing with.
        FilterLiteral key = ByRow ? default : other.Key;
        long rank = await _inner.RankOfAsync(other, upper, cancellationToken).ConfigureAwait(false);
        return rank - (ByRow ? ExcludedBelowRank(rank) : Below(key, upper));
    }

    internal override async ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken)
    {
        if (rank < 0 || rank >= _entries)
        {
            _inner.Invalidate();
            return false;
        }

        if (ByRow)
        {
            // The kept entry of that rank is the kept row that many kept rows past the offset's.
            long row = _rows.KeptRow(rank + _offset - _rows.ExcludedBefore(_offset));
            return await _inner.SeekRankAsync(row - _offset, cancellationToken).ConfigureAwait(false);
        }

        // The rank among all entries whose kept rank is the one sought: past every entry left out
        // below it. The count of those grows as the rank does, so the least fixed point is reached
        // from below, one landing at a time.
        long at = rank;
        while (true)
        {
            if (!await _inner.SeekRankAsync(at, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            long before = Before(_inner.Key, _inner.Row);
            bool kept = !_rows.Excludes(_inner.Row);
            if (at - before == rank && kept)
            {
                return true;
            }

            at = rank + before + (kept ? 0 : 1);
        }
    }

    internal override async ValueTask<long> CountAtKeyAsync(CancellationToken cancellationToken)
    {
        FilterLiteral key = _inner.Key;
        return await UpperRankAsync(key, cancellationToken).ConfigureAwait(false)
            - await RankAsync(key, cancellationToken).ConfigureAwait(false);
    }

    internal override ValueTask<int> RunsOverlappingAsync(
        System.Collections.Generic.List<(long Low, long High)> slices, CancellationToken cancellationToken) =>
        _inner.RunsOverlappingAsync(slices, cancellationToken);

    internal override void Invalidate() => _inner.Invalidate();

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>
    /// Steps on in the direction past the entries left out; false when the walk runs out. When ranks
    /// are rows, the run of rows an entry left out lies in is passed in one seek, to the rank past its
    /// end or before its start; a few steps cost less than a seek, and a short run is stepped over.
    /// </summary>
    private async ValueTask<bool> KeptAsync(bool forward, CancellationToken cancellationToken)
    {
        while (_inner.IsValid && _rows.Excludes(_inner.Row))
        {
            if (ByRow)
            {
                int run = _rows.FirstEndingAfter(_inner.Row);
                long past = forward ? _rows.EndOf(run) : _rows.StartOf(run) - 1;
                if (Math.Abs(past - _inner.Row) > SteppedRun)
                {
                    long rank = past - _offset;
                    if (rank < 0 || rank >= _inner.EntryCount)
                    {
                        _inner.Invalidate();
                        return false;
                    }

                    if (!await _inner.SeekRankAsync(rank, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    continue;
                }
            }

            if (!(forward
                ? await _inner.NextAsync(cancellationToken).ConfigureAwait(false)
                : await _inner.PrevAsync(cancellationToken).ConfigureAwait(false)))
            {
                return false;
            }
        }

        return _inner.IsValid;
    }

    /// <summary>The longest run of rows left out that a walk steps over rather than seeks past.</summary>
    private const int SteppedRun = 16;

    /// <summary>The entries left out below an entry rank, when ranks are rows.</summary>
    private long ExcludedBelowRank(long rank) => _rows.ExcludedBefore(_offset + rank) - _rows.ExcludedBefore(_offset);

    /// <summary>The entries left out whose key is below <paramref name="key"/>, or at or below it with <paramref name="upper"/>.</summary>
    private long Below(FilterLiteral key, bool upper)
    {
        int low = 0;
        int high = _keys.Length;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            int order = KeyOrder.Total(_keys[middle].Key, key);
            if (order < 0 || (upper && order == 0))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The entries left out before the entry <c>(key, row)</c> in <c>(key, row)</c> order.</summary>
    private long Before(FilterLiteral key, long row)
    {
        int low = 0;
        int high = _keys.Length;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            int order = KeyOrder.Total(_keys[middle].Key, key);
            if (order < 0 || (order == 0 && _keys[middle].Row < row))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
