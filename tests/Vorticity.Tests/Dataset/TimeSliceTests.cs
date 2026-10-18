// Time slices: how rows sorted by their instant are cut, by a calendar unit chosen from their volume
// and finer where a burst fills one, and the slice table a file carries in its index directory.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class TimeSliceTests
{
    private const long Second = 1_000_000;

    private static readonly long Noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheWidthIsTheCoarsestUnitThatHoldsTheTarget()
    {
        // 10 000 rows over a day: 417 an hour, 7 a minute; over a second: all in one.
        long[] day = [.. Enumerable.Range(0, 10_000).Select(i => Noon + (i * 8_640_000L))];
        long[] second = [.. Enumerable.Range(0, 10_000).Select(i => Noon + (i * 100L))];
        Assert.Equal(SliceWidth.Month, TimeSlicer.WidthFor(day, TimeUnit.Microseconds, 10_000));
        Assert.Equal(SliceWidth.Hour, TimeSlicer.WidthFor(day, TimeUnit.Microseconds, 500));
        Assert.Equal(SliceWidth.Minute, TimeSlicer.WidthFor(day, TimeUnit.Microseconds, 100));
        Assert.Equal(SliceWidth.Second, TimeSlicer.WidthFor(second, TimeUnit.Microseconds, 100));
    }

    [Fact]
    public void SlicesFollowEachOtherAndCoverEveryRowOnce()
    {
        // Ten rows a minute over an hour, a target of ten: slices of a minute.
        long[] instants = [.. Enumerable.Range(0, 600).Select(i => Noon + (i * 6 * Second))];
        List<TimeSlice> slices = TimeSlicer.Cut(instants, TimeUnit.Microseconds, targetRows: 10);
        Assert.Equal(60, slices.Count);
        Assert.All(slices, slice => Assert.Equal((60 * Second, 10L), (slice.End - slice.Start, slice.Rows)));
        Assert.Equal(600, new SliceTable(TimeUnit.Microseconds, slices).Rows);
        for (int i = 0; i < slices.Count; i++)
        {
            Assert.Equal(i * 10, slices[i].FirstRow);
            Assert.InRange(instants[slices[i].FirstRow], slices[i].Start, slices[i].End - 1);
        }
    }

    [Fact]
    public void ABurstIsCutIntoFinerUnits()
    {
        // Ten rows a minute, then 200 rows in one minute: past twice the target, that minute is cut into seconds.
        List<long> instants = [.. Enumerable.Range(0, 100).Select(i => Noon + (i * 6 * Second))];
        long burst = Noon + (10 * 60 * Second);
        instants.AddRange(Enumerable.Range(0, 200).Select(i => burst + (i * 300_000)));
        instants.AddRange(Enumerable.Range(0, 20).Select(i => burst + (60 * Second) + (i * 6 * Second)));
        List<TimeSlice> slices = TimeSlicer.Cut([.. instants], TimeUnit.Microseconds, targetRows: 10);
        TimeSlice[] seconds = [.. slices.Where(slice => slice.End - slice.Start == Second)];
        Assert.Equal(60, seconds.Length);
        Assert.All(seconds, slice => Assert.InRange(slice.Rows, 3, 4));
        Assert.Equal(instants.Count, new SliceTable(TimeUnit.Microseconds, slices).Rows);
        Assert.Throws<ArgumentException>(() => TimeSlicer.Cut([Noon, Noon - 1], TimeUnit.Microseconds, 10));
    }

    [Fact]
    public void AMonthIsACalendarMonth()
    {
        long january = new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long february = new DateTimeOffset(2026, 2, 1, 1, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        List<TimeSlice> slices = TimeSlicer.Cut([january, february], TimeUnit.Milliseconds, targetRows: 100);
        Assert.Equal(2, slices.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), slices[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), slices[0].End);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), slices[1].End);
    }

    [Fact]
    public void ATableIsCheckedAndReadBackFromItsBytes()
    {
        SliceTable table = new(TimeUnit.Microseconds, [new TimeSlice(-5, 10, 0, 3), new TimeSlice(20, 30, 3, 7)]);
        SliceTable read = SliceTable.FromBytes(table.ToBytes())!;
        Assert.Equal(table.Slices, read.Slices);
        Assert.Equal(TimeUnit.Microseconds, read.Unit);
        Assert.Null(SliceTable.FromBytes(table.ToBytes()[..^1]));
        Assert.Null(SliceTable.FromBytes([9, 1, 1]));
        Assert.Throws<ArgumentException>(() => new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 10, 0, 3), new TimeSlice(5, 30, 3, 7)]));
        Assert.Throws<ArgumentException>(() => new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 10, 0, 3), new TimeSlice(10, 30, 4, 7)]));
        Assert.Throws<ArgumentException>(() => new SliceTable(TimeUnit.Microseconds, []));
    }

    /// <summary>A writer that declares its slices writes them into its index directory, and a file read back finds them.</summary>
    [Fact]
    public async Task AFileCarriesItsSlicesInItsIndexDirectory()
    {
        Reading[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Reading(DateTimeOffset.FromUnixTimeMilliseconds((Noon / 1000) + (i * 30L)), i % 7))];
        long[] instants = [.. rows.Select(r => r.At.ToUnixTimeMilliseconds() * 1000)];
        SliceTable table = new(TimeUnit.Microseconds, TimeSlicer.Cut(instants, TimeUnit.Microseconds, targetRows: 1_000));
        Assert.True(table.Slices.Count > 10);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-slices-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new() { Indexes = IndexPolicy.Auto.SortedRuns(nameof(Reading.At), true) };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, options))
            {
                await writer.WriteAsync<Reading>(rows, Ct);
                writer.DeclareSlices(nameof(Reading.At), table);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            SliceTable read = (await SliceTable.ReadAsync(file, Ct))!;
            Assert.Equal(table.Slices, read.Slices);
            Assert.Equal(20_000, (await file.Scan<Reading>().Select(r => r.Value).ToListAsync(Ct)).Count);

            // Slices that do not cover the file's rows leave it unwritten.
            await using VortexFileWriter refused = VortexSession.Default.CreateWriter<Reading>(path + ".2", options);
            await refused.WriteAsync<Reading>(rows[..100], Ct);
            refused.DeclareSlices(nameof(Reading.At), table);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await refused.CompleteAsync(Ct));
            Assert.Throws<ArgumentException>(() => refused.DeclareSlices("Missing", table));
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(path + ".2");
        }
    }

    [VortexRecord]
    public partial record struct Reading(DateTimeOffset At, long Value);
}
