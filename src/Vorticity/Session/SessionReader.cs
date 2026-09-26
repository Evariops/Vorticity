using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity;

/// <summary>
/// A file's reader as its session sees it: every request waits on the session's bound on reads in
/// flight, and every segment passes through the session's cache.
/// </summary>
/// <remarks>Only a reader that does I/O is wrapped: memory and a mapping have nothing to bound or to cache.</remarks>
internal sealed class SessionReader : ISegmentReader
{
    private readonly ISegmentReader _inner;
    private readonly SemaphoreSlim _gate;
    private readonly SegmentCache? _cache;

    private SessionReader(ISegmentReader inner, SemaphoreSlim gate, SegmentCache? cache)
    {
        _inner = inner;
        _gate = gate;
        _cache = cache;
    }

    internal ISegmentReader Inner => _inner;

    internal static ISegmentReader Wrap(ISegmentReader inner, VortexSession session) =>
        inner is MemorySegmentSource or MemoryMappedSegmentSource or SessionReader
            ? inner
            : new SessionReader(inner, session.ReadGate, session.Options.SegmentCache);

    internal static ISegmentReader Unwrap(ISegmentReader reader) => reader is SessionReader session ? session._inner : reader;

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => _inner.GetLengthAsync(cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        if (_cache is not null && _cache.TryGet(_inner, (long)spec.Offset, (int)spec.Length, out SegmentOwner cached, out VortexBuffer view))
        {
            return Slice(cached, view);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SegmentOwner owner;
        try
        {
            owner = await _inner.ReadAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _cache?.Add(_inner, (long)spec.Offset, owner, owner.Buffer);
        return owner;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.IsPopulated)
        {
            return;
        }

        // Only what the source reads goes into the cache: a slot filled before the read comes from
        // memory the caller holds, a segment its scan kept or the tail its file read, and a slot
        // the cache fills is in it already.
        bool[]? held = null;
        try
        {
            if (_cache is not null)
            {
                for (int slot = 0; slot < requests.Count; slot++)
                {
                    if (requests.IsFilled(slot))
                    {
                        (held ??= Marks(requests.Count))[slot] = true;
                        continue;
                    }

                    SegmentSpec spec = requests.GetSpec(slot);
                    if (_cache.TryGet(_inner, (long)spec.Offset, (int)spec.Length, out SegmentOwner owner, out VortexBuffer view))
                    {
                        try
                        {
                            requests.SetSharedResult(slot, owner, view);
                        }
                        finally
                        {
                            owner.Release();
                        }

                        requests.NoteCacheHit();
                        (held ??= Marks(requests.Count))[slot] = true;
                    }
                }
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _inner.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (_cache is not null)
            {
                for (int slot = 0; slot < requests.Count; slot++)
                {
                    if (held is null || !held[slot])
                    {
                        _cache.Add(_inner, (long)requests.GetSpec(slot).Offset, requests.GetOwner(slot), requests.GetBuffer(slot));
                    }
                }
            }
        }
        finally
        {
            if (held is not null)
            {
                ArrayPool<bool>.Shared.Return(held);
            }
        }
    }

    /// <summary>One mark per slot, all clear.</summary>
    private static bool[] Marks(int count)
    {
        bool[] marks = ArrayPool<bool>.Shared.Rent(count);
        Array.Clear(marks, 0, count);
        return marks;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.ReadRangeAsync(offset, length, alignment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _cache?.Evict(_inner);
        return _inner.DisposeAsync();
    }

    private static SegmentOwner Slice(SegmentOwner cached, VortexBuffer view)
    {
        try
        {
            return new SliceSegmentOwner(cached, view);
        }
        finally
        {
            cached.Release();
        }
    }
}
