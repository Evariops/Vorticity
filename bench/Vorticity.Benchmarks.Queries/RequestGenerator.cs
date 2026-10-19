using System;
using System.Collections.Generic;
using System.Linq;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The requests the time-window design reckons with, drawn deterministically from a seed: an instant, the
/// service and the endpoint it serves, a method and a status that go together, a region, a version of the
/// service, one of the hosts that serve it, a request's own id, a duration and a weight. The cardinalities
/// are the design's: 600 services, fifty endpoints each, seventeen hosts each, two versions, five regions,
/// eight methods and five statuses in a minute.
/// </summary>
/// <remarks>
/// <para>
/// The columns follow each other as real ones do, which is what the runs of a sorted slice are made of: a
/// service runs in its home region, four services in five only read, one in ten takes health checks and
/// probes, sent from one monitoring region each, a status goes with some methods only (201 with a write,
/// 304 with a read), an endpoint belongs to one service and answers one method, a host serves its service's
/// endpoints, and the current version takes 97 % of a service's rows. Popularity follows Zipf laws, flat
/// over services and steep over a method's endpoints. So drawn, a minute at the design's rate holds the
/// runs the design reckons with, each column within half of its figure (<c>--micro generator</c>).
/// </para>
/// <para>
/// Rows come mostly in time order: a share arrives a few seconds late, and a thin tail up to an hour late,
/// as several writers append batches of a few seconds each.
/// </para>
/// </remarks>
internal sealed class RequestGenerator
{
    private const int Services = 600;
    private const int EndpointsPerService = 50;
    private const int HostsPerService = 17;

    private static readonly string[] Regions = ["eu-west", "us-east", "ap-south", "us-west", "sa-east"];
    private static readonly double[] RegionWeights = [0.40, 0.30, 0.15, 0.10, 0.05];

    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE"];
    private static readonly double[] MethodWeights = [0.75, 0.15, 0.04, 0.03, 0.02, 0.007, 0.0025, 0.0005];

    private readonly Random _random;
    private readonly double[] _serviceWeights;
    private readonly double[] _endpointWeights;
    private readonly int[] _versions;
    private readonly string[] _serviceNames;
    private readonly string[][] _endpointNames;
    private readonly string[][] _versionNames;
    private readonly string[][] _hostNames;
    private long _next;

    /// <param name="seed">The seed: the same seed draws the same rows.</param>
    internal RequestGenerator(int seed)
    {
        _random = new Random(seed);
        _serviceWeights = Cumulative(Zipf(Services, 0.8));
        _endpointWeights = Cumulative(Zipf(EndpointsPerService, 2.0));
        _versions = [.. Enumerable.Range(0, Services).Select(_ => 2)];
        _serviceNames = [.. Enumerable.Range(0, Services).Select(s => $"service-{s:D3}")];
        _endpointNames = [.. Enumerable.Range(0, Services).Select(s => Enumerable.Range(0, EndpointsPerService).Select(e => $"/s{s}/e{e}").ToArray())];
        _versionNames = [.. Enumerable.Range(0, Services).Select(s => Enumerable.Range(0, _versions[s]).Select(v => $"{s}.{v}.0").ToArray())];
        _hostNames = [.. Enumerable.Range(0, Services).Select(s => Enumerable.Range(0, HostsPerService).Select(h => $"host-{s:D3}-{h:D2}").ToArray())];
    }

    /// <summary>One request at <paramref name="at"/>, its other columns drawn.</summary>
    internal GeneratedRequest Next(DateTimeOffset at)
    {
        // A service runs in its home region, which serves nearly all its requests.
        int service = Lower(_serviceWeights, _random.NextDouble());
        int home = HomeOf(service);
        // Four services in five only read: their writes are reads. One in ten takes health checks and probes.
        int method = Pick(MethodWeights);
        method = (service % 5 < 4 && method is >= 1 and <= 4) || (service % 10 != 0 && method >= 5) ? 0 : method;
        string region = method switch
        {
            // The rare methods are health checks and probes, sent from one monitoring region each.
            5 => Regions[1],
            6 => Regions[2],
            7 => Regions[3],
            _ => Regions[home],
        };
        short status = StatusOf(method);

        // An endpoint answers one method: the service's endpoints are shared out among them, the reads taking most.
        (int first, int count) = EndpointsOf(method);
        int endpoint = first + Math.Min(Lower(_endpointWeights, _random.NextDouble()) % count, count - 1);

        // The current version takes most of the rows, the others the rest.
        int version = _random.NextDouble() < 0.97 ? 0 : 1 + _random.Next(_versions[service] - 1);
        int host = _random.Next(HostsPerService);
        long id = _next++;
        double duration = Math.Exp(15 + (_random.NextDouble() * 3));
        return new GeneratedRequest(
            at, _serviceNames[service], _endpointNames[service][endpoint], Methods[method], status, region,
            _versionNames[service][version], _hostNames[service][host], $"req-{id:x12}", (long)duration, _random.NextDouble());
    }

    /// <summary>
    /// The rows of <paramref name="span"/> from <paramref name="start"/>, at <paramref name="perSecond"/>, appended by
    /// <paramref name="writers"/> writers in batches of <paramref name="batch"/> each: most rows in their batch's time,
    /// a share of them seconds late, a thin tail up to an hour late; all in time order when <paramref name="late"/> is
    /// false.
    /// </summary>
    internal IEnumerable<GeneratedRequest[]> Appends(DateTimeOffset start, TimeSpan span, double perSecond, TimeSpan batch, int writers, bool late = true)
    {
        long rows = (long)(span.TotalSeconds * perSecond);
        long perBatch = Math.Max(1, (long)(batch.TotalSeconds * perSecond / writers));
        double step = 1 / perSecond;
        List<GeneratedRequest> pending = [];
        for (long row = 0; row < rows; row++)
        {
            DateTimeOffset at = start.AddSeconds(row * step);
            double lag = late ? _random.NextDouble() : 1;
            at = lag < 0.0001 ? at.AddSeconds(-_random.Next(60, 3_600))
                : lag < 0.02 ? at.AddSeconds(-_random.Next(1, 10))
                : at;
            pending.Add(Next(at < start ? start : at));
            if (pending.Count == perBatch)
            {
                yield return [.. pending];
                pending.Clear();
            }
        }

        if (pending.Count > 0)
        {
            yield return [.. pending];
        }
    }

    /// <summary>
    /// The runs each synopsis column holds in the rows, sorted by the design's order (status, region, method, service,
    /// version, endpoint, host): the distinct combinations of the first k columns, k from one to seven.
    /// </summary>
    internal static long[] RunsByDepth(IReadOnlyList<GeneratedRequest> rows)
    {
        HashSet<string>[] seen = [.. Enumerable.Range(0, 7).Select(_ => new HashSet<string>(StringComparer.Ordinal))];
        System.Text.StringBuilder prefix = new System.Text.StringBuilder(128);
        foreach (GeneratedRequest row in rows)
        {
            prefix.Clear();
            string[] parts = [row.Status.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Region, row.Method, row.Service, row.Version, row.Endpoint, row.Host];
            for (int k = 0; k < parts.Length; k++)
            {
                prefix.Append(parts[k]).Append('\u0001');
                seen[k].Add(prefix.ToString());
            }
        }

        return [.. seen.Select(s => (long)s.Count)];
    }

    /// <summary>
    /// <c>--micro generator</c>: a minute at the design's rate, 5 × 10⁴ rows a second, and the runs of each
    /// synopsis column against the design's figures.
    /// </summary>
    internal static int Run(int seed)
    {
        RequestGenerator generator = new RequestGenerator(seed);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        List<GeneratedRequest> minute = new List<GeneratedRequest>(3_000_000);
        for (int i = 0; i < 3_000_000; i++)
        {
            minute.Add(generator.Next(start.AddTicks(i * 200)));
        }

        long[] runs = RunsByDepth(minute);
        (string Column, double Design)[] expected =
        [
            ("Status", 5), ("Region", 20), ("Method", 45), ("Service", 3e3), ("Version", 5e3), ("Endpoint", 4e4), ("Host", 5e5),
        ];
        Console.WriteLine($"{"column",-10} {"runs",10} {"design",10} {"ratio",8}");
        bool held = true;
        for (int k = 0; k < runs.Length; k++)
        {
            double ratio = runs[k] / expected[k].Design;
            held &= ratio is >= 0.5 and <= 1.5;
            Console.WriteLine($"{expected[k].Column,-10} {runs[k],10} {expected[k].Design,10:G3} {ratio,8:F2}");
        }

        Console.WriteLine(held ? "every column within 50 % of the design" : "a column is past 50 % of the design");
        return held ? 0 : 1;
    }

    /// <summary>The home region of a service, the regions taking services as they take traffic.</summary>
    private static int HomeOf(int service)
    {
        double draw = ((service * 2_654_435_761L) & 0xFFFF) / 65_536.0;
        for (int region = 0; region < RegionWeights.Length; region++)
        {
            draw -= RegionWeights[region];
            if (draw < 0)
            {
                return region;
            }
        }

        return RegionWeights.Length - 1;
    }

    /// <summary>The endpoints of a service that answer a method: thirty reads, ten creations, a few of the rest.</summary>
    private static (int First, int Count) EndpointsOf(int method) => method switch
    {
        0 => (0, 30),
        1 => (30, 10),
        2 => (40, 4),
        3 => (44, 3),
        4 => (47, 1),
        5 => (48, 1),
        _ => (49, 1),
    };

    private short StatusOf(int method)
    {
        double draw = _random.NextDouble();
        return method switch
        {
            0 => draw < 0.88 ? (short)200 : draw < 0.94 ? (short)304 : draw < 0.98 ? (short)404 : (short)500,
            1 => draw < 0.80 ? (short)200 : draw < 0.97 ? (short)201 : (short)500,
            _ => (short)200,
        };
    }

    private int Pick(double[] weights)
    {
        double draw = _random.NextDouble();
        for (int i = 0; i < weights.Length; i++)
        {
            draw -= weights[i];
            if (draw < 0)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    private static double[] Zipf(int count, double exponent)
    {
        double[] weights = new double[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = 1 / Math.Pow(i + 1, exponent);
        }

        double total = weights.Sum();
        return [.. weights.Select(w => w / total)];
    }

    private static double[] Cumulative(double[] weights)
    {
        double[] sums = new double[weights.Length];
        double sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            sum += weights[i];
            sums[i] = sum;
        }

        return sums;
    }

    /// <summary>The first index whose cumulative weight reaches <paramref name="draw"/>.</summary>
    private static int Lower(double[] cumulative, double draw)
    {
        int index = Array.BinarySearch(cumulative, draw);
        return Math.Min(index >= 0 ? index : ~index, cumulative.Length - 1);
    }
}

/// <summary>A request, as the time-window design writes its table.</summary>
[VortexRecord]
public partial record struct GeneratedRequest(
    DateTimeOffset At,
    string Service,
    string Endpoint,
    string Method,
    short Status,
    string Region,
    string Version,
    string Host,
    string RequestId,
    long DurationNs,
    double Weight);
