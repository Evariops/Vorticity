using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;

namespace Vorticity.Keys
{
    /// <summary>What walks the entries of one key column in key order: a file's cursor, or a merge of a dataset's.</summary>
    internal interface IKeyWalker : IAsyncDisposable
    {
        bool IsValid { get; }

        bool HasRows { get; }

        FilterLiteral Key { get; }

        long Row { get; }

        ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default);

        ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken = default);

        ValueTask<bool> SeekAsync(FilterLiteral key, SeekOp op, CancellationToken cancellationToken = default);

        ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken = default);

        ValueTask<bool> NextAsync(CancellationToken cancellationToken = default);

        ValueTask<bool> PrevAsync(CancellationToken cancellationToken = default);

        ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken = default);

        ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken = default);

        ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default);

        ValueTask<long> KeyCountAsync(CancellationToken cancellationToken = default);
    }
}

namespace Vorticity
{
    /// <summary>Builds a cursor over the keys of one column, from the file's key source.</summary>
    /// <typeparam name="TKey">The key column's type.</typeparam>
    public sealed class KeyCursorBuilder<TKey>
    {
        private readonly ScanSource _source;
        private readonly ColumnSym _column;
        private readonly bool _indexes;
        private bool _distinct;

        internal KeyCursorBuilder(ScanSource source, ColumnSym column, bool indexes)
        {
            _source = source;
            _column = column;
            _indexes = indexes;
        }

        /// <summary>Walks each distinct key once, rows aside: a postings index serves it too.</summary>
        /// <returns>This builder.</returns>
        public KeyCursorBuilder<TKey> Distinct()
        {
            _distinct = true;
            return this;
        }

        /// <summary>Opens the cursor, unpositioned.</summary>
        /// <param name="cancellationToken">Cancels the reads of the key source.</param>
        /// <returns>The cursor; the caller disposes it.</returns>
        /// <exception cref="VortexUnsupportedException">The column is neither sorted nor indexed; the message names the index the writer would have to build.</exception>
        public async ValueTask<KeyCursor<TKey>> OpenAsync(CancellationToken cancellationToken = default)
        {
            IKeyWalker walker = await _source.OpenKeysAsync(_column.Field.Path, _distinct, _indexes, cancellationToken).ConfigureAwait(false);
            return new KeyCursor<TKey>(walker, _column);
        }

        /// <summary>Which key source the cursor would walk, and why the others were refused.</summary>
        /// <param name="cancellationToken">Cancels the reads of the key sources.</param>
        /// <returns>The plan.</returns>
        public ValueTask<KeyPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
            _source.ExplainKeysAsync(_column.Field.Path, _distinct, _indexes, cancellationToken);
    }

    /// <summary>
    /// A position in one column's entries, in the file's key order: seek, step, rank and count
    /// without reading rows.
    /// </summary>
    /// <typeparam name="TKey">The key column's type.</typeparam>
    /// <remarks>
    /// The order of keys is the file's order for the dtype; compare keys obtained from the cursor,
    /// which already arrive in it. Not thread-safe.
    /// </remarks>
    public sealed class KeyCursor<TKey> : IAsyncDisposable
    {
        private readonly IKeyWalker _walker;
        private readonly ColumnSym _column;

        internal KeyCursor(IKeyWalker walker, ColumnSym column)
        {
            _walker = walker;
            _column = column;
        }

        /// <summary>Whether the cursor is positioned on an entry.</summary>
        public bool IsValid => _walker.IsValid;

        /// <summary>The current entry's key.</summary>
        /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
        public TKey Key => LiteralValues.ToValue<TKey>(_walker.Key, _column.Type)!;

        /// <summary>The current entry's file row; then <c>Rows(row)</c> on a scan reads it.</summary>
        /// <exception cref="InvalidOperationException">The cursor is not positioned, or it walks distinct keys without rows.</exception>
        public long Row => _walker.Row;

        /// <summary>Whether the entries carry rows: false for a distinct walk a postings index serves.</summary>
        public bool HasRows => _walker.HasRows;

        /// <summary>Positions on the smallest key.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default) => _walker.SeekFirstAsync(cancellationToken);

        /// <summary>Positions on the largest key.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken = default) => _walker.SeekLastAsync(cancellationToken);

        /// <summary>Positions relative to <paramref name="key"/>.</summary>
        /// <param name="key">The key, in the column's type.</param>
        /// <param name="op">Exact, at or after, after, at or before, before.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether an entry satisfies it.</returns>
        public ValueTask<bool> SeekAsync(TKey key, SeekOp op, CancellationToken cancellationToken = default) =>
            _walker.SeekAsync(Literal(key), op, cancellationToken);

        /// <summary>Positions on the entry of rank <paramref name="rank"/>, counting from zero in key order.</summary>
        /// <param name="rank">The rank.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken = default) => _walker.SeekRankAsync(rank, cancellationToken);

        /// <summary>Moves to the next entry.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> NextAsync(CancellationToken cancellationToken = default) => _walker.NextAsync(cancellationToken);

        /// <summary>Moves to the previous entry.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> PrevAsync(CancellationToken cancellationToken = default) => _walker.PrevAsync(cancellationToken);

        /// <summary>Moves to the first entry of the next distinct key.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken = default) => _walker.NextKeyAsync(cancellationToken);

        /// <summary>Moves to the first entry of the previous distinct key.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken = default) => _walker.PrevKeyAsync(cancellationToken);

        /// <summary>The number of entries whose key is below <paramref name="key"/>.</summary>
        /// <param name="key">The key.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The rank.</returns>
        public ValueTask<long> RankAsync(TKey key, CancellationToken cancellationToken = default) => _walker.RankAsync(Literal(key), cancellationToken);

        /// <summary>The number of distinct keys.</summary>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The count.</returns>
        public ValueTask<long> KeyCountAsync(CancellationToken cancellationToken = default) => _walker.KeyCountAsync(cancellationToken);

        /// <summary>Releases the key source.</summary>
        /// <returns>A task that completes when it is released.</returns>
        public ValueTask DisposeAsync() => _walker.DisposeAsync();

        private FilterLiteral Literal(TKey key)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key), "A cursor seeks a key, never a null.");
            }

            return SymLowering.Literal(_column, ClrShape.For<TKey>.Value, key);
        }
    }
}
