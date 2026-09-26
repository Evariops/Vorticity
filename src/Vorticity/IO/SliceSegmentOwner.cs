using System;
using Vorticity.Buffers;

namespace Vorticity.IO;

/// <summary>
/// A <see cref="SegmentOwner"/> for a view into memory owned by another owner — one segment inside
/// a whole-file memory mapping, inside a coalesced run buffer, or inside the tail an open read.
/// </summary>
/// <remarks>
/// Construction <see cref="SegmentOwner.Retain"/>s the parent and the last
/// <see cref="SegmentOwner.Release"/> gives that reference back, so the mapping cannot be unmapped
/// while a slice of it is still live. <see cref="SegmentRequestSet.SetSharedResult"/> is the
/// allocation-free path for the same idea and is what <c>ReadManyAsync</c> uses; this type exists
/// for the single-segment <see cref="ISegmentReader.ReadAsync"/> entry point, which must hand back
/// an owner of its own.
/// </remarks>
internal sealed class SliceSegmentOwner : SegmentOwner
{
    private readonly SegmentOwner _parent;

    /// <summary>Retains <paramref name="parent"/> and publishes <paramref name="slice"/>.</summary>
    /// <param name="parent">The owner of the memory <paramref name="slice"/> points into.</param>
    /// <param name="slice">The view to publish. The caller guarantees it lies inside the parent.</param>
    internal SliceSegmentOwner(SegmentOwner parent, VortexBuffer slice)
    {
        ArgumentNullException.ThrowIfNull(parent);

        // Retain first: if it throws (the parent is already gone) this object is never published.
        _parent = parent.Retain();
        Buffer = slice;
    }

    protected override void FreeCore() => _parent.Release();
}
