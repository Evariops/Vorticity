// How a page's summary is computed from what the page holds - docs/13-dataset.md §4.2: an internal
// entry "carries the union of its children's key ranges and summaries and the sum of their rows",
// so that "a predicate that the node's summaries refute skips the whole subtree".
//
// WHY THE TREE TAKES THIS AS A SEAM instead of knowing what a summary is. The tree's own business is
// the KEY (§4.1: "the boundary rule looks at the key alone") and the row count; everything else in an
// entry is bytes it carries. A summary is the one exception that has to travel UP, because a parent's
// summary is a function of its children's. Making that function a parameter keeps the chunker, the
// page format and the two oracles free of columns, dtypes and bounds -- and it is what lets the
// B+tree rule of §13.J run on the same bench without pretending to summarise anything.
//
// WHAT IT COSTS, stated rather than hidden: a leaf's summary is recovered from the entry's VALUE, so
// a commit parses every entry of every leaf page it re-emits. Pages it reuses by reference it does
// not touch, which is the common case after the first commit, and a re-emitted page is a few hundred
// entries. The alternative -- a second copy of the summary beside the value in every leaf -- would
// make the page bigger for every reader to save a parse for one writer.
using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>Turns what a page holds into the summary its parent carries.</summary>
public interface ISummaryFold
{
    /// <summary>The summary of one leaf entry, read back out of its value.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>Its summary's bytes, empty when it carries none.</returns>
    ReadOnlyMemory<byte> OfLeaf(TreeEntry entry);

    /// <summary>The summary of a page, from the summaries of the entries it holds.</summary>
    /// <param name="parts">The entries' summaries, in page order.</param>
    /// <returns>The union's bytes, empty when nothing is known.</returns>
    ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts);
}

/// <summary>A fold that summarises nothing, for a tree whose entries carry no bounds.</summary>
public sealed class NoSummary : ISummaryFold
{
    /// <summary>The one instance; it holds no state.</summary>
    public static NoSummary Instance { get; } = new NoSummary();

    private NoSummary()
    {
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => default;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts) => default;
}
