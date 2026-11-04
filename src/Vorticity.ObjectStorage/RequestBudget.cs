using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage;

/// <summary>
/// What the stores of a process send at most: requests in flight, and requests a second. Every store whose transport names
/// the budget shares it (<see cref="StoreTransportOptions.RequestBudget"/>), the process's own by default
/// (<see cref="Process"/>), so that the questions and the jobs of every session, sent at once, stay under what a service
/// sustains on a prefix: S3 serves at least 5 500 reads a second on one, and splits it only under a load that lasts.
/// </summary>
/// <remarks>
/// <para>
/// A request waits for a place in flight, then for its turn in the second, a bucket of a tenth of a second's requests
/// letting a burst through at once.
/// </para>
/// <para>
/// A throttle slows the store it came from, under the budget, and not the process's other stores, whose services may not
/// be the one that throttles: each store sends at most the budget's rate, which a throttle halves, once per episode, as a
/// congestion window halves once per round trip. Its bucket empties then, so that the requests the throttles send again
/// wait for their turns at the new rate. Once the service has been calm for a second, the store's rate grows back by a
/// sixteenth of the budget's a second, until the next throttle: it goes up and down around what the service sustains.
/// A store also bounds its connections (<see cref="StoreTransportOptions.MaxConnections"/>), which a throttle halves too.
/// </para>
/// </remarks>
public sealed class RequestBudget
{
    /// <summary>The requests in flight at most by default, every store of the process together.</summary>
    public const int DefaultMaxInFlight = 256;

    /// <summary>The requests a second at most by default, every store of the process together.</summary>
    public const double DefaultRequestsPerSecond = 2_000;

    /// <summary>The share of a second's requests the bucket holds, which a burst takes at once.</summary>
    private const double BurstSeconds = 0.1;

    /// <summary>The rate's floor, a share of its ceiling: halvings stop there.</summary>
    private const double FloorShare = 1.0 / 64;

    /// <summary>The share of its ceiling the rate grows back by, each second once calm.</summary>
    private const double StepShare = 1.0 / 16;

    /// <summary>
    /// How long a store's rate stays down after a throttle before it grows again: a second, where a store's connections wait
    /// ten (<see cref="ConcurrencyLimit.Calm"/>), since a rate that throttles coming together drop fast to its floor would
    /// otherwise read at its floor for ten seconds.
    /// </summary>
    private static readonly TimeSpan Calm = TimeSpan.FromSeconds(1);

    private readonly ConcurrencyLimit? _inFlight;
    private readonly bool _adapts;
    private readonly TimeProvider _time;
    private readonly object _gate = new object();
    private double _rate;
    private double _tokens;
    private long _refilled;
    private int _episode;
    private DateTimeOffset _calmFrom;
    private DateTimeOffset _grown;

    /// <summary>A budget of <paramref name="maxInFlight"/> requests at once and <paramref name="requestsPerSecond"/> a second.</summary>
    /// <param name="maxInFlight">The requests in flight at most.</param>
    /// <param name="requestsPerSecond">The requests a second at most.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive, or the rate is not finite.</exception>
    public RequestBudget(int maxInFlight = DefaultMaxInFlight, double requestsPerSecond = DefaultRequestsPerSecond)
        : this(maxInFlight, requestsPerSecond, TimeProvider.System, adapts: false)
    {
    }

    private RequestBudget(int maxInFlight, double requestsPerSecond, TimeProvider time, bool adapts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInFlight);
        if (!(requestsPerSecond > 0) || double.IsInfinity(requestsPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerSecond), requestsPerSecond, "A positive, finite rate was expected.");
        }

        MaxInFlight = maxInFlight;
        RequestsPerSecond = requestsPerSecond;
        _time = time;
        _adapts = adapts;
        _inFlight = maxInFlight == int.MaxValue ? null : new ConcurrencyLimit(maxInFlight, time);
        _rate = requestsPerSecond;
        _tokens = Capacity;
        _refilled = time.GetTimestamp();
    }

    /// <summary>The process's budget, which every store shares unless its transport names another.</summary>
    public static RequestBudget Process { get; } = new RequestBudget();

    /// <summary>The requests in flight at most.</summary>
    public int MaxInFlight { get; }

    /// <summary>The requests a second at most.</summary>
    public double RequestsPerSecond { get; }

    /// <summary>The requests a second the budget lets through now: its ceiling, or a store's less after its service throttled.</summary>
    internal double CurrentRequestsPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _rate;
            }
        }
    }

    /// <summary>The episode of throttling the rate is in, which a request notes once it enters.</summary>
    internal int Episode => Volatile.Read(ref _episode);

    /// <summary>The bucket's size: a tenth of a second's requests at the rate, one at least. Under the gate.</summary>
    private double Capacity => Math.Max(1, _rate * BurstSeconds);

    /// <summary>
    /// A store's own rate under a budget of <paramref name="requestsPerSecond"/>: no bound in flight, which the store's
    /// connections and the budget hold, and a rate a throttle halves.
    /// </summary>
    internal static RequestBudget ForStore(double requestsPerSecond, TimeProvider time) =>
        new RequestBudget(int.MaxValue, requestsPerSecond, time, adapts: true);

    /// <summary>Takes a place in flight, then a turn in the second, waiting for each in order.</summary>
    internal async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        if (_inFlight is not null)
        {
            await _inFlight.EnterAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_gate)
                {
                    Refill();
                    if (_tokens >= 1)
                    {
                        _tokens--;
                        return;
                    }

                    wait = TimeSpan.FromSeconds((1 - _tokens) / _rate);
                }

                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _inFlight?.Exit();
            throw;
        }
    }

    /// <summary>Gives a place in flight back.</summary>
    internal void Exit() => _inFlight?.Exit();

    /// <summary>
    /// The service throttled a request that entered in <paramref name="episode"/>: a store's rate halves, down to a
    /// sixty-fourth of its ceiling, and stays down until the service has been calm a while, unless it already halved since
    /// the request entered. A budget the stores share does not move.
    /// </summary>
    internal void Throttled(int episode)
    {
        if (!_adapts)
        {
            return;
        }

        lock (_gate)
        {
            if (episode != _episode)
            {
                return;
            }

            _episode++;
            _rate = Math.Max(RequestsPerSecond * FloorShare, _rate / 2);
            _tokens = 0;
            _refilled = _time.GetTimestamp();
            _calmFrom = _time.GetUtcNow() + Calm;
            _grown = _calmFrom;
        }
    }

    /// <summary>A request succeeded: once calm, a store's rate grows by a sixteenth of its ceiling a second, up to it.</summary>
    internal void Succeeded()
    {
        if (!_adapts || Volatile.Read(ref _episode) == 0)
        {
            return;
        }

        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_rate < RequestsPerSecond && now >= _calmFrom && now - _grown >= TimeSpan.FromSeconds(1))
            {
                Refill();
                _rate = Math.Min(RequestsPerSecond, _rate + (RequestsPerSecond * StepShare));
                _grown = now;
            }
        }
    }

    /// <summary>Adds the requests the rate gave since the last refill, up to the bucket's size. Under the gate.</summary>
    private void Refill()
    {
        long now = _time.GetTimestamp();
        double seconds = (now - _refilled) / (double)_time.TimestampFrequency;
        _refilled = now;
        _tokens = Math.Min(Capacity, _tokens + (seconds * _rate));
    }
}
