// Tables of sketches: a distribution of a grain is a question only a table of sketches answers, which the choice
// weighs beside the table of states of the grain; an hour whose groups outgrow what its first slice promised gives up
// its deepest tables of sketches, and every table kept holds the sketches of the rows it covers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisTableTests
{
    /// <summary>
    /// A table of states by the hour and the table of sketches of its grain, ten times its bytes: with room for both, the
    /// states are kept first, for their fewer bytes, then the sketches for the distributions they alone answer; without
    /// room for the sketches, the states alone.
    /// </summary>
    [Fact]
    public void ADistributionIsAQuestionTheStatesDoNotAnswer()
    {
        static SynopsisTableInfo Info(SynopsisTableKind kind) => new(kind, SynopsisBucket.Hour, [1], [3], 20);
        (SynopsisTableInfo, long)[] tables = [(Info(SynopsisTableKind.Coarse), 500), (Info(SynopsisTableKind.Sketches), 5_000)];
        static long Columns(int field) => field switch { 1 => 100, 3 => 10_000, _ => 0 };
        SynopsisPrices prices = new SynopsisPrices(1e-8, 1e-8);

        (bool[] kept, string[] verdicts) = SynopsisTableChoice.Choose(tables, Columns, 10_000, 1_000_000, prices);
        Assert.Equal([true, true], kept);
        Assert.Contains("over 1 questions", verdicts[1], StringComparison.Ordinal);
        (kept, verdicts) = SynopsisTableChoice.Choose(tables, Columns, 10_000, 4_000, prices);
        Assert.Equal([true, false], kept);
        Assert.StartsWith("left out: 5000 bytes past the 3500 the budget of 4000 left", verdicts[1], StringComparison.Ordinal);
    }

    /// <summary>A table of sketches reads back from its entry's options as one; a kind past it does not read.</summary>
    [Fact]
    public void ATableOfSketchesReadsBackFromItsOptions()
    {
        SynopsisTableInfo info = new(SynopsisTableKind.Sketches, SynopsisBucket.Object, [1, 2], [3, 4], 7);
        byte[] options = info.OptionBytes();
        SynopsisTableInfo read = SynopsisTableInfo.From(options)!;
        Assert.Equal((info.Kind, info.Bucket, info.Rows), (read.Kind, read.Bucket, read.Rows));
        Assert.Equal(info.Keys, read.Keys);
        Assert.Equal(info.Measures, read.Measures);
        options[1] = (byte)SynopsisTableKind.Sketches + 1;
        Assert.Null(SynopsisTableInfo.From(options));
    }

    /// <summary>
    /// The spread rows, whose request is almost unique to each row with the two keys before it: a slice holds 2 000
    /// groups of the three keys, which the first slice promises, but an hour some 19 000, past what an hour holds, so
    /// that the tables of sketches of the three keys are given up, with their reason, the hour's groups merged into
    /// those of the two keys; those are kept, and every table kept holds the sketches of the rows it covers.
    /// </summary>
    [Fact]
    public async Task SketchesWhoseGroupsOutgrowTheHourAreGivenUp()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-sketches-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            (string path, WriteReport report) = await SpreadAsync(scratch, budgetPerMille: 1_000, earlyChoiceBytes: IndexWriter.DefaultEarlyChoiceBytes);
            List<IndexWriteReport> tables = [.. report.Indexes.Where(r => r.Kind == IndexKinds.SynopsisTable)];
            string Listed() => string.Join('\n', tables.Select(t => $"  {t.Column}: {t.Outcome} {t.Reason}"));
            foreach (string grain in (string[])["Sketches per Hour of (Region, Status, Request)", "Sketches per Object of (Region, Status, Request)"])
            {
                IndexWriteReport table = Assert.Single(tables, t => t.Column == grain);
                Assert.True(
                    table.Outcome == IndexOutcome.Abandoned && table.Reason!.StartsWith("given up as its groups grew", StringComparison.Ordinal),
                    $"{grain} was not given up as its groups grew:\n{Listed()}");
            }

            Assert.True(tables.Exists(t => t.Column == "Sketches per Hour of (Region, Status)" && t.Outcome == IndexOutcome.Built), Listed());
            List<(SynopsisTableInfo Info, List<TableRow> Rows, int Chunks)> read = await SpreadCountsExactlyAsync(path);
            Assert.Contains(read, t => t.Info.Kind == SynopsisTableKind.Sketches && t.Info.Keys.Length == 2);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
