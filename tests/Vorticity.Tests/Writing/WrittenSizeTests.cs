// How big our output is, next to the reference's, over the whole corpus.
//
// docs/05-benchmarks.md §3 states the write target as "output size <= 105% of Rust's on the same
// data, edition and configuration, with the delta reported per dataset", and explains why it is not
// byte-parity: the compressor is a sampler, so two honest implementations diverge on borderline
// data. We are nowhere near 105% yet, which is exactly why this needs to be a measurement rather
// than a remembered figure - docs/90-registry.md quotes ratios that nothing reproduces.
//
// SO THIS IS A RATCHET, NOT A GATE. Asserting 105% today would be a permanently red test that
// everyone learns to ignore; asserting nothing would let the ratio drift upward unnoticed. The
// ceiling is set just above the measured value, so the test fails on a regression and passes on
// every improvement - and the ceiling is lowered by hand when the improvement lands, which is the
// only part a human should have to do.
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
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class WrittenSizeTests
{
    /// <summary>
    /// The whole-corpus ceiling. Lower it when a compression improvement lands; never raise it
    /// without saying in the commit message what got bigger and why that is acceptable.
    ///
    /// Now just above the 1.05 TARGET rather than far above it, so this has stopped being a
    /// ratchet on a failing number and started being a guard on a passing one.
    /// </summary>
    private const double CorpusCeiling = 1.06;

    /// <summary>How many of the worst offenders to name, so the number is actionable.</summary>
    private const int Worst = 12;

    [Fact]
    public async Task TheCorpusRewritesWithinTheSizeRatchet()
    {
        Decoders.EnsureRegistered();

        long ours = 0;
        long theirs = 0;
        int files = 0;
        List<(string Id, double Ratio, long Ours, long Theirs)> entries = [];

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            long reference = new FileInfo(entry.Path).Length;
            long written = await Rewrite(entry);
            ours += written;
            theirs += reference;
            files++;
            if (reference > 0)
            {
                entries.Add((entry.Id, (double)written / reference, written, reference));
            }
        }

        entries.Sort((a, b) => b.Ours - b.Theirs == a.Ours - a.Theirs
            ? b.Ratio.CompareTo(a.Ratio)
            : (b.Ours - b.Theirs).CompareTo(a.Ours - a.Theirs));

        StringBuilder report = new StringBuilder();
        double ratio = (double)ours / theirs;
        report.Append("WRITTEN SIZE: ")
            .Append(files.ToString(CultureInfo.InvariantCulture))
            .Append(" files, ")
            .Append(ours.ToString(CultureInfo.InvariantCulture))
            .Append(" bytes against the reference's ")
            .Append(theirs.ToString(CultureInfo.InvariantCulture))
            .Append(" -- ratio ")
            .Append(ratio.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" (target 1.05, ceiling ")
            .Append(CorpusCeiling.ToString("F2", CultureInfo.InvariantCulture))
            .Append(")\n");

        // Ranked by BYTES LOST, not by ratio: a 40x ratio on a 300-byte file is a rounding error,
        // and optimizing for it would be the most expensive way to move the total by nothing.
        report.Append("  worst by bytes lost:\n");
        for (int i = 0; i < Math.Min(Worst, entries.Count); i++)
        {
            (string id, double each, long mine, long reference) = entries[i];
            report.Append("    ")
                .Append(id.PadRight(46))
                .Append((mine - reference).ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(" bytes  ")
                .Append(each.ToString("F2", CultureInfo.InvariantCulture))
                .Append("x  (")
                .Append(mine.ToString(CultureInfo.InvariantCulture))
                .Append(" vs ")
                .Append(reference.ToString(CultureInfo.InvariantCulture))
                .Append(")\n");
        }

        Console.Out.Write(report.ToString());

        Assert.True(files > 700, $"only {files} files were measured");
        Assert.True(
            ratio <= CorpusCeiling,
            $"the corpus rewrites at {ratio:F3}x, above the {CorpusCeiling:F2}x ratchet.\n{report}");
    }

    /// <summary>Writes one corpus file out and returns how many bytes it took.</summary>
    private static async Task<long> Rewrite(CorpusEntry entry)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-size-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile source = await VortexFile.OpenAsync(entry.Path, CancellationToken.None))
            await using (VortexFileWriter writer = VortexFileWriter.Create(written, source.Schema))
            {
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return new FileInfo(written).Length;
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
