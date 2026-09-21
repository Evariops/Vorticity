using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// The engine's view of a caller's <see cref="ISegmentSource"/>: every lease becomes a segment
/// owner, kept as it came when it is one pinned, aligned block and copied into an aligned block
/// otherwise.
/// </summary>
internal sealed class SourceReader : ISegmentReader
{
    private readonly ISegmentSource _source;
    private readonly bool _ownsSource;
    private readonly AlignedBufferPool _pool;

    internal SourceReader(ISegmentSource source, bool ownsSource, AlignedBufferPool pool)
    {
        _source = source;
        _ownsSource = ownsSource;
        _pool = pool;
    }

    internal ISegmentSource Source => _source;

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(_source.Length);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, _source.Length);
        if (length == 0)
        {
            return new EmptySegmentOwner();
        }

        SegmentLease lease = await _source.ReadAsync(new SegmentRange(offset, length), cancellationToken).ConfigureAwait(false);
        return Adopt(ref lease, offset, length, 1 << spec.AlignmentExponent);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.IsPopulated)
        {
            return;
        }

        int pending = 0;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            pending += requests.IsFilled(slot) || requests.GetSpec(slot).Length == 0 ? 0 : 1;
        }

        int[] slots = ArrayPool<int>.Shared.Rent(Math.Max(pending, 1));
        SegmentRange[] ranges = ArrayPool<SegmentRange>.Shared.Rent(Math.Max(pending, 1));
        SegmentLease[] leases = ArrayPool<SegmentLease>.Shared.Rent(Math.Max(pending, 1));
        int adopted = 0;
        try
        {
            int n = 0;
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                SegmentIo.ValidateSpec(in spec, out long offset, out int length);
                SegmentIo.CheckInFile(offset, length, _source.Length);
                if (length == 0)
                {
                    requests.SetResult(slot, new EmptySegmentOwner());
                    continue;
                }

                slots[n] = slot;
                ranges[n] = new SegmentRange(offset, length);
                n++;
            }

            if (n > 0)
            {
                await _source.ReadAsync(ranges.AsMemory(0, n), leases.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            }

            for (; adopted < n; adopted++)
            {
                SegmentSpec spec = requests.GetSpec(slots[adopted]);
                requests.SetResult(slots[adopted], Adopt(ref leases[adopted], ranges[adopted].Offset, ranges[adopted].Length, 1 << spec.AlignmentExponent));
            }

            requests.Complete();
        }
        catch
        {
            for (int i = adopted; i < pending; i++)
            {
                leases[i].Dispose();
            }

            requests.AbandonPending();
            throw;
        }
        finally
        {
            Array.Clear(leases, 0, Math.Max(pending, 1));
            ArrayPool<SegmentLease>.Shared.Return(leases);
            ArrayPool<SegmentRange>.Shared.Return(ranges);
            ArrayPool<int>.Shared.Return(slots);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        int actual = SegmentIo.ClampRange(offset, length, alignment, _source.Length);
        if (actual == 0)
        {
            return new EmptySegmentOwner();
        }

        SegmentLease lease = await _source.ReadAsync(new SegmentRange(offset, actual), cancellationToken).ConfigureAwait(false);
        return Adopt(ref lease, offset, actual, alignment);
    }

    public ValueTask DisposeAsync() => _ownsSource ? _source.DisposeAsync() : ValueTask.CompletedTask;

    private unsafe SegmentOwner Adopt(ref SegmentLease lease, long offset, int length, int alignment)
    {
        SegmentLease taken = lease;
        lease = default;
        if (taken.Bytes.Length != length)
        {
            taken.Dispose();
            SegmentIo.ThrowTruncatedRead(offset, length, (int)Math.Min(taken.Bytes.Length, int.MaxValue));
        }

        if (taken.IsContiguous)
        {
            MemoryHandle pin = taken.Memory.Pin();
            if (((nuint)pin.Pointer & (nuint)(alignment - 1)) == 0)
            {
                return new LeaseOwner(taken, pin, length, alignment);
            }

            pin.Dispose();
        }

        NativeSegmentOwner block = _pool.Rent(length, VortexLimits.MaxAlignment);
        try
        {
            taken.Bytes.CopyTo(block.WritableSpan);
        }
        catch
        {
            block.Dispose();
            throw;
        }
        finally
        {
            taken.Dispose();
        }

        return block;
    }

    /// <summary>A lease kept as it came: pinned, aligned, released with the owner.</summary>
    private sealed unsafe class LeaseOwner : SegmentOwner
    {
        private SegmentLease _lease;
        private MemoryHandle _pin;

        internal LeaseOwner(SegmentLease lease, MemoryHandle pin, int length, int alignment)
        {
            _lease = lease;
            _pin = pin;
            Buffer = VortexBuffer.FromPointer((byte*)pin.Pointer, length, System.Numerics.BitOperations.TrailingZeroCount(alignment));
        }

        protected override void FreeCore()
        {
            _pin.Dispose();
            _lease.Dispose();
        }
    }
}
