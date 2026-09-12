// How much the CALLER's batching decides the file's size.
//
// WrittenSizeTests measures one batching: whatever the source file's own chunking produces. That
// leaves a property of the public API completely unguarded, and it is a large one. Rewriting
// `types/binary_nonnull_r8193` at forced batch sizes, where the only variable is how many chunks
// VortexFileWriter emits:
//
//     2 chunks (the source's own)     65 980 bytes
//     3 chunks                        92 212
//     5 chunks                       146 076
//     9 chunks                       169 860
//
// Roughly 26 kB per extra chunk, because every chunk builds and writes its OWN dictionary of the
// same distinct values. The reference shares one values child across a column's chunks through the
// `vortex.dict` layout, which this writer does not emit.
//
// SO THIS IS A RATCHET ON A KNOWN-BAD NUMBER, which is the unusual case and worth saying plainly.
// It does not assert that the behaviour is acceptable - it is not. It asserts that it does not get
// WORSE while nobody is looking, and it is the test that will turn red, loudly and correctly, on
// the day a shared dictionary layout lands. Lower the ceiling then.
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What splitting the same rows across more batches costs on disk.</summary>
public sealed class BatchingSizeTests
{
    /// <summary>A column the writer dictionary-encodes, at 8193 rows so it splits.</summary>
    private const string Entry = "types/binary_nonnull_r8193";

    /// <summary>Rows per batch for the fragmented write; the file holds 8193.</summary>
    private const int SmallBatch = 1024;

    /// <summary>
    /// What nine chunks may cost against two, as a multiple.
    /// </summary>
    /// <remarks>
    /// Measured at 2.57×. LOWER THIS when the writer stops rewriting a dictionary per chunk; it is
    /// a bound on a defect, not a budget for one.
    /// </remarks>
    private const double FragmentationCeiling = 2.65;

    [Fact]
    public async Task SplittingTheSameRowsAcrossMoreBatchesStaysWithinItsRatchet()
    {
        Decoders.EnsureRegistered();

        (long whole, int wholeChunks) = await Rewrite(cap: 0);
        (long fragmented, int smallChunks) = await Rewrite(SmallBatch);

        Assert.True(smallChunks > wholeChunks, "the small-batch write must produce more chunks");

        double ratio = (double)fragmented / whole;
        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"BATCHING SIZE: {Entry} written as {wholeChunks} chunks is {whole} bytes, as " +
            $"{smallChunks} chunks is {fragmented} -- {ratio:F2}x (ceiling {FragmentationCeiling:F2})");
        Console.Out.WriteLine(report);

        Assert.True(ratio <= FragmentationCeiling, report);
    }

    /// <summary>Reads the entry and writes it back, optionally forcing a batch size.</summary>
    /// <param name="cap">Rows per batch, or 0 to take the source's own chunking.</param>
    private static async Task<(long Bytes, int Chunks)> Rewrite(int cap)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-batching-{Guid.NewGuid():N}.vortex");
        try
        {
            int chunks = 0;
            await using (VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Entry), CancellationToken.None))
            await using (VortexFileWriter writer = VortexFileWriter.Create(written, source.Schema))
            {
                ScanBuilder scan = source.Scan();
                if (cap > 0)
                {
                    scan = scan.WithMaxBatchRows(cap);
                }

                await foreach (RecordBatch batch in scan.ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    chunks++;
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return (new FileInfo(written).Length, chunks);
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
