using Vorticity.Buffers;

namespace Vorticity.File;

/// <summary>
/// An owner for a slice of another owner's memory, used when a requested segment already lies
/// inside the buffer read from the file's tail and so costs no further reading. Retaining the
/// parent keeps the tail buffer alive for as long as the slice is, and releasing this owner gives
/// that reference back, so the caller still releases exactly once.
/// </summary>
internal sealed class TailSliceSegmentOwner : SegmentOwner
{
    private readonly SegmentOwner _parent;

    /// <summary>Retains <paramref name="parent"/> and publishes <paramref name="slice"/>.</summary>
    /// <param name="parent">Owner of the memory <paramref name="slice"/> points into.</param>
    /// <param name="slice">The view to publish; the caller guarantees it lies inside the parent.</param>
    internal TailSliceSegmentOwner(SegmentOwner parent, VortexBuffer slice)
    {
        // Retain first: if the parent is already gone this throws before the object is published.
        _parent = parent.Retain();
        Buffer = slice;
    }

    protected override void FreeCore() => _parent.Release();
}
