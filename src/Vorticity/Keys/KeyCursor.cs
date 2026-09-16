// The order model - docs/12-index-reads.md §4. The scan answers "which rows"; a cursor answers
// "what is next", which no set can.
//
// OVER A SORTED COLUMN THE MERGE IS DEGENERATE, and that is the whole reason this source comes
// first. The entries are the column's non-null rows, contiguous and already in key order, so a step
// is an addition, a rank is a subtraction, and the k-way merge docs/12 §9 describes -- a heap of
// run positions, `O(log r)` per step -- is what the `SortedRuns` source will need when a file
// carries several runs. Nothing here pretends to be that heap; the cursor is written against entry
// INDICES so that the heap can be slid underneath without the surface moving.
//
// A DIRECTION FLIP COSTS NOTHING HERE. §4.2 charges a re-seek for it, because a min-heap of run
// positions does not run backwards. One contiguous run does, so `Prev` after `Next` is `i - 1`, and
// the charge stays in the spec for the source that will owe it.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>A position in one column's entries, in key order.</summary>
/// <remarks>
/// Not thread-safe, like the builder that made it. Every positioning method is asynchronous
/// because it may decode a zone the file has not loaded yet; a step inside a loaded zone completes
/// synchronously and allocates nothing.
/// </remarks>
public sealed class KeyCursor : IAsyncDisposable
{
    private readonly SortedColumnSource _source;
    private long _entry = -1;
    private bool _disposed;

    internal KeyCursor(SortedColumnSource source) => _source = source;

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => _entry >= 0;

    /// <summary>Whether an entry can say which file row it came from. Always true on a sorted column.</summary>
    public bool HasRows => true;

    /// <summary>The column's comparison domain: what a seek key must be.</summary>
    public FilterLiteralKind KeyKind => _source.KeyKind;

    /// <summary>The source's entries, known without a walk on a sorted column.</summary>
    public long? EntryCount => _source.EntryCount;

    /// <summary>
    /// The current entry's key. <b>Copies</b> a byte key, since a <see cref="FilterLiteral"/> owns
    /// its array; <see cref="KeyBytes"/> lends it instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public FilterLiteral Key
    {
        get
        {
            RequireValid();
            return _source.LoadedKey(_entry);
        }
    }

    /// <summary>
    /// The current entry's key as bytes, borrowed until the next positioning call, and empty for a
    /// column whose keys are not bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ReadOnlySpan<byte> KeyBytes
    {
        get
        {
            RequireValid();
            return _source.BytesAt(_entry);
        }
    }

    /// <summary>The current entry's file row, the coordinate <c>Take</c> and <c>Rows</c> accept.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public long Row
    {
        get
        {
            RequireValid();
            return _source.RowOf(_entry);
        }
    }

    /// <summary>
    /// Orders two keys in the total order of docs/12-index-reads.md §4.4, so that a consumer
    /// merging two cursors does not write a comparator that disagrees with theirs.
    /// </summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other, of the same domain.</param>
    /// <returns>The sign of <c>left - right</c>.</returns>
    /// <remarks>
    /// THIS IS THE TOTAL ORDER, which separates <c>-0.0</c> from <c>+0.0</c> and places NaN at the
    /// ends. A cursor over a sorted COLUMN walks the order its file is actually in, which is IEEE,
    /// so on that source the two zeros are one key and no NaN occurs at all -- a float column
    /// holding one is not <c>is_sorted</c>. The two orders differ nowhere else.
    /// </remarks>
    /// <exception cref="ArgumentException">The two keys are of different domains.</exception>
    public static int Compare(FilterLiteral left, FilterLiteral right) => KeyOrder.Total(left, right);

    /// <summary>Positions the cursor relative to <paramref name="key"/>.</summary>
    /// <param name="key">The sought key, in the column's domain.</param>
    /// <param name="op">Where to land.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    /// <returns>Whether an entry was found; the cursor is invalid when not.</returns>
    /// <exception cref="ArgumentException">The key is of the wrong domain, or is null.</exception>
    public async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        long entries = _source.EntryCount;
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
                if (at >= entries)
                {
                    return Invalidate();
                }

                // `lower_bound` lands on the first entry NOT below the key, which is the key
                // itself when it is present and its successor when it is not.
                await _source.EnsureEntryAsync(at, cancellationToken).ConfigureAwait(false);
                if (SortedColumnSource.Compare(_source.LoadedKey(at), key) != 0)
                {
                    return Invalidate();
                }

                break;
        }

        return await PositionAsync(at, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Positions on the smallest key's first entry.</summary>
    /// <param name="cancellationToken">Cancels the decode this makes.</param>
    /// <returns>Whether the source has an entry at all.</returns>
    public ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default) =>
        PositionAsync(0, cancellationToken);

    /// <summary>Positions on the largest key's last entry.</summary>
    /// <param name="cancellationToken">Cancels the decode this makes.</param>
    /// <returns>Whether the source has an entry at all.</returns>
    public ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken = default) =>
        PositionAsync(_source.EntryCount - 1, cancellationToken);

    /// <summary>Steps to the next entry in <c>(key, row)</c> order.</summary>
    /// <param name="cancellationToken">Cancels the decode this may make.</param>
    /// <returns>Whether there was one; the cursor is invalid past the end.</returns>
    public ValueTask<bool> NextAsync(CancellationToken cancellationToken = default) =>
        IsValid ? PositionAsync(_entry + 1, cancellationToken) : new ValueTask<bool>(false);

    /// <summary>Steps to the previous entry.</summary>
    /// <param name="cancellationToken">Cancels the decode this may make.</param>
    /// <returns>Whether there was one; the cursor is invalid past the start.</returns>
    public ValueTask<bool> PrevAsync(CancellationToken cancellationToken = default) =>
        IsValid ? PositionAsync(_entry - 1, cancellationToken) : new ValueTask<bool>(false);

    /// <summary>
    /// Steps to the first entry of the next distinct key: the loose index scan, one seek per group
    /// rather than one step per row.
    /// </summary>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    /// <returns>Whether there was a next key.</returns>
    public async ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsValid)
        {
            return false;
        }

        long at = await _source.UpperBoundAsync(Key, cancellationToken).ConfigureAwait(false);
        return await PositionAsync(at, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Steps to the LAST entry of the previous distinct key.</summary>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    /// <returns>Whether there was a previous key.</returns>
    public async ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsValid)
        {
            return false;
        }

        long at = await _source.LowerBoundAsync(Key, cancellationToken).ConfigureAwait(false) - 1;
        return await PositionAsync(at, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many entries have a key below <paramref name="key"/> (docs/12 §4.5).</summary>
    /// <param name="key">The key to rank, in the column's domain.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    /// <returns>The rank, between zero and <see cref="EntryCount"/>.</returns>
    /// <exception cref="ArgumentException">The key is of the wrong domain, or is null.</exception>
    public ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        return _source.LowerBoundAsync(key, cancellationToken);
    }

    /// <summary>Positions on the entry of rank <paramref name="rank"/>, zero-based.</summary>
    /// <param name="rank">The rank.</param>
    /// <param name="cancellationToken">Cancels the decode this makes.</param>
    /// <returns>Whether the rank names an entry.</returns>
    public ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken = default) =>
        PositionAsync(rank, cancellationToken);

    /// <summary>How many entries share the current key: <c>rank(after) - rank(at)</c>.</summary>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    /// <returns>The count, at least one.</returns>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public async ValueTask<long> KeyCountAsync(CancellationToken cancellationToken = default)
    {
        RequireValid();
        FilterLiteral key = Key;
        long low = await _source.LowerBoundAsync(key, cancellationToken).ConfigureAwait(false);
        long high = await _source.UpperBoundAsync(key, cancellationToken).ConfigureAwait(false);

        // The positioning the count consumed is put back, so a caller can count and then step.
        await PositionAsync(_entry, cancellationToken).ConfigureAwait(false);
        return high - low;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entry = -1;
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> PositionAsync(long entry, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry < 0 || entry >= _source.EntryCount)
        {
            return Invalidate();
        }

        await _source.EnsureEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        _entry = entry;
        return true;
    }

    private bool Invalidate()
    {
        _entry = -1;
        return false;
    }

    private void RequireValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                "The cursor is not positioned on an entry; check the result of the positioning call.");
        }
    }

    private void RequireKey(FilterLiteral key)
    {
        if (key.Kind == FilterLiteralKind.Null)
        {
            throw new ArgumentException(
                "No entry has a null key: nulls are in no key source (docs/12-index-reads.md §3).",
                nameof(key));
        }

        if (!KeyOrder.Fits(key, KeyKind))
        {
            throw new ArgumentException(
                $"This column's keys are {KeyKind}; a {key.Kind} key cannot be ordered against " +
                "them (docs/12-index-reads.md §4.4).",
                nameof(key));
        }
    }
}
