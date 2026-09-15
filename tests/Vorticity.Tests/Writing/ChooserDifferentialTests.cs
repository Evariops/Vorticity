// Stage R3's oracle (IMPL-PLAN.md §1.2): the chooser by formulas of docs/11-write-strategy.md §3.4.1
// is built BESIDE the chooser it replaces, and the two are compared plan against plan on every
// column chunk of every in-scope corpus file, through the probe `ColumnCompressor.Differential`.
//
// WHY PLANS AND NOT BYTES. `WrittenSizeTests` is byte-exact and it is the anchor of the whole
// refactor, but a byte total cannot see two divergences that compensate, and a byte-identical file
// can hide a chooser that reaches the same plan for a different reason. A description says what the
// chooser DECIDED -- scheme, width, transform, reference, entry count, a fingerprint of the arrays
// -- and two choosers that agree on every description agree on every byte and on why.
//
// TWO QUESTIONS, TWO TESTS. Under today's run-end rule the formula chooser must agree with today's
// chooser on every chunk: that is the test of the harness itself, and a disagreement there is a bug
// in the formulas. Under the spec's rule -- run-end priced in bytes and made to compete -- every
// disagreement is a chunk the spec would encode differently, and the test asserts the only thing it
// can without writing the file twice: that run-end competing is the ONLY source of disagreement.
// What those chunks cost on the wire is the question stage R4 answers by letting the formula chooser
// decide and reading `WrittenSizeTests`.
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

public sealed class ChooserDifferentialTests
{
    [Fact]
    public async Task UnderTodaysRuleTheFormulaChooserAgreesOnEveryChunk()
    {
        List<string> disagreements = await Sweep(runEndCompetes: false);
        Assert.True(
            disagreements.Count == 0,
            $"{disagreements.Count} chunk(s) where the chooser by formulas disagrees with today's " +
            "under today's own rule, so the formulas are wrong somewhere:\n" +
            string.Join("\n", disagreements));
    }

    [Fact]
    public async Task UnderTheSpecsRuleOnlyRunEndCompetingChangesAPlan()
    {
        List<string> disagreements = await Sweep(runEndCompetes: true);

        // The report, for the decision R4 takes: which chunks, from what, to what.
        StringBuilder report = new StringBuilder();
        report.Append("CHOOSER DIFFERENTIAL: ")
            .Append(disagreements.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" chunk(s) the spec's run-end rule would encode differently\n");
        foreach (string line in disagreements)
        {
            report.Append("  ").Append(line).Append('\n');
        }

        Console.Out.Write(report.ToString());

        List<string> foreign = disagreements.FindAll(static line => !line.Contains(": runend ", StringComparison.Ordinal));
        Assert.True(
            foreign.Count == 0,
            $"{foreign.Count} disagreement(s) that are not run-end competing, which is the only " +
            "rule the two choosers differ on:\n" + string.Join("\n", foreign));
    }

    /// <summary>
    /// Writes every in-scope corpus file with the probe installed and collects one line per
    /// disagreeing chunk: <c>file: today =&gt; formula</c>.
    /// </summary>
    private static async Task<List<string>> Sweep(bool runEndCompetes)
    {
        Decoders.EnsureRegistered();
        string root = Path.Combine(
            Path.GetTempPath(), "vorticity-differential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        List<string> disagreements = [];
        int files = 0;
        try
        {
            foreach (CorpusEntry entry in CorpusManifest.InScope())
            {
                if (!entry.HasDTypeSegment)
                {
                    continue;
                }

                string id = entry.Id;
                string destination = Path.Combine(root, id.Replace('/', '_') + ".vortex");

                // Installed inside the loop so that the reported file is the one being written,
                // and cleared in `finally` so that a failing write cannot leak it into another test
                // on the same async flow.
                ColumnCompressor.Differential.Value = new ColumnCompressor.DifferentialProbe(
                    runEndCompetes,
                    (node, today, formula) => disagreements.Add(id + " node " + node + ": " + today + " => " + formula));
                try
                {
                    await using VortexFile source = await VortexFile.OpenAsync(
                        entry.Path, VortexOpenOptions.Default, CancellationToken.None);
                    await using VortexFileWriter writer = VortexFileWriter.Create(destination, source.Schema);
                    await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                        .WithCancellation(CancellationToken.None))
                    {
                        await writer.WriteAsync(batch, CancellationToken.None);
                    }

                    await writer.CompleteAsync(CancellationToken.None);
                }
                finally
                {
                    ColumnCompressor.Differential.Value = null;
                }

                files++;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.True(files > 700, "the sweep must cover the corpus for a clean run to mean anything");
        return disagreements;
    }
}
