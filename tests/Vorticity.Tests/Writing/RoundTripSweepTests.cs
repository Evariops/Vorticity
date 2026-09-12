// The round trip over the WHOLE in-scope corpus, not nine hand-picked files.
//
// Nine files test the shapes whoever wrote the list thought of. The corpus has every dtype crossed
// with nullability crossed with the row counts where block boundaries live, plus the container
// shapes no type matrix reaches -- and a writer fails on exactly the shape nobody listed. So this
// sweeps all of them and reports every failure at once rather than stopping at the first, because
// "the writer is broken" is useless next to "the writer is broken on lists and on nothing else".
//
// A shape the writer cannot serialize is a FAILURE here, not a skip. The point of a sweep is that
// its coverage is the corpus's rather than the author's, and a silent skip list would put the
// author back in charge of it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class RoundTripSweepTests
{
    [Fact]
    public async Task EveryInScopeCorpusFileSurvivesAWriteAndAReadBack()
    {
        Decoders.EnsureRegistered();

        StringBuilder failures = new StringBuilder();
        int checkedFiles = 0;
        int rows = 0;

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            try
            {
                rows += await RoundTrip(entry);
                checkedFiles++;
            }
            catch (Exception error) when (failures.Length < 12_000)
            {
                failures.Append(entry.Id).Append(": ")
                    .Append(error.GetType().Name).Append(" - ")
                    .Append(error.Message).Append('\n');
            }
        }

        Console.Out.Write(
            "WRITER SWEEP: " + checkedFiles.ToString(CultureInfo.InvariantCulture) + " corpus files " +
            "written and read back, " + rows.ToString(CultureInfo.InvariantCulture) +
            " rows compared value for value.\n");

        Assert.Equal(string.Empty, failures.ToString());
        Assert.True(checkedFiles > 700, $"only {checkedFiles} files round-tripped");
    }

    [Fact]
    public async Task WritesTheCorpusOutForTheRustCrossCheck()
    {
        // The .NET half of criterion 2 (docs/01-scope.md §4): produce the files, and let
        // `cargo run --example verify_written` decide whether the reference agrees with them. It is
        // env-gated because it costs a full corpus write and only the cross-check consumes the
        // output -- and it SKIPS rather than passes when the variable is absent, so a CI job that
        // forgets to set it does not look like a green cross-check.
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_CORPUS");
        Assert.SkipWhen(
            string.IsNullOrEmpty(root),
            "Set VORTICITY_WRITE_CORPUS to a directory to produce files for the Rust cross-check.");

        Decoders.EnsureRegistered();
        Directory.CreateDirectory(root!);

        int written = 0;
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            string destination = Path.Combine(root!, entry.Id.Replace('/', Path.DirectorySeparatorChar) + ".vortex");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using VortexFile source = await VortexFile.OpenAsync(entry.Path, CancellationToken.None);
            await using VortexFileWriter writer = VortexFileWriter.Create(destination, source.Schema);
            await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
            written++;
        }

        Console.Out.Write(
            "WROTE " + written.ToString(CultureInfo.InvariantCulture) + " files to " + root + "\n");
        Assert.True(written > 700);
    }

    /// <summary>Writes one corpus file out and reads it back, comparing every value.</summary>
    /// <returns>How many rows were compared.</returns>
    private static async Task<int> RoundTrip(CorpusEntry entry)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");
        try
        {
            List<string> original = [];
            DType schema;

            await using (VortexFile source = await VortexFile.OpenAsync(entry.Path, CancellationToken.None))
            {
                schema = source.Schema;
                await using VortexFileWriter writer = VortexFileWriter.Create(written, schema);
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Values.Describe(batch, original);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            List<string> readBack = [];
            await using (VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None))
            {
                if (target.RowCount != entry.RowCount)
                {
                    throw new InvalidOperationException(
                        $"wrote {entry.RowCount} rows and read back {target.RowCount}");
                }

                await foreach (RecordBatch batch in target.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Values.Describe(batch, readBack);
                }
            }

            if (original.Count != readBack.Count)
            {
                throw new InvalidOperationException(
                    $"wrote {original.Count} values and read back {readBack.Count}");
            }

            for (int i = 0; i < original.Count; i++)
            {
                if (!string.Equals(original[i], readBack[i], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"value {i} was '{original[i]}' and read back '{readBack[i]}'");
                }
            }

            return (int)entry.RowCount;
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }
}
