// A temporal or decimal column copied whole reads its storage at its own width, its unit and zone
// resolved once; read row by row, the indexer reads each value through the storage's width at every
// row. The two are held to the same values over every unit, a zone, nulls, instants before the epoch
// and every width of a decimal, and a stored value outside the .NET type is reported at its row.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Columns;

public sealed partial class TemporalCopyTests
{
    private const int Rows = 20_000;

    [Fact]
    public async Task ACopyHoldsWhatEachRowReads()
    {
        string path = TempPath();
        try
        {
            Times[] rows = new Times[Rows];
            Random random = new Random(1105);
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = Row(random, i);
            }

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Times>(path))
            {
                await writer.WriteAsync<Times>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            int checkedRows = 0;
            await foreach (Columns<Times> batch in file.Scan<Times>().WithCancellation(Ct))
            {
                int n = batch.RowCount;
                Same(batch.Column<DateTime>(0), n);
                Same(batch.Column<DateTime>(1), n);
                Same(batch.Column<DateTime>(2), n);
                Same(batch.Column<DateTime>(3), n);
                Same(batch.Column<DateTime?>(4), n);
                Same(batch.Column<DateOnly>(5), n);
                Same(batch.Column<DateOnly?>(6), n);
                Same(batch.Column<TimeOnly>(7), n);
                Same(batch.Column<TimeOnly>(8), n);
                Same(batch.Column<DateTimeOffset>(9), n);
                Same(batch.Column<DateTimeOffset?>(10), n);
                Same(batch.Column<decimal>(11), n);
                Same(batch.Column<decimal>(12), n);
                Same(batch.Column<decimal>(13), n);
                Same(batch.Column<decimal?>(14), n);
                Same(batch.Column<decimal>(15), n);
                checkedRows += n;
            }

            Assert.Equal(Rows, checkedRows);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AStoredValueOutsideTheTypeIsMalformedAtItsRow()
    {
        string path = TempPath();
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Seconds>(path))
            {
                // Through the storage, which a long writes: seconds past what a DateTime holds.
                ColumnsBuilder builder = writer.Builder();
                builder.Column<long>(0).Append([0L, long.MaxValue / 2, 1L]);
                builder.Column<int>(1).Append([0, int.MaxValue, 1]);
                await writer.WriteAsync(builder, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await foreach (Columns<Seconds> batch in file.Scan<Seconds>().WithCancellation(Ct))
            {
                string? instant = null;
                try
                {
                    batch.Column<DateTime>(0).CopyTo(new DateTime[batch.RowCount]);
                }
                catch (VortexFormatException e)
                {
                    instant = e.Message;
                }

                Assert.StartsWith("Row 1 stores", instant, StringComparison.Ordinal);
                Assert.Equal(DateTime.UnixEpoch.AddSeconds(1), batch.Column<DateTime>(0)[2]);

                string? day = null;
                try
                {
                    batch.Column<DateOnly>(1).CopyTo(new DateOnly[batch.RowCount]);
                }
                catch (VortexFormatException e)
                {
                    day = e.Message;
                }

                Assert.StartsWith("Row 1 stores", day, StringComparison.Ordinal);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // The column's own CopyTo against its indexer, for each type the test copies.
    private static void Same(Column<DateTime> column, int rows)
    {
        DateTime[] copied = new DateTime[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], column[i].Kind == copied[i].Kind);
        }
    }

    private static void Same(Column<DateTime?> column, int rows)
    {
        DateTime?[] copied = new DateTime?[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], column[i]?.Kind == copied[i]?.Kind);
        }
    }

    private static void Same(Column<DateOnly> column, int rows)
    {
        DateOnly[] copied = new DateOnly[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], true);
        }
    }

    private static void Same(Column<DateOnly?> column, int rows)
    {
        DateOnly?[] copied = new DateOnly?[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], true);
        }
    }

    private static void Same(Column<TimeOnly> column, int rows)
    {
        TimeOnly[] copied = new TimeOnly[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], true);
        }
    }

    private static void Same(Column<DateTimeOffset> column, int rows)
    {
        DateTimeOffset[] copied = new DateTimeOffset[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], column[i].Offset == copied[i].Offset);
        }
    }

    private static void Same(Column<DateTimeOffset?> column, int rows)
    {
        DateTimeOffset?[] copied = new DateTimeOffset?[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], column[i]?.Offset == copied[i]?.Offset);
        }
    }

    private static void Same(Column<decimal> column, int rows)
    {
        decimal[] copied = new decimal[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], column[i].Scale == copied[i].Scale);
        }
    }

    private static void Same(Column<decimal?> column, int rows)
    {
        decimal?[] copied = new decimal?[rows];
        column.CopyTo(copied);
        for (int i = 0; i < rows; i++)
        {
            Check(column.Type, i, column[i], copied[i], column[i]?.Scale == copied[i]?.Scale);
        }
    }

    private static void Check<T>(VortexType type, int row, T read, T copied, bool alike)
    {
        if (!EqualityComparer<T>.Default.Equals(read, copied) || !alike)
        {
            Assert.Fail($"Row {row} of a column of {type}: {read} read, {copied} copied.");
        }
    }

    private static Times Row(Random random, int i)
    {
        // Instants a century either side of the epoch, to the tick, so that every unit floors some.
        long ticks = DateTime.UnixEpoch.Ticks + random.NextInt64(-36_500L * TimeSpan.TicksPerDay, 36_500L * TimeSpan.TicksPerDay);
        DateTime naive = new DateTime(ticks, DateTimeKind.Unspecified);
        DateTime utc = new DateTime(ticks, DateTimeKind.Utc);
        bool missing = i % 9 == 0;
        decimal price = random.NextInt64(-1_000_000_000_000, 1_000_000_000_000) / 100m;
        return new Times(
            Floor(naive, 100),
            Floor(utc, TimeSpan.TicksPerMicrosecond),
            Floor(naive, TimeSpan.TicksPerMillisecond),
            Floor(utc, TimeSpan.TicksPerSecond),
            missing ? null : Floor(naive, TimeSpan.TicksPerMicrosecond),
            DateOnly.FromDateTime(naive),
            missing ? null : DateOnly.FromDateTime(naive),
            new TimeOnly(ticks % TimeSpan.TicksPerDay / 100 * 100),
            new TimeOnly(ticks % TimeSpan.TicksPerDay / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
            new DateTimeOffset(Floor(utc, TimeSpan.TicksPerMicrosecond)),
            missing ? null : new DateTimeOffset(Floor(utc, TimeSpan.TicksPerSecond)),
            random.Next(-9, 10) / 10m,
            random.Next(-9_999, 10_000) / 100m,
            random.Next(-999_999_999, 1_000_000_000) / 100m,
            missing ? null : price,
            price * 1_000_000m + 0.0001m);
    }

    private static DateTime Floor(DateTime value, long ticks) => new DateTime(value.Ticks - (((value.Ticks % ticks) + ticks) % ticks), value.Kind);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"vorticity-temporal-copy-{Guid.NewGuid():N}.vortex");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Times(
        [VortexColumn(Unit = TimeUnit.Nanoseconds)] DateTime Nanos,
        [VortexColumn(TimeZone = "UTC")] DateTime Micros,
        [VortexColumn(Unit = TimeUnit.Milliseconds)] DateTime Millis,
        [VortexColumn(Unit = TimeUnit.Seconds, TimeZone = "UTC")] DateTime Seconds,
        DateTime? MicrosN,
        DateOnly Day,
        [VortexColumn(Unit = TimeUnit.Milliseconds)] DateOnly? DayMillisN,
        [VortexColumn(Unit = TimeUnit.Nanoseconds)] TimeOnly TimeNanos,
        [VortexColumn(Unit = TimeUnit.Seconds)] TimeOnly TimeSeconds,
        [VortexColumn(TimeZone = "Europe/Paris")] DateTimeOffset Zoned,
        [VortexColumn(Unit = TimeUnit.Seconds, TimeZone = "Europe/Paris")] DateTimeOffset? ZonedN,
        [VortexColumn(Precision = 2, Scale = 1)] decimal Dec8,
        [VortexColumn(Precision = 4, Scale = 2)] decimal Dec16,
        [VortexColumn(Precision = 9, Scale = 2)] decimal Dec32,
        [VortexColumn(Precision = 18, Scale = 2)] decimal? Dec64N,
        [VortexColumn(Precision = 28, Scale = 4)] decimal Dec128);

    [VortexRecord]
    public partial record struct Seconds([VortexColumn(Unit = TimeUnit.Seconds)] DateTime At, DateOnly Day);
}
