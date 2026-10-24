using System;
using System.Linq;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The spans of a tracing table, drawn deterministically from a seed: a dozen facets that repeat, as the
/// time-window design's worst case of many facets holds them, then a route deep under them, and two identifiers
/// and a duration. Facets follow each other as a service's spans do: a service runs in one environment, one
/// namespace and one region, on its own pods and a few hosts; a span's kind decides whether it has an HTTP method
/// and a route, a database system or an RPC system; a client span names the service it calls.
/// </summary>
internal sealed class SpanGenerator
{
    private const int Services = 200;
    private const int RoutesPerService = 20;
    private const int PodsPerService = 5;
    private const int Hosts = 50;

    private static readonly string[] Kinds = ["server", "client", "internal", "producer", "consumer"];
    private static readonly double[] KindSums = Cumulative([0.45, 0.35, 0.12, 0.04, 0.04]);
    private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "DELETE"];
    private static readonly string[] DbSystems = ["postgresql", "redis", "mongodb", "mysql", "elasticsearch", "cassandra"];
    private static readonly string[] Regions = ["eu-west", "us-east", "ap-south", "us-west", "sa-east"];

    private readonly Random _random;
    private readonly double[] _serviceWeights;
    private readonly double[] _routeWeights;

    /// <param name="seed">The seed: the same seed draws the same spans.</param>
    internal SpanGenerator(int seed)
    {
        _random = new Random(seed);
        _serviceWeights = Cumulative([.. Enumerable.Range(1, Services).Select(rank => 1 / Math.Pow(rank, 0.9))]);
        _routeWeights = Cumulative([.. Enumerable.Range(1, RoutesPerService).Select(rank => 1 / Math.Pow(rank, 1.5))]);
    }

    /// <summary>One span at <paramref name="at"/>, its other columns drawn.</summary>
    internal GeneratedSpan Next(DateTimeOffset at)
    {
        int service = Pick(_serviceWeights);
        int kind = Pick(KindSums);
        bool http = kind is 0 or 1 && _random.NextDouble() < 0.8;
        bool database = kind == 1 && !http;
        int route = Pick(_routeWeights);
        bool error = _random.NextDouble() < 0.03;
        return new GeneratedSpan(
            at,
            $"svc-{service:D3}",
            service % 10 == 0 ? "staging" : "prod",
            $"team-{service % 20:D2}",
            Regions[service % Regions.Length],
            Kinds[kind],
            http ? HttpMethods[route % 7 == 0 ? 1 + (route % 3) : 0] : null,
            error ? "error" : "ok",
            http ? (error ? (short)500 : route % 11 == 0 ? (short)404 : (short)200) : (short)0,
            database ? DbSystems[service % DbSystems.Length] : null,
            kind == 1 && !http && !database ? "grpc" : null,
            kind == 1 ? $"svc-{(service * 7 + route) % Services:D3}" : null,
            $"svc-{service:D3}-pod-{_random.Next(PodsPerService)}",
            $"host-{(service * 3 + _random.Next(3)) % Hosts:D2}",
            http ? $"/svc{service}/route{route}" : $"op-{service}-{route % 5}",
            $"{_random.NextInt64():x16}{_random.NextInt64():x16}",
            $"{_random.NextInt64():x16}",
            (long)Math.Exp(12 + (_random.NextDouble() * 6)));
    }

    private int Pick(double[] cumulative)
    {
        int index = Array.BinarySearch(cumulative, _random.NextDouble() * cumulative[^1]);
        return Math.Min(index >= 0 ? index : ~index, cumulative.Length - 1);
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
}

/// <summary>A span, as a tracing table writes it: a dozen facets, a route under them, two identifiers and a duration.</summary>
[VortexRecord]
public partial record struct GeneratedSpan(
    DateTimeOffset At,
    string Service,
    string Environment,
    string Namespace,
    string Region,
    string Kind,
    string? HttpMethod,
    string Status,
    short HttpStatus,
    string? DbSystem,
    string? RpcSystem,
    string? PeerService,
    string Pod,
    string Host,
    string Route,
    string TraceId,
    string SpanId,
    long DurationNs);
