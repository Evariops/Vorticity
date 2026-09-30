using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// Turns what a page holds into the summary its parent carries. A parameter rather than something
/// the tree knows, so that the chunker and the page format stay free of columns, dtypes and bounds.
/// </summary>
internal interface ISummaryFold
{
    /// <summary>What one leaf entry gives its page's summary, read out of its value; empty when it gives nothing.</summary>
    ReadOnlyMemory<byte> OfLeaf(TreeEntry entry);

    /// <summary>
    /// The summary of a page, from what the entries it holds give it, in page order: with
    /// <paramref name="leaves"/>, what <see cref="OfLeaf"/> gave for each, and otherwise each child's
    /// own summary. Empty when nothing is known.
    /// </summary>
    ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts, bool leaves);
}

/// <summary>A fold that summarises nothing, for a tree whose entries carry no bounds.</summary>
internal sealed class NoSummary : ISummaryFold
{
    /// <summary>The one instance.</summary>
    public static NoSummary Instance { get; } = new NoSummary();

    private NoSummary()
    {
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => default;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts, bool leaves) => default;
}
