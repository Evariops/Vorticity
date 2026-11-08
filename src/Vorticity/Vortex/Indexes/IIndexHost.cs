using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>
/// What a file's locating indexes are read through: its index directory, its rows and schema, where each
/// origin's regions lie, and the context their payloads decode in. An open file is one; a file whose
/// table of contents names its directory is another, read with no open (<see cref="FooterlessIndexes"/>).
/// </summary>
internal interface IIndexHost
{
    /// <summary>Whether the file names an index directory.</summary>
    bool HasIndexDirectory { get; }

    /// <summary>The file's rows.</summary>
    long RowCount { get; }

    /// <summary>The file's schema.</summary>
    DType DType { get; }

    /// <summary>The file's index directory, or null when it has none or it does not decode.</summary>
    ValueTask<IndexDirectory?> ReadIndexDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>What the regions of the runs of <paramref name="origin"/> are read from.</summary>
    ISegmentReader IndexSourceOf(int origin);

    /// <summary>A context to decode the payloads of the runs of <paramref name="origin"/> in, which the caller disposes.</summary>
    ScanContext CreateIndexContext(int origin);
}

/// <summary>
/// A context to decode the payloads of an origin's runs in, lent until the lease is disposed: one of the process's pool of
/// metadata contexts over an open file's own encodings (<see cref="ScanContexts.Metadata"/>), given back to it; any other
/// made for the lease and disposed with it.
/// </summary>
internal readonly struct IndexContext(ScanContext context, bool pooled) : System.IDisposable
{
    /// <summary>The context.</summary>
    internal ScanContext Context { get; } = context;

    /// <summary>A context to decode the payloads of the runs of <paramref name="origin"/> of <paramref name="file"/> in.</summary>
    internal static IndexContext Of(IIndexHost file, int origin) =>
        file is VortexFile open && open.DecodesOverOwnEncodings(origin)
            ? new IndexContext(ScanContexts.Metadata.Rent(open), pooled: true)
            : new IndexContext(file.CreateIndexContext(origin), pooled: false);

    /// <summary>Gives the context back to the pool, or disposes it.</summary>
    public void Dispose()
    {
        if (pooled)
        {
            ScanContexts.Metadata.Return(Context);
        }
        else
        {
            Context.Dispose();
        }
    }
}

/// <summary>
/// The indexes of a file read with no open: its directory, already read from the region its table of
/// contents names, and its own regions, decoded over the encodings the contents list. Index fragments
/// another commit attached are not the file's and are not read here.
/// </summary>
internal sealed class FooterlessIndexes(ISegmentReader source, DType schema, IndexDirectory directory, string[] encodings) : IIndexHost
{
    public bool HasIndexDirectory => true;

    public long RowCount => (long)directory.RowCount;

    public DType DType => schema;

    public ValueTask<IndexDirectory?> ReadIndexDirectoryAsync(CancellationToken cancellationToken) => new ValueTask<IndexDirectory?>(directory);

    public ISegmentReader IndexSourceOf(int origin) => origin == 0
        ? source
        : throw new VortexFormatException($"A run of origin {origin} lies in an index fragment, which a read with no open does not have.");

    public ScanContext CreateIndexContext(int origin) => new ScanContext(encodings);
}
