using System.Threading;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// What the scans of a file did to its pages, counted for the gates of the performance contract: the
/// data pages and dictionary pages decoded, the pages decompressed, and the batches copied out of the
/// pages they span rather than sliced from one in place.
/// </summary>
internal sealed class PageCounters
{
    private long _pages;
    private long _dictionaries;
    private long _decompressions;
    private long _gathers;

    /// <summary>Data pages decoded: their levels and values made into the page's slots.</summary>
    internal long Pages => Interlocked.Read(ref _pages);

    /// <summary>Dictionary pages decoded, to prune or to read.</summary>
    internal long Dictionaries => Interlocked.Read(ref _dictionaries);

    /// <summary>Pages decompressed, dictionary pages among them.</summary>
    internal long Decompressions => Interlocked.Read(ref _decompressions);

    /// <summary>Batches whose values were copied out of the pages they span: any other batch is one page's slots, in place.</summary>
    internal long Gathers => Interlocked.Read(ref _gathers);

    internal void AddPage() => Interlocked.Increment(ref _pages);

    internal void AddDictionary() => Interlocked.Increment(ref _dictionaries);

    internal void AddDecompression() => Interlocked.Increment(ref _decompressions);

    internal void AddGather() => Interlocked.Increment(ref _gathers);

    /// <summary>Starts every count again from zero.</summary>
    internal void Reset()
    {
        Interlocked.Exchange(ref _pages, 0);
        Interlocked.Exchange(ref _dictionaries, 0);
        Interlocked.Exchange(ref _decompressions, 0);
        Interlocked.Exchange(ref _gathers, 0);
    }
}
