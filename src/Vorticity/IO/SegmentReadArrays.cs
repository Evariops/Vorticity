using System;
using System.Numerics;
using System.Threading.Tasks;
using Vorticity.Buffers;

namespace Vorticity.IO;

/// <summary>
/// The arrays a reader of a <see cref="SegmentRequestSet"/> holds references or memory of another type in while it reads its
/// runs: the reads in flight and the blocks they land in, the ranges and leases a source reads through; and the segment
/// table of a scan whose first claim came through the set. A block of the pool holds none of them: these are the set's own,
/// kept from one batch to the next as its slots are, so that a set a lane keeps reads and scans without allocating once it
/// has read as many runs; made at the first read or claim that needs them.
/// </summary>
internal sealed class SegmentReadArrays
{
    /// <summary>
    /// The segment table of a scan whose first claim came through the set (<see cref="Scanning.ScanSegments"/>), lent to it
    /// and kept between its scans; null while a scan holds it, when a scan beside it makes one for the set. The context that
    /// holds the set outlives its scans, and a scan's allocation is held to a ceiling counted in bytes.
    /// </summary>
    internal Scanning.ScanSegments.EntryTable? KeptScanTable;

    /// <summary>The object store's reads, one a run, in flight together.</summary>
    internal ValueTask<ObjectRead>[] Reads = [];

    /// <summary>The block each run of the object store lands in.</summary>
    internal BlockOwner?[] Owners = [];

    /// <summary>The ranges a segment source reads, one a pending segment.</summary>
    internal SegmentRange[] Ranges = [];

    /// <summary>The leases a segment source hands back, one a range.</summary>
    internal SegmentLease[] Leases = [];

    /// <summary><paramref name="array"/>, made anew at a power of two of at least <paramref name="count"/> entries when it is shorter.</summary>
    internal static T[] Ensure<T>(ref T[] array, int count)
    {
        if (array.Length < count)
        {
            array = new T[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 1))];
        }

        return array;
    }
}
