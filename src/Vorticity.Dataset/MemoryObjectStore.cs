// The store the tests run against - docs/13-dataset.md §11: "an in-memory store with injectable
// latency, failures and crashes for the tests".
//
// WHY THE THREE INJECTIONS ARE NOT TEST HELPERS BUT PART OF THE STORE. §9.2's third invariant is
// about a LATENCY, not a count: "the in-memory store injects a latency λ and no CPU cost; a cold
// clustering-key lookup completes within D × λ, D the count of §9.1, which no total of requests can
// prove, since parallel requests hide in a total". A store that could not be told to be slow could
// not prove that invariant at all. Failures and crashes are the same argument applied to §8's
// protocol: a commit is a conditional creation, and the interesting states are the ones where the
// writer never learns the answer.
//
// A CRASH IS NOT A FAILURE, and the distinction is the whole reason both exist. A failure throws
// and changes nothing. A crash STORES THE OBJECT AND THEN THROWS -- the put succeeded, the writer
// was never told, and the next attempt will find the key taken by bytes it wrote itself. §8.2's
// rebase exists for exactly that state, and a test that cannot produce it tests the easy half.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>An <see cref="IObjectStore"/> in memory, with injectable latency and faults.</summary>
/// <remarks>
/// Thread-safe, as the seam requires: one lock over a sorted map, held for the map's own work and
/// never across an await.
/// </remarks>
public sealed class MemoryObjectStore : IObjectStore
{
    private readonly object _gate = new object();
    private readonly SortedDictionary<string, Entry> _objects = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
    private long _tokens;
    private bool _disposed;

    /// <summary>One stored object: its bytes, the token it was created with, and when.</summary>
    private readonly record struct Entry(byte[] Bytes, string Token, DateTimeOffset Created);

    /// <summary>
    /// The store's clock, which stamps every object it creates (<see cref="ObjectHead.LastModified"/>).
    /// The system's by default; a test that needs an object older than a retention window moves its
    /// own instead of waiting for one (§10).
    /// </summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The delay every operation waits before doing anything. Zero by default.</summary>
    /// <remarks>
    /// The delay is per OPERATION, so operations a caller issues together overlap and operations it
    /// issues one after another do not: that is what makes the wall clock measure §9.1's dependent
    /// depth rather than the request count.
    /// </remarks>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Asked before every operation; when it returns true the operation throws
    /// <see cref="ObjectStoreException"/> and changes nothing.
    /// </summary>
    public Func<ObjectOperation, string, bool>? Fails { get; set; }

    /// <summary>
    /// Asked after a <see cref="PutIfAbsentAsync"/> has stored its bytes; when it returns true the
    /// call throws anyway, so the object exists and its writer never learned it (§8.2).
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

        // COPIED BEFORE THE LOCK, because the seam says the store copies what it needs before
        // returning and a caller's buffer may be rented.
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
    /// <param name="operation">What is about to run.</param>
    /// <param name="key">Its key, or the prefix for a listing.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
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
public enum ObjectOperation
{
    /// <summary><see cref="IObjectStore.GetRangeAsync"/>.</summary>
    GetRange = 0,

    /// <summary><see cref="IObjectStore.HeadAsync"/>.</summary>
    Head = 1,

    /// <summary><see cref="IObjectStore.PutIfAbsentAsync"/>.</summary>
    PutIfAbsent = 2,

    /// <summary><see cref="IObjectStore.DeleteAsync"/>.</summary>
    Delete = 3,

    /// <summary><see cref="IObjectStore.ListAsync"/>.</summary>
    List = 4,
}
