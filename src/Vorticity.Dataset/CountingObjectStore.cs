// The counter of docs/13-dataset.md §9.2, one level up from `Vorticity.IO.CountingSegmentSource`
// (step 27): that one counts what a reader asks of ONE file, this one counts what a dataset asks of
// a STORE.
//
// WHY A COUNT IS NOT ENOUGH, and why this also records a depth. §9.2's third invariant:
// "the dependent requests are the critical path ... which no total of requests can prove, since
// parallel requests hide in a total". Ten requests issued together cost one round trip; ten issued
// one after another cost ten. A decorator can tell those apart without a clock, because the caller
// tells it by construction: requests that OVERLAP are requests the caller did not have to wait for.
//
// SO THE DEPTH IS COUNTED LIKE THIS: an operation that starts while none is in flight begins a new
// step; one that starts while another is in flight joins the current step. The depth is the number
// of steps. It is a LOWER BOUND on the dependent depth -- a caller that issues two independent
// requests one after the other is charged two steps, as it should be, since it paid for two -- and
// it is exact for the way this library reads, which registers every segment of a split before
// reading any of it (03 §3.5).
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>Wraps a store and records what passes through it (§9.2).</summary>
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

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The store that does the work.</param>
    /// <param name="ownsInner">Whether disposing this disposes <paramref name="inner"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
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

    /// <summary>How many of <paramref name="operation"/> were asked.</summary>
    /// <param name="operation">The kind.</param>
    /// <returns>The count.</returns>
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

    /// <summary>
    /// The critical path: how many times an operation started with the store idle, which is how
    /// many round trips the caller waited for one after another.
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

    /// <summary>Forgets everything counted so far.</summary>
    /// <remarks>Call it with nothing in flight; otherwise the step in progress is counted twice.</remarks>
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
            // COUNTED BEFORE THE CALL, and counted even when the key turns out to be taken: the
            // bytes crossed the seam either way, which is what a caller pays for on a network.
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

    /// <summary>Counts the operation and, when the store was idle, the step it begins.</summary>
    /// <param name="operation">The kind.</param>
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
