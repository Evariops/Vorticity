using System;
using Vorticity.Arrays;
using Vorticity.Indexes;

namespace Vorticity;

/// <summary>
/// Turns the rows of several columns into byte strings whose <c>memcmp</c> order is the tuple order.
/// A composite locating index keys each row by the encoding of its tuple; the core only names this
/// shape, so that it depends on no encoder, and a writer offered a composite key without one
/// abandons that index and says why.
/// </summary>
internal interface IKeyEncoder
{
    /// <summary>
    /// What the bytes follow, e.g. <c>vortex-row 0.86.1 asc-nf,asc-nf</c>: the directory records it
    /// and a cursor reports it (<c>KeyCursor.KeyFormat</c>), because a key is comparable only with a
    /// key of the same format.
    /// </summary>
    string Format { get; }

    /// <summary>Encodes every row of <paramref name="columns"/>.</summary>
    /// <param name="arena">The arena holding the columns.</param>
    /// <param name="columns">One canonical node per key column, in key order, all of one length.</param>
    /// <returns>One key per row; the caller disposes it.</returns>
    IEncodedKeys Encode(CanonicalArena arena, ReadOnlySpan<int> columns);
}

/// <summary>The keys an <see cref="IKeyEncoder"/> produced.</summary>
internal interface IEncodedKeys : IDisposable
{
    /// <summary>How many keys.</summary>
    int RowCount { get; }

    /// <summary>One row's key, valid until disposal.</summary>
    /// <param name="index">The row.</param>
    /// <returns>The key's bytes.</returns>
    ReadOnlySpan<byte> Row(int index);
}
