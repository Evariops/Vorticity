using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Tests.Dataset;

/// <summary>A store that notes each read it hands on: the key, where it starts (-1 for a tail), its length and the object's.</summary>
/// <param name="inner">The store that does the work; this one never disposes it.</param>
internal sealed class LoggingStore(IObjectStore inner) : ForwardingStore(inner)
{
    /// <summary>The reads, in the order they were answered.</summary>
    internal List<(string Key, long Offset, long Length, long Size)> Reads { get; } = [];

    public override async ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        ObjectRead read = await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        lock (Reads)
        {
            Reads.Add((key, offset, destination.Length, read.ObjectLength));
        }

        return read;
    }

    public override async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectRead read = await Inner.ReadTailAsync(key, destination, cancellationToken);
        lock (Reads)
        {
            Reads.Add((key, -1, destination.Length, read.ObjectLength));
        }

        return read;
    }
}
