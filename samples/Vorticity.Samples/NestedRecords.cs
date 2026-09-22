using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>A parcel whose destination may be missing: a nullable nested record.</summary>
[VortexRecord]
public partial record struct Parcel(long Id, Address? Destination);

/// <summary>A visit read for its origin's country alone.</summary>
[VortexRecord]
public partial record struct VisitCountry(CountryOnly Origin);

/// <summary>The one field of <see cref="Address"/> a projection keeps.</summary>
[VortexRecord]
public partial record struct CountryOnly(string Country);

internal static class NestedRecords
{
    internal static async Task RunAsync()
    {
        string path = await Demo.VisitsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        long visits = 0, pagesSeen = 0, longest = 0, withoutCity = 0, french = 0;
        long[] byPage = new long[40];
        await foreach (var v in file.Scan<Visit>())
        {
            Columns<Address> origin = v.Origin;
            Column<string?> originCity = origin.City;
            withoutCity += originCity.NullCount;
            for (int i = 0; i < origin.RowCount; i++)
            {
                if (origin.Country[i].SequenceEqual("FR"u8))
                {
                    french++;
                }
            }

            Column<ReadOnlyMemory<int>> pages = v.Pages;
            ReadOnlySpan<int> allPages = pages.Elements.Values;
            for (int i = 0; i < pages.Length; i++)
            {
                ReadOnlySpan<int> list = allPages[pages[i]];
                longest = Math.Max(longest, list.Length);
                foreach (int page in list)
                {
                    byPage[page]++;
                }
            }

            visits += v.RowCount;
            pagesSeen += allPages.Length;
        }

        Console.WriteLine($"{visits} visits, {pagesSeen} pages in their lists, the longest list {longest}, page 7 seen {byPage[7]} times");
        Console.WriteLine($"origin: {french} from FR, {withoutCity} without a city");

        Console.WriteLine($"Where(v => v.Origin.Country == \"FR\"): {await file.Scan<Visit>().Where(v => v.Origin.Country == "FR").CountAsync()}");
        Console.WriteLine($"Where(v => v.Origin.City.IsNull): {await file.Scan<Visit>().Where(v => v.Origin.City.IsNull).CountAsync()}");
        Console.WriteLine($"Where(v => v.Pages.Contains(7)): {await file.Scan<Visit>().Where(v => v.Pages.Contains(7)).CountAsync()}");
        Console.WriteLine($"Where(v => v.Origin.IsNull), on a member that is never null: {await file.Scan<Visit>().Where(v => v.Origin.IsNull).CountAsync()}");

        Scan<Visit> whole = file.Scan<Visit>();
        await foreach (var _ in whole)
        {
        }

        Scan<VisitCountry> country = file.Scan<VisitCountry>();
        await foreach (var _ in country)
        {
        }

        Console.WriteLine($"every column of Visit: {whole.Statistics.BytesRequested} bytes; Origin.Country alone: {country.Statistics.BytesRequested} bytes");

        await ParcelsAsync();
    }

    private static async Task ParcelsAsync()
    {
        string path = Demo.Path("parcels.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Parcel>(path))
        {
            Parcel[] parcels = new Parcel[20_000];
            for (int i = 0; i < parcels.Length; i++)
            {
                parcels[i] = new Parcel(i, i % 5 == 0 ? null : new Address("FR", Demo.Cities[i % Demo.Cities.Length]));
            }

            await writer.WriteAsync<Parcel>(parcels);
            await writer.CompleteAsync();
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        long missing = 0, cities = 0;
        await foreach (var p in file.Scan<Parcel>())
        {
            Columns<Address> destination = p.Destination;
            for (int i = 0; i < destination.RowCount; i++)
            {
                if (!destination.IsValid(i))
                {
                    missing++;
                }
                else if (!destination.City.IsValid(i))
                {
                    cities++;
                }
            }
        }

        Console.WriteLine($"parcels: {missing} without a destination, by IsValid; {cities} with one but no city");
        Console.WriteLine($"Where(p => p.Destination.IsNull): {await file.Scan<Parcel>().Where(p => p.Destination.IsNull).CountAsync()}");

        await foreach (Parcel parcel in file.Scan<Parcel>().Rows(0, 1).ToRecordsAsync())
        {
            Console.WriteLine($"  as a record: {parcel.Id} -> {parcel.Destination?.City ?? "no destination"}");
        }
    }
}
