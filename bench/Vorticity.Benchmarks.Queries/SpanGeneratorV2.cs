using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Linq;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The spans of a tracing table at the cardinalities a client's review gives for a day at the design's rate, drawn
/// deterministically from a seed: three statuses, five kinds, five environments, eight HTTP methods, sixty HTTP statuses,
/// some 300 hosts, 600 services, a thousand peers, 1 500 versions, 10⁴ pods, 2 × 10⁴ query fingerprints and 3 × 10⁴
/// routes, with a trace id shared by about ten spans, a span id of its own, an exception group on the errors and a
/// duration (<c>--micro spans</c> holds each column to its figure).
/// </summary>
/// <remarks>
/// <para>
/// The columns follow each other less neatly than <see cref="SpanGenerator"/>'s, which derived a service's environment,
/// region and hosts from its number. A service's spans come from any environment, most from production. Its pods are
/// replicas, more for a busy service, replaced at each of its deploys, one to four a day, each deploy a version of its
/// own; a pod runs on a host its name hashes to, so hosts are shared by services. A client span calls one of the
/// service's eight peers, among the services and some external peers, and a database call runs one of the service's
/// query fingerprints. Popularity follows Zipf laws mild enough that a day at a hundredth of the rate holds nearly every
/// value: what the columns hold then does not depend on the rate a bench runs at.
/// </para>
/// <para>
/// Spans come in time order, each trace's ten or so one after the other, as a collector flushes them.
/// </para>
/// </remarks>
internal sealed class SpanGeneratorV2
{
    private const int Services = 600;
    private const int RoutesPerService = 50;
    private const int FingerprintsPerService = 33;
    private const int PeersPerService = 8;
    private const int ExternalPeers = 400;
    private const int Hosts = 300;
    private const int ExceptionGroups = 500;
    private const int ExceptionsPerService = 6;

    private static readonly string[] Statuses = ["ok", "error", "unset"];
    private static readonly string[] Kinds = ["server", "client", "internal", "producer", "consumer"];
    private static readonly double[] KindSums = Cumulative([0.40, 0.35, 0.15, 0.05, 0.05]);
    private static readonly string[] Environments = ["prod", "staging", "dev", "qa", "perf"];
    private static readonly double[] EnvironmentSums = Cumulative([0.85, 0.08, 0.04, 0.02, 0.01]);
    private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE"];
    private static readonly double[] HttpMethodSums = Cumulative([0.70, 0.18, 0.05, 0.03, 0.02, 0.01, 0.007, 0.003]);

    // Sixty HTTP statuses, the successes first: 200 takes most, the others a Zipf law over the rest.
    private static readonly short[] HttpStatuses =
    [
        200, 201, 204, 304, 302, 404, 400, 401, 403, 500, 503, 502, 504, 429, 409, 422, 301, 206, 202, 307,
        308, 405, 406, 408, 410, 411, 412, 413, 414, 415, 416, 417, 418, 421, 423, 424, 425, 426, 428, 431,
        451, 501, 505, 506, 507, 508, 510, 511, 203, 205, 207, 208, 226, 300, 303, 305, 402, 407, 499, 520,
    ];

    private static readonly double[] HttpStatusSums = Cumulative([0.80, .. Zipf(HttpStatuses.Length - 1, 1.1).Select(w => 0.20 * w)]);

    private static readonly double[] ServiceSums = Cumulative(Zipf(Services, 0.9));
    private static readonly double[] RouteSums = Cumulative(Zipf(RoutesPerService, 1.0));
    private static readonly double[] FingerprintSums = Cumulative(Zipf(FingerprintsPerService, 0.8));
    private static readonly double[] ExceptionSums = Cumulative(Zipf(ExceptionsPerService, 1.0));

    private static readonly string[] ServiceNames = [.. Enumerable.Range(0, Services).Select(s => $"svc-{s:D3}")];
    private static readonly string[] PeerNames =
        [.. ServiceNames, .. Enumerable.Range(0, ExternalPeers).Select(p => $"ext-{p:D3}")];
    private static readonly string[] HostNames = [.. Enumerable.Range(0, Hosts).Select(h => $"node-{h:D3}")];
    private static readonly string[][] RouteNames =
        [.. Enumerable.Range(0, Services).Select(s => Enumerable.Range(0, RoutesPerService).Select(r => $"/api/s{s}/r{r}").ToArray())];
    private static readonly string[][] FingerprintNames =
        [.. Enumerable.Range(0, Services).Select(s => Enumerable.Range(0, FingerprintsPerService).Select(f => Hex64(Mix(s, f, 11))[..12]).ToArray())];
    private static readonly string[] ExceptionNames = [.. Enumerable.Range(0, ExceptionGroups).Select(e => $"Exception{e:D3}")];

    // A service's replicas, more for a busy one: 2 for the least busy, 12 for the busiest.
    private static readonly int[] Replicas = [.. Enumerable.Range(0, Services).Select(s => 2 + (10 * (Services - s) / Services))];

    // A service's deploys a day, one to four, each a version and a new set of pods.
    private static readonly int[] Deploys = [.. Enumerable.Range(0, Services).Select(s => 1 + (int)(Mix(s, 0, 23) % 4))];

    private readonly Random _random;
    private readonly long _seedMix;
    private long _span;
    private long _trace;

    /// <param name="seed">The seed: the same seed draws the same spans.</param>
    /// <param name="firstSpan">The number of the first span, so that generators of several hours draw ids apart.</param>
    internal SpanGeneratorV2(int seed, long firstSpan = 0)
    {
        _random = new Random(seed);
        _seedMix = seed;
        _span = firstSpan;
        _trace = firstSpan;
    }

    /// <summary>One span at <paramref name="at"/>, its other columns drawn.</summary>
    internal GeneratedSpanV2 Next(DateTimeOffset at)
    {
        // A trace runs on for about ten spans.
        if (_random.NextDouble() < 0.1)
        {
            _trace++;
        }

        int service = Pick(ServiceSums);
        int kind = Pick(KindSums);
        string environment = Environments[Pick(EnvironmentSums)];

        // The deploy of the day the span falls in: a generation of pods, and its version.
        double day = (at.TimeOfDay.TotalHours + (Mix(service, 1, 29) % 24)) / 24 % 1;
        int generation = (int)(day * Deploys[service]);
        int replica = _random.Next(Replicas[service]);
        string pod = $"{ServiceNames[service]}-{Hex64(Mix(service, generation, 31))[..5]}-{Hex64(Mix(service, (generation * 64) + replica, 37))[..5]}";
        string host = HostNames[(int)(Mix(service, (generation * 64) + replica, 41) % Hosts)];
        string version = $"{1 + (Mix(service, generation, 43) % 9)}.{Mix(service, generation, 47) % 100}.{Mix(service, generation, 67) % 100}";

        bool http = kind is 0 or 1 && _random.NextDouble() < 0.75;
        bool database = kind == 1 && !http && _random.NextDouble() < 0.7;
        string route = RouteNames[service][Pick(RouteSums)];
        string? method = http ? HttpMethods[Pick(HttpMethodSums)] : null;
        short status = http ? HttpStatuses[Pick(HttpStatusSums)] : (short)0;
        bool error = status >= 500 || (!http && _random.NextDouble() < 0.02);
        string state = error ? "error" : kind == 2 && _random.NextDouble() < 0.7 ? "unset" : "ok";
        string? peer = kind == 1 ? PeerNames[(int)(Mix(service, _random.Next(PeersPerService), 53) % (ulong)PeerNames.Length)] : null;
        string? fingerprint = database ? FingerprintNames[service][Pick(FingerprintSums)] : null;
        string? exception = error ? ExceptionNames[(int)(Mix(service, Pick(ExceptionSums), 59) % ExceptionGroups)] : null;
        long span = _span++;
        double duration = Math.Exp((kind == 2 ? 9 : 11) + (_random.NextDouble() * 6));
        return new GeneratedSpanV2(
            at, state, Kinds[kind], environment, method, status, host, ServiceNames[service], peer, version, pod, fingerprint, route,
            exception, TraceIdOf(_trace ^ (_seedMix << 40)), SpanIdOf(span ^ (_seedMix << 40)), (long)duration);
    }

    /// <summary>The 32 hexadecimal digits of trace <paramref name="trace"/>: its number's XXH3-128.</summary>
    internal static string TraceIdOf(long trace)
    {
        Span<byte> number = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(number, trace);
        return XxHash128.HashToUInt128(number).ToString("x32", CultureInfo.InvariantCulture);
    }

    /// <summary>The 16 hexadecimal digits of span <paramref name="span"/>: its number's XXH3-64.</summary>
    internal static string SpanIdOf(long span) => Hex64(Mix(span, 0, 61));

    /// <summary>
    /// <c>--micro spans</c>: <paramref name="rows"/> spans spread over a day, and the values each column holds against the
    /// client's figures for a day at the design's rate, within half of them; and the spans of a trace.
    /// </summary>
    internal static int Run(int seed, int rows)
    {
        SpanGeneratorV2 generator = new SpanGeneratorV2(seed);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        long step = TimeSpan.TicksPerDay / rows;
        string[] columns = ["Status", "Kind", "Environment", "HttpMethod", "HttpStatus", "Host", "Service", "PeerService", "Version", "Pod", "DbQueryFingerprint", "Route", "ExceptionGroup"];
        double[] figures = [3, 5, 5, 8, 60, 300, 600, 1e3, 1.5e3, 1e4, 2e4, 3e4, 500];
        HashSet<string>[] seen = [.. columns.Select(_ => new HashSet<string>(StringComparer.Ordinal))];
        HashSet<string> traces = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> spans = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rows; i++)
        {
            GeneratedSpanV2 s = generator.Next(start.AddTicks(i * step));
            string?[] values =
            [
                s.Status, s.Kind, s.Environment, s.HttpMethod, s.HttpStatus == 0 ? null : s.HttpStatus.ToString(CultureInfo.InvariantCulture), s.Host,
                s.Service, s.PeerService, s.Version, s.Pod, s.DbQueryFingerprint, s.Route, s.ExceptionGroup,
            ];
            for (int c = 0; c < values.Length; c++)
            {
                if (values[c] is { } value)
                {
                    seen[c].Add(value);
                }
            }

            traces.Add(s.TraceId);
            spans.Add(s.SpanId);
        }

        Console.WriteLine($"{rows:N0} spans over a day; the values of each column against the figures of a day at the design's rate");
        Console.WriteLine($"{"column",-20} {"values",10} {"figure",10} {"ratio",8}");
        bool held = true;
        for (int c = 0; c < columns.Length; c++)
        {
            double ratio = seen[c].Count / figures[c];
            held &= ratio is >= 0.5 and <= 1.5;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{columns[c],-20} {seen[c].Count,10} {figures[c],10:G3} {ratio,8:F2}"));
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{traces.Count:N0} traces, {rows / (double)traces.Count:F1} spans each; {spans.Count:N0} span ids for {rows:N0} spans"));
        held &= spans.Count == rows && rows / (double)traces.Count is >= 8 and <= 12;
        Console.WriteLine(held ? "every column within 50 % of its figure" : "a column is past 50 % of its figure");
        return held ? 0 : 1;
    }

    private int Pick(double[] cumulative)
    {
        int index = Array.BinarySearch(cumulative, _random.NextDouble() * cumulative[^1]);
        return Math.Min(index >= 0 ? index : ~index, cumulative.Length - 1);
    }

    /// <summary>A number of its own for (<paramref name="a"/>, <paramref name="b"/>), salted: what fixes a service's names and choices.</summary>
    private static ulong Mix(long a, long b, int salt)
    {
        Span<byte> bytes = stackalloc byte[2 * sizeof(long) + sizeof(int)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, a);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[sizeof(long)..], b);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[(2 * sizeof(long))..], salt);
        return XxHash3.HashToUInt64(bytes);
    }

    private static string Hex64(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    private static double[] Zipf(int count, double exponent)
    {
        double[] weights = [.. Enumerable.Range(1, count).Select(rank => 1 / Math.Pow(rank, exponent))];
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
}

/// <summary>
/// A span of the client's tracing table: three facets of few values, the service and what it ran on and called, the
/// route, two identifiers, an exception group on the errors and a duration.
/// </summary>
[VortexRecord]
public partial record struct GeneratedSpanV2(
    DateTimeOffset At,
    string Status,
    string Kind,
    string Environment,
    string? HttpMethod,
    short HttpStatus,
    string Host,
    string Service,
    string? PeerService,
    string Version,
    string Pod,
    string? DbQueryFingerprint,
    string Route,
    string? ExceptionGroup,
    string TraceId,
    string SpanId,
    long DurationNs);
