using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Tests.IO;

/// <summary>
/// The one operation an object-storage backend actually offers: "give me these bytes".
/// <see cref="HttpRangeSegmentSource"/> is written against this and nothing else, which is what
/// makes it a faithful stand-in for a real HTTP range reader.
/// </summary>
public interface IRangeTransport
{
    /// <summary>The object's total size in bytes.</summary>
    long Length { get; }

    /// <summary>Fills <paramref name="destination"/> with the bytes at <paramref name="offset"/>.</summary>
    /// <param name="offset">A non-negative offset inside the object.</param>
    /// <param name="destination">The buffer to fill completely.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>A task that completes when the whole range has arrived.</returns>
    ValueTask ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}
