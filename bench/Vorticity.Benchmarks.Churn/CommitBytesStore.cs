using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;

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

    public ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken) =>
        inner.GetRangeAsync(key, offset, length, cancellationToken);

    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
        inner.HeadAsync(key, cancellationToken);

    public ValueTask<PutOutcome> PutIfAbsentAsync(string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        if (key.StartsWith(CommitKey.Prefix, System.StringComparison.Ordinal))
        {
            Interlocked.Add(ref _bytes, length);
        }

        return inner.PutIfAbsentAsync(key, content, length, cancellationToken);
    }

    public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        inner.DeleteAsync(keys, cancellationToken);

    public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, startAfter, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
