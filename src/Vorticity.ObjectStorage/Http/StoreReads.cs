using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>The reads every store answers the same way, whatever its service.</summary>
internal static class StoreReads
{
    /// <summary>
    /// A read into no memory: a head, which says the length and the token a read would, and refuses
    /// what a read would, an absent key, another token, or an offset past the end.
    /// </summary>
    internal static async ValueTask<ObjectRead> EmptyAsync(IObjectStore store, string key, long offset, string? ifToken, CancellationToken cancellationToken)
    {
        ObjectHead head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
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
