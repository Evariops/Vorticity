using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity;

// Over a source that fetches its bytes, or that would map for a scan a file the open read whole,
// the file is the reader its scans read through, which never dispose it: a segment inside the tail
// the open read is a view of it, and the source reads the others. Such a segment is not asked of the source, so it
// is not counted among a scan's requests either: a scan fills it with FillFromTail before it
// counts, and every other count asks Holds.
public sealed partial class VortexFile : ISegmentReader
{
    /// <summary>
    /// A view of the segment <paramref name="spec"/> names inside the tail the open read, when the
    /// segment lies there at the alignment it declares.
    /// </summary>
    private bool TryViewAligned(in SegmentSpec spec, out VortexBuffer view)
    {
        if (!TryViewInTail(in spec, out VortexBuffer within))
        {
            view = default;
            return false;
        }

        view = VortexBuffer.FromPinned(within.Span, spec.AlignmentExponent);
        return view.IsAligned;
    }

    /// <summary>Whether the tail holds the segment <paramref name="spec"/> names.</summary>
    internal bool Holds(in SegmentSpec spec) => TryViewAligned(in spec, out _);

    /// <summary>
    /// Fills from the tail the empty slots of <paramref name="requests"/> it holds, when
    /// <paramref name="reader"/> is a file that serves its tail. A scan does so before it claims
    /// what it lacks: its other batches wait for a claimed segment to be read, and the tail's never is.
    /// </summary>
    internal static void FillFromTail(ISegmentReader reader, SegmentRequestSet requests)
    {
        if (reader is VortexFile file)
        {
            file.Serve(requests);
        }
    }

    /// <summary>Fills from the tail the empty slots of <paramref name="requests"/> it holds.</summary>
    /// <returns>Whether a slot is left for the source.</returns>
    private bool Serve(SegmentRequestSet requests)
    {
        bool left = false;
        try
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                if (TryViewAligned(in spec, out VortexBuffer view))
                {
                    requests.SetSharedResult(slot, _tail, view);
                }
                else
                {
                    left = true;
                }
            }
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }

        return left;
    }

    ValueTask<long> ISegmentReader.GetLengthAsync(CancellationToken cancellationToken) => _source.GetLengthAsync(cancellationToken);

    ValueTask<SegmentOwner> ISegmentReader.ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        if (TryViewAligned(in spec, out VortexBuffer view))
        {
            return new ValueTask<SegmentOwner>(new SliceSegmentOwner(_tail, view));
        }

        return _source.ReadAsync(spec, cancellationToken);
    }

    ValueTask ISegmentReader.ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        if (requests.IsPopulated)
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Serve(requests))
        {
            return ReadLeftAsync(requests, cancellationToken);
        }

        requests.Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Reads what the tail does not hold, and on a failure gives back what it served too.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadLeftAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        try
        {
            await _source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken) =>
        _source.ReadRangeAsync(offset, length, alignment, cancellationToken);
}
