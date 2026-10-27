using System;
using Vorticity.Buffers;

namespace Vorticity.IO;

/// <summary>
/// Tuning for a segment source that performs real I/O. Immutable once constructed; a single
/// instance is safely shared by every concurrent read. Beyond the coalescing gap, the size
/// thresholds are there to bound allocations whose size would otherwise come from file content.
/// </summary>
internal sealed class SegmentReadOptions
{
    /// <summary>1 MiB.</summary>
    public const int DefaultCoalesceGapBytes = 1 << 20;

    /// <summary>16 MiB. The ceiling on a single coalesced read, and therefore on one allocation.</summary>
    public const int DefaultMaxCoalescedReadBytes = 16 << 20;

    /// <summary>32 MiB, matching <see cref="AlignedBufferPool"/>'s own default ceiling.</summary>
    public const int DefaultMaxPooledBytes = 32 * 1024 * 1024;

    private readonly int _coalesceGapBytes = DefaultCoalesceGapBytes;
    private readonly int _maxCoalescedReadBytes = DefaultMaxCoalescedReadBytes;
    private readonly int _maxPooledBytes = DefaultMaxPooledBytes;

    /// <summary>Creates an options instance carrying every default.</summary>
    public SegmentReadOptions()
    {
    }

    /// <summary>The shared default instance.</summary>
    public static SegmentReadOptions Default { get; } = new SegmentReadOptions();

    /// <summary>
    /// Two ranges separated by no more than this many bytes are read as one. Default
    /// <see cref="DefaultCoalesceGapBytes"/>. Zero disables coalescing of non-adjacent ranges.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int CoalesceGapBytes
    {
        get => _coalesceGapBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _coalesceGapBytes = value;
        }
    }

    /// <summary>
    /// A coalesced read never grows beyond this many bytes. Default
    /// <see cref="DefaultMaxCoalescedReadBytes"/>.
    /// </summary>
    /// <remarks>
    /// This is the bound that stops a merely-large gap from turning into a merely-large
    /// allocation: two segments a gigabyte apart are within any generous gap threshold, and
    /// merging them would allocate a gigabyte to deliver a few kilobytes. A single segment larger
    /// than this is still read whole — it cannot be split — so the bound limits merging, not the
    /// file.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is below <see cref="VortexLimits.MaxAlignment"/>, which is the smallest run a
    /// 64-byte-rounded start can produce.
    /// </exception>
    public int MaxCoalescedReadBytes
    {
        get => _maxCoalescedReadBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, VortexLimits.MaxAlignment);
            _maxCoalescedReadBytes = value;
        }
    }

    /// <summary>
    /// Buffers of at most this size come from <see cref="AlignedBufferPool.Shared"/>; larger ones
    /// are allocated and freed outright. Default <see cref="DefaultMaxPooledBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxPooledBytes
    {
        get => _maxPooledBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxPooledBytes = value;
        }
    }
}
