using System;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The log records the variant design reckons with, drawn deterministically from a seed: a few columns every
/// record has, and attributes whose keys are data. Three sources write them as the design's three rows do: web
/// services (60 % of the rows) send a route, a status code, a user and a duration; databases (25 %) a system, a
/// query and its rows; a mobile application (15 %) a build, its feature flags as a list, a flag of its own on
/// three rows in ten, and on one row in a hundred a score under a key that names its user.
/// </summary>
/// <remarks>
/// One web service of eight sends its status code as text, as a legacy source does: a typed question reads it
/// as null, a converting one parses it. Routes follow a Zipf law, as the requests' endpoints do.
/// </remarks>
internal sealed class LogGenerator
{
    private const int Routes = 40;

    private static readonly string[] Queries = ["SELECT * FROM orders WHERE id = $1", "UPDATE carts SET total = $1", "SELECT count(*) FROM sessions", "INSERT INTO events VALUES ($1, $2)"];

    private readonly Random _random;
    private readonly double[] _routeWeights;
    private readonly string[] _routes;

    /// <param name="seed">The seed: the same seed draws the same records.</param>
    internal LogGenerator(int seed)
    {
        _random = new Random(seed);
        double[] weights = new double[Routes];
        double sum = 0;
        for (int i = 0; i < Routes; i++)
        {
            sum += 1 / Math.Pow(i + 1, 1.2);
            weights[i] = sum;
        }

        for (int i = 0; i < Routes; i++)
        {
            weights[i] /= sum;
        }

        _routeWeights = weights;
        _routes = new string[Routes];
        for (int i = 0; i < Routes; i++)
        {
            _routes[i] = $"/api/r{i}/{{id}}";
        }
    }

    /// <summary>One record at <paramref name="at"/>, its other columns drawn.</summary>
    internal LogRow Next(DateTimeOffset at)
    {
        double source = _random.NextDouble();
        if (source < 0.60)
        {
            int service = _random.Next(8);
            int route = Array.BinarySearch(_routeWeights, _random.NextDouble());
            route = Math.Min(route >= 0 ? route : ~route, Routes - 1);
            double draw = _random.NextDouble();
            long status = draw < 0.90 ? 200 : draw < 0.95 ? 404 : draw < 0.98 ? 500 : 502;
            Variant code = service == 7 ? Variant.From(status.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Variant.From(status);
            Variant attributes = Variant.Object(
            [
                ("http.route", Variant.From(_routes[route])),
                ("http.response.status_code", code),
                ("user.id", Variant.From($"u-{_random.Next(50_000)}")),
                ("http.server.request.duration", Variant.From(Math.Round(_random.NextDouble() * 250, 3))),
            ]);
            return new LogRow(at, $"web-{service}", status >= 500 ? (byte)17 : (byte)9, $"{_random.NextInt64():x16}{_random.NextInt64():x16}", attributes);
        }

        if (source < 0.85)
        {
            Variant attributes = Variant.Object(
            [
                ("db.system", Variant.From("postgresql")),
                ("db.query.text", Variant.From(Queries[_random.Next(Queries.Length)])),
                ("db.response.rows", Variant.From((long)_random.Next(200))),
            ]);
            return new LogRow(at, $"db-{_random.Next(3)}", 9, null, attributes);
        }

        bool flagged = _random.NextDouble() < 0.30;
        bool scored = _random.NextDouble() < 0.01;
        (string, Variant)[] fields = new (string, Variant)[2 + (flagged ? 1 : 0) + (scored ? 1 : 0)];
        int next = 0;
        fields[next++] = ("app.build", Variant.From(4_400L + _random.Next(20)));
        fields[next++] = ("feature.flags", _random.Next(2) == 0 ? Variant.Array([Variant.From("new-cart"), Variant.From("dark")]) : Variant.Array([Variant.From("dark")]));
        if (flagged)
        {
            fields[next++] = ("feature.flag.new-cart", Variant.From(_random.Next(3) == 0 ? "on" : "off"));
        }

        if (scored)
        {
            fields[next] = ($"user.{_random.Next(10_000)}.score", Variant.From(Math.Round(_random.NextDouble() * 100, 1)));
        }

        return new LogRow(at, "mobile", 9, null, Variant.Object(fields));
    }
}

/// <summary>A log record, as the variant design writes its table: its body left out, which no question reads.</summary>
[VortexRecord]
public partial record struct LogRow(DateTimeOffset At, string ServiceName, byte SeverityNumber, string? TraceId, Variant Attributes);
