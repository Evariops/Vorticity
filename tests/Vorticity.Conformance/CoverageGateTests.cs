// The corpus coverage gate: the union of `array_specs` and `layout_specs` across the corpus must
// cover every component we claim to support, and a claimed-but-untested encoding fails the build.
//
// Sharpened here to the union across the IN-SCOPE files, because that is the set the value
// comparison actually reads. An encoding that appears only in files we refuse is claimed and
// untested, which is the state this gate exists to catch.
//
// The one exemption is data-driven: `coverage.layouts.missing` in the corpus manifest, whose
// `skipped` list explains that vortex.stats has no writer path in Vortex 0.86.1 at all. Reading the
// exemption from the corpus keeps it visible and keeps it from silently growing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Conformance.Corpus;
using Vorticity.Layouts;
using Xunit;

namespace Vorticity.Conformance;

public sealed class CoverageGateTests
{
    [Fact]
    public void EveryArrayEncodingThisBuildDecodesAppearsInAnInScopeFile()
    {
        HashSet<string> used = InScopeArrayIds();
        List<string> untested = new List<string>();

        foreach (ArrayEncodingId id in Enum.GetValues<ArrayEncodingId>())
        {
            if (id == ArrayEncodingId.Unknown || !ArrayDecoderTable.IsImplemented(id))
            {
                continue;
            }

            if (!used.Contains(WireId(id)))
            {
                untested.Add(WireId(id));
            }
        }

        Assert.True(
            untested.Count == 0,
            "these encodings are decoded by this build but appear in no in-scope corpus file, so " +
            "nothing verifies them against the reference: " + string.Join(", ", untested));
    }

    [Fact]
    public void EveryLayoutThisBuildReadsAppearsInTheCorpusOrIsDeclaredMissing()
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScopeVerdict verdict in CorpusCatalog.InScope())
        {
            foreach (string id in verdict.Entry.LayoutIds)
            {
                used.Add(id);
            }
        }

        HashSet<string> exempt = new HashSet<string>(CorpusCatalog.MissingLayoutIds, StringComparer.Ordinal);
        List<string> untested = new List<string>();

        foreach (LayoutEncodingId id in Enum.GetValues<LayoutEncodingId>())
        {
            if (id == LayoutEncodingId.Unknown || !LayoutReaderTable.IsImplemented(id))
            {
                continue;
            }

            string wire = LayoutWireId(id);
            if (!used.Contains(wire) && !exempt.Contains(wire))
            {
                untested.Add(wire);
            }
        }

        Assert.True(
            untested.Count == 0,
            "these layouts are read by this build but appear in no in-scope corpus file and are not " +
            "declared missing by the corpus: " + string.Join(", ", untested));
    }

    /// <summary>
    /// The gap this gate cannot close, stated rather than left to be discovered: every corpus file
    /// carrying non-ASCII or NUL-bearing UTF-8 VALUES is compressed with vortex.fsst or
    /// vortex.onpair and is therefore out of Phase 1 scope. So the in-scope pass compares no
    /// multi-byte string value, and the sidecar's `char_count` - which exists precisely because it
    /// differs from `len` on every non-ASCII value - is never exercised by it.
    /// (Non-ASCII field NAMES are covered: types/struct_field_names is in scope.)
    /// </summary>
    [Fact]
    public void ReportsTheNonAsciiUtf8CoverageGap()
    {
        int inScope = 0;
        foreach (ScopeVerdict verdict in CorpusCatalog.InScope())
        {
            if (verdict.Entry.HasNonAsciiUtf8 || verdict.Entry.HasEmbeddedNulUtf8)
            {
                inScope++;
            }
        }

        int total = 0;
        foreach (CorpusEntry entry in CorpusCatalog.Entries)
        {
            if (entry.HasNonAsciiUtf8 || entry.HasEmbeddedNulUtf8)
            {
                total++;
            }
        }

        Console.Out.Write(
            "UTF-8 COVERAGE: " + inScope.ToString(CultureInfo.InvariantCulture) + " of " +
            total.ToString(CultureInfo.InvariantCulture) + " corpus files carrying non-ASCII or " +
            "NUL-bearing utf8 values are in scope; the rest reach a component this build does not " +
            "decode.\n");

        Assert.True(total > 0, "the corpus is supposed to carry non-ASCII utf8 somewhere");

        // The number this gate exists for. Every non-ASCII utf8 file in the corpus was out of scope
        // until vortex.fsst and vortex.onpair landed, so the suite was checking UTF-8 handling
        // against nothing at all -- multi-byte sequences straddling the 12-byte inline/reference
        // view boundary, embedded NULs, the lot. A regression that put them back out of scope would
        // be invisible in the pass count.
        Assert.True(
            inScope * 2 > total,
            $"only {inScope} of {total} non-ASCII utf8 files are in scope; UTF-8 handling is then " +
            "largely untested whatever the rest of the suite reports");
    }

    private static HashSet<string> InScopeArrayIds()
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScopeVerdict verdict in CorpusCatalog.InScope())
        {
            foreach (string id in verdict.Entry.ArrayIds)
            {
                used.Add(id);
            }
        }

        return used;
    }

    private static string WireId(ArrayEncodingId id) => id switch
    {
        ArrayEncodingId.Null => "vortex.null",
        ArrayEncodingId.Bool => "vortex.bool",
        ArrayEncodingId.Primitive => "vortex.primitive",
        ArrayEncodingId.Decimal => "vortex.decimal",
        ArrayEncodingId.VarBin => "vortex.varbin",
        ArrayEncodingId.VarBinView => "vortex.varbinview",
        ArrayEncodingId.Struct => "vortex.struct",
        ArrayEncodingId.List => "vortex.list",
        ArrayEncodingId.ListView => "vortex.listview",
        ArrayEncodingId.FixedSizeList => "vortex.fixed_size_list",
        ArrayEncodingId.Extension => "vortex.ext",
        ArrayEncodingId.Chunked => "vortex.chunked",
        ArrayEncodingId.Constant => "vortex.constant",
        ArrayEncodingId.Masked => "vortex.masked",
        ArrayEncodingId.FastLanesFor => "fastlanes.for",
        ArrayEncodingId.FastLanesBitPacked => "fastlanes.bitpacked",
        ArrayEncodingId.FastLanesRle => "fastlanes.rle",
        ArrayEncodingId.ZigZag => "vortex.zigzag",
        ArrayEncodingId.RunEnd => "vortex.runend",
        ArrayEncodingId.Dict => "vortex.dict",
        ArrayEncodingId.Sparse => "vortex.sparse",
        ArrayEncodingId.Sequence => "vortex.sequence",
        _ => "vortex.bytebool",
    };

    private static string LayoutWireId(LayoutEncodingId id) => id switch
    {
        LayoutEncodingId.Flat => "vortex.flat",
        LayoutEncodingId.Chunked => "vortex.chunked",
        LayoutEncodingId.Struct => "vortex.struct",
        LayoutEncodingId.Dict => "vortex.dict",
        LayoutEncodingId.Zoned => "vortex.zoned",
        _ => "vortex.stats",
    };
}
