using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Editions;
using Vorticity.File;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class Editions
{
    internal static async Task RunAsync()
    {
        Console.WriteLine($"the newest edition this build knows is {EditionRegistry.Newest} " +
            $"({EditionRegistry.Name(EditionRegistry.Newest)}), and it reads back to " +
            $"{EditionRegistry.ReadForeverFloor} ({EditionRegistry.Name(EditionRegistry.ReadForeverFloor)})");

        foreach (VortexEdition edition in Enum.GetValues<VortexEdition>())
        {
            Console.WriteLine($"  {edition}: {EditionRegistry.Name(edition)}, " +
                $"from Vortex {EditionRegistry.MinimumLibraryVersion(edition)}");
        }

        // When a component became readable by everyone, and who has it.
        foreach (string encoding in new[] { "vortex.alp", "vortex.fsst", "vortex.zstd", "vortex.sequence" })
        {
            VortexEdition? introduced = EditionRegistry.IntroducedIn(ComponentKind.Array, encoding);
            Console.WriteLine($"  {encoding}: introduced in {introduced?.ToString() ?? "no pinned edition"}, " +
                $"in the oldest edition: {EditionRegistry.Contains(EditionRegistry.ReadForeverFloor, ComponentKind.Array, encoding)}, " +
                $"in the newest: {EditionRegistry.Contains(EditionRegistry.Newest, ComponentKind.Array, encoding)}");
        }

        // What targeting an older edition costs, on the same rows.
        foreach (VortexEdition edition in Enum.GetValues<VortexEdition>())
        {
            string path = Demo.Path($"edition-{edition}.vortex");
            WriteReport report = await Demo.WriteCitiesAsync(
                path, new VortexWriteOptions { TargetEdition = edition });
            string encodings = string.Empty;
            foreach (ColumnWriteReport column in report.Columns)
            {
                encodings += $"{column.Path}={string.Join("/", column.Encodings)} ";
            }

            await using (VortexFile written = await VortexFile.OpenAsync(path))
            {
                Console.WriteLine($"  {edition}: {new FileInfo(path).Length} bytes, {encodings}" +
                    $"reads back {written.RowCount} rows");
            }

            System.IO.File.Delete(path);
        }
    }
}
