using Vorticity.Buffers;

namespace Vorticity.IO;

/// <summary>
/// The owner of a zero-length segment: no memory, nothing to free, <see cref="SegmentOwner.Buffer"/>
/// left at <see cref="VortexBuffer.Empty"/>.
/// </summary>
/// <remarks>
/// A zero-length segment is legal — the footer's specs are sorted by offset with a
/// non-decreasing comparison precisely because of them — so this is a normal case, not a
/// defensive one. It exists so that every populated slot of a <see cref="SegmentRequestSet"/>
/// has a real owner and the refcount bookkeeping stays uniform. A shared singleton would work
/// only until an adversarial file registered two billion zero-length segments and overflowed its
/// count, so each slot gets its own — an object header, no native allocation, no syscall.
/// </remarks>
internal sealed class EmptySegmentOwner : SegmentOwner
{
    protected override void FreeCore()
    {
        // Nothing was allocated.
    }
}
