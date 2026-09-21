using System;

namespace Vorticity.Dataset;

/// <summary>
/// One data object, as a leaf page carries it. Entries are ordered by <c>memcmp</c> over the key,
/// which is what the row encoding exists to make meaningful.
/// </summary>
/// <param name="Key">Its sort key: the row-encoded clustering key, or its first row position.</param>
/// <param name="Value">Everything else about the object, opaque to the tree.</param>
/// <param name="Rows">The object's rows, summed up the tree.</param>
internal readonly record struct TreeEntry(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value, long Rows)
{
    /// <summary>The bytes this entry takes in a leaf page, its length prefixes included.</summary>
    internal int Bytes =>
        TreePage.VarintBytes((ulong)Key.Length) + Key.Length
        + TreePage.VarintBytes((ulong)Rows)
        + TreePage.VarintBytes((ulong)Value.Length) + Value.Length;
}

/// <summary>
/// One entry and the position of its first row among all the level's rows, which is what answers a
/// row-range query without opening any object.
/// </summary>
internal readonly record struct PositionedEntry(TreeEntry Entry, long FirstRow);

/// <summary>One child page, as an internal page carries it.</summary>
/// <param name="MinKey">The smallest key of the subtree.</param>
/// <param name="MaxKey">Its largest.</param>
/// <param name="Rows">The rows of every object under it.</param>
/// <param name="Child">Where the child page lies and what it hashes to.</param>
/// <param name="Summary">
/// The union of the subtree's summaries, folded by an <see cref="ISummaryFold"/>. Empty when the
/// tree summarises nothing, which makes every predicate over it answer "may match".
/// </param>
internal readonly record struct InternalEntry(
    ReadOnlyMemory<byte> MinKey,
    ReadOnlyMemory<byte> MaxKey,
    long Rows,
    PageReference Child,
    ReadOnlyMemory<byte> Summary = default)
{
    /// <summary>The bytes this entry takes in an internal page.</summary>
    internal int Bytes =>
        TreePage.VarintBytes((ulong)MinKey.Length) + MinKey.Length
        + TreePage.VarintBytes((ulong)MaxKey.Length) + MaxKey.Length
        + TreePage.VarintBytes((ulong)Rows)
        + PageReference.Bytes
        + TreePage.VarintBytes((ulong)Summary.Length) + Summary.Length;
}
