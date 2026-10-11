using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IObjectStore"/> in memory, with injectable latency and faults. Thread-safe: one
/// lock over a sorted map, never held across an await.
/// </summary>
public sealed class MemoryObjectStore : IObjectStore
{
    private const int ListPage = 1_000;

    private readonly object _gate = new object();
    private readonly SortedDictionary<string, Entry> _objects = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
    private long _tokens;
    private bool _disposed;

    private readonly HashSet<string> _holds = new HashSet<string>(StringComparer.Ordinal);

    private readonly record struct Entry(byte[] Bytes, string Token, DateTimeOffset Created, DateTimeOffset? RetainUntil);

    /// <summary>
    /// The store's clock, which stamps every object it creates
    /// (<see cref="ObjectHead.LastModified"/>). The system's by default.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// The delay every operation waits before doing anything, zero by default. It is per operation,
    /// and per page of a listing, so operations issued together overlap and the wall clock measures
    /// the depth of the dependent chain rather than the request count.
    /// </summary>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Asked before every operation, with the key or the prefix it acts on; when it returns true the
    /// operation throws <see cref="ObjectStoreException"/> and changes nothing.
    /// </summary>
    public Func<ObjectOperation, string, bool>? Fails { get; set; }

    /// <summary>
    /// Asked after a <see cref="PutAsync"/> has stored its bytes; when it returns true the
    /// call throws anyway, so the object exists and its writer never learned it.
    /// </summary>
    public Func<string, bool>? CrashesAfterPut { get; set; }

    /// <summary>
    /// How long the store keeps each object it creates under a retention lock, as a bucket under S3
    /// Object Lock in compliance mode does: a head reports the date, and a delete of the object before
    /// it is refused; null, the default, locks nothing.
    /// </summary>
    public TimeSpan? RetainFor { get; set; }

    /// <summary>
    /// Puts the object at <paramref name="key"/> under a legal hold, or lifts it: a head reports it, and
    /// a delete of the object is refused while it holds.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="held">Whether the hold is placed or lifted.</param>
    public void Hold(string key, bool held)
    {
        ObjectKey.Check(key);
        lock (_gate)
        {
            if (held)
            {
                _holds.Add(key);
            }
            else
            {
                _holds.Remove(key);
            }
        }
    }

    /// <summary>The objects currently stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _objects.Count;
            }
        }
    }

    /// <summary>The bytes currently stored, over every object.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (Entry entry in _objects.Values)
                {
                    total += entry.Bytes.Length;
                }

                return total;
            }
        }
    }

    /// <summary>Reads suffixes, lists in order, keeps locks, which each entry of its listing reports, and replaces on a token's match.</summary>
    public ObjectStoreCapabilities Capabilities =>
        ObjectStoreCapabilities.ReadsSuffix | ObjectStoreCapabilities.ListsInOrder
        | ObjectStoreCapabilities.ReplacesOnTokenMatch | ObjectStoreCapabilities.KeepsLocks;

    /// <inheritdoc/>
    public async ValueTask<ObjectRead> ReadAsync(
        string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await StartAsync(ObjectOperation.Read, key, cancellationToken).ConfigureAwait(false);

        // An object's array is never written after it is stored, so it is copied out after the lock.
        Entry entry = Find(key, ifToken);
        if (offset >= entry.Bytes.Length && !(offset == 0 && entry.Bytes.Length == 0))
        {
            throw ObjectStoreExtensions.PastEnd(key, offset, entry.Bytes.Length);
        }

        int available = (int)Math.Min(destination.Length, entry.Bytes.Length - offset);
        entry.Bytes.AsSpan((int)offset, available).CopyTo(destination.Span);
        return new ObjectRead(available, entry.Bytes.Length, entry.Token);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        await StartAsync(ObjectOperation.Read, key, cancellationToken).ConfigureAwait(false);
        Entry entry = Find(key, null);
        int available = Math.Min(destination.Length, entry.Bytes.Length);
        entry.Bytes.AsSpan(entry.Bytes.Length - available).CopyTo(destination.Span);
        return new ObjectRead(available, entry.Bytes.Length, entry.Token);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        await StartAsync(ObjectOperation.Head, key, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return _objects.TryGetValue(key, out Entry entry) ? HeadOf(key, entry) : null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutAsync(
        string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            ObjectKey.Check(key);
            await StartAsync(ObjectOperation.Put, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refused)
        {
            await content.CompleteAsync(refused).ConfigureAwait(false);
            throw;
        }

        // Read whole before the lock: the object appears all at once or not at all.
        byte[] bytes = await ObjectStoreExtensions.ReadAllAsync(content, length, key, cancellationToken).ConfigureAwait(false);
        PutOutcome outcome;
        lock (_gate)
        {
            bool found = _objects.TryGetValue(key, out Entry current);
            DateTimeOffset now = TimeProvider.GetUtcNow();
            if (condition.Kind == PutConditionKind.IfAbsent && found)
            {
                outcome = PutOutcome.Exists;
            }
            else if (condition.Kind == PutConditionKind.IfMatch && (!found || !string.Equals(current.Token, condition.Token, StringComparison.Ordinal)))
            {
                outcome = PutOutcome.Changed;
            }
            else if (found && (_holds.Contains(key) || current.RetainUntil > now))
            {
                throw new ObjectStoreException($"'{key}' is locked, and the store refuses to overwrite it.");
            }
            else
            {
                _objects[key] = new Entry(bytes, Token(), now, RetainFor is { } term ? now + term : null);
                outcome = PutOutcome.Created;
            }
        }

        if (outcome == PutOutcome.Created && CrashesAfterPut is { } crashes && crashes(key))
        {
            throw new ObjectStoreException(
                $"The store crashed after creating '{key}': the object is there and this writer was never told.");
        }

        return outcome;
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (string key in keys)
        {
            ObjectKey.Check(key);
        }

        await DelayAsync(cancellationToken).ConfigureAwait(false);
        foreach (string key in keys)
        {
            Check(ObjectOperation.Delete, key);
        }

        lock (_gate)
        {
            // All or nothing, as a locked store refuses: one locked object keeps every key of the batch.
            DateTimeOffset now = TimeProvider.GetUtcNow();
            foreach (string key in keys)
            {
                if (_objects.TryGetValue(key, out Entry entry) && (_holds.Contains(key) || entry.RetainUntil > now))
                {
                    throw new ObjectStoreException(_holds.Contains(key)
                        ? $"'{key}' is under a legal hold, and the store refuses to delete it."
                        : $"'{key}' is locked until {entry.RetainUntil:O}, and the store refuses to delete it.");
                }
            }

            foreach (string key in keys)
            {
                _objects.Remove(key);
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectListing> ListAsync(
        string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string? after = startAfter;
        while (true)
        {
            // One page per round trip, taken under the lock and handed out after it, so a caller
            // that stops early pays for one page and a slow consumer holds no lock.
            await StartAsync(ObjectOperation.List, prefix, cancellationToken).ConfigureAwait(false);
            List<ObjectListing> page = new List<ObjectListing>(ListPage);
            lock (_gate)
            {
                foreach ((string key, Entry entry) in _objects)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)
                        || (after is not null && string.CompareOrdinal(key, after) <= 0))
                    {
                        continue;
                    }

                    page.Add(new ObjectListing(key, HeadOf(key, entry), LockReported: true));
                    if (page.Count == ListPage)
                    {
                        break;
                    }
                }
            }

            foreach (ObjectListing listed in page)
            {
                yield return listed;
            }

            if (page.Count < ListPage)
            {
                yield break;
            }

            after = page[^1].Key;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _objects.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The latency and the failure injection, in that order, before any operation's work.</summary>
    private async ValueTask StartAsync(ObjectOperation operation, string key, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        Check(operation, key);
    }

    private async ValueTask DelayAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Check(ObjectOperation operation, string key)
    {
        if (Fails is { } fails && fails(operation, key))
        {
            throw new ObjectStoreException($"The store was told to fail {operation} on '{key}'.");
        }
    }

    /// <summary>A token no other object of this store has had.</summary>
    private string Token() =>
        Interlocked.Increment(ref _tokens).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The object at <paramref name="key"/>, which must still have <paramref name="ifToken"/> when one is given.</summary>
    private Entry Find(string key, string? ifToken)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out Entry entry))
            {
                throw ObjectNotFoundException.For(key);
            }

            if (ifToken is not null && !string.Equals(ifToken, entry.Token, StringComparison.Ordinal))
            {
                throw ObjectStoreExtensions.Changed(key, ifToken, entry.Token);
            }

            return entry;
        }
    }

    /// <summary>The head of an object, its lock included; under the gate.</summary>
    private ObjectHead HeadOf(string key, Entry entry) =>
        new ObjectHead(entry.Bytes.Length, entry.Token, entry.Created) { RetainUntil = entry.RetainUntil, LegalHold = _holds.Contains(key) };
}

/// <summary>The five operations of <see cref="IObjectStore"/>, for fault injection and counting.</summary>
public enum ObjectOperation
{
    /// <summary><see cref="IObjectStore.ReadAsync"/> and <see cref="IObjectStore.ReadTailAsync"/>.</summary>
    Read = 0,

    /// <summary><see cref="IObjectStore.HeadAsync"/>.</summary>
    Head = 1,

    /// <summary>A put, from a stream or from bytes in memory, whatever its condition.</summary>
    Put = 2,

    /// <summary><see cref="IObjectStore.DeleteAsync"/>, once per batch.</summary>
    Delete = 3,

    /// <summary><see cref="IObjectStore.ListAsync"/>, once per page.</summary>
    List = 4,
}
