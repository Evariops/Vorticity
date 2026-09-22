using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static partial class WriteListsAndRecords
{
    private const int Visits = 100_000;

    /// <summary>A trip whose destination may be unknown: a nullable nested record.</summary>
    [VortexRecord]
    public partial record struct Trip(int Id, Address? Destination);

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("write-lists-and-records.vortex");
        DateTime now = new DateTime(2026, 9, 22, 8, 30, 0, DateTimeKind.Utc);
        int[] pagesSeen = [2, 3, 5, 7];

        await using (VortexFileWriter writer = session.CreateWriter<Visit>(path))
        {
            ColumnsBuilder<Visit> v = writer.Builder<Visit>();

            v.Id.Append(Guid.CreateVersion7(now));
            v.StartedAt.Append(now);
            v.DurationMs.Append(1_250);
            v.Referrer.AppendNull();
            v.Pages.Append([1, 4, 9]);                                 // one list, one call
            ColumnsBuilder<Address> origin = v.Struct<Address>(5);     // the same builder as v.Origin
            origin.Country.Append("FR"u8);
            origin.City.AppendNull();

            v.Id.Append(Guid.CreateVersion7(now.AddSeconds(1)));
            v.StartedAt.Append(now.AddSeconds(1));
            v.DurationMs.Append(830);
            v.Referrer.Append("https://example.org/"u8);
            v.Pages.BeginList();                                       // or element by element
            foreach (int page in pagesSeen) v.Pages.Elements.Append(page);
            v.Pages.EndList();
            v.Origin.Country.Append("DE"u8);
            v.Origin.City.Append("Berlin"u8);

            await writer.WriteAsync(v, ct);
            await writer.CompleteAsync(ct);
        }

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            await foreach (Visit visit in file.Scan<Visit>().ToRecordsAsync(ct))
            {
                Console.WriteLine($"  {visit.DurationMs} ms, pages [{string.Join(", ", visit.Pages.ToArray())}], " +
                    $"from {visit.Origin.Country}/{visit.Origin.City ?? "null"}, referrer {visit.Referrer ?? "null"}");
            }
        }

        string trips = Demo.Path("trips.vortex");
        await using (VortexFileWriter writer = session.CreateWriter<Trip>(trips))
        {
            ColumnsBuilder<Trip> t = writer.Builder<Trip>();
            t.Id.Append(1);
            t.Destination.Country.Append("FR"u8);
            t.Destination.City.Append("Lyon"u8);

            t.Id.Append(2);
            t.Destination.AppendNull();                                // a null record: its fields get placeholders

            await writer.WriteAsync(t, ct);
            await writer.CompleteAsync(ct);
        }

        await using (VortexFile file = await VortexFile.OpenAsync(trips))
        {
            await foreach (Trip trip in file.Scan<Trip>().ToRecordsAsync(ct))
            {
                Console.WriteLine($"  trip {trip.Id}: {(trip.Destination is { } d ? $"{d.Country}/{d.City}" : "no destination")}");
            }
        }

        await using (VortexFileWriter writer = session.CreateWriter<Visit>(path))
        {
            ColumnsBuilder<Visit> v = writer.Builder<Visit>();
            v.Pages.BeginList();
            try
            {
                await writer.WriteAsync(v, ct);
            }
            catch (VortexSchemaException e)
            {
                Console.WriteLine($"a list left open: {e.Message}");
            }

            writer.Abandon();
        }

        await Bulk(session, path, ct);
    }

    /// <summary>A hundred thousand visits, column by column and then as rows, timed.</summary>
    private static async Task Bulk(VortexSession session, string path, CancellationToken ct)
    {
        DateTime start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        byte[][] cities = Array.ConvertAll(Demo.Cities, Encoding.UTF8.GetBytes);
        byte[][] referrers = new byte[13][];
        for (int i = 0; i < referrers.Length; i++) referrers[i] = Encoding.UTF8.GetBytes("https://example.org/" + i);

        Visit[] visits = new Visit[Visits];
        Guid[] ids = new Guid[Visits];
        DateTime[] startedAt = new DateTime[Visits];
        for (int row = 0; row < Visits; row++)
        {
            int[] pages = new int[row % 5];
            for (int p = 0; p < pages.Length; p++) pages[p] = (row + p) % 40;
            visits[row] = new Visit(
                Guid.CreateVersion7(start.AddSeconds(row)), start.AddSeconds(row * 3), row % 90_000,
                row % 4 == 0 ? null : "https://example.org/" + (row % 13), pages,
                new Address(row % 3 == 0 ? "DE" : "FR", row % 11 == 0 ? null : Demo.Cities[row % Demo.Cities.Length]));
            ids[row] = visits[row].Id;
            startedAt[row] = visits[row].StartedAt;
        }

        long columnar = long.MaxValue;
        WriteReport? report = null;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            await using VortexFileWriter writer = session.CreateWriter<Visit>(path);
            ColumnsBuilder<Visit> v = writer.Builder<Visit>();
            for (int first = 0; first < Visits; first += writer.BlockRows)
            {
                int count = Math.Min(writer.BlockRows, Visits - first);
                v.Id.Append(ids.AsSpan(first, count));
                v.StartedAt.Append(startedAt.AsSpan(first, count));
                Span<int> durations = v.DurationMs.GetSpan(count);
                for (int i = 0; i < count; i++) durations[i] = (first + i) % 90_000;
                v.DurationMs.Advance(count);

                for (int row = first; row < first + count; row++)
                {
                    if (row % 4 == 0) v.Referrer.AppendNull();
                    else v.Referrer.Append(referrers[row % 13]);

                    v.Pages.Append(visits[row].Pages.Span);
                    v.Origin.Country.Append(row % 3 == 0 ? "DE"u8 : "FR"u8);
                    if (row % 11 == 0) v.Origin.City.AppendNull();
                    else v.Origin.City.Append(cities[row % cities.Length]);
                }

                await writer.WriteAsync(v, ct);
            }

            report = await writer.CompleteAsync(ct);
            columnar = Math.Min(columnar, clock.ElapsedMilliseconds);
        }

        Console.WriteLine($"{Visits} visits column by column: {report!.Bytes.Total} bytes, best of three {columnar} ms");
        foreach (ColumnWriteReport column in report.Columns)
        {
            Console.WriteLine($"  {column.Path}: {WriteReportText.Encodings(column.Encodings)}");
        }

        long asRows = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            await using VortexFileWriter writer = session.CreateWriter<Visit>(path);
            await writer.WriteAsync<Visit>(visits.AsSpan(), ct);
            report = await writer.CompleteAsync(ct);
            asRows = Math.Min(asRows, clock.ElapsedMilliseconds);
        }

        Console.WriteLine($"the same visits as rows: {report.Bytes.Total} bytes, best of three {asRows} ms");

        await using VortexFile file = await VortexFile.OpenAsync(path);
        long lists = 0;
        long elements = 0;
        long nullCities = 0;
        await foreach (var visit in file.Scan<Visit>())
        {
            Column<ReadOnlyMemory<int>> pages = visit.Pages;
            lists += pages.Length;
            elements += pages.Elements.Length;
            nullCities += visit.Origin.City.NullCount;
        }

        Console.WriteLine($"read back: {file.RowCount} visits, {lists} lists holding {elements} pages, {nullCities} origins without a city, " +
            $"{new FileInfo(path).Length} bytes");
    }
}
