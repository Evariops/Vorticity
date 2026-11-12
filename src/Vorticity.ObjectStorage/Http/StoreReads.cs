using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>The reads every store answers the same way, whatever its service.</summary>
internal static class StoreReads
{
    /// <summary>
    /// What a read asked if present answers for a key the service says is absent, a 404, inside the transport: no
    /// token, which every object's read has. <see cref="Present"/> makes it null.
    /// </summary>
    internal static ObjectRead Absent => default;

    /// <summary>A read asked if present: null for <see cref="Absent"/>, the read otherwise.</summary>
    internal static ObjectRead? Present(ObjectRead read) => read.Token is null ? null : read;

    /// <summary>
    /// A read into no memory: a head, which says the length and the token a read would, and refuses
    /// what a read would, an absent key, another token, or an offset past the end.
    /// </summary>
    internal static async ValueTask<ObjectRead> EmptyAsync(IObjectStore store, string key, long offset, string? ifToken, CancellationToken cancellationToken) =>
        await EmptyIfPresentAsync(store, key, offset, ifToken, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);

    /// <summary><see cref="EmptyAsync"/>, an absent key answered null, as the head says it.</summary>
    internal static async ValueTask<ObjectRead?> EmptyIfPresentAsync(IObjectStore store, string key, long offset, string? ifToken, CancellationToken cancellationToken)
    {
        if (await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is not { } head)
        {
            return null;
        }

        if (ifToken is not null && !string.Equals(ifToken, head.Token, StringComparison.Ordinal))
        {
            throw StoreFailures.Changed(key, ifToken, head.Token);
        }

        if (offset >= head.Length && !(offset == 0 && head.Length == 0))
        {
            throw StoreFailures.PastEnd(key, offset, head.Length);
        }

        return new ObjectRead(0, head.Length, head.Token);
    }
}
