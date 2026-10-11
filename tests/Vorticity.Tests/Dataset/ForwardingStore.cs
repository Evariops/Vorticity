using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Tests.Dataset;

/// <summary>A store that hands every call to another, so that a test overrides only the call it intercepts.</summary>
/// <param name="inner">The store that does the work; this one never disposes it.</param>
internal abstract class ForwardingStore(IObjectStore inner) : IObjectStore
{
    /// <summary>The store that does the work.</summary>
    protected IObjectStore Inner => inner;

    public virtual ObjectStoreCapabilities Capabilities => inner.Capabilities;

    public virtual ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken) =>
        inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);

    public virtual ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken) =>
        inner.ReadTailAsync(key, destination, cancellationToken);

    public virtual ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
        inner.HeadAsync(key, cancellationToken);

    public virtual ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken) =>
        inner.PutAsync(key, content, length, condition, cancellationToken);

    public virtual ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        inner.DeleteAsync(keys, cancellationToken);

    public virtual IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, startAfter, cancellationToken);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
