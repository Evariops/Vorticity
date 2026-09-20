using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>Wraps a store and records what passes through it, including a lower bound on how many
/// round trips the caller waited for one after another.</summary>
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

    /// <summary>Wraps a store; <c>ownsInner</c> makes disposing this dispose it too.</summary>
    public CountingObjectStore(IObjectStore inner, bool ownsInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownsInner = ownsInner;
    }

    /// <summary>Every operation counted, by kind.</summary>
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

    /// <summary>How many operations of one kind were asked.</summary>
    public long CountOf(ObjectOperation operation)
    {
        lock (_gate)
        {
            return _operations[(int)operation];
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

    /// <summary>Bytes accepted by <see cref="PutIfAbsentAsync"/>, whether or not the key was free.</summary>
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

    /// <summary>Keys returned by <see cref="ListAsync"/>.</summary>
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

    /// <summary>How many times an operation started with the store idle, a lower bound on the
    /// round trips waited for one after another.</summary>
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

    /// <summary>Forgets everything counted so far. Call it with nothing in flight, or the step in
    /// progress is counted twice.</summary>
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
        string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.PutIfAbsent);
        lock (_gate)
        {
            // Counted even when the key turns out to be taken: the bytes crossed the seam anyway.
            _bytesWritten += content.Length;
        }

        try
        {
            return await _inner.PutIfAbsentAsync(key, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.Delete);
        try
        {
            return await _inner.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> ListAsync(
        string prefix, string? startAfter, int max, CancellationToken cancellationToken)
    {
        Enter(ObjectOperation.List);
        try
        {
            IReadOnlyList<string> keys = await _inner.ListAsync(prefix, startAfter, max, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _keysListed += keys.Count;
            }

            return keys;
        }
        finally
        {
            Leave();
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
