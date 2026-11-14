using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Dataset;

/// <summary>
/// An object's reader through a lease of it open: what a read with no open reads through when the object's own file
/// holds its table of contents (<see cref="Indexes.ObjectContents.InFile"/>). Such an object is small, its open brought
/// it whole, and every region its table names lies in those bytes, so the read asks the store for nothing while the
/// object stays open, and for the object once when it does not. Disposing the reader gives the lease back.
/// </summary>
internal sealed class LeasedReader(ObjectLease lease) : ISegmentReader
{
    private ISegmentReader File => lease.File;

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => File.GetLengthAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) => File.ReadAsync(spec, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken) => File.ReadManyAsync(requests, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken) =>
        File.ReadRangeAsync(offset, length, alignment, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => lease.DisposeAsync();
}
