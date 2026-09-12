// A SegmentOwner for a view into the open file's tail buffer.
//
// VortexFile.ReadMetadataAsync must hand back an owner the caller releases exactly once
// (PHASE1-CONTRACTS.md §2.2 rule 3), but a metadata segment covered by the initial tail read costs
// no I/O at all - the bytes are already in the window. Retaining the tail owner and publishing a
// slice of it is the whole implementation: the tail cannot be freed while the slice is alive, and
// the caller's Release() gives the reference back.
using Vorticity.Buffers;

namespace Vorticity.File;

/// <summary>An owner for a slice of another owner's memory. Retains the parent for its lifetime.</summary>
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

    /// <inheritdoc/>
    protected override void FreeCore() => _parent.Release();
}
