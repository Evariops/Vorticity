using System;
using System.Net;

namespace Vorticity.ObjectStorage;

/// <summary>How a store talks to its service: connections, deadlines and retries.</summary>
/// <remarks>
/// The defaults suit a service across a network. A store keeps its own connections, one pool for
/// every view of it under a prefix, which its disposal closes.
/// </remarks>
public sealed class StoreTransportOptions
{
    /// <summary>The connections a store keeps open to its endpoint at most, 64 by default; a request waits for one rather than opening another.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>
    /// How long a new connection may take to open, TLS included, 10 seconds by default, before the
    /// attempt is given up and, when the request may be sent again, retried.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long an attempt waits for the first byte of its answer once its request is sent, the end of
    /// its body included, 15 seconds by default, before it is abandoned and, when the request may be
    /// sent again, retried.
    /// </summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a body, sent or received, may go without a byte, 30 seconds by default, before its
    /// connection is closed and the request retried: a stalled connection is abandoned rather than
    /// awaited, and a slow one, which makes progress, is waited for.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a request is sent at most, the first time included, 5 by default.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// The budget of requests the store shares with the process's other stores: requests in flight and requests a second,
    /// which a throttle halves; null, the default, for the process's own (<see cref="ObjectStorage.RequestBudget.Process"/>,
    /// 256 in flight and 2 000 a second).
    /// </summary>
    public RequestBudget? RequestBudget { get; init; }

    /// <summary>The proxy, or null for the environment's (<c>HTTPS_PROXY</c>, <c>NO_PROXY</c>), as the platform reads it.</summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>
    /// A directory where a put read from a stream is held whole, on disk, before it is sent, so that any
    /// attempt can send it again: a part, the whole object, or the upload itself. Null, the default,
    /// sends a stream as it is read, holding in memory the parts in flight, or a put small enough to go
    /// whole.
    /// </summary>
    /// <remarks>
    /// A put of bytes already in memory is never spooled. Each put takes a file of its own, deleted when
    /// the put ends, and its upload starts once its content has ended.
    /// </remarks>
    public string? SpoolDirectory { get; init; }

    /// <summary>
    /// The most a put read from a stream holds in memory to send whole, 256 MiB by default: what a
    /// conditional put to a service that takes a condition only on a whole object, R2 or Cloud Storage,
    /// may reach unless <see cref="SpoolDirectory"/> holds it on disk. Past it, such a put is refused
    /// before a byte is sent.
    /// </summary>
    public long MaxHeldBytes { get; init; } = 256L << 20;

    /// <summary>The first delay of the exponential backoff, before its jitter.</summary>
    internal TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest delay between two attempts, a <c>Retry-After</c> included.</summary>
    internal TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the abort of an upload given up may take, its retries included, whatever became of the
    /// caller's token: past it, the upload is left to the bucket's lifecycle rule.
    /// </summary>
    internal TimeSpan AbortTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock the delays run on.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConnections, nameof(MaxConnections));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAttempts, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero, nameof(ConnectTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FirstByteTimeout, TimeSpan.Zero, nameof(FirstByteTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StallTimeout, TimeSpan.Zero, nameof(StallTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxHeldBytes, nameof(MaxHeldBytes));
        if (SpoolDirectory is { } spool && !System.IO.Directory.Exists(spool))
        {
            throw new ArgumentException($"The spool directory '{spool}' does not exist.", nameof(SpoolDirectory));
        }
    }
}
