using System;
using System.IO;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>
/// The files the samples read. Every file is written once, under one temporary directory, and
/// deleted when the run ends.
/// </summary>
internal static class Demo
{
    /// <summary>A million readings: the day in order, a temperature missing one row in fifty, eight cities.</summary>
    internal const int ReadingRows = 1_000_000;

    /// <summary>A hundred thousand visits.</summary>
    internal const int VisitRows = 100_000;

    internal static readonly string[] Cities = ["Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Lille"];

    private static readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vorticity-samples");

    private static string? s_readings;
    private static string? s_visits;

    /// <summary>A path under the samples' directory.</summary>
    internal static string Path(string name)
    {
        Directory.CreateDirectory(Root);
        return System.IO.Path.Combine(Root, name);
    }

    /// <summary>The readings file, written on first use.</summary>
    internal static async ValueTask<string> ReadingsAsync()
    {
        if (s_readings is not null)
        {
            return s_readings;
        }

        string path = Path("readings.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            Reading[] block = new Reading[writer.BlockRows];
            for (int start = 0; start < ReadingRows; start += block.Length)
            {
                int count = Math.Min(block.Length, ReadingRows - start);
                for (int i = 0; i < count; i++)
                {
                    int row = start + i;
                    block[i] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Cities[row / 7 % Cities.Length]);
                }

                await writer.WriteAsync<Reading>(block.AsSpan(0, count));
            }

            await writer.CompleteAsync();
        }

        return s_readings = path;
    }

    /// <summary>The visits file, written on first use.</summary>
    internal static async ValueTask<string> VisitsAsync()
    {
        if (s_visits is not null)
        {
            return s_visits;
        }

        string path = Path("visits.vortex");
        DateTime start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
        {
            Visit[] block = new Visit[writer.BlockRows];
            for (int first = 0; first < VisitRows; first += block.Length)
            {
                int count = Math.Min(block.Length, VisitRows - first);
                for (int i = 0; i < count; i++)
                {
                    int row = first + i;
                    int[] pages = new int[row % 5];
                    for (int p = 0; p < pages.Length; p++)
                    {
                        pages[p] = (row + p) % 40;
                    }

                    block[i] = new Visit(
                        Guid.CreateVersion7(start.AddSeconds(row)),
                        start.AddSeconds(row * 3),
                        row % 90_000,
                        row % 4 == 0 ? null : "https://example.org/" + (row % 13),
                        pages,
                        new Address(row % 3 == 0 ? "DE" : "FR", row % 11 == 0 ? null : Cities[row % Cities.Length]));
                }

                await writer.WriteAsync<Visit>(block.AsSpan(0, count));
            }

            await writer.CompleteAsync();
        }

        return s_visits = path;
    }

    /// <summary>Deletes everything the samples wrote.</summary>
    internal static void Clean()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
