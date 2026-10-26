using System;

namespace Vorticity.Scanning;

/// <summary>
/// The cost model of a read from an object store, which a planner prices a path by before reading anything: a round trip
/// of 30 ms among 128 requests in flight, over a link of 1.25 GB/s. A request is then worth what 293 KB of transfer cost,
/// and a read's time is its bytes and its requests' worth over the link.
/// </summary>
internal static class ReadCost
{
    /// <summary>The bytes a request's round trip is worth: 30 ms among 128 in flight, at the link's rate.</summary>
    internal const long RequestBytes = 293_000;

    /// <summary>The link's bytes a second.</summary>
    internal const double LinkBytesPerSecond = 1.25e9;

    /// <summary>The time <paramref name="requests"/> requests carrying <paramref name="bytes"/> bytes take on the link, cold.</summary>
    internal static TimeSpan Time(long requests, long bytes) =>
        TimeSpan.FromSeconds((bytes + (requests * (double)RequestBytes)) / LinkBytesPerSecond);
}
