using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The log records of a client's day, drawn deterministically from a seed: some forty sources, each with attributes of
/// its own, keys that are data, a few thousand to some ten thousand of them a day, and paths whose type changes from a
/// source to the next (<c>--micro logs</c> counts them). The record is <see cref="LogGenerator"/>'s, so that the same
/// questions read both.
/// </summary>
/// <remarks>
/// <para>
/// The sources are web services (half the rows, 24 of them), databases (a fifth, 6), mobile builds (a tenth, 4), batch
/// jobs (a tenth, 6) and edge proxies (a tenth, 4); popularity among each kind's sources follows a Zipf law. A web service
/// sends a route among its own, a status code, a method, a user, a duration, and the headers of its request under keys
/// that name them; one in four sends its status code as text and its duration as milliseconds in text, as a legacy
/// source does. A database sends its system, a query among its own and its rows; a mobile build its build, its flags
/// under keys that name them, and, on one row in a hundred, a score under a key that names its user; a batch job its
/// job, its step and its records, an integer or a text; an edge its cache keys under keys of their own, a hit or a miss.
/// </para>
/// <para>
/// The keys that are data come from vocabularies of their own, 10⁴ users, 300 flags, 60 headers and 2 000 cache keys,
/// drawn by mild Zipf laws, so that a day holds most of them whatever the bench's rate.
/// </para>
/// </remarks>
internal sealed class LogGeneratorV2
{
    private const int Users = 10_000;
    private const int Flags = 300;
    private const int Headers = 60;
    private const int CacheKeys = 2_000;
    private const int RoutesPerService = 40;
    private const int QueriesPerDatabase = 25;

    private static readonly (string Kind, int Sources, double Share)[] SourceKinds =
        [("web", 24, 0.50), ("db", 6, 0.20), ("mobile", 4, 0.10), ("batch", 6, 0.10), ("edge", 4, 0.10)];

    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE", "PATCH"];
    private static readonly double[] MethodSums = Cumulative([0.70, 0.18, 0.06, 0.04, 0.02]);
    private static readonly long[] StatusCodes = [200, 201, 204, 301, 304, 400, 401, 403, 404, 409, 429, 500, 502, 503, 504];
    private static readonly double[] StatusSums = Cumulative([0.80, 0.04, 0.02, 0.005, 0.03, 0.02, 0.01, 0.005, 0.03, 0.005, 0.005, 0.01, 0.005, 0.01, 0.005]);
    private static readonly string[] DbSystems = ["postgresql", "mysql", "redis", "mongodb", "cassandra", "elasticsearch"];

    private static readonly double[] KindSums = Cumulative([.. SourceKinds.Select(k => k.Share)]);
    private static readonly double[][] SourceSums = [.. SourceKinds.Select(k => Cumulative(Zipf(k.Sources, 0.8)))];
    private static readonly string[][] SourceNames = [.. SourceKinds.Select(k => Enumerable.Range(0, k.Sources).Select(s => $"{k.Kind}-{s:D2}").ToArray())];
    private static readonly double[] RouteSums = Cumulative(Zipf(RoutesPerService, 1.0));
    private static readonly double[] QuerySums = Cumulative(Zipf(QueriesPerDatabase, 1.0));
    private static readonly double[] UserSums = Cumulative(Zipf(Users, 0.5));
    private static readonly double[] FlagSums = Cumulative(Zipf(Flags, 0.7));
    private static readonly double[] HeaderSums = Cumulative(Zipf(Headers, 0.8));
    private static readonly double[] CacheSums = Cumulative(Zipf(CacheKeys, 0.6));

    private static readonly string[][] Routes =
        [.. Enumerable.Range(0, 24).Select(s => Enumerable.Range(0, RoutesPerService).Select(r => $"/api/w{s}/r{r}/{{id}}").ToArray())];
    private static readonly string[][] Queries =
        [.. Enumerable.Range(0, 6).Select(d => Enumerable.Range(0, QueriesPerDatabase).Select(q => $"SELECT c{q} FROM t{d}_{q % 7} WHERE k = $1").ToArray())];
    private static readonly string[] UserKeys = [.. Enumerable.Range(0, Users).Select(u => $"user.{u}.score")];
    private static readonly string[] UserIds = [.. Enumerable.Range(0, Users).Select(u => $"u-{u}")];
    private static readonly string[] FlagKeys = [.. Enumerable.Range(0, Flags).Select(f => f == 0 ? "feature.flag.new-cart" : $"feature.flag.f{f:D3}")];
    private static readonly string[] HeaderKeys = [.. Enumerable.Range(0, Headers).Select(h => $"http.request.header.x-h{h:D2}")];
    private static readonly string[] CacheKeyNames = [.. Enumerable.Range(0, CacheKeys).Select(c => $"cache.k{c:D4}.hit")];

    private readonly Random _random;

    /// <param name="seed">The seed: the same seed draws the same records.</param>
    internal LogGeneratorV2(int seed) => _random = new Random(seed);

    /// <summary>One record at <paramref name="at"/>, its other columns drawn.</summary>
    internal LogRow Next(DateTimeOffset at)
    {
        int kind = Pick(KindSums);
        int source = Pick(SourceSums[kind]);
        string name = SourceNames[kind][source];
        List<(string, Variant)> fields = [];
        byte severity = 9;
        string? trace = null;
        switch (kind)
        {
            case 0:
            {
                // A web service: one in four a legacy source, its status code and its duration as text.
                bool legacy = source % 4 == 3;
                long status = StatusCodes[Pick(StatusSums)];
                double duration = Math.Round(Math.Exp(1 + (_random.NextDouble() * 5)), 3);
                fields.Add(("http.route", Variant.From(Routes[source][Pick(RouteSums)])));
                fields.Add(("http.request.method", Variant.From(Methods[Pick(MethodSums)])));
                fields.Add(("http.response.status_code", legacy ? Variant.From(status.ToString(CultureInfo.InvariantCulture)) : Variant.From(status)));
                fields.Add(("http.server.request.duration", legacy ? Variant.From(string.Create(CultureInfo.InvariantCulture, $"{duration}ms")) : Variant.From(duration)));
                fields.Add(("user.id", Variant.From(UserIds[Pick(UserSums)])));
                for (int h = _random.Next(3); h > 0; h--)
                {
                    fields.Add((HeaderKeys[Pick(HeaderSums)], Variant.From(_random.Next(2) == 0 ? "1" : "0")));
                }

                severity = status >= 500 ? (byte)17 : status >= 400 ? (byte)13 : (byte)9;
                trace = $"{_random.NextInt64():x16}{_random.NextInt64():x16}";
                break;
            }

            case 1:
                fields.Add(("db.system", Variant.From(DbSystems[source])));
                fields.Add(("db.query.text", Variant.From(Queries[source][Pick(QuerySums)])));
                fields.Add(("db.response.rows", Variant.From((long)_random.Next(500))));
                break;

            case 2:
            {
                fields.Add(("app.build", Variant.From(4_400L + (source * 100) + _random.Next(20))));
                for (int f = 1 + _random.Next(3); f > 0; f--)
                {
                    fields.Add((FlagKeys[Pick(FlagSums)], Variant.From(_random.Next(3) == 0 ? "on" : "off")));
                }

                if (_random.NextDouble() < 0.01)
                {
                    int user = Pick(UserSums);
                    fields.Add((UserKeys[user], Variant.From(Math.Round(_random.NextDouble() * 100, 1))));
                }

                break;
            }

            case 3:
            {
                // A batch job counts its records as an integer, or, from its older steps, as text.
                long records = _random.Next(100_000);
                fields.Add(("job.name", Variant.From($"job-{source}")));
                fields.Add(("job.step", Variant.From((long)_random.Next(12))));
                fields.Add(("job.records", _random.Next(5) == 0 ? Variant.From(records.ToString(CultureInfo.InvariantCulture)) : Variant.From(records)));
                severity = _random.NextDouble() < 0.02 ? (byte)17 : (byte)9;
                break;
            }

            default:
                for (int c = 1 + _random.Next(2); c > 0; c--)
                {
                    fields.Add((CacheKeyNames[Pick(CacheSums)], Variant.From(_random.Next(4) != 0)));
                }

                fields.Add(("edge.pop", Variant.From($"pop-{source}")));
                break;
        }

        return new LogRow(at, name, severity, trace, Variant.Object(Distinct(fields)));
    }

    /// <summary>
    /// <c>--micro logs</c>: <paramref name="rows"/> records spread over a day, and what their attributes hold: the sources,
    /// the keys, those that hold more than one type, and the keys of each source.
    /// </summary>
    internal static int Run(int seed, int rows)
    {
        LogGeneratorV2 generator = new LogGeneratorV2(seed);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        long step = TimeSpan.TicksPerDay / rows;
        HashSet<string> sources = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> kinds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> keysBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (int i = 0; i < rows; i++)
        {
            LogRow row = generator.Next(start.AddTicks(i * step));
            sources.Add(row.ServiceName);
            if (!keysBySource.TryGetValue(row.ServiceName, out HashSet<string>? own))
            {
                keysBySource[row.ServiceName] = own = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (string key in row.Attributes.GetKeys())
            {
                if (!kinds.TryGetValue(key, out HashSet<string>? classes))
                {
                    kinds[key] = classes = new HashSet<string>(StringComparer.Ordinal);
                }

                classes.Add(ClassOf(row.Attributes[key]!.Value.Kind));
                own.Add(key);
            }
        }

        int mixed = kinds.Count(k => k.Value.Count > 1);
        Console.WriteLine($"{rows:N0} log records over a day");
        Console.WriteLine($"{sources.Count} sources; {kinds.Count:N0} keys, {mixed} of them of more than one type: {string.Join(", ", kinds.Where(k => k.Value.Count > 1).Select(k => k.Key))}");
        Console.WriteLine($"keys a source: {keysBySource.Values.Min(k => k.Count)} to {keysBySource.Values.Max(k => k.Count)}");
        bool held = sources.Count is >= 20 and <= 80 && kinds.Count is >= 1_000 and <= 100_000 && mixed >= 2;
        Console.WriteLine(held ? "tens of sources, 10^3 to 10^5 keys, and types that change" : "the records miss their figures");
        return held ? 0 : 1;
    }

    /// <summary>The class a question reads a value at: an integer of any width, a float of any width, a text, a boolean.</summary>
    private static string ClassOf(VariantKind kind) => kind switch
    {
        VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => "integer",
        VariantKind.Float or VariantKind.Double => "double",
        VariantKind.String => "string",
        VariantKind.Bool => "bool",
        _ => kind.ToString(),
    };

    /// <summary>The fields, a key drawn twice kept once: a variant object names each key once.</summary>
    private static (string, Variant)[] Distinct(List<(string Key, Variant Value)> fields)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. fields.Where(f => seen.Add(f.Key))];
    }

    private int Pick(double[] cumulative)
    {
        int index = Array.BinarySearch(cumulative, _random.NextDouble() * cumulative[^1]);
        return Math.Min(index >= 0 ? index : ~index, cumulative.Length - 1);
    }

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
