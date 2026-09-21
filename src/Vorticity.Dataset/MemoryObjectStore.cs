using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IObjectStore"/> in memory, with injectable latency and faults. Thread-safe: one
/// lock over a sorted map, never held across an await.
/// </summary>
internal sealed class MemoryObjectStore : IObjectStore
{
    private readonly object _gate = new object();
    private readonly SortedDictionary<string, Entry> _objects = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
    private long _tokens;
    private bool _disposed;

    private readonly record struct Entry(byte[] Bytes, string Token, DateTimeOffset Created);

    /// <summary>
    /// The store's clock, which stamps every object it creates
    /// (<see cref="ObjectHead.LastModified"/>). The system's by default.
    /// </summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// The delay every operation waits before doing anything, zero by default. It is per operation,
    /// so operations issued together overlap and the wall clock measures the depth of the dependent
    /// chain rather than the request count.
    /// </summary>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Asked before every operation; when it returns true the operation throws
    /// <see cref="ObjectStoreException"/> and changes nothing.
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

            int available = (int)Math.Min(length, entry.Bytes.Length - offset);
            return ObjectRange.CopyOf(entry.Bytes.AsSpan((int)offset, available), entry.Token);
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
        string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        await StartAsync(ObjectOperation.PutIfAbsent, key, cancellationToken).ConfigureAwait(false);

        // Copied before the lock: the caller's buffer may be rented, and the store owes a copy
        // before it returns.
        byte[] bytes = content.ToArray();
        PutOutcome outcome;
        lock (_gate)
        {
            if (_objects.ContainsKey(key))
            {
                outcome = PutOutcome.Exists;
            }
            else
            {
                _objects[key] = new Entry(bytes, Token(), Clock.GetUtcNow());
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
    public async ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        await StartAsync(ObjectOperation.Delete, key, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return _objects.Remove(key);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> ListAsync(
        string prefix, string? startAfter, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        await StartAsync(ObjectOperation.List, prefix, cancellationToken).ConfigureAwait(false);

        List<string> keys = [];
        lock (_gate)
        {
            foreach (string key in _objects.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (startAfter is { } after && string.CompareOrdinal(key, after) <= 0)
                {
                    continue;
                }

                keys.Add(key);
                if (keys.Count == max)
                {
                    break;
                }
            }
        }

        return keys;
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        }

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
    GetRange = 0,

    Head = 1,

    PutIfAbsent = 2,

    Delete = 3,

    List = 4,
}
