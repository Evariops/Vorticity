// What a page of the dataset tree holds - docs/13-dataset.md §4.2.
//
// TWO KINDS, ONE ORDER. A leaf entry describes one data object; an internal entry names a child
// page and carries "the union of its children's key ranges and summaries and the sum of their
// rows". Both are ordered by the same key -- the row-encoded clustering key, or the first row
// position when the dataset declares no clustering key -- and both are compared by `memcmp`, which
// is what the row encoding of 06 exists to make true.
//
// THE VALUE IS OPAQUE HERE, and that is deliberate at this step. §4.2's leaf entry carries the
// object's key, size, row count, identity, summaries and index descriptor; the TREE needs none of
// them. It needs the sort key, the bytes to carry, and the rows to sum. Keeping the rest opaque
// means the entry's schema can grow (step 39's dataset, step 42's fragments) without touching the
// chunker, the page format or the oracles -- and it keeps this file honest about what the tree
// actually knows.
using System;

namespace Vorticity.Dataset;

/// <summary>One data object, as a leaf page carries it.</summary>
/// <param name="Key">Its sort key: the row-encoded clustering key, or its first row position.</param>
/// <param name="Value">Everything else §4.2 lists, serialized by the layer above.</param>
/// <param name="Rows">The object's rows, summed up the tree.</param>
public readonly record struct TreeEntry(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value, long Rows)
{
    /// <summary>The bytes this entry takes in a leaf page, its length prefixes included.</summary>
    internal int Bytes =>
        TreePage.VarintBytes((ulong)Key.Length) + Key.Length
        + TreePage.VarintBytes((ulong)Rows)
        + TreePage.VarintBytes((ulong)Value.Length) + Value.Length;
}

/// <summary>One child page, as an internal page carries it.</summary>
/// <param name="MinKey">The smallest key of the subtree.</param>
/// <param name="MaxKey">Its largest.</param>
/// <param name="Rows">The rows of every object under it.</param>
/// <param name="Child">Where the child page lies and what it hashes to.</param>
public readonly record struct InternalEntry(
    ReadOnlyMemory<byte> MinKey, ReadOnlyMemory<byte> MaxKey, long Rows, PageReference Child)
{
    /// <summary>The bytes this entry takes in an internal page.</summary>
    internal int Bytes =>
        TreePage.VarintBytes((ulong)MinKey.Length) + MinKey.Length
        + TreePage.VarintBytes((ulong)MaxKey.Length) + MaxKey.Length
        + TreePage.VarintBytes((ulong)Rows)
        + PageReference.Bytes;
}
