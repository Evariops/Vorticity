using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// Wraps a store and records what passes through it, including a lower bound on how many round
/// trips the caller waited for one after another: the number a read budget is held to.
/// </summary>
public sealed class CountingObjectStore : IObjectStore
{
    private readonly IObjectStore _inner;
    private readonly bool _ownsInner;
    private readonly object _gate = new object();
    private readonly long[] _operations = new long[5];
    private long _bytesRead;
    private long _bytesWritten;
    private long _keysListed;
    private int _inFlight;
    private long _steps;

    /// <summary>Wraps a store.</summary>
    /// <param name="inner">The store that does the work.</param>
    /// <param name="ownsInner">Whether disposing this store disposes <paramref name="inner"/>.</param>
    public CountingObjectStore(IObjectStore inner, bool ownsInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownsInner = ownsInner;
    }

    /// <summary>Every operation counted, whatever its kind.</summary>
    public long Requests
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (long count in _operations)
                {
                    total += count;
                }

                return total;
            }
        }
    }

    /// <summary>Bytes handed back by <see cref="GetRangeAsync"/>.</summary>
    public long BytesRead
    {
        get
        {
            lock (_gate)
            {
                return _bytesRead;
            }
        }
    }

    /// <summary>Bytes announced to <see cref="PutIfAbsentAsync"/>, whether or not the key was free.</summary>
    public long BytesWritten
    {
        get
        {
            lock (_gate)
            {
                return _bytesWritten;
            }
        }
    }

    /// <summary>Keys handed back by <see cref="ListAsync"/>.</summary>
    public long KeysListed
    {
        get
        {
            lock (_gate)
            {
                return _keysListed;
            }
        }
    }

    /// <summary>
    /// How many times an operation started with the store idle: a lower bound on the round trips
    /// waited for one after another, which a total of requests cannot give.
    /// </summary>
    public long DependentSteps
    {
        get
        {
            lock (_gate)
            {
                return _steps;
            }
        }
    }

    /// <summary>How many operations of one kind were asked; a listing counts once however many keys it hands back.</summary>
    /// <param name="operation">The kind.</param>
    /// <returns>The count.</returns>
    public long CountOf(ObjectOperation operation)
    {
        lock (_gate)
        {
            return _operations[(int)operation];
        }
    }

    /// <summary>Forgets everything counted so far; call it with nothing in flight, or the step in progress is counted twice.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_operations);
            _bytesRead = 0;
            _bytesWritten = 0;
            _keysListed = 0;
            _steps = 0;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectRange> GetRangeAsync(
        string key, long offset, int length, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.GetRange);
        try
        {
            ObjectRange range = await _inner.GetRangeAsync(key, offset, length, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _bytesRead += range.Length;
            }

            return range;
        }
        finally
        {
            Leave();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.Head);
        try
        {
            return await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutIfAbsentAsync(
        string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.PutIfAbsent);
        lock (_gate)
        {
            // Counted even when the key turns out to be taken: the bytes were offered to the seam.
            _bytesWritten += length;
        }

        try
        {
            return await _inner.PutIfAbsentAsync(key, content, length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.Delete);
        try
        {
            await _inner.DeleteAsync(keys, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> ListAsync(
        string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // One operation per listing, in flight while its first key is awaited: that is the request a
        // caller waits for, and the later keys of a page cost it nothing.
        IAsyncEnumerator<string> keys = _inner.ListAsync(prefix, startAfter, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (keys.ConfigureAwait(false))
        {
            bool first = true;
            while (true)
            {
                bool more;
                if (first)
                {
                    Enter(ObjectOperation.List);
                    try
                    {
                        more = await keys.MoveNextAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Leave();
                    }

                    first = false;
                }
                else
                {
                    more = await keys.MoveNextAsync().ConfigureAwait(false);
                }

                if (!more)
                {
                    yield break;
                }

                lock (_gate)
                {
                    _keysListed++;
                }

                yield return keys.Current;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _ownsInner ? _inner.DisposeAsync() : ValueTask.CompletedTask;

    private void Enter(ObjectOperation operation)
    {
        lock (_gate)
        {
            _operations[(int)operation]++;
            if (_inFlight == 0)
            {
                _steps++;
            }

            _inFlight++;
        }
    }

    private void Leave()
    {
        lock (_gate)
        {
            _inFlight--;
        }
    }
}
