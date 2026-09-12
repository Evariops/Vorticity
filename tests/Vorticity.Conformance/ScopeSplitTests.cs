// PHASE1-CONTRACTS.md §14.1: the in-scope split, and the guard that keeps it honest.
//
// The split is computed from the library's own registries (see Phase1Components), so these numbers
// are a MEASUREMENT of what this build claims, not a configuration of what it should claim. That is
// what makes them worth asserting: the day a decoder is added the counts move and this test says
// which component moved them, and the day a decoder is quietly dropped it says that too.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Conformance.Corpus;
using Xunit;

namespace Vorticity.Conformance;

public sealed class ScopeSplitTests
{
    private const int ExpectedInScope = 616;
    private const int ExpectedOutOfScope = 203;

    [Fact]
    public void TheCorpusSplitsIntoSixHundredSixteenInScopeAndTwoHundredThreeOut()
    {
        int total = CorpusCatalog.Entries.Length;
        int inScope = 0;
        int outOfScope = 0;
        SortedDictionary<string, int> blocking = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (ScopeVerdict verdict in CorpusCatalog.Verdicts)
        {
            if (verdict.InScope)
            {
                inScope++;
                continue;
            }

            outOfScope++;
            foreach (string id in verdict.AllUnsupported())
            {
                blocking[id] = blocking.TryGetValue(id, out int count) ? count + 1 : 1;
            }
        }

        StringBuilder report = new StringBuilder();
        report.Append("corpus scope split: ").Append(Text(inScope)).Append(" in, ")
            .Append(Text(outOfScope)).Append(" out of ").Append(Text(total)).Append('\n');
        foreach (KeyValuePair<string, int> pair in blocking)
        {
            report.Append("  ").Append(pair.Key).Append(": ").Append(Text(pair.Value)).Append(" files\n");
        }

        Console.Out.Write(report.ToString());

        Assert.Equal(819, total);
        Assert.Equal(ExpectedInScope, inScope);
        Assert.Equal(ExpectedOutOfScope, outOfScope);
    }

    [Fact]
    public void EveryCorpusFileIsEitherInScopeOrNamesTheComponentThatExcludesIt()
    {
        foreach (ScopeVerdict verdict in CorpusCatalog.OutOfScope())
        {
            bool named = false;
            foreach (string unused in verdict.AllUnsupported())
            {
                named = true;
                break;
            }

            Assert.True(named, $"{verdict.Entry.Id} is out of scope for no stated reason");
        }
    }

    /// <summary>
    /// PHASE1-CONTRACTS.md §15.1, as a measurement rather than a comment:
    /// <see cref="Vorticity.Arrays.ArrayDecoderTable"/>'s static constructor must name every
    /// decoder the build owns, so that a caller who opens a file and scans it from an application -
    /// no test harness in the process - decodes something. While that constructor was empty the
    /// harness registered the decoders itself and every corpus number below was a harness result,
    /// not a library one. This is the test that stops that from coming back silently.
    /// </summary>
    [Fact]
    public void TheShippedDecoderTableIsWiredUp()
    {
        Assert.False(
            Phase1Components.ShippedTableWasEmpty,
            "ArrayDecoderTable shipped EMPTY: no array decoder is registered by the library " +
            "itself, so VortexFile.Scan() throws VortexUnsupportedException on the first batch " +
            "of every file. Its static constructor must register all 23 decoders (14 canonical, " +
            "9 compressed: " +
            string.Join(", ", Phase1Components.CompressedDecodersRegisteredByTheHarness) + ").");

        foreach (string id in Phase1Components.CompressedDecodersRegisteredByTheHarness)
        {
            Assert.True(Phase1Components.DecodesArray(id), id + " is not in the shipped table");
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
