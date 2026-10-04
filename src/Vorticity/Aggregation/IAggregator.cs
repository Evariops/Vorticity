using System;

namespace Vorticity;

/// <summary>
/// A caller's aggregate over a column of <typeparamref name="T"/>, folded into a
/// <typeparamref name="TState"/>: seeded once per group and per chunk, stepped once per block, merged
/// at the end. Every member is static, so a scan calls it monomorphised, without a virtual call.
/// </summary>
/// <typeparam name="T">The column's storage type: the primitive a <c>Column&lt;T&gt;.Values</c> span holds.</typeparam>
/// <typeparam name="TState">What the aggregate keeps between blocks.</typeparam>
/// <remarks>
/// <see cref="Step"/> receives the canonical form, which forces the decode of the column it reads;
/// <see cref="IEncodedAggregator{T, TState}"/> is how an aggregator reads the encoded form instead.
/// </remarks>
public interface IAggregator<T, TState>
    where T : unmanaged
{
    /// <summary>The state of a group that has seen no row.</summary>
    /// <returns>The empty state.</returns>
    static abstract TState Seed();

    /// <summary>Folds the rows of one block into <paramref name="state"/>.</summary>
    /// <param name="state">The group's state.</param>
    /// <param name="values">The block's values, one per row; undefined where the row is null.</param>
    /// <param name="validity">The validity words, bit <c>i % 64</c> of word <c>i / 64</c> for row <c>i</c>; empty when every row holds a value.</param>
    /// <param name="rows">The rows of the block that belong to the group and passed the scan's filter.</param>
    static abstract void Step(ref TState state, ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity, Selection rows);

    /// <summary>Folds <paramref name="other"/>, the state of another chunk of the same group, into <paramref name="into"/>.</summary>
    /// <param name="into">The state that survives.</param>
    /// <param name="other">The state merged into it.</param>
    static abstract void Merge(ref TState into, in TState other);
}

/// <summary>
/// An aggregator that also reads the encoded forms of a block, so that a dictionary, a run-end or a
/// constant block is folded without being decoded.
/// </summary>
/// <typeparam name="T">The column's storage type.</typeparam>
/// <typeparam name="TState">What the aggregate keeps between blocks.</typeparam>
/// <remarks>
/// The scan folds the validity of the rows into <c>rows</c> before calling an encoded step: every
/// selected row holds a value. A block whose distinct values or run values hold a null is handed to
/// <see cref="IAggregator{T, TState}.Step"/> in canonical form instead.
/// </remarks>
public interface IEncodedAggregator<T, TState> : IAggregator<T, TState>
    where T : unmanaged
{
    /// <summary>Folds a dictionary block: one code per row into the distinct values.</summary>
    /// <param name="state">The group's state.</param>
    /// <param name="codes">One code per row of the block.</param>
    /// <param name="dictionary">The distinct values, in code order.</param>
    /// <param name="rows">The rows to fold; each holds a value.</param>
    static abstract void StepDictionary(ref TState state, ReadOnlySpan<uint> codes, ReadOnlySpan<T> dictionary, Selection rows);

    /// <summary>Folds a run-end block: runs of equal values.</summary>
    /// <param name="state">The group's state.</param>
    /// <param name="runEnds">
    /// The exclusive end of each run, relative to the block: the block's runs, or only those a range
    /// of its rows overlaps, the first of which then starts, as far as the aggregator can tell, at the
    /// block's first row. <paramref name="rows"/> says which rows of them to fold.
    /// </param>
    /// <param name="values">One value per run.</param>
    /// <param name="rows">The rows to fold; each holds a value.</param>
    static abstract void StepRunEnd(ref TState state, ReadOnlySpan<uint> runEnds, ReadOnlySpan<T> values, Selection rows);

    /// <summary>Folds <paramref name="count"/> rows that all hold <paramref name="value"/>.</summary>
    /// <param name="state">The group's state.</param>
    /// <param name="value">The value.</param>
    /// <param name="count">How many rows hold it; at least one.</param>
    static abstract void StepConstant(ref TState state, T value, int count);
}
