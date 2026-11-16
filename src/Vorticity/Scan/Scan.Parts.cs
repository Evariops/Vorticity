using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// A scan cut into parts a caller reads side by side (<see cref="Scan{TRecord}.SplitAsync"/>), and the fold of every batch
/// by an aggregator of the caller's, a state a part, on the session's lanes (<see cref="Scan{TRecord}.AggregateAsync{TAggregator, TState}"/>).
/// </summary>
/// <remarks>
/// A scan delivers its batches in order to one loop: whatever the loop does with a batch is done on one thread, while the
/// chunks decode on the lanes. A loop that costs a seventh of a scan bounds the scan at five times its speed on one lane,
/// whatever the lanes (2026-11-17). Cut into parts, the caller's work runs on as many threads as there are parts read.
/// </remarks>
public sealed partial class Scan<TRecord>
{
    /// <summary>
    /// The scan cut into at most <paramref name="count"/> scans of disjoint rows that together read every row it reads, in
    /// the rows' order: each starts where every column the scan reads starts a chunk, so that a chunk is read by one part
    /// alone. Each part is a scan of its own, its filter and its options this one's, enumerated by whoever takes it.
    /// </summary>
    /// <param name="count">The most parts, one at least; a source that cannot be cut gives one part, this scan.</param>
    /// <param name="cancellationToken">Cancels what cutting the rows reads.</param>
    /// <returns>The parts, in the rows' order.</returns>
    /// <exception cref="InvalidOperationException">The scan is in a key's order or takes rows by index: its rows are not cut.</exception>
    public async ValueTask<IReadOnlyList<Scan<TRecord>>> SplitAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (_orderPath is not null || _take is not null)
        {
            throw new InvalidOperationException("A scan in a key's order, or of rows taken by index, is read whole: it is not split.");
        }

        IReadOnlyList<RowRange>? ranges = count == 1 ? null : await _source.PartsAsync(Spec(), count, cancellationToken).ConfigureAwait(false);
        if (ranges is not { Count: > 1 })
        {
            return [this];
        }

        Scan<TRecord>[] parts = new Scan<TRecord>[ranges.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Part(ranges[i]);
        }

        return parts;
    }

    /// <summary>
    /// The state <typeparamref name="TAggregator"/> folds every batch of the scan into: a state for each part the scan's rows
    /// cut into at the starts of its chunks, each part read and folded on a lane of the scan's degree, the states merged in
    /// the parts' order. The parts are the chunks', not the lanes': the state is the same, bit for bit, at every degree.
    /// </summary>
    /// <typeparam name="TAggregator">The aggregator.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The state.</returns>
    public async ValueTask<TState> AggregateAsync<TAggregator, TState>(CancellationToken cancellationToken = default)
        where TAggregator : IBatchAggregator<TRecord, TState>
    {
        IReadOnlyList<Scan<TRecord>> parts = _orderPath is null && _take is null
            ? await SplitAsync(int.MaxValue, cancellationToken).ConfigureAwait(false)
            : [this];
        TState[] states = new TState[parts.Count];
        int degree = Math.Max(1, _options.DegreeOfParallelism > 0 ? _options.DegreeOfParallelism : _source.Session.Options.MaxDegreeOfParallelism);
        if (parts.Count == 1 || degree == 1)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                states[i] = await FoldAsync<TAggregator, TState>(parts[i], degree, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // A part a lane: each part reads its chunks on one, its batches folded on the thread that decoded them.
            ParallelOptions lanes = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(degree, parts.Count), CancellationToken = cancellationToken };
            await Parallel.ForAsync(0, parts.Count, lanes, async (i, token) =>
                states[i] = await FoldAsync<TAggregator, TState>(parts[i], 1, token).ConfigureAwait(false)).ConfigureAwait(false);
        }

        TState state = states[0];
        for (int i = 1; i < states.Length; i++)
        {
            TAggregator.Merge(ref state, in states[i]);
        }

        return state;
    }

    /// <summary>The state <typeparamref name="TAggregator"/> folds every batch of <paramref name="part"/> into, the part read at <paramref name="degree"/>.</summary>
    private static async ValueTask<TState> FoldAsync<TAggregator, TState>(Scan<TRecord> part, int degree, CancellationToken cancellationToken)
        where TAggregator : IBatchAggregator<TRecord, TState>
    {
        TState state = TAggregator.Seed();
        part._options = part._options with { DegreeOfParallelism = degree };
        await foreach (Columns<TRecord> batch in part.WithCancellation(cancellationToken))
        {
            TAggregator.Step(ref state, batch);
        }

        return state;
    }

    /// <summary>A scan of this one's source, filter and options over <paramref name="rows"/>: a part of it.</summary>
    private Scan<TRecord> Part(RowRange rows) =>
        new Scan<TRecord>(_source)
        {
            _binding = _binding,
            _filter = _filter,
            _rows = rows,
            _options = _options,
            _cancellation = _cancellation,
            _schema = _schema,
        };
}

/// <summary>
/// A caller's aggregate over whole batches of a scan's records, folded into a <typeparamref name="TState"/>: seeded once a
/// part of the scan, stepped once a batch on the thread that decoded it, merged in the parts' order at the end
/// (<see cref="Scan{TRecord}.AggregateAsync{TAggregator, TState}"/>). Every member is static, so a scan calls it
/// monomorphised, without a virtual call.
/// </summary>
/// <typeparam name="TRecord">The scan's record.</typeparam>
/// <typeparam name="TState">What the aggregate keeps between batches.</typeparam>
/// <remarks>
/// The batch is borrowed for the call: a span or a column taken from it is not kept past <see cref="Step"/>. What
/// <see cref="Step"/> reads of the columns is the canonical form, or the encoded one through <see cref="Column{T}.Encoding"/>,
/// as an <c>await foreach</c> over the scan reads them.
/// </remarks>
public interface IBatchAggregator<TRecord, TState>
    where TRecord : IVortexRecord<TRecord>
{
    /// <summary>The state of a part that has seen no row.</summary>
    /// <returns>The empty state.</returns>
    static abstract TState Seed();

    /// <summary>Folds one batch into <paramref name="state"/>.</summary>
    /// <param name="state">The part's state.</param>
    /// <param name="batch">The batch's columns, borrowed for the call.</param>
    static abstract void Step(ref TState state, Columns<TRecord> batch);

    /// <summary>Folds <paramref name="other"/>, the state of the part after, into <paramref name="into"/>.</summary>
    /// <param name="into">The state that survives.</param>
    /// <param name="other">The state merged into it.</param>
    static abstract void Merge(ref TState into, in TState other);
}
