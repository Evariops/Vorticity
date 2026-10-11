using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Bench.Churn;

/// <summary>
/// A store that counts the bytes of the commit objects put into it, apart from the data objects: what
/// the dataset's metadata costs a commit, which the counting store's total mixes with the rows.
/// </summary>
internal sealed class CommitBytesStore(IObjectStore inner) : IObjectStore
{
    private long _bytes;

    /// <summary>The bytes of every commit object offered to the store so far.</summary>
    internal long Bytes => Interlocked.Read(ref _bytes);

    public ObjectStoreCapabilities Capabilities => inner.Capabilities;

    public ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken) =>
        inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);

    public ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken) =>
        inner.ReadTailAsync(key, destination, cancellationToken);

    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
        inner.HeadAsync(key, cancellationToken);

    public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        if (key.StartsWith(CommitKey.Prefix, StringComparison.Ordinal))
        {
            Interlocked.Add(ref _bytes, length);
        }

        return inner.PutAsync(key, content, length, condition, cancellationToken);
    }

    public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        inner.DeleteAsync(keys, cancellationToken);

    public IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, startAfter, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
