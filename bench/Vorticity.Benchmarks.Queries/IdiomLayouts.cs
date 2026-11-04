using System;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The layouts the design recommends for the bench's tables (<c>--idiom</c>), its idiom of a coarse grain as the
/// partition, then the entity, a fine grain, the low-cardinality columns and the instant: each table partitioned by day,
/// sorted by its service first, tiered, and consolidated once a day is calm by the data's own clock
/// (<see cref="DataClock"/>).
/// </summary>
/// <remarks>
/// A column a filter keeps and the entity does not settle comes right after the entity: the region of a request, which is
/// its service's but for the probes of a service in ten, sent from three regions. The grain of time is the shortest window
/// the questions ask, a quarter of an hour for the spans. Stores built before 2026-11-02 hold the earlier keys, the region
/// out of the requests' and the hour in the spans'.
/// </remarks>
internal static class IdiomLayouts
{
    /// <summary>
    /// The requests, the design's reference table: the service, the region, the service's version and host, the hour, the
    /// status and the endpoint, then the instant; the first five indexed.
    /// </summary>
    internal static DatasetLayout Requests { get; } = new DatasetLayout
    {
        Partition = LayoutValue.Column(nameof(GeneratedRequest.At)).Truncate(CalendarUnit.Day),
        SortKey =
        [
            LayoutValue.Column(nameof(GeneratedRequest.Service)),
            LayoutValue.Column(nameof(GeneratedRequest.Region)),
            LayoutValue.Column(nameof(GeneratedRequest.Version)),
            LayoutValue.Column(nameof(GeneratedRequest.Host)),
            LayoutValue.Column(nameof(GeneratedRequest.At)).Truncate(CalendarUnit.Hour),
            LayoutValue.Column(nameof(GeneratedRequest.Status)),
            LayoutValue.Column(nameof(GeneratedRequest.Endpoint)),
            LayoutValue.Column(nameof(GeneratedRequest.At)),
        ],
        IndexedPrefix = 5,
    };

    /// <summary>The client's spans: the service, the quarter of an hour, the span's status, its HTTP status and route, then the instant; the first two indexed.</summary>
    internal static DatasetLayout Spans { get; } = new DatasetLayout
    {
        Partition = LayoutValue.Column(nameof(GeneratedSpanV2.At)).Truncate(CalendarUnit.Day),
        SortKey =
        [
            LayoutValue.Column(nameof(GeneratedSpanV2.Service)),
            LayoutValue.Column(nameof(GeneratedSpanV2.At)).Bucket(TimeSpan.FromMinutes(15)),
            LayoutValue.Column(nameof(GeneratedSpanV2.Status)),
            LayoutValue.Column(nameof(GeneratedSpanV2.HttpStatus)),
            LayoutValue.Column(nameof(GeneratedSpanV2.Route)),
            LayoutValue.Column(nameof(GeneratedSpanV2.At)),
        ],
        IndexedPrefix = 2,
    };

    /// <summary>The client's log records: the service, the hour, the severity, then the instant; the first two indexed.</summary>
    internal static DatasetLayout Logs { get; } = new DatasetLayout
    {
        Partition = LayoutValue.Column(nameof(LogRow.At)).Truncate(CalendarUnit.Day),
        SortKey =
        [
            LayoutValue.Column(nameof(LogRow.ServiceName)),
            LayoutValue.Column(nameof(LogRow.At)).Truncate(CalendarUnit.Hour),
            LayoutValue.Column(nameof(LogRow.SeverityNumber)),
            LayoutValue.Column(nameof(LogRow.At)),
        ],
        IndexedPrefix = 2,
    };

    /// <summary>
    /// A handle's clock that runs with the data rather than the wall: what a build sets to the end of the hour it appended,
    /// so that a day turns calm, and is consolidated, an hour of data after its last rows came, as it would in a day of
    /// ingestion, whatever the minutes the build takes.
    /// </summary>
    internal sealed class DataClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = QuestionBench.Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
