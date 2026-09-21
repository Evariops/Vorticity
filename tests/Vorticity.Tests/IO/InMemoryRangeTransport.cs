using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Tests.IO;

/// <summary>
/// A deterministic <see cref="IRangeTransport"/> over a byte array, with injectable latency,
/// failures and cancellation — the harness for the open-latency metric and for the seam's
/// failure contract.
/// </summary>
public sealed class InMemoryRangeTransport : IRangeTransport
{
    private readonly byte[] _content;
    private int _requestCount;
    private long _bytesServed;

    /// <summary>Serves <paramref name="content"/>.</summary>
    /// <param name="content">The object bytes. Not copied; the test owns them.</param>
    public InMemoryRangeTransport(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
    }

    /// <inheritdoc/>
    public long Length => _content.Length;

    /// <summary>How many ranges have been served or attempted.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>How many bytes were actually handed back.</summary>
    public long BytesServed => Interlocked.Read(ref _bytesServed);

    /// <summary>
    /// Simulated round-trip latency, applied before each range is served. Zero still yields, so
    /// the code path is genuinely asynchronous without depending on the clock for a result.
    /// </summary>
    public int LatencyMilliseconds { get; set; }

    /// <summary>
    /// Invoked with the zero-based request ordinal before each range is served. Throw from it to
    /// inject a transport failure or a cancellation.
    /// </summary>
    public Action<int>? BeforeRequest { get; set; }

    /// <inheritdoc/>
    public async ValueTask ReadRangeAsync(
        long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int ordinal = Interlocked.Increment(ref _requestCount) - 1;

        if (LatencyMilliseconds > 0)
        {
            await Task.Delay(LatencyMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.Yield();
        }

        BeforeRequest?.Invoke(ordinal);
        cancellationToken.ThrowIfCancellationRequested();

        if (offset < 0 || offset + destination.Length > _content.Length)
        {
            throw new InvalidOperationException(
                $"The source asked for [{offset}, {offset + destination.Length}) of a " +
                $"{_content.Length}-byte object.");
        }

        _content.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
        Interlocked.Add(ref _bytesServed, destination.Length);
    }
}
