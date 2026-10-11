using System;
using System.Net;

namespace Vorticity.ObjectStorage;

/// <summary>How a store talks to its service: connections, deadlines and retries.</summary>
/// <remarks>
/// The defaults suit a service across a network. A store keeps its own connections, one pool for
/// every view of it under a prefix, and the session that holds it disposes it.
/// </remarks>
public sealed class StoreTransportOptions
{
    /// <summary>The connections a store keeps open to its endpoint at most, 64 by default; a request waits for one rather than opening another.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>
    /// How long an attempt waits for the first byte of its answer, 15 seconds by default, before it is
    /// abandoned and, when the request may be sent again, retried.
    /// </summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a body may go without a byte, 30 seconds by default, before its connection is closed
    /// and the request retried: a stalled connection is abandoned rather than awaited.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a request is sent at most, the first time included, 5 by default.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>The proxy, or null for the environment's (<c>HTTPS_PROXY</c>, <c>NO_PROXY</c>), as the platform reads it.</summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>
    /// The percentile of the store's recent first-byte latencies past which a read that has not seen its
    /// first byte is sent a second time, the first answer kept and the other's connection closed; null,
    /// the default, sends no hedge. 99 is the usual choice. A hedge is a request, and requests cost money.
    /// </summary>
    public double? HedgeAfterPercentile { get; init; }

    /// <summary>The most reads hedged, as a share of the reads sent, 2 % by default.</summary>
    public double HedgeBudgetPercent { get; init; } = 2;

    /// <summary>The first delay of the exponential backoff, before its jitter.</summary>
    internal TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest delay between two attempts, a <c>Retry-After</c> included.</summary>
    internal TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The clock the delays run on.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConnections, nameof(MaxConnections));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAttempts, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FirstByteTimeout, TimeSpan.Zero, nameof(FirstByteTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StallTimeout, TimeSpan.Zero, nameof(StallTimeout));
        if (HedgeAfterPercentile is { } percentile && percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(HedgeAfterPercentile), percentile, "A percentile in (0, 100] was expected.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(HedgeBudgetPercent, nameof(HedgeBudgetPercent));
    }
}
