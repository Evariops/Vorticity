// The corpus manifest, and its split into in-scope and out-of-scope files.
//
// THE SPLIT IS COMPUTED, NOT LISTED. A hand-maintained list of the 616 files Phase 1 can read would
// be wrong the first time a decoder lands and nobody would notice; worse, it is the exact mechanism
// by which a failing file gets quietly "excluded". So the classification asks the LIBRARY: every
// array id in the manifest entry goes through EncodingRegistry.ResolveArray and
// ArrayDecoderTable.IsImplemented, every layout id through ResolveLayout and
// LayoutReaderTable.IsImplemented, every extension dtype id through ExtensionDTypeRegistry.Resolve.
// A file is in scope when the build claims every component it uses, and out of scope otherwise.
//
// The expected counts are then asserted as a GUARD (ScopeSplitTests), not used as an
// input: if a decoder is registered or dropped, the split moves and the guard says so.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Conformance.Sidecar;

namespace Vorticity.Conformance.Corpus;

/// <summary>One record of <c>corpus/manifest.json</c>, reduced to what the harness needs.</summary>
internal sealed class CorpusEntry
{
    internal required string Id { get; init; }

    /// <summary>Corpus-relative path of the <c>.vortex</c>.</summary>
    internal required string Path { get; init; }

    /// <summary>Corpus-relative path of the <c>.jsonl</c> sidecar.</summary>
    internal required string SidecarPath { get; init; }

    internal required long RowCount { get; init; }

    /// <summary>The reference implementation's Display rendering of the file dtype.</summary>
    internal required string DType { get; init; }

    internal required string[] ArrayIds { get; init; }

    internal required string[] LayoutIds { get; init; }

    internal required string[] ExtensionDTypeIds { get; init; }

    internal required bool HasDTypeSegment { get; init; }

    /// <summary>How many user metadata segments the file carries.</summary>
    internal required int MetadataSegmentCount { get; init; }

    /// <summary>The file holds at least one utf8 value outside ASCII.</summary>
    internal required bool HasNonAsciiUtf8 { get; init; }

    /// <summary>The file holds a utf8 value containing a NUL.</summary>
    internal required bool HasEmbeddedNulUtf8 { get; init; }

    internal required string Sha256 { get; init; }

    internal required long SizeBytes { get; init; }

    /// <summary>The absolute path of the <c>.vortex</c>.</summary>
    internal string FullPath => CorpusCatalog.Resolve(Path);

    /// <summary>The absolute path of the sidecar.</summary>
    internal string FullSidecarPath => CorpusCatalog.Resolve(SidecarPath);

    public override string ToString() => Id;
}

/// <summary>Why one file is outside Phase 1: the component ids the build does not implement.</summary>
internal sealed class ScopeVerdict
{
    internal required CorpusEntry Entry { get; init; }

    /// <summary>Array ids this build does not decode. Empty when there are none.</summary>
    internal required string[] UnsupportedArrays { get; init; }

    /// <summary>Layout ids this build does not read.</summary>
    internal required string[] UnsupportedLayouts { get; init; }

    /// <summary>Extension dtype ids this build does not resolve.</summary>
    internal required string[] UnsupportedExtensionDTypes { get; init; }

    internal bool InScope =>
        UnsupportedArrays.Length == 0 &&
        UnsupportedLayouts.Length == 0 &&
        UnsupportedExtensionDTypes.Length == 0;

    /// <summary>Every unsupported id, whatever its kind, for a message.</summary>
    internal IEnumerable<string> AllUnsupported()
    {
        foreach (string id in UnsupportedArrays)
        {
            yield return id;
        }

        foreach (string id in UnsupportedLayouts)
        {
            yield return id;
        }

        foreach (string id in UnsupportedExtensionDTypes)
        {
            yield return id;
        }
    }
}

/// <summary>The golden corpus: located once, parsed once, classified once.</summary>
internal static class CorpusCatalog
{
    private static readonly Lazy<string> RootLazy = new Lazy<string>(() => FindConformanceRoot());
    private static readonly Lazy<CorpusEntry[]> EntriesLazy = new Lazy<CorpusEntry[]>(LoadEntries);
    private static readonly Lazy<string[]> MissingLayoutsLazy = new Lazy<string[]>(LoadMissingLayouts);
    private static readonly Lazy<ScopeVerdict[]> VerdictsLazy = new Lazy<ScopeVerdict[]>(Classify);

    /// <summary>The <c>tests/Vorticity.Conformance</c> directory.</summary>
    internal static string Root => RootLazy.Value;

    /// <summary>The <c>corpus</c> directory.</summary>
    internal static string CorpusRoot => System.IO.Path.Combine(Root, "corpus");

    /// <summary>The <c>forged</c> directory.</summary>
    internal static string ForgedRoot => System.IO.Path.Combine(Root, "forged");

    /// <summary>Every manifest record, in manifest order.</summary>
    internal static CorpusEntry[] Entries => EntriesLazy.Value;

    /// <summary>
    /// Layout ids the corpus itself declares it does not contain (<c>coverage.layouts.missing</c>).
    /// The generator's `skipped` list says why - <c>vortex.stats</c> has no writer path in 0.86.1 -
    /// and the coverage gate reads the exemption from here rather than hard-coding it.
    /// </summary>
    internal static string[] MissingLayoutIds => MissingLayoutsLazy.Value;

    /// <summary>One scope verdict per entry, parallel to <see cref="Entries"/>.</summary>
    internal static ScopeVerdict[] Verdicts => VerdictsLazy.Value;

    /// <summary>The files every component of which this build implements.</summary>
    internal static IEnumerable<ScopeVerdict> InScope()
    {
        foreach (ScopeVerdict verdict in Verdicts)
        {
            if (verdict.InScope)
            {
                yield return verdict;
            }
        }
    }

    /// <summary>The files that use at least one component this build does not implement.</summary>
    internal static IEnumerable<ScopeVerdict> OutOfScope()
    {
        foreach (ScopeVerdict verdict in Verdicts)
        {
            if (!verdict.InScope)
            {
                yield return verdict;
            }
        }
    }

    /// <summary>The verdict for one manifest id.</summary>
    internal static ScopeVerdict Verdict(string id)
    {
        foreach (ScopeVerdict verdict in Verdicts)
        {
            if (string.Equals(verdict.Entry.Id, id, StringComparison.Ordinal))
            {
                return verdict;
            }
        }

        throw new InvalidOperationException($"No corpus entry '{id}'.");
    }

    /// <summary>Turns a corpus-relative path into an absolute one.</summary>
    internal static string Resolve(string relative) =>
        System.IO.Path.Combine(CorpusRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string FindConformanceRoot([CallerFilePath] string thisFile = "")
    {
        // Walk up from this source file: the corpus is 122 MB and is deliberately NOT copied to the
        // output directory, so AppContext.BaseDirectory says nothing about where it lives.
        DirectoryInfo? directory = new FileInfo(thisFile).Directory;
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(
                directory.FullName, "tests", "Vorticity.Conformance", "corpus", "manifest.json");
            if (System.IO.File.Exists(candidate))
            {
                return System.IO.Path.Combine(directory.FullName, "tests", "Vorticity.Conformance");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests/Vorticity.Conformance/corpus above '{thisFile}'.");
    }

    private static string[] LoadMissingLayouts()
    {
        string text = System.IO.File.ReadAllText(
            System.IO.Path.Combine(CorpusRoot, "manifest.json"), Encoding.UTF8);
        JsonValue manifest = JsonParser.Parse(text);
        return Strings(manifest.Require("coverage").Require("layouts"), "missing");
    }

    private static CorpusEntry[] LoadEntries()
    {
        string text = System.IO.File.ReadAllText(
            System.IO.Path.Combine(CorpusRoot, "manifest.json"), Encoding.UTF8);
        JsonValue manifest = JsonParser.Parse(text);

        string format = manifest.RequireString("format");
        if (!string.Equals(format, "vortex-conformance-corpus/2", StringComparison.Ordinal))
        {
            throw new SidecarFormatException($"corpus/manifest.json declares format '{format}'.");
        }

        JsonValue files = manifest.Require("files");
        if (files.Kind != JsonKind.Array)
        {
            throw new SidecarFormatException("corpus/manifest.json: 'files' is not an array");
        }

        CorpusEntry[] entries = new CorpusEntry[files.Items.Length];
        for (int i = 0; i < files.Items.Length; i++)
        {
            JsonValue file = files.Items[i];
            entries[i] = new CorpusEntry
            {
                Id = file.RequireString("id"),
                Path = file.RequireString("path"),
                SidecarPath = file.RequireString("sidecar"),
                RowCount = file.RequireInt64("row_count"),
                DType = file.RequireString("dtype"),
                ArrayIds = Strings(file, "array_ids"),
                LayoutIds = Strings(file, "layout_ids"),
                ExtensionDTypeIds = Strings(file, "extension_dtype_ids"),
                HasDTypeSegment = file.RequireBoolean("has_dtype_segment"),
                MetadataSegmentCount = file.Require("metadata_segments").Items.Length,
                HasNonAsciiUtf8 = file.RequireBoolean("utf8_non_ascii"),
                HasEmbeddedNulUtf8 = file.RequireBoolean("utf8_embedded_nul"),
                Sha256 = file.RequireString("sha256"),
                SizeBytes = file.RequireInt64("size_bytes"),
            };
        }

        return entries;
    }

    private static string[] Strings(JsonValue owner, string key)
    {
        JsonValue array = owner.Require(key);
        if (array.Kind != JsonKind.Array)
        {
            throw new SidecarFormatException($"manifest member '{key}' is not an array");
        }

        string[] values = new string[array.Items.Length];
        for (int i = 0; i < values.Length; i++)
        {
            JsonValue item = array.Items[i];
            if (item.Kind != JsonKind.String)
            {
                throw new SidecarFormatException($"manifest member '{key}' holds a non-string");
            }

            values[i] = item.Text;
        }

        return values;
    }

    private static ScopeVerdict[] Classify()
    {
        Phase1Components.EnsureRegistered();

        CorpusEntry[] entries = Entries;
        ScopeVerdict[] verdicts = new ScopeVerdict[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            CorpusEntry entry = entries[i];
            verdicts[i] = new ScopeVerdict
            {
                Entry = entry,
                UnsupportedArrays = Filter(entry.ArrayIds, Phase1Components.DecodesArray),
                UnsupportedLayouts = Filter(entry.LayoutIds, Phase1Components.ReadsLayout),
                UnsupportedExtensionDTypes = Filter(
                    entry.ExtensionDTypeIds, Phase1Components.ResolvesExtensionDType),
            };
        }

        return verdicts;
    }

    private static string[] Filter(string[] ids, Func<string, bool> supported)
    {
        List<string>? unsupported = null;
        foreach (string id in ids)
        {
            if (supported(id))
            {
                continue;
            }

            unsupported ??= new List<string>();
            unsupported.Add(id);
        }

        return unsupported is null ? [] : unsupported.ToArray();
    }
}
