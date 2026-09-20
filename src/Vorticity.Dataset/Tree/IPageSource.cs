using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>Reads tree pages by reference.</summary>
/// <remarks>
/// An implementation must answer several reads in flight at once: a walk prefetches a window of
/// sibling pages in parallel.
/// </remarks>
public interface IPageSource
{
    /// <summary>Reads one page and checks it against its reference.</summary>
    /// <exception cref="CommitFormatException">The page is not there or does not hash to its reference.</exception>
    ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(PageReference reference, CancellationToken cancellationToken);
}

/// <summary>
/// Takes the pages a commit writes. Writing is synchronous where reading is not: a commit object is
/// assembled in memory, so writing a page is a copy into a buffer.
/// </summary>
public interface IPageSink
{
    /// <summary>
    /// Writes one page and returns the reference that names it, whose offset the commit object
    /// rebases when it is laid out.
    /// </summary>
    PageReference WritePage(ReadOnlySpan<byte> page);
}

/// <summary>
/// A page source over pages already in memory, and the sink that fills it. Also serves the pages a
/// commit header inlines, which arrived with the header and cost no request.
/// </summary>
public sealed class MemoryPageStore : IPageSource, IPageSink
{
    private readonly Dictionary<PageReference, ReadOnlyMemory<byte>> _pages = [];
    private readonly ulong _version;

    /// <summary>A store that attributes the pages written to it to the given version.</summary>
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
    public long Reads => Interlocked.Read(ref _reads);

    private long _reads;

    /// <summary>Forgets the read count, not the pages.</summary>
    public void ResetReads() => Interlocked.Exchange(ref _reads, 0);

    /// <summary>Takes a page already known, under the reference that names it.</summary>
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
