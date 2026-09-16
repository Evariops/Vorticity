// The slot a composite key's encoder fills - docs/12-index-reads.md §13, first question, and
// docs/10-indexes.md §6.5.
//
// THE CORE WRITES WHAT IT IS HANDED AND DEPENDS ON NOTHING. A composite locating index keys each row
// by the row encoding of its tuple, and the row encoding is a separate 0.x package the core must not
// reference (docs/09-contracts.md §3). So the core names the shape of an encoder and the package
// provides one (`Vorticity.RowEncoding.RowKeyEncoder`); a writer asked for a composite key without
// an encoder abandons that index and says why.
using System;
using Vorticity.Arrays;

namespace Vorticity.Indexes;

/// <summary>Turns the rows of several columns into byte strings whose <c>memcmp</c> order is the tuple order.</summary>
public interface IKeyEncoder
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
public interface IEncodedKeys : IDisposable
{
    /// <summary>How many keys.</summary>
    int RowCount { get; }

    /// <summary>One row's key, valid until disposal.</summary>
    /// <param name="index">The row.</param>
    /// <returns>The key's bytes.</returns>
    ReadOnlySpan<byte> Row(int index);
}
