using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>A position in a sorted column's entries.</summary>
/// <remarks>
/// The entries are the column's non-null rows, contiguous and already in key order, so there is no
/// merge to do: a step is an addition, a rank is a subtraction, and reversing direction costs
/// nothing. That is why this source is preferred over the sorted runs whenever it exists.
/// </remarks>
internal sealed class SortedColumnWalker : KeySource
{
    private readonly SortedColumnSource _source;
    private long _entry = -1;

    internal SortedColumnWalker(SortedColumnSource source) => _source = source;

    /// <summary>The file row of the first entry: the column's entries are the rows from here on.</summary>
    internal long FirstRow => _source.RowOf(0);

    internal override FilterLiteralKind KeyKind => _source.KeyKind;

    internal override long? EntryCount => _source.EntryCount;

    internal override int Runs => 1;

    internal override bool IsValid => _entry >= 0;

    internal override FilterLiteral Key => _source.LoadedKey(_entry);

    internal override ReadOnlySpan<byte> KeyBytes => _source.BytesAt(_entry);

    internal override long Row => _source.RowOf(_entry);

    internal override void Share(Scanning.ScanSegments held, Arrays.RetainedChunks retained) => _source.Share(held, retained);

    internal override async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op, CancellationToken cancellationToken)
    {
        long at;
        switch (op)
        {
            case SeekOp.AtOrAfter:
                at = await _source.LowerBoundAsync(key, cancellationToken).ConfigureAwait(false);
                break;
            case SeekOp.After:
                at = await _source.UpperBoundAsync(key, cancellationToken).ConfigureAwait(false);
                break;
            case SeekOp.AtOrBefore:
                at = await _source.UpperBoundAsync(key, cancellationToken).ConfigureAwait(false) - 1;
                break;
            case SeekOp.Before:
                at = await _source.LowerBoundAsync(key, cancellationToken).ConfigureAwait(false) - 1;
                break;
            default:
                at = await _source.LowerBoundAsync(key, cancellationToken).ConfigureAwait(false);
                if (at >= _source.EntryCount)
                {
                    Invalidate();
                    return false;
                }

                // `lower_bound` lands on the first entry that is not below the key, which is the
                // key itself when it is present and its successor when it is not.
                await _source.EnsureEntryAsync(at, cancellationToken).ConfigureAwait(false);
                if (SortedColumnSource.Compare(_source.LoadedKey(at), key) != 0)
                {
                    Invalidate();
                    return false;
                }

                break;
        }

        return await PositionAsync(at, cancellationToken).ConfigureAwait(false);
    }

    internal override ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken) =>
        PositionAsync(0, cancellationToken);

    internal override ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken) =>
        PositionAsync(_source.EntryCount - 1, cancellationToken);

    internal override ValueTask<bool> NextAsync(CancellationToken cancellationToken) =>
        PositionAsync(_entry + 1, cancellationToken);

    internal override ValueTask<bool> PrevAsync(CancellationToken cancellationToken) =>
        PositionAsync(_entry - 1, cancellationToken);

    internal override ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken) =>
        PastKeyAsync(forward: true, cancellationToken);

    internal override ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken) =>
        PastKeyAsync(forward: false, cancellationToken);

    /// <remarks>
    /// The key's neighbour is sought from here, in the loaded zone, where it usually is, and landing
    /// there is the whole step; a bound over the whole column is taken only when the key reaches the
    /// zone's edge.
    /// </remarks>
    private ValueTask<bool> PastKeyAsync(bool forward, CancellationToken cancellationToken)
    {
        long at = _source.PastKeyInZone(_entry, forward);
        if (at < 0)
        {
            return PastZoneAsync(forward, cancellationToken);
        }

        _entry = at;
        return new ValueTask<bool>(true);
    }

    /// <summary>Steps past the current key the long way, when its entries reach the loaded zone's edge.</summary>
    private async ValueTask<bool> PastZoneAsync(bool forward, CancellationToken cancellationToken)
    {
        long at = forward
            ? await _source.UpperBoundAsync(Key, cancellationToken).ConfigureAwait(false)
            : await _source.LowerBoundAsync(Key, cancellationToken).ConfigureAwait(false) - 1;
        return await PositionAsync(at, cancellationToken).ConfigureAwait(false);
    }

    internal override ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(key, upper: false, cancellationToken);

    internal override ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(key, upper: true, cancellationToken);

    internal override ValueTask<long> RankOfAsync(KeySource other, bool upper, CancellationToken cancellationToken) =>
        KeyKind == FilterLiteralKind.Bytes
            ? KeepingPositionAsync(_source.BoundOfAsync(other, upper, cancellationToken), cancellationToken)
            : BoundAsync(other.Key, upper, cancellationToken);

    private ValueTask<long> BoundAsync(FilterLiteral key, bool upper, CancellationToken cancellationToken) =>
        KeepingPositionAsync(
            upper ? _source.UpperBoundAsync(key, cancellationToken) : _source.LowerBoundAsync(key, cancellationToken),
            cancellationToken);

    private async ValueTask<long> KeepingPositionAsync(ValueTask<long> bound, CancellationToken cancellationToken)
    {
        long rank = await bound.ConfigureAwait(false);

        // The bisection may have decoded another zone; the position is put back.
        if (IsValid)
        {
            await _source.EnsureEntryAsync(_entry, cancellationToken).ConfigureAwait(false);
        }

        return rank;
    }

    internal override ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken) =>
        PositionAsync(rank, cancellationToken);

    internal override async ValueTask<long> CountAtKeyAsync(CancellationToken cancellationToken)
    {
        FilterLiteral key = Key;
        long low = await _source.LowerBoundAsync(key, cancellationToken).ConfigureAwait(false);
        long high = await _source.UpperBoundAsync(key, cancellationToken).ConfigureAwait(false);

        // The positioning the count consumed is put back, so a caller can count and then step.
        await _source.EnsureEntryAsync(_entry, cancellationToken).ConfigureAwait(false);
        return high - low;
    }

    internal override void Invalidate() => _entry = -1;

    public override ValueTask DisposeAsync()
    {
        _entry = -1;
        return _source.DisposeAsync();
    }

    private async ValueTask<bool> PositionAsync(long entry, CancellationToken cancellationToken)
    {
        if (entry < 0 || entry >= _source.EntryCount)
        {
            Invalidate();
            return false;
        }

        await _source.EnsureEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        _entry = entry;
        return true;
    }
}
