// Where a tree's pages come from and go to.
//
// TWO SEAMS, ASYMMETRIC ON PURPOSE. Reading is async because a page lives in an object store and a
// read is a request (13 §9.1 counts them). Writing is not: a commit object is assembled in memory
// and created with ONE `PutIfAbsent` (§3), so a page "written" during a commit is a copy into a
// buffer and returning a reference to it. The asymmetry is the format's, not an oversight.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>Reads tree pages by reference.</summary>
/// <remarks>
/// <b>Thread safety.</b> An implementation must answer several reads in flight at once: a walk
/// prefetches a window of sibling pages in parallel (13 §6.6, "children prefetched in parallel").
/// </remarks>
public interface IPageSource
{
    /// <summary>Reads one page and checks it against its reference.</summary>
    /// <param name="reference">Where the page lies and what it must hash to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page's bytes.</returns>
    /// <exception cref="CommitFormatException">The page is not there or does not hash to its reference.</exception>
    ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(PageReference reference, CancellationToken cancellationToken);
}

/// <summary>Takes the pages a commit writes.</summary>
public interface IPageSink
{
    /// <summary>Writes one page and returns the reference that names it.</summary>
    /// <param name="page">The page's bytes.</param>
    /// <returns>Its reference, whose offset the commit object rebases when it is laid out.</returns>
    PageReference WritePage(ReadOnlySpan<byte> page);
}

/// <summary>A page source over pages already in memory, and the sink that fills it.</summary>
/// <remarks>
/// What a test uses, and what a reader uses for the pages §3 inlines in the commit header: those
/// arrived with the header and cost no request at all.
/// </remarks>
public sealed class MemoryPageStore : IPageSource, IPageSink
{
    private readonly Dictionary<PageReference, ReadOnlyMemory<byte>> _pages = [];
    private readonly ulong _version;

    /// <summary>A store whose pages belong to <paramref name="version"/>.</summary>
    /// <param name="version">The version the written pages are attributed to.</param>
    public MemoryPageStore(ulong version = 1) => _version = version;

    /// <summary>The pages it holds.</summary>
    public int Count => _pages.Count;

    /// <summary>The bytes it holds.</summary>
    public long Bytes
    {
        get
        {
            long total = 0;
            foreach (ReadOnlyMemory<byte> page in _pages.Values)
            {
                total += page.Length;
            }

            return total;
        }
    }

    /// <summary>How many pages were read through <see cref="ReadPageAsync"/>.</summary>
    /// <remarks>Counted atomically: a walk reads a window of siblings at once.</remarks>
    public long Reads => Interlocked.Read(ref _reads);

    private long _reads;

    /// <summary>Forgets the read count, not the pages.</summary>
    public void ResetReads() => Interlocked.Exchange(ref _reads, 0);

    /// <summary>Takes a page already known, under the reference that names it.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="page">Its bytes.</param>
    public void Add(PageReference reference, ReadOnlyMemory<byte> page) => _pages[reference] = page;

    /// <inheritdoc/>
    public PageReference WritePage(ReadOnlySpan<byte> page)
    {
        byte[] bytes = page.ToArray();
        PageReference reference = new PageReference(
            _version, _pages.Count == 0 ? 0 : Bytes, bytes.Length, System.IO.Hashing.XxHash128.HashToUInt128(bytes));
        _pages[reference] = bytes;
        return reference;
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(
        PageReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_pages.TryGetValue(reference, out ReadOnlyMemory<byte> page))
        {
            throw new CommitFormatException($"No page at {reference}.");
        }

        Interlocked.Increment(ref _reads);
        return new ValueTask<ReadOnlyMemory<byte>>(page);
    }
}
