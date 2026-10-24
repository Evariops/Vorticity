using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A store as an object store in the cloud serves it: every request is answered <see cref="Latency"/> after it
/// is sent, to the tenth of a millisecond (<see cref="PreciseDelay"/>), the store under it working meanwhile; a
/// read's bytes then cross a link that every read shares, at <see cref="LinkBitsPerSecond"/>, in the order they
/// arrive, and each read's at <see cref="BytesPerSecond"/> at most, as a connection carries them. A listing waits
/// once, for its first key.
/// </summary>
internal sealed class LatencyObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly Lock _link = new Lock();
    private long _linkFree;

    /// <summary>What a request waits before it is answered; zero for none.</summary>
    internal TimeSpan Latency { get; set; }

    /// <summary>The bits a second the link to the store carries, every read sharing it; zero for no limit.</summary>
    internal double LinkBitsPerSecond { get; set; }

    /// <summary>The bytes a second one read receives at most, as one connection; zero for no limit.</summary>
    internal double BytesPerSecond { get; set; }

    /// <summary>Where each read is written down, its key, offset and length, one line each; null for none.</summary>
    internal System.IO.TextWriter? Trace { get; set; }

    public ObjectStoreCapabilities Capabilities => inner.Capabilities;

    public async ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        Trace?.WriteLine($"{key}\t{offset}\t{destination.Length}");
        if (Trace is not null && Environment.GetEnvironmentVariable("VORTICITY_TRACE_STACKS") == "1")
        {
            Trace.WriteLine(Environment.StackTrace);
        }

        long sent = Stopwatch.GetTimestamp();
        Task answered = PreciseDelay.Delay(Latency);
        ObjectRead read = await inner.ReadAsync(key, offset, destination, ifToken, cancellationToken).ConfigureAwait(false);
        await answered.ConfigureAwait(false);
        await TransferAsync(sent, read.BytesRead).ConfigureAwait(false);
        return read;
    }

    public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Trace?.WriteLine($"{key}\ttail\t{destination.Length}");
        long sent = Stopwatch.GetTimestamp();
        Task answered = PreciseDelay.Delay(Latency);
        ObjectRead read = await inner.ReadTailAsync(key, destination, cancellationToken).ConfigureAwait(false);
        await answered.ConfigureAwait(false);
        await TransferAsync(sent, read.BytesRead).ConfigureAwait(false);
        return read;
    }

    public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        await PreciseDelay.Delay(Latency).ConfigureAwait(false);
        return await inner.HeadAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        await PreciseDelay.Delay(Latency).ConfigureAwait(false);
        return await inner.PutAsync(key, content, length, condition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, long maxLength, PutCondition condition, CancellationToken cancellationToken)
    {
        await PreciseDelay.Delay(Latency).ConfigureAwait(false);
        return await inner.PutAsync(key, content, length, maxLength, condition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PutOutcome> PutAsync(string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken)
    {
        await PreciseDelay.Delay(Latency).ConfigureAwait(false);
        return await inner.PutAsync(key, content, condition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await PreciseDelay.Delay(Latency).ConfigureAwait(false);
        await inner.DeleteAsync(keys, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await PreciseDelay.Delay(Latency).ConfigureAwait(false);
        await foreach (ObjectListing listing in inner.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            yield return listing;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// Waits for a read's bytes, its first one arriving <see cref="Latency"/> after <paramref name="sent"/>: their
    /// turn on the link, which carries the reads' bytes one after another at its rate, and their time on the read's
    /// connection, whichever ends last.
    /// </summary>
    private Task TransferAsync(long sent, int bytes)
    {
        if (bytes <= 0 || (LinkBitsPerSecond <= 0 && BytesPerSecond <= 0))
        {
            return Task.CompletedTask;
        }

        long first = sent + (long)(Latency.TotalSeconds * Stopwatch.Frequency);
        long done = BytesPerSecond > 0 ? first + (long)(bytes / BytesPerSecond * Stopwatch.Frequency) : first;
        if (LinkBitsPerSecond > 0)
        {
            long crossing = (long)(bytes * 8.0 / LinkBitsPerSecond * Stopwatch.Frequency);
            lock (_link)
            {
                _linkFree = Math.Max(_linkFree, first) + crossing;
                done = Math.Max(done, _linkFree);
            }
        }

        return PreciseDelay.Until(done);
    }
}
