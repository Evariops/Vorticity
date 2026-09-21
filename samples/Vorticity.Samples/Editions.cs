using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class Editions
{
    private const int Rows = 1_000_000;

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("editions.vortex");

        Console.WriteLine($"Default {VortexEditions.Name(VortexEditions.Default)}, Newest {VortexEditions.Name(VortexEditions.Newest)}, " +
            $"ReadFloor {VortexEditions.Name(VortexEditions.ReadFloor)}");
        foreach (VortexEdition edition in Enum.GetValues<VortexEdition>())
        {
            Console.WriteLine($"  {edition}: {VortexEditions.Name(edition)}, read by Vortex {VortexEditions.MinimumRustVersion(edition)} and later");
        }

        foreach ((ComponentKind kind, string id) in (ReadOnlySpan<(ComponentKind, string)>)
            [(ComponentKind.Array, "vortex.alp"), (ComponentKind.Array, "vortex.zstd"), (ComponentKind.Array, "fastlanes.rle"),
             (ComponentKind.Layout, "vortex.zoned"), (ComponentKind.DType, "vortex.uuid"), (ComponentKind.Array, "vortex.patched")])
        {
            VortexEdition? introduced = VortexEditions.IntroducedIn(kind, id);
            Console.WriteLine($"  {kind} {id}: introduced in {(introduced is { } e ? VortexEditions.Name(e) : "no edition")}, " +
                $"in the read floor: {VortexEditions.Contains(VortexEditions.ReadFloor, kind, id)}");
        }

        Console.WriteLine();
        Reading[] readings = Readings(Rows);
        foreach (VortexEdition edition in Enum.GetValues<VortexEdition>())
        {
            VortexWriteOptions options = new() { TargetEdition = edition };
            WriteReport report;
            await using (VortexFileWriter writer = session.CreateWriter<Reading>(path, options))
            {
                await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
                report = await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync(ct);
            Console.WriteLine($"{VortexEditions.Name(edition)}: {report.Bytes.Total} bytes, zone maps {report.Bytes.ZoneMaps}, " +
                $"the file reads as {VortexEditions.Name(file.Edition)}; Day >= 900 reads {plan.LiveBlocks} of {plan.Blocks} blocks; " +
                $"Day {WriteReportText.Encodings(report.Columns[0].Encodings)}, Celsius {WriteReportText.Encodings(report.Columns[1].Encodings)}");
        }

        Console.WriteLine();
        try
        {
            await using VortexFileWriter writer = session.CreateWriter<Visit>(path, new VortexWriteOptions { TargetEdition = VortexEdition.Core20260802 });
            Console.WriteLine("a Guid member under core2026.08.2: accepted");
        }
        catch (VortexUnsupportedException e)
        {
            Console.WriteLine($"a Guid member under core2026.08.2: Kind {e.Kind}, ComponentId {e.ComponentId}");
            Console.WriteLine($"  {e.Message}");
        }
    }

    private static Reading[] Readings(int count)
    {
        Reading[] readings = new Reading[count];
        for (int row = 0; row < count; row++)
        {
            readings[row] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return readings;
    }
}
