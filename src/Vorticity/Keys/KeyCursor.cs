using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>A position in one column's entries, in key order.</summary>
/// <remarks>
/// A scan answers "which rows"; a cursor answers "what is next", which no set of rows can. The
/// surface is the same whatever serves it: a sorted column walks as one contiguous run, sorted runs
/// walk as a k-way merge. The cursor itself only checks the arguments and the lifetime — a key of
/// the column's domain, never null, and a position to be on — so those rules are stated once here
/// and the source does the walking.
/// <para>
/// Not thread-safe, like the builder that made it. Every positioning method is asynchronous because
/// it may read a zone or a run the file has not loaded yet; a step inside a loaded one completes
/// synchronously and allocates nothing.
/// </para>
/// </remarks>
public sealed class KeyCursor : IAsyncDisposable
{
    private readonly KeySource _source;
    private readonly bool _distinct;
    private bool _disposed;

    internal KeyCursor(KeySource source, bool distinct = false)
    {
        _source = source;
        _distinct = distinct;
    }

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => !_disposed && _source.IsValid;

    /// <summary>
    /// Whether an entry can say which file row it came from: false for a <c>Distinct()</c> cursor
    /// served by postings or a dictionary, which hold keys without rows.
    /// </summary>
    public bool HasRows => _source.HasRows;

    /// <summary>
    /// Whether the cursor visits each key once (<c>KeyCursorBuilder.Distinct()</c>): every step is
    /// a <see cref="NextKeyAsync"/>, and an entry is its key's first.
    /// </summary>
    public bool IsDistinct => _distinct;

    /// <summary>
    /// For a composite key, what its bytes follow -- the writer's <c>IKeyEncoder.Format</c>, which
    /// names the encoding and the per-column direction and null placement. A seek key is comparable
    /// only when built the same way. Null for a single column.
    /// </summary>
    public string? KeyFormat => _source.KeyFormat;

    /// <summary>The column's comparison domain: what a seek key must be.</summary>
    public FilterLiteralKind KeyKind => _source.KeyKind;

    /// <summary>
    /// The source's entries, when known without a walk: the column's non-null rows for a source
    /// with rows, null for a source of keys without rows, whose entries repeat a key per chunk.
    /// </summary>
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
            return _source.Key;
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
            return _source.KeyBytes;
        }
    }

    /// <summary>
    /// The current entry's file row, the coordinate <c>Take</c> and <c>Rows</c> accept; for a
    /// <c>Distinct()</c> cursor, the key's first row.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned, or its source has no rows (<see cref="HasRows"/>).</exception>
    public long Row
    {
        get
        {
            RequireValid();
            RequireRows(nameof(Row));
            return _source.Row;
        }
    }

    /// <summary>
    /// Orders two keys in the keys' own total order, so that a consumer merging two cursors does
    /// not write a comparator that disagrees with theirs.
    /// </summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other, of the same domain.</param>
    /// <returns>The sign of <c>left - right</c>.</returns>
    /// <remarks>
    /// The total order separates <c>-0.0</c> from <c>+0.0</c> and places NaN at the ends, and it is
    /// the order sorted runs are in. A cursor over a sorted column walks the order its file is
    /// actually in, which is IEEE, so on that source the two zeros are one key and no NaN occurs at
    /// all -- a float column holding one is not marked sorted. The two orders differ nowhere else.
    /// </remarks>
    /// <exception cref="ArgumentException">The two keys are of different domains.</exception>
    public static int Compare(FilterLiteral left, FilterLiteral right) => KeyOrder.Total(left, right);

    /// <summary>Positions the cursor relative to <paramref name="key"/>.</summary>
    /// <param name="key">The sought key, in the column's domain.</param>
    /// <param name="op">Where to land.</param>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>Whether an entry was found; the cursor is invalid when not.</returns>
    /// <exception cref="ArgumentException">The key is of the wrong domain, or is null.</exception>
    public ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Diagnostics.VortexEventSource.Seek();
        ValueTask<bool> seek = _source.SeekAsync(key, op, cancellationToken);
        return _distinct && op is SeekOp.AtOrBefore or SeekOp.Before
            ? FirstOfKeyAsync(seek, cancellationToken)
            : seek;
    }

    /// <summary>
    /// A distinct cursor's backward landing, moved to its key's first entry: a backward seek lands
    /// on a key's last one, and a distinct entry is the key's first.
    /// </summary>
    private async ValueTask<bool> FirstOfKeyAsync(ValueTask<bool> landing, CancellationToken cancellationToken) =>
        await landing.ConfigureAwait(false)
        && await _source.SeekAsync(_source.Key, SeekOp.AtOrAfter, cancellationToken).ConfigureAwait(false);

    /// <summary>Positions on the smallest key's first entry.</summary>
    /// <param name="cancellationToken">Cancels the read this makes.</param>
    /// <returns>Whether the source has an entry at all.</returns>
    public ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Diagnostics.VortexEventSource.Seek();
        return _source.SeekFirstAsync(cancellationToken);
    }

    /// <summary>Positions on the largest key's last entry.</summary>
    /// <param name="cancellationToken">Cancels the read this makes.</param>
    /// <returns>Whether the source has an entry at all.</returns>
    public ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Diagnostics.VortexEventSource.Seek();
        ValueTask<bool> seek = _source.SeekLastAsync(cancellationToken);
        return _distinct ? FirstOfKeyAsync(seek, cancellationToken) : seek;
    }

    /// <summary>
    /// Steps to the next entry in <c>(key, row)</c> order; on a <c>Distinct()</c> cursor, to the
    /// next key.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read this may make.</param>
    /// <returns>Whether there was one; the cursor is invalid past the end.</returns>
    public ValueTask<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_source.IsValid)
        {
            return new ValueTask<bool>(false);
        }

        // A distinct step is a seek past the key's other entries.
        if (_distinct)
        {
            Diagnostics.VortexEventSource.Seek();
            return _source.NextKeyAsync(cancellationToken);
        }

        Diagnostics.VortexEventSource.Step();
        return _source.NextAsync(cancellationToken);
    }

    /// <summary>
    /// Steps to the previous entry; on a <c>Distinct()</c> cursor, to the previous key's first entry.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read this may make.</param>
    /// <returns>Whether there was one; the cursor is invalid past the start.</returns>
    /// <remarks>
    /// Over sorted runs, stepping against the direction of the last step re-seeks at the current
    /// entry: a min-heap of run positions does not run backwards.
    /// </remarks>
    public ValueTask<bool> PrevAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_source.IsValid)
        {
            return new ValueTask<bool>(false);
        }

        if (_distinct)
        {
            Diagnostics.VortexEventSource.Seek();
            return FirstOfKeyAsync(_source.PrevKeyAsync(cancellationToken), cancellationToken);
        }

        Diagnostics.VortexEventSource.Step();
        return _source.PrevAsync(cancellationToken);
    }

    /// <summary>
    /// Steps to the first entry of the next distinct key: the loose index scan, one seek per group
    /// rather than one step per row.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>Whether there was a next key.</returns>
    public ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Diagnostics.VortexEventSource.Seek();
        return _source.IsValid ? _source.NextKeyAsync(cancellationToken) : new ValueTask<bool>(false);
    }

    /// <summary>Steps to the last entry of the previous distinct key.</summary>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>Whether there was a previous key.</returns>
    public ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Diagnostics.VortexEventSource.Seek();
        return _source.IsValid ? _source.PrevKeyAsync(cancellationToken) : new ValueTask<bool>(false);
    }

    /// <summary>How many entries have a key below <paramref name="key"/>.</summary>
    /// <param name="key">The key to rank, in the column's domain.</param>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>The rank, between zero and <see cref="EntryCount"/>.</returns>
    /// <remarks>The cursor's position does not move.</remarks>
    /// <exception cref="ArgumentException">The key is of the wrong domain, or is null.</exception>
    /// <exception cref="InvalidOperationException">The source has no rows (<see cref="HasRows"/>), so no entry count to rank in.</exception>
    public ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireRows(nameof(RankAsync));
        return _source.RankAsync(key, cancellationToken);
    }

    /// <summary>Positions on the entry of rank <paramref name="rank"/>, zero-based.</summary>
    /// <param name="rank">The rank.</param>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>Whether the rank names an entry.</returns>
    /// <exception cref="InvalidOperationException">The source has no rows (<see cref="HasRows"/>).</exception>
    public ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireRows(nameof(SeekRankAsync));
        Diagnostics.VortexEventSource.Seek();
        return _source.SeekRankAsync(rank, cancellationToken);
    }

    /// <summary>How many entries share the current key: <c>rank(after) - rank(at)</c>.</summary>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>The count, at least one.</returns>
    /// <remarks>The cursor's position does not move.</remarks>
    /// <exception cref="InvalidOperationException">The cursor is not positioned, or its source has no rows (<see cref="HasRows"/>).</exception>
    public ValueTask<long> KeyCountAsync(CancellationToken cancellationToken = default)
    {
        RequireValid();
        RequireRows(nameof(KeyCountAsync));
        return _source.KeyCountAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void RequireValid()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_source.IsValid)
        {
            throw new InvalidOperationException(
                "The cursor is not positioned on an entry; check the result of the positioning call.");
        }
    }

    private void RequireRows(string member)
    {
        if (!_source.HasRows)
        {
            throw new InvalidOperationException(
                $"{member} needs rows, and this cursor's source holds keys without rows. " +
                "A cursor opened without Distinct() is always served with rows.");
        }
    }

    private void RequireKey(FilterLiteral key)
    {
        if (key.Kind == FilterLiteralKind.Null)
        {
            throw new ArgumentException(
                "No entry has a null key: a null is in no key source.",
                nameof(key));
        }

        if (!KeyOrder.Fits(key, KeyKind))
        {
            throw new ArgumentException(
                $"This column's keys are {KeyKind}; a {key.Kind} key cannot be ordered against " +
                "them.",
                nameof(key));
        }
    }
}
