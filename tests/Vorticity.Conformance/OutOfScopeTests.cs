// The other half of the acceptance test: "every file outside scope fails with a named component
// rather than a wrong answer."
//
// The message is a requirement, not a courtesy: the exception must
// name BOTH the component id as the file spells it and the component kind. A reader that throws
// "unsupported encoding" tells a user nothing about which encoding, which edition introduced it, or
// which of the three registries refused it - and a requirement no test checks is a wish.
//
// "Rather than a wrong answer" is the other half of that sentence and it is enforced here too. A
// file can carry its unsupported encoding ONLY inside a zone map, which an unfiltered scan never
// decodes (ZonedLayoutReader reads only the data child). For such a file, not throwing is
// correct - and the harness proves it by requiring the file to read back value for value, exactly
// as an in-scope file must. Silence alone is never accepted.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Conformance.Comparison;
using Vorticity.Conformance.Corpus;
using Vorticity.Conformance.Sidecar;
using Vorticity.File;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Conformance;

public sealed class OutOfScopeTests
{
    private static readonly ComponentKind[] Kinds = [ComponentKind.Array, ComponentKind.Layout, ComponentKind.DType];

    /// <summary>
    /// Every file that uses at least one component this build does not implement.
    /// </summary>
    /// <remarks>
    /// A `vortex.acme.future_codec` placeholder keeps the theory non-empty, because xunit fails a
    /// `[MemberData]` theory with no cases rather than skipping it, and a test class that cannot
    /// run is worse than one that runs over one synthetic case. The placeholder is recognised and
    /// skipped by the theory body; every real entry, when the corpus grows one, is exercised.
    /// </remarks>
    public static TheoryData<string> OutOfScopeFiles()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (ScopeVerdict verdict in CorpusCatalog.OutOfScope())
        {
            data.Add(verdict.Entry.Id);
        }

        if (data.Count == 0)
        {
            data.Add(NoSuchEntry);
        }

        return data;
    }

    /// <summary>The placeholder id <see cref="OutOfScopeFiles"/> uses when the corpus is all in scope.</summary>
    private const string NoSuchEntry = "<none: every corpus file is in scope>";

    /// <summary>
    /// THE CORPUS HAS NO OUT-OF-SCOPE FILE LEFT, so this asserts that and keeps the machinery.
    /// </summary>
    /// <remarks>
    /// `FailsWithTheComponentIdAndTheKind` below is still a theory over
    /// <see cref="OutOfScopeFiles"/> -- it simply has nothing to run on, and an empty theory is a
    /// test that passes by finding nothing. This Fact is the guard against that: it states the
    /// count, so a corpus regeneration that introduces a component this build lacks makes the
    /// count move rather than quietly re-enabling a theory nobody was watching.
    /// </remarks>
    [Fact]
    public void EveryCorpusFileIsInScope()
    {
        int outOfScope = 0;
        foreach (ScopeVerdict verdict in CorpusCatalog.OutOfScope())
        {
            outOfScope++;
        }

        Assert.Equal(0, outOfScope);
        Assert.Equal(876, CorpusCatalog.Entries.Length);
    }

    [Theory]
    [MemberData(nameof(OutOfScopeFiles))]
    public async Task FailsWithTheComponentIdAndTheKind(string id)
    {
        if (string.Equals(id, NoSuchEntry, StringComparison.Ordinal))
        {
            // The placeholder: the corpus has no out-of-scope file, which `EveryCorpusFileIsInScope`
            // asserts directly rather than by this theory finding nothing.
            return;
        }

        ScopeVerdict verdict = CorpusCatalog.Verdict(id);
        CorpusEntry entry = verdict.Entry;

        if (!entry.HasDTypeSegment)
        {
            // A file that embeds no DType and is given none cannot be opened at all: that is
            // malformed input, not an unsupported component. It is
            // the only such file in the corpus.
            await Assert.ThrowsAsync<VortexFormatException>(async () =>
                await VortexFile.OpenAsync(entry.FullPath, TestContext.Current.CancellationToken));
            return;
        }

        Phase1Components.EnsureRegistered();

        List<string> unsupportedOnTheDataPath = UnsupportedOnTheDataPath(verdict);
        if (unsupportedOnTheDataPath.Count == 0)
        {
            await ReadsCorrectlyDespiteAnUnreachableComponentAsync(verdict);
            return;
        }

        // Opening must succeed whatever the file uses: ids are classified at open and refused at
        // use. An open that throws here is a lazy-resolution regression.
        await using VortexFile file = await VortexFile.OpenAsync(entry.FullPath, TestContext.Current.CancellationToken);
        Assert.Equal(entry.RowCount, file.RowCount);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        Assert.Contains(error.ComponentId, unsupportedOnTheDataPath);
        Assert.Contains(error.Kind, Kinds);

        // Both, in the message a user actually sees, where the kind is spelled in lower case.
        Assert.Contains(error.ComponentId, error.Message, StringComparison.Ordinal);
        Assert.Contains(error.Kind.ToString().ToLowerInvariant(), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file whose unsupported components are all unreachable must read back exactly, like any
    /// other file. "It did not throw" is not the assertion; "it returned the right answer" is.
    /// </summary>
    private static async Task ReadsCorrectlyDespiteAnUnreachableComponentAsync(ScopeVerdict verdict)
    {
        FileResult result = await ConformanceRunner.CompareAsync(
            verdict.Entry, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.True(result.Log.IsClean, result.Describe());
        Assert.Equal(verdict.Entry.RowCount, result.Rows);
    }

    /// <summary>
    /// The file's unsupported ids, restricted to the ones an unfiltered scan actually reaches. The
    /// sidecar's layout tree says where each id lives.
    /// </summary>
    internal static List<string> UnsupportedOnTheDataPath(ScopeVerdict verdict)
    {
        using SidecarReader sidecar = SidecarReader.Open(verdict.Entry.FullSidecarPath);
        LayoutComponentIndex index = LayoutComponentIndex.Build(sidecar.LayoutTree);

        List<string> reachable = new List<string>();
        foreach (string id in verdict.AllUnsupported())
        {
            // An unsupported EXTENSION dtype is reached through the dtype rather than the layout,
            // so it is always on the data path; the corpus has none, and this keeps the day it does
            // from being silently classified as unreachable.
            if (index.OnTheDataPath.Contains(id) || !index.InZoneMapsOnly.Contains(id))
            {
                reachable.Add(id);
            }
        }

        return reachable;
    }
}
