using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Vorticity.IO;

/// <summary>
/// Where some bytes lie and what reaching them costs: a round trip's latency, the requests in flight, the bytes a second.
/// A request is worth the bytes the link carries during its share of the latency, <c>L × D / Q</c>.
/// </summary>
/// <param name="Latency">A request's round trip, zero or more.</param>
/// <param name="InFlight">The requests in flight at once, one at least.</param>
/// <param name="BytesPerSecond">The bytes a second the tier delivers, finite and above zero.</param>
/// <exception cref="ArgumentOutOfRangeException">A value is outside its range: a rate of zero divided a prediction by zero.</exception>
public sealed record CostTier(TimeSpan Latency, int InFlight, double BytesPerSecond)
{
    private readonly TimeSpan _latency = CheckLatency(Latency);
    private readonly int _inFlight = CheckInFlight(InFlight);
    private readonly double _bytesPerSecond = CheckRate(BytesPerSecond);

    /// <summary>A request's round trip, zero or more.</summary>
    public TimeSpan Latency { get => _latency; init => _latency = CheckLatency(value); }

    /// <summary>The requests in flight at once, one at least.</summary>
    public int InFlight { get => _inFlight; init => _inFlight = CheckInFlight(value); }

    /// <summary>The bytes a second the tier delivers, finite and above zero.</summary>
    public double BytesPerSecond { get => _bytesPerSecond; init => _bytesPerSecond = CheckRate(value); }

    /// <summary>The bytes a request's round trip is worth: what the link carries during its share of the latency.</summary>
    public long RequestBytes => (long)Math.Round(Latency.TotalSeconds * BytesPerSecond / InFlight);

    private static TimeSpan CheckLatency(TimeSpan value) =>
        value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Latency), value, "A round trip takes no time or some.");

    private static int CheckInFlight(int value) =>
        value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(InFlight), value, "One request at least is in flight.");

    private static double CheckRate(double value) =>
        double.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(BytesPerSecond), value, "A tier delivers some bytes a second.");
}

/// <summary>How a value is encoded, by what reading it costs the processor (spec B, 2.1).</summary>
public enum EncodingClass
{
    /// <summary>A constant or a sequence: nothing a value.</summary>
    Free,

    /// <summary>Run-end: a run at a time.</summary>
    Runs,

    /// <summary>Bit-packing, frame of reference, zigzag, decimal parts: a few tenths of a nanosecond a value.</summary>
    Light,

    /// <summary>Sparse: a little a row, and a value a present one.</summary>
    Sparse,

    /// <summary>ALP, ALP-RD, a dictionary, primitives as they are, datetime parts: about a nanosecond a value.</summary>
    Medium,

    /// <summary>Var-bin, views, lists: a few nanoseconds a value.</summary>
    Text,

    /// <summary>FSST, OnPair, pco: up to some twelve nanoseconds a value.</summary>
    Heavy,
}

/// <summary>What the processor costs a value of an encoding class, in nanoseconds on one core.</summary>
/// <param name="Decode">Decoding a value.</param>
/// <param name="Judge">Judging a pushed predicate on a row, in the encoded domain where the kernels can.</param>
public readonly record struct ProcessorPrice(double Decode, double Judge);

/// <summary>
/// What reading and computing cost where a dataset's bytes lie (spec B, 2): the tier of the store, the tiers the caches hold
/// regions in, and the processor's price a value by encoding class. One object, consumed by every planner that prices a
/// path: a path's weight is its bytes, plus a request's worth a request, plus each value's processing in bytes of the link.
/// </summary>
/// <remarks>
/// <para>
/// The prices are constants an application may change, never measured at start. By default, a remote object store's: a
/// round trip of 30 ms among 128 requests in flight over 1.25 GB/s, a request worth some 293 KB; a local disk's is 0.1 ms
/// among 64 over 3 GB/s, some 4.7 KB; memory's, nothing.
/// </para>
/// <para>
/// Two ranges a gap of less than a request's worth apart are read as one, the gap rounded down to a power of two (256 KiB
/// at the remote tier), up to <see cref="MaxRequestBytes"/> a request (8 MiB).
/// </para>
/// </remarks>
public sealed record CostProfile
{
    private static readonly ImmutableArray<ProcessorPrice> DefaultPrices =
    [
        new ProcessorPrice(0, 0),
        new ProcessorPrice(10, 10),
        new ProcessorPrice(0.45, 0.15),
        new ProcessorPrice(3.2, 3.2),
        new ProcessorPrice(1.0, 0.2),
        new ProcessorPrice(2.6, 3.0),
        new ProcessorPrice(7.0, 1.5),
    ];

    /// <summary>A remote object store's tier: 30 ms, 128 requests in flight, 1.25 GB/s.</summary>
    public static CostTier RemoteTier { get; } = new CostTier(TimeSpan.FromMilliseconds(30), 128, 1.25e9);

    /// <summary>A local NVMe disk's tier: 0.1 ms, 64 requests in flight, 3 GB/s.</summary>
    public static CostTier LocalTier { get; } = new CostTier(TimeSpan.FromMilliseconds(0.1), 64, 3e9);

    /// <summary>Memory's tier: no round trip, some 10 GB/s.</summary>
    public static CostTier MemoryTier { get; } = new CostTier(TimeSpan.Zero, 1, 1e10);

    /// <summary>A remote object store's profile, every store's by default.</summary>
    public static CostProfile Remote { get; } = new CostProfile { Store = RemoteTier };

    /// <summary>A store on a local disk's profile.</summary>
    public static CostProfile LocalDisk { get; } = new CostProfile { Store = LocalTier };

    /// <summary>A store in memory's profile.</summary>
    public static CostProfile Memory { get; } = new CostProfile { Store = MemoryTier };

    private readonly CostTier _store = RemoteTier;
    private readonly CostTier _diskCache = LocalTier;
    private readonly CostTier _memoryCache = MemoryTier;
    private readonly int _maxRequestBytes = 8 << 20;
    private readonly double _foldValuesPerSecond = 5e9;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly IReadOnlyList<ProcessorPrice> _prices = DefaultPrices;

    /// <summary>Where the objects lie.</summary>
    public CostTier Store { get => _store; init => _store = value ?? throw new ArgumentNullException(nameof(Store)); }

    /// <summary>Where a cache on disk holds its regions: the local tier by default.</summary>
    public CostTier DiskCache { get => _diskCache; init => _diskCache = value ?? throw new ArgumentNullException(nameof(DiskCache)); }

    /// <summary>Where a cache in memory holds its regions.</summary>
    public CostTier MemoryCache { get => _memoryCache; init => _memoryCache = value ?? throw new ArgumentNullException(nameof(MemoryCache)); }

    /// <summary>The most bytes a request carries, ranges joined: 8 MiB by default, one at least.</summary>
    public int MaxRequestBytes
    {
        get => _maxRequestBytes;
        init => _maxRequestBytes = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxRequestBytes), value, "A request carries one byte at least.");
    }

    /// <summary>The values a node folds a second, aggregating or grouping, a run and a dictionary's code counting for one: 5·10⁹.</summary>
    public double FoldValuesPerSecond
    {
        get => _foldValuesPerSecond;
        init => _foldValuesPerSecond = double.IsFinite(value) && value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(FoldValuesPerSecond), value, "A node folds some values a second.");
    }

    /// <summary>The cores of a node, which the processor's prices are shared over: the machine's by default, one at least.</summary>
    public int Cores
    {
        get => _cores;
        init => _cores = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(Cores), value, "A node has one core at least.");
    }

    /// <summary>
    /// The processor's price a value of each encoding class, by <see cref="EncodingClass"/>: one at least, the last
    /// standing for the classes past the list.
    /// </summary>
    public IReadOnlyList<ProcessorPrice> Prices
    {
        get => _prices;
        init => _prices = value is { Count: > 0 }
            ? value
            : throw new ArgumentException("The prices hold one at least, the last standing for the classes past them.", nameof(Prices));
    }

    /// <summary>The bytes a request to the store is worth.</summary>
    public long RequestBytes => Store.RequestBytes;

    /// <summary>
    /// The gap two ranges of one object are read across as one: a request's worth, rounded down to a power of two, 256 KiB at
    /// the remote tier; none in memory.
    /// </summary>
    public int JoinedGap => RequestBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue / 2, 1L << (63 - (int)long.LeadingZeroCount(RequestBytes)));

    /// <summary>The price of a value of <paramref name="kind"/>.</summary>
    public ProcessorPrice PriceOf(EncodingClass kind) => (int)kind < Prices.Count ? Prices[(int)kind] : Prices[^1];

    /// <summary>
    /// What a path weighs, as a planner compares paths: its bytes, a request's worth a request, both at the store's tier, and
    /// its processing in bytes of the link, <paramref name="nanoseconds"/> spread over the node's cores.
    /// </summary>
    public double Weight(long requests, long bytes, double nanoseconds = 0) =>
        bytes + ((double)requests * RequestBytes) + (nanoseconds * 1e-9 * Store.BytesPerSecond / Math.Max(Cores, 1));

    /// <summary>
    /// The time a step of <paramref name="requests"/> requests carrying <paramref name="bytes"/> bytes, which costs the
    /// processor <paramref name="nanoseconds"/> of one core, takes cold (spec B, 2.2): its requests in rounds of those in
    /// flight, a round trip each, its bytes over the link, its processing over the cores.
    /// </summary>
    public TimeSpan Predict(long requests, long bytes, double nanoseconds) =>
        TimeSpan.FromSeconds(
            (Math.Ceiling((double)requests / Math.Max(Store.InFlight, 1)) * Store.Latency.TotalSeconds)
            + (bytes / Store.BytesPerSecond)
            + (nanoseconds * 1e-9 / Math.Max(Cores, 1)));

    /// <summary>
    /// The time a read of <paramref name="requests"/> requests carrying <paramref name="bytes"/> bytes takes cold, many
    /// requests in flight: its bytes and its requests' worth over the link.
    /// </summary>
    public TimeSpan Time(long requests, long bytes) =>
        TimeSpan.FromSeconds((bytes + ((double)requests * RequestBytes)) / Store.BytesPerSecond);

    /// <summary>The coalescing a source of this store's objects reads with: the joined gap, the most bytes a request.</summary>
    internal SegmentReadOptions ReadOptions => new SegmentReadOptions
    {
        CoalesceGapBytes = JoinedGap,
        MaxCoalescedReadBytes = Math.Max(MaxRequestBytes, VortexLimits.MaxAlignment),
    };
}
