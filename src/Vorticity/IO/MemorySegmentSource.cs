// An ISegmentSource over bytes already in memory.
//
// The two built-in sources of docs/03-architecture.md §3.5 are both local-file: mmap and
// RandomAccess. This is the third case that keeps turning up and that every caller was otherwise
// writing for themselves -- a file already in a buffer, because it came off a network, out of a
// cache, or out of a test fixture.
//
// It COPIES each segment rather than handing out a view of the array, and that is not laziness.
// VortexBuffer's contract is that a buffer's real base address satisfies its declared alignment,
// which is what makes a decoder's reinterpretation of it legal; a managed array offers no such
// guarantee at an arbitrary offset. PinnedArraySegmentOwner copies into a block that does, so this
// source loses zero-copy and keeps correctness, which is the right trade for a source whose bytes
// were going to be copied into memory anyway.
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>Reads segments out of a byte buffer already in memory.</summary>
public sealed class MemorySegmentSource : ISegmentSource
{
    private readonly ReadOnlyMemory<byte> _bytes;

    /// <summary>Wraps <paramref name="bytes"/>.</summary>
    /// <param name="bytes">The whole file. Not copied; the caller keeps it alive.</param>
    public MemorySegmentSource(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(_bytes.Length);
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);
        if (spec.End > (ulong)_bytes.Length)
        {
            throw new VortexFormatException(
                $"Segment {spec.Offset}+{spec.Length} escapes the {_bytes.Length}-byte file.");
        }

        return new ValueTask<SegmentOwner>(PinnedArraySegmentOwner.CopyOf(
            _bytes.Span.Slice((int)spec.Offset, (int)spec.Length), 1 << spec.AlignmentExponent));
    }

    /// <inheritdoc/>
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.IsPopulated)
        {
            return;
        }

        try
        {
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
        catch
        {
            // All or nothing, as the other sources do it. Without this the slots filled before the
            // failure keep their owners: the caller cannot release what it never received, and a
            // retry over the same set hits them as already filled rather than reading them again.
            requests.AbandonPending();
            throw;
        }
    }

    /// <inheritdoc/>
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

        // A tail read asks for more than the file holds by design: the open path reads 64 KiB
        // whatever the file's size, so a short answer is the normal case and not an error.
        int available = (int)Math.Min(length, _bytes.Length - offset);
        return new ValueTask<SegmentOwner>(
            PinnedArraySegmentOwner.CopyOf(_bytes.Span.Slice((int)offset, available), alignment));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
