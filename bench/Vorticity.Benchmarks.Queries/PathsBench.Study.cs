using System;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

internal static partial class PathsBench
{
    /// <summary>A user the client's log records name, of middling popularity.</summary>
    private const string StudyUser = "u-4321";

    /// <summary>
    /// The questions of the design's table of what questions cost (<c>docs/design/23-query-architecture.md</c>, section 11)
    /// that one of the bench's tables answers (<c>--study</c>): over the requests, the design's reference table, "30
    /// days" the whole store and "7 days" its last week; over the client's log records and spans, "a day" their last day
    /// and "7 days" their last week, each store's newest hour as now. A window longer than its store reads the whole of it.
    /// </summary>
    private static Question[] StudyQuestions(Settings settings, string requestNeedle)
    {
        DateTimeOffset start = QuestionBench.Start;
        DateTimeOffset requestsEnd = start.AddHours(settings.Hours);
        DateTimeOffset requestsWeek = Later(start, requestsEnd.AddDays(-7));
        DateTimeOffset end = start.AddHours(settings.QpHours);
        DateTimeOffset week = Later(start, end.AddDays(-7));
        DateTimeOffset day = Later(start, end.AddDays(-1));
        return
        [
            Asked<GeneratedRequest>("requests", "T1 an emitter's errors per hour", scan => scan.Where(r => r.Service == "service-000" && r.Host == "host-000-00" && r.Status >= 500).GroupBy(r => r.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "T2 5xx per endpoint, all services", scan => scan.Where(r => r.Status >= 500).GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "T3 5xx per endpoint and hour, 7 days", scan => scan.Where(r => r.Status >= 500 && r.At >= requestsWeek).GroupBy(r => (r.Endpoint, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Endpoint, g.Key.Item2, g.Count()))),
            Rows<GeneratedRequest>("requests", "T4 the rows of one request id (needle)", scan => scan.Where(r => r.RequestId == requestNeedle)),
            new Question("requests", "T5 the 100 slowest requests", false, (dataset, options) =>
            {
                Scan<SlowRequest> slowest = dataset.Scan<GeneratedRequest>().With(options).OrderByDescending(r => r.DurationNs)
                    .Select(r => (r.At, r.Service, r.DurationNs, r.RequestId)).Take(100).As<SlowRequest>();
                return new Asking(slowest.ToBatchesAsync().GetAsyncEnumerator(), () => slowest.Metrics);
            }),
            Asked<GeneratedRequest>("requests", "T6 p99 of duration per service and hour, 7 days, within 5.9 %", scan => scan.Where(r => r.At >= requestsWeek).GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Quantile(r => r.DurationNs, 0.99, 0.059)))),
            Asked<GeneratedRequest>("requests", "T7 the same within 1 %", scan => scan.Where(r => r.At >= requestsWeek).GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Quantile(r => r.DurationNs, 0.99)))),
            Asked<GeneratedRequest>("requests", "T8 the 100 busiest endpoints", scan => scan.GroupBy(r => r.Endpoint).OrderByDescending(g => g.Count()).Take(100).Select(g => (g.Key, g.Count()))),
            Rows<LogRow>("logs2", "T9 the rows of one user.id", scan => scan.Where(r => r.Attributes["user.id"].AsString() == StudyUser)),
            Asked<LogRow>("logs2", "T10 one route under 5xx, per service, 7 days", scan => scan.Where(r => r.Attributes["http.route"].AsString() == "/api/w0/r0/{id}" && r.Attributes["http.response.status_code"].AsInt64() >= 500 && r.At >= week).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
            Asked<LogRow>("logs2", "T11 per route under 5xx, all services, a day", scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500 && r.At >= day).GroupBy(r => r.Attributes["http.route"].AsString()).Select(g => (g.Key, g.Count()))),
            Asked<LogRow>("logs2", "T12 a rare path per service, 7 days", scan => scan.Where(r => r.Attributes["feature.flag.new-cart"].Exists && r.At >= week).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
            Asked<LogRow>("logs2", "T13 a count per user.id, a day", scan => scan.Where(r => r.At >= day).GroupBy(r => r.Attributes["user.id"].AsString()).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedSpanV2>("spans", "T14 one route under 5xx, per host", scan => scan.Where(s => s.Route == "/api/s0/r0" && s.HttpStatus >= 500).GroupBy(s => s.Host).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedSpanV2>("spans", "T15 a count per trace, a day (high cardinality)", scan => scan.Where(s => s.At >= day).GroupBy(s => s.TraceId).Select(g => (g.Key, g.Count()))),
        ];

        static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
    }
}

/// <summary>A request of the slowest, as <c>--study</c> reads them.</summary>
[VortexRecord]
public partial record struct SlowRequest(DateTimeOffset At, string Service, long DurationNs, string RequestId);
