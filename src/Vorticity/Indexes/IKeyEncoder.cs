using System;

namespace Vorticity;

/// <summary>
/// Turns the rows of several columns into byte strings whose <c>memcmp</c> order is the tuple order.
/// A composite locating index keys each row by the encoding of its tuple; the core only names this
/// shape, so that it depends on no encoder, and a writer offered a composite key without one
/// abandons that index and says why. <c>Vorticity.RowEncoding</c> provides one.
/// </summary>
public interface IKeyEncoder
{
    /// <summary>
    /// What the bytes follow, e.g. <c>vortex-row 0.86.1 asc-nf,asc-nf</c>: the file's index directory
    /// records it, because a key is comparable only with a key of the same format.
    /// </summary>
    string Format { get; }

    /// <summary>Encodes every row of <paramref name="columns"/>.</summary>
    /// <param name="columns">The key columns, in key order, one column per key part.</param>
    /// <returns>One key per row; the caller disposes it.</returns>
    /// <exception cref="VortexUnsupportedException">A column's type has no order the encoder defines.</exception>
    IEncodedKeys Encode(BatchView columns);
}

/// <summary>The keys an <see cref="IKeyEncoder"/> produced.</summary>
public interface IEncodedKeys : IDisposable
{
    /// <summary>How many keys.</summary>
    int RowCount { get; }

    /// <summary>One row's key, valid until disposal.</summary>
    /// <param name="index">The row.</param>
    /// <returns>The key's bytes.</returns>
    ReadOnlySpan<byte> Row(int index);
}
