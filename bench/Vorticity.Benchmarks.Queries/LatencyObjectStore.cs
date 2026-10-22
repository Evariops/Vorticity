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
/// <remarks>
/// As S3 serves them (<c>--profile s3</c>), the waits spread: each is drawn from a log-normal law whose median is
/// <see cref="Latency"/> and whose 99th percentile is <see cref="LatencyP99"/>, 30 and 120 ms, from a seed, so that
/// two runs draw the same waits in the order their requests come; and the reads of a prefix of keys take their turns,
/// <see cref="ReadsPerSecondPerPrefix"/> a second at most, 5 500 on S3, a read past them waiting for its turn before
/// its round trip starts.
/// </remarks>
internal sealed class LatencyObjectStore(IObjectStore inner) : IObjectStore
{
    // The 99th percentile of a standard normal law: a log-normal wait of median m and spread s reaches m·e^(2.326·s).
    private const double Z99 = 2.3263478740408408;

    private readonly Lock _link = new Lock();
    private readonly Lock _draws = new Lock();
    private readonly Random _random = new Random(17);
    private readonly Dictionary<string, long> _turns = new Dictionary<string, long>(StringComparer.Ordinal);
    private long _linkFree;

    /// <summary>What a request waits before it is answered, the median when the waits spread; zero for none.</summary>
    internal TimeSpan Latency { get; set; }

    /// <summary>
    /// What a request waits at the 99th percentile, the waits drawn from a log-normal law of median <see cref="Latency"/>;
    /// zero, or no more than the median, for the median every time.
    /// </summary>
    internal TimeSpan LatencyP99 { get; set; }

    /// <summary>The reads a second the store serves a prefix of keys at most, a read past them waiting its turn; zero for no limit.</summary>
    internal double ReadsPerSecondPerPrefix { get; set; }

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

        long answered = Answered(key, read: true);
        Task answer = PreciseDelay.Until(answered);
        ObjectRead read = await inner.ReadAsync(key, offset, destination, ifToken, cancellationToken).ConfigureAwait(false);
        await answer.ConfigureAwait(false);
        await TransferAsync(answered, read.BytesRead).ConfigureAwait(false);
        return read;
    }

    public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Trace?.WriteLine($"{key}\ttail\t{destination.Length}");
        long answered = Answered(key, read: true);
        Task answer = PreciseDelay.Until(answered);
        ObjectRead read = await inner.ReadTailAsync(key, destination, cancellationToken).ConfigureAwait(false);
        await answer.ConfigureAwait(false);
        await TransferAsync(answered, read.BytesRead).ConfigureAwait(false);
        return read;
    }

    public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        await PreciseDelay.Until(Answered(key, read: true)).ConfigureAwait(false);
        return await inner.HeadAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        await PreciseDelay.Until(Answered(key, read: false)).ConfigureAwait(false);
        return await inner.PutAsync(key, content, length, condition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, long maxLength, PutCondition condition, CancellationToken cancellationToken)
    {
        await PreciseDelay.Until(Answered(key, read: false)).ConfigureAwait(false);
        return await inner.PutAsync(key, content, length, maxLength, condition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PutOutcome> PutAsync(string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken)
    {
        await PreciseDelay.Until(Answered(key, read: false)).ConfigureAwait(false);
        return await inner.PutAsync(key, content, condition, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await PreciseDelay.Until(Answered(string.Empty, read: false)).ConfigureAwait(false);
        await inner.DeleteAsync(keys, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await PreciseDelay.Until(Answered(prefix, read: false)).ConfigureAwait(false);
        await foreach (ObjectListing listing in inner.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            yield return listing;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// When the answer to a request about <paramref name="key"/> sent now arrives, a <see cref="Stopwatch"/> timestamp: a
    /// read once its turn among its prefix's reads has come, then the request's wait, the median or a draw of the
    /// log-normal law <see cref="LatencyP99"/> spreads.
    /// </summary>
    internal long Answered(string key, bool read)
    {
        long now = Stopwatch.GetTimestamp();
        long turn = now;
        double wait = Latency.TotalSeconds;
        bool spread = Latency > TimeSpan.Zero && LatencyP99 > Latency;
        bool limited = read && ReadsPerSecondPerPrefix > 0;
        if (spread || limited)
        {
            lock (_draws)
            {
                if (spread)
                {
                    wait *= Math.Exp(Math.Log(LatencyP99 / Latency) / Z99 * Normal());
                }

                if (limited)
                {
                    string prefix = key[..(key.LastIndexOf('/') + 1)];
                    turn = _turns.TryGetValue(prefix, out long free) ? Math.Max(free, now) : now;
                    _turns[prefix] = turn + (long)(Stopwatch.Frequency / ReadsPerSecondPerPrefix);
                }
            }
        }

        return turn + (long)(wait * Stopwatch.Frequency);
    }

    /// <summary>A draw of the standard normal law, by Box and Muller; under <see cref="_draws"/>.</summary>
    private double Normal()
    {
        double u = 1 - _random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * _random.NextDouble());
    }

    /// <summary>
    /// Waits for a read's bytes, its first one arriving at <paramref name="first"/>: their turn on the link, which carries
    /// the reads' bytes one after another at its rate, and their time on the read's connection, whichever ends last.
    /// </summary>
    private Task TransferAsync(long first, int bytes)
    {
        if (bytes <= 0 || (LinkBitsPerSecond <= 0 && BytesPerSecond <= 0))
        {
            return Task.CompletedTask;
        }

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
