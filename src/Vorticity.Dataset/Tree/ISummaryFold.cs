using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// Turns what a page holds into the summary its parent carries. A parameter rather than something
/// the tree knows, so that the chunker and the page format stay free of columns, dtypes and bounds.
/// </summary>
public interface ISummaryFold
{
    /// <summary>The summary of one leaf entry, read back out of its value, empty when it carries none.</summary>
    ReadOnlyMemory<byte> OfLeaf(TreeEntry entry);

    /// <summary>
    /// The summary of a page, from the summaries of the entries it holds in page order; empty when
    /// nothing is known.
    /// </summary>
    ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts);
}

/// <summary>A fold that summarises nothing, for a tree whose entries carry no bounds.</summary>
public sealed class NoSummary : ISummaryFold
{
    /// <summary>The one instance.</summary>
    public static NoSummary Instance { get; } = new NoSummary();

    private NoSummary()
    {
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => default;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts) => default;
}
