// Time slices cut as the rows come: a steady pace cut as a count of every row cuts it, a burst cut where the stream
// meets it, the first slice holding the rows an order is judged from, and rows out of order refused.
using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class TimeSliceTests
{
    private const long Minute = 60 * Second;

    private const long Hour = 60 * Minute;

    /// <summary>
    /// Rows at a steady pace, a day of 10 000 or a second of them, are cut as they come into the slices a count of every
    /// row cuts, at the width given or judged from their first rows, fewer than a unit holds, whatever the batches they
    /// come in. Rows that fill a day of a month that could hold them all are cut at their pace, a day, where a count of
    /// them takes the month.
    /// </summary>
    [Fact]
    public void RowsAtASteadyPaceAreCutAsTheirCountCutsThem()
    {
        long[] day = [.. Enumerable.Range(0, 10_000).Select(i => Noon + (i * 8_640_000L))];
        long[] second = [.. Enumerable.Range(0, 10_000).Select(i => Noon + (i * 100L))];
        foreach ((long[] rows, long target) in (ReadOnlySpan<(long[], long)>)[(day, 10_000), (day, 500), (day, 100), (second, 100)])
        {
            List<TimeSlice> counted = TimeSlicer.Cut(rows, TimeUnit.Microseconds, target);
            foreach (int batch in (ReadOnlySpan<int>)[1, 7, 1_000, rows.Length])
            {
                Assert.Equal(counted, Streamed(new SliceStream(TimeUnit.Microseconds, TimeSlicer.WidthFor(rows, TimeUnit.Microseconds, target), target), rows, batch));
                SliceStream judged = new SliceStream(TimeUnit.Microseconds, target, least: 5);
                List<TimeSlice> streamed = Streamed(judged, rows, batch);
                if (target < rows.Length)
                {
                    Assert.Equal(counted, streamed);
                }
                else
                {
                    Assert.Equal(SliceWidth.Day, judged.Width);
                    Assert.Equal([5_000L, 5_000L], streamed.Select(slice => slice.Rows));
                }
            }
        }
    }

    /// <summary>
    /// A burst in an hour of a request a minute ends its slice with the minute that holds the row past twice the target,
    /// and that minute, past the bound itself, with the second that holds it; the rest of the minute goes to slices of a
    /// second, the rest of the hour back to slices of a minute, and the next hour is an hour again.
    /// </summary>
    [Fact]
    public void ABurstIsCutWhereTheStreamMeetsIt()
    {
        // Three hours of a request a minute; at half past one, a hundred more, half a second apart.
        long[] rows =
        [
            .. Enumerable.Range(0, 3 * 60).Select(i => Noon + (i * Minute)),
            .. Enumerable.Range(1, 100).Select(i => Noon + Hour + (30 * Minute) + (i * Second / 2)),
        ];
        Array.Sort(rows);
        List<TimeSlice> slices = Streamed(new SliceStream(TimeUnit.Microseconds, SliceWidth.Hour, 40), rows, rows.Length);

        long burst = Noon + Hour + (30 * Minute);
        List<(long Start, long End, long Rows)> expected =
        [
            (Noon, Noon + Hour, 60),
            (Noon + Hour, burst + (41 * Second), 112),
            .. Enumerable.Range(41, 10).Select(s => (burst + (s * Second), burst + ((s + 1) * Second), s == 50 ? 1L : 2L)),
            .. Enumerable.Range(31, 29).Select(m => (Noon + Hour + (m * Minute), Noon + Hour + ((m + 1) * Minute), 1L)),
            (Noon + (2 * Hour), Noon + (3 * Hour), 60),
        ];
        Assert.Equal(expected, slices.Select(slice => (slice.Start, slice.End, slice.Rows)));
        Assert.Equal(rows.Length, slices.Sum(slice => slice.Rows));
    }

    /// <summary>
    /// The first slice of a stream that judges its width holds the rows it judges from: rows that start a minute before an
    /// hour's end take the hour after it in their first slice. An object of fewer is one slice, cut at the pace of all its
    /// rows, those of a single instant at as many a second.
    /// </summary>
    [Fact]
    public void TheFirstSliceHoldsTheRowsTheOrderIsJudgedFrom()
    {
        long[] late = [.. Enumerable.Range(0, 3 * 60).Select(i => Noon + (59 * Minute) + (i * Minute))];
        List<TimeSlice> slices = Streamed(new SliceStream(TimeUnit.Microseconds, 40, least: 30), late, 7);
        Assert.Equal((Noon, Noon + (2 * Hour), 61L), (slices[0].Start, slices[0].End, slices[0].Rows));
        Assert.Equal((Noon + (2 * Hour), Noon + (3 * Hour), 60L), (slices[1].Start, slices[1].End, slices[1].Rows));
        Assert.Equal(late.Length, slices.Sum(slice => slice.Rows));

        // Twenty rows a minute apart: one slice, an hour wide at their pace; twenty in one microsecond: twenty a second, a
        // second wide under twice the target of 40, a day under twice 3 000 000.
        long[] few = [.. Enumerable.Range(0, 20).Select(i => Noon + (i * Minute))];
        Assert.Equal([new TimeSlice(Noon, Noon + Hour, 0, 20)], Streamed(new SliceStream(TimeUnit.Microseconds, 40, least: 30), few, 3));
        foreach ((long target, SliceWidth width) in (ReadOnlySpan<(long, SliceWidth)>)[(40, SliceWidth.Second), (3_000_000, SliceWidth.Day)])
        {
            SliceStream instant = new SliceStream(TimeUnit.Microseconds, target, least: 30);
            Assert.Equal(20, Streamed(instant, [.. Enumerable.Repeat(Noon, 20)], 20).Single().Rows);
            Assert.Equal(width, instant.Width);
        }
    }

    /// <summary>A row before the one taken before it, in a batch or the one after, is refused, the registers' check as the walk's.</summary>
    [Fact]
    public void RowsOutOfOrderAreRefused()
    {
        long[] rows = [.. Enumerable.Range(0, 100).Select(i => Noon + (i * Second))];
        long[] fallen = [.. rows];
        fallen[63] = fallen[10];
        Assert.Throws<ArgumentException>(() => Streamed(new SliceStream(TimeUnit.Microseconds, SliceWidth.Minute, 1_000), fallen, 100));
        Assert.Throws<ArgumentException>(() => Streamed(new SliceStream(TimeUnit.Microseconds, 1_000, least: 1_000), fallen, 100));
        Assert.Throws<ArgumentException>(() => Streamed(new SliceStream(TimeUnit.Microseconds, SliceWidth.Minute, 1_000), fallen, 63));

        Random random = new Random(0x5_11CE);
        for (int round = 0; round < 200; round++)
        {
            long[] values = [.. Enumerable.Range(0, random.Next(0, 70)).Select(_ => (long)random.Next(0, 40))];
            Array.Sort(values);
            if (values.Length > 1 && random.Next(2) == 0)
            {
                int at = random.Next(1, values.Length);
                values[at] = values[at - 1] - 1;
            }

            Assert.Equal(SliceStream.FallsFrom(values, 1), SliceStream.Falls(values));
        }
    }

    /// <summary>The slices <paramref name="stream"/> cuts of <paramref name="instants"/>, handed over <paramref name="batch"/> at a time, as a sorter hands them.</summary>
    private static List<TimeSlice> Streamed(SliceStream stream, long[] instants, int batch)
    {
        long held = 0;
        for (int from = 0; from < instants.Length; from += batch)
        {
            ReadOnlySpan<long> rows = instants.AsSpan(from, Math.Min(batch, instants.Length - from));
            int at = 0;
            while (at < rows.Length)
            {
                (int take, bool ends) = stream.Take(rows[at..], held);
                held += take;
                at += take;
                if (ends)
                {
                    stream.Close(held);
                    held = 0;
                }
            }
        }

        if (held > 0)
        {
            stream.Close(held);
        }

        return [.. stream.Slices];
    }
}
