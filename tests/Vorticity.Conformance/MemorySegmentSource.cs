// An in-memory ISegmentSource, so a mutated corpus file never has to be written to disk.
//
// The malformed-input tests build hundreds of one-byte-different files; memory-mapping each of them
// would put the harness's correctness at the mercy of the filesystem, and a zero-length or
// truncated file cannot be memory-mapped at all - which would turn "the reader rejects it cleanly"
// into "the OS refused to map it", a different and much weaker statement.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Conformance;

/// <summary>An <see cref="ISegmentSource"/> over a byte array.</summary>
internal sealed class MemorySegmentSource : ISegmentSource
{
    private readonly byte[] _bytes;

    internal MemorySegmentSource(byte[] bytes) => _bytes = bytes;

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(_bytes.LongLength);
    }

    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);
        if (spec.End > (ulong)_bytes.Length)
        {
            throw new VortexFormatException($"Segment {spec.Offset}+{spec.Length} escapes the file.");
        }

        return new ValueTask<SegmentOwner>(PinnedArraySegmentOwner.CopyOf(
            _bytes.AsSpan((int)spec.Offset, (int)spec.Length),
            1 << spec.AlignmentExponent));
    }

    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            SegmentOwner owner = await ReadAsync(requests.GetSpec(slot), cancellationToken).ConfigureAwait(false);
            requests.SetResult(slot, owner);
        }

        requests.Complete();
    }

    public ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
