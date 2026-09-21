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
internal sealed class MemoryObjectStore : IObjectStore
{
    private const int ListPage = 1_000;

    private readonly object _gate = new object();
    private readonly SortedDictionary<string, Entry> _objects = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
    private long _tokens;
    private bool _disposed;

    private readonly record struct Entry(byte[] Bytes, string Token, DateTimeOffset Created);

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
    /// Asked after a <see cref="PutIfAbsentAsync"/> has stored its bytes; when it returns true the
    /// call throws anyway, so the object exists and its writer never learned it.
    /// </summary>
    public Func<string, bool>? CrashesAfterPut { get; set; }

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

    /// <inheritdoc/>
    public async ValueTask<ObjectRange> GetRangeAsync(
        string key, long offset, int length, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        await StartAsync(ObjectOperation.GetRange, key, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out Entry entry))
            {
                throw ObjectNotFoundException.For(key);
            }

            if (offset >= entry.Bytes.Length && !(offset == 0 && entry.Bytes.Length == 0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    offset,
                    $"'{key}' holds {entry.Bytes.Length} bytes, so the range starts past its end.");
            }

            // An object's array is never written after it is stored, so a lease on it needs no copy
            // and no owner: a delete drops the map's reference, and the lease keeps its own.
            int available = (int)Math.Min(length, entry.Bytes.Length - offset);
            return new ObjectRange(new SegmentLease(entry.Bytes.AsMemory((int)offset, available)), entry.Token);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        await StartAsync(ObjectOperation.Head, key, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return _objects.TryGetValue(key, out Entry entry)
                ? new ObjectHead(entry.Bytes.Length, entry.Token, entry.Created)
                : null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutIfAbsentAsync(
        string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            ObjectKey.Check(key);
            await StartAsync(ObjectOperation.PutIfAbsent, key, cancellationToken).ConfigureAwait(false);
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
            if (_objects.ContainsKey(key))
            {
                outcome = PutOutcome.Exists;
            }
            else
            {
                _objects[key] = new Entry(bytes, Token(), TimeProvider.GetUtcNow());
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
            foreach (string key in keys)
            {
                _objects.Remove(key);
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> ListAsync(
        string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string? after = startAfter;
        while (true)
        {
            // One page per round trip, taken under the lock and handed out after it, so a caller
            // that stops early pays for one page and a slow consumer holds no lock.
            await StartAsync(ObjectOperation.List, prefix, cancellationToken).ConfigureAwait(false);
            List<string> page = new List<string>(ListPage);
            lock (_gate)
            {
                foreach (string key in _objects.Keys)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)
                        || (after is not null && string.CompareOrdinal(key, after) <= 0))
                    {
                        continue;
                    }

                    page.Add(key);
                    if (page.Count == ListPage)
                    {
                        break;
                    }
                }
            }

            foreach (string key in page)
            {
                yield return key;
            }

            if (page.Count < ListPage)
            {
                yield break;
            }

            after = page[^1];
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
}

/// <summary>The five operations of <see cref="IObjectStore"/>, for fault injection and counting.</summary>
internal enum ObjectOperation
{
    /// <summary><see cref="IObjectStore.GetRangeAsync"/>.</summary>
    GetRange = 0,

    /// <summary><see cref="IObjectStore.HeadAsync"/>.</summary>
    Head = 1,

    /// <summary><see cref="IObjectStore.PutIfAbsentAsync"/>.</summary>
    PutIfAbsent = 2,

    /// <summary><see cref="IObjectStore.DeleteAsync"/>, once per batch.</summary>
    Delete = 3,

    /// <summary><see cref="IObjectStore.ListAsync"/>, once per page.</summary>
    List = 4,
}
