// User metadata segments: keys AND payload bytes, against the sidecar.
//
// They need their own test because both corpus files that carry metadata - types/user_metadata_segments
// and containers/postscript_max_metadata - use encodings Phase 1 does not decode, so the in-scope
// value pass never reaches them. Their metadata is readable all the same: metadata values are lazy
// and live in their own segments, entirely independent of the column encodings, and a reader that
// could not hand back a 256-byte blob because some other column is ALP would be wrong.
//
// The sidecar distinguishes a present-but-empty segment (`b64: ""`) from an absent one (no key at
// all), and conformance.empty is exactly that case.
using System;
using System.Threading.Tasks;
using Vorticity.Conformance.Comparison;
using Vorticity.Conformance.Corpus;
using Vorticity.Conformance.Sidecar;
using Vorticity.File;
using Xunit;

namespace Vorticity.Conformance;

public sealed class MetadataValueTests
{
    public static TheoryData<string> FilesWithMetadata()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (CorpusEntry entry in CorpusCatalog.Entries)
        {
            if (entry.MetadataSegmentCount > 0)
            {
                data.Add(entry.Id);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FilesWithMetadata))]
    public async Task MetadataPayloadsMatchTheSidecar(string id)
    {
        CorpusEntry entry = CorpusCatalog.Verdict(id).Entry;
        MismatchLog log = new MismatchLog(id);

        using SidecarReader sidecar = SidecarReader.Open(entry.FullSidecarPath);
        sidecar.VerifyPairing(entry.FullPath);
        Assert.Equal(entry.MetadataSegmentCount, sidecar.Metadata.Length);

        await using VortexFile file = await VortexFile.OpenAsync(
            entry.FullPath, TestContext.Current.CancellationToken);

        await ConformanceRunner.CompareMetadataAsync(
            file, sidecar, log, TestContext.Current.CancellationToken);

        Assert.True(log.IsClean, log.Render());
    }

    /// <summary>An empty metadata value is a value, and it is not a null.</summary>
    [Fact]
    public void APresentButEmptySegmentIsNotAnAbsentOne()
    {
        CorpusEntry entry = CorpusCatalog.Verdict("types/user_metadata_segments").Entry;
        using SidecarReader sidecar = SidecarReader.Open(entry.FullSidecarPath);

        bool sawEmpty = false;
        foreach (SidecarMetadataSegment segment in sidecar.Metadata)
        {
            if (string.Equals(segment.Key, "conformance.empty", StringComparison.Ordinal))
            {
                sawEmpty = true;
                Assert.Empty(segment.Value);
            }
        }

        Assert.True(sawEmpty, "the fixture must carry a present-but-empty metadata segment");
    }
}
