using System;
using System.Threading;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// What a store has measured of its link: the first-byte latency of its recent requests, from which a
/// hedge's delay is a percentile, and the rate its bodies are delivered at, from which a window of reads
/// in flight is sized.
/// </summary>
/// <remarks>
/// The delivery rate is counted over periods of a quarter of a second, all bodies together, and the
/// link's rate is the best of the last sixteen periods: a period the store spent idle says nothing of
/// the link, and the best one ignores it, as a congestion controller's bandwidth filter does.
/// </remarks>
internal sealed class LinkStatistics(TimeProvider time)
{
    private const int Samples = 1024;
    private const int Refresh = 64;
    private const int Periods = 16;
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new object();
    private readonly double[] _firstByte = new double[Samples];
    private readonly double[] _sorted = new double[Samples];
    private readonly double[] _rates = new double[Periods];
    private int _count;
    private int _next;
    private int _sortedCount;
    private int _sinceSort;
    private int _nextRate;
    private long _periodStart = time.GetTimestamp();
    private long _periodBytes;

    /// <summary>The first-byte latencies recorded, at most the last 1 024.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The link's delivery rate, the best of the last sixteen periods, in bytes a second; 0 before a period has ended.</summary>
    internal double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                double best = 0;
                foreach (double rate in _rates)
                {
                    best = Math.Max(best, rate);
                }

                return best;
            }
        }
    }

    /// <summary>
    /// The bytes the link holds in flight, its delivery rate times its median first-byte latency; 0
    /// before it has measured both.
    /// </summary>
    internal long BandwidthDelayBytes
    {
        get
        {
            double rate = BytesPerSecond;
            return rate > 0 && Percentile(50, minimum: 8) is { } latency ? (long)(rate * latency.TotalSeconds) : 0;
        }
    }

    /// <summary>Records one request's first-byte latency.</summary>
    internal void FirstByte(TimeSpan latency)
    {
        lock (_gate)
        {
            _firstByte[_next] = latency.TotalMilliseconds;
            _next = (_next + 1) % Samples;
            _count = Math.Min(_count + 1, Samples);
            _sinceSort++;
        }
    }

    /// <summary>Records bytes of a body as they arrive, closing the period once it has lasted its quarter of a second.</summary>
    internal void Delivered(int bytes)
    {
        Interlocked.Add(ref _periodBytes, bytes);
        long now = time.GetTimestamp();
        if (time.GetElapsedTime(Volatile.Read(ref _periodStart), now) < Period)
        {
            return;
        }

        lock (_gate)
        {
            TimeSpan elapsed = time.GetElapsedTime(_periodStart, now);
            if (elapsed < Period)
            {
                // Another body closed it meanwhile.
                return;
            }

            _rates[_nextRate] = Interlocked.Exchange(ref _periodBytes, 0) / elapsed.TotalSeconds;
            _nextRate = (_nextRate + 1) % Periods;
            Volatile.Write(ref _periodStart, now);
        }
    }

    /// <summary>
    /// The <paramref name="percentile"/>-th first-byte latency, sorted again every 64 samples and at each
    /// sample before them; null before <paramref name="minimum"/> samples.
    /// </summary>
    internal TimeSpan? Percentile(double percentile, int minimum = Refresh)
    {
        lock (_gate)
        {
            if (_count < minimum || _count == 0)
            {
                return null;
            }

            if (_sinceSort >= Refresh || (_sortedCount < Refresh && _sortedCount != _count))
            {
                Array.Copy(_firstByte, _sorted, _count);
                Array.Sort(_sorted, 0, _count);
                _sortedCount = _count;
                _sinceSort = 0;
            }

            int at = (int)Math.Clamp(Math.Ceiling(percentile / 100 * _sortedCount) - 1, 0, _sortedCount - 1);
            return TimeSpan.FromMilliseconds(_sorted[at]);
        }
    }
}
