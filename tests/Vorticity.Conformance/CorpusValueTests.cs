// THE ACCEPTANCE TEST OF THE READER, one test case per corpus file.
//
// Every corpus file whose encodings this build implements reads back value for value equal to its
// Rust-produced sidecar. One [Theory] case per file rather than one loop over the whole corpus,
// because at this volume the runner's own per-case reporting is the localization mechanism: a
// failure names the file in the test id before its message names the column and the row.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Conformance.Corpus;
using Xunit;

namespace Vorticity.Conformance;

public sealed class CorpusValueTests
{
    /// <summary>Every file whose every component this build implements.</summary>
    public static TheoryData<string> InScopeFiles()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (ScopeVerdict verdict in CorpusCatalog.InScope())
        {
            data.Add(verdict.Entry.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InScopeFiles))]
    public async Task ReadsBackEveryValue(string id)
    {
        ScopeVerdict verdict = CorpusCatalog.Verdict(id);
        Assert.True(verdict.InScope, $"{id} is not in scope and must not be in this theory");

        FileResult result = await ConformanceRunner.CompareAsync(verdict.Entry, TestContext.Current.CancellationToken);

        if (result.Error is not null)
        {
            // Rethrown rather than reported as an assertion, so the stack trace of the real failure
            // survives: a decoder that throws is a different bug from a decoder that lies.
            throw new InvalidOperationException(
                $"{id}: reading the file threw before the comparison finished.", result.Error);
        }

        Assert.True(result.Log.IsClean, result.Describe());
    }
}
