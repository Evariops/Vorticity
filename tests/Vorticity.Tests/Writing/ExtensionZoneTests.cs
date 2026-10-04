// The zone maps of an extension column: an instant's and a date's bounds are their storage's
// integers, under the column's own type as the reference writes them, so that a range of instants
// prunes by the zone maps as a range of integers does -- on a file written once, and on one written
// in two halves, the old zones keeping their bounds beside the new.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed partial class ExtensionZoneTests
{
    private const int Rows = 40_000;

    private static readonly DateTime Start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInstantsAndADatesZonesPruneTheirRanges(bool appended)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Event[] rows = [.. Enumerable.Range(0, Rows).Select(Event.Of)];
        string path = Path.Combine(Path.GetTempPath(), $"extension-zones-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = 1_024 };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Event>(path, options))
            {
                await writer.WriteAsync<Event>(appended ? rows.AsSpan(0, Rows / 2) : rows, ct);
                await writer.CompleteAsync(ct);
            }

            if (appended)
            {
                await using VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, cancellationToken: ct);
                await writer.WriteAsync<Event>(rows.AsSpan(Rows / 2), ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);

            // A day of each half: about 2 900 rows, three or four blocks of 1 024 among 40.
            foreach (DateTime day in new[] { Start.AddDays(2), Start.AddDays(10) })
            {
                DateOnly date = DateOnly.FromDateTime(day);
                (Func<Probe<Event>, Predicate> Filter, Func<Event, bool> Oracle)[] ranges =
                [
                    (e => e.At >= day & e.At < day.AddDays(1), e => e.At >= day && e.At < day.AddDays(1)),
                    (e => e.Local >= new DateTimeOffset(day) & e.Local < new DateTimeOffset(day.AddDays(1)), e => e.Local >= new DateTimeOffset(day) && e.Local < new DateTimeOffset(day.AddDays(1))),
                    (e => e.Day == date, e => e.Day == date),
                ];

                foreach ((Func<Probe<Event>, Predicate> filter, Func<Event, bool> oracle) in ranges)
                {
                    ScanPlan plan = await file.Scan<Event>().Where(filter).With(new ScanOptions { UseIndexes = false }).ExplainAsync(ct);
                    PruningStep zones = Assert.Single(plan.Pruning, step => step.Structure == "zone map");
                    Assert.True(plan.LiveBlocks <= 4, $"{plan.LiveBlocks} of {plan.Blocks} blocks live: {string.Join(", ", plan.Pruning)}");
                    Assert.Equal(plan.Blocks - plan.LiveBlocks, zones.BlocksPruned);
                    Assert.Equal(rows.Count(oracle), await file.Scan<Event>().Where(filter).CountAsync(ct));
                }
            }

            // The file's statistics bound the instants too: a range past them is answered without a block.
            DateTime later = Start.AddYears(1);
            ScanPlan past = await file.Scan<Event>().Where(e => e.At >= later).ExplainAsync(ct);
            Assert.False(past.MayMatch);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Event(long Id, DateTime At, DateOnly Day, DateTimeOffset Local)
    {
        internal static Event Of(int row)
        {
            DateTime at = Start.AddSeconds(row * 30L);
            return new Event(row, at, DateOnly.FromDateTime(at), new DateTimeOffset(at));
        }
    }
}
