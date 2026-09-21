// An in-memory ISegmentSource that counts every call. Counting is the point: opening a file
// promises one or two round trips, and nothing else in the test suite measures that number.
// It is also the only way to feed VortexFile a byte-for-byte mutation of a corpus file without
// writing to disk.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.File;

/// <summary>An in-memory, call-counting segment source over a byte array.</summary>
internal sealed class TestSegmentSource : ISegmentReader
{
    private readonly byte[] _bytes;
    private readonly long _declaredLength;

    internal TestSegmentSource(byte[] bytes)
        : this(bytes, bytes.Length)
    {
    }

    /// <summary>Creates a source whose reported length differs from the byte array's.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="declaredLength">What <see cref="GetLengthAsync"/> reports.</param>
    internal TestSegmentSource(byte[] bytes, long declaredLength)
    {
        _bytes = bytes;
        _declaredLength = declaredLength;
    }

    /// <summary>Number of <see cref="GetLengthAsync"/> calls.</summary>
    internal int LengthProbes { get; private set; }

    /// <summary>Number of <see cref="ReadRangeAsync"/> calls.</summary>
    internal int RangeReads { get; private set; }

    /// <summary>Number of <see cref="ReadAsync"/> calls.</summary>
    internal int SegmentReads { get; private set; }

    /// <summary>Number of <see cref="ReadManyAsync"/> calls.</summary>
    internal int BatchReads { get; private set; }

    /// <summary>Total round trips of every kind; an open costs at most two.</summary>
    internal int TotalReads => RangeReads + SegmentReads + BatchReads;

    /// <summary>Whether <see cref="DisposeAsync"/> ran.</summary>
    internal bool Disposed { get; private set; }

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LengthProbes++;
        return new ValueTask<long>(_declaredLength);
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SegmentReads++;
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);
        ulong end = spec.End;
        if (end > (ulong)_bytes.Length)
        {
            throw new VortexFormatException($"Segment {spec.Offset}+{spec.Length} escapes the file.");
        }

        return new ValueTask<SegmentOwner>(PinnedArraySegmentOwner.CopyOf(
            _bytes.AsSpan((int)spec.Offset, (int)spec.Length),
            1 << spec.AlignmentExponent));
    }

    /// <inheritdoc/>
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        BatchReads++;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            SegmentOwner owner = await ReadAsync(requests.GetSpec(slot), cancellationToken)
                .ConfigureAwait(false);
            requests.SetResult(slot, owner);
        }

        requests.Complete();
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RangeReads++;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (offset > _bytes.Length || (offset == _bytes.Length && length > 0))
        {
            throw new VortexFormatException($"Range at {offset} starts past the end of the file.");
        }

        int available = (int)Math.Min(length, _bytes.Length - offset);
        return new ValueTask<SegmentOwner>(
            PinnedArraySegmentOwner.CopyOf(_bytes.AsSpan((int)offset, available), alignment));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
