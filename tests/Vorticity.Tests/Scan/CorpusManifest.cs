// The corpus manifest, read once for the whole scan suite.
//
// DISPOSITION IS COMPUTED, NEVER LISTED. Whether a file is in Phase 1 scope is decided by asking
// the registries - ArrayDecoderTable.IsImplemented, LayoutReaderTable.IsImplemented,
// ExtensionDTypeRegistry.Resolve - about the ids the manifest says the file actually contains. A
// hand-maintained list rots the first time a decoder lands (contract §14.1), and it would let a
// missing registration hide as "out of scope".
//
// `array_ids` is the WALKED truth; `declared_array_ids` over-reports, because the writer
// pre-populates array_specs with every id its editions permit for byte determinism (manifest
// caveat 4). Reading the wrong one would put 40-odd files out of scope that are perfectly readable.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using Vorticity.Arrays;
using Vorticity.Layouts;

namespace Vorticity.Tests.Scan;

internal sealed class CorpusEntry
{
    internal CorpusEntry(
        string id,
        long rowCount,
        string dtype,
        bool hasDTypeSegment,
        string[] arrayIds,
        string[] layoutIds,
        string[] extensionIds)
    {
        Id = id;
        RowCount = rowCount;
        DTypeDisplay = dtype;
        HasDTypeSegment = hasDTypeSegment;
        ArrayIds = arrayIds;
        LayoutIds = layoutIds;
        ExtensionIds = extensionIds;
    }

    internal string Id { get; }

    internal long RowCount { get; }

    internal string DTypeDisplay { get; }

    /// <summary>
    /// <see langword="false"/> for a file written with <c>exclude_dtype()</c>. Opening one without
    /// <see cref="Vorticity.File.VortexOpenOptions.DType"/> is a format error by design
    /// (contract §7.4), so the scan tests skip it: it never reaches the scan at all.
    /// </summary>
    internal bool HasDTypeSegment { get; }

    internal string[] ArrayIds { get; }

    internal string[] LayoutIds { get; }

    internal string[] ExtensionIds { get; }

    internal string Path => Corpus.Path(Id);
}

internal static class CorpusManifest
{
    private static readonly Lazy<CorpusEntry[]> Entries = new Lazy<CorpusEntry[]>(Load);

    internal static IReadOnlyList<CorpusEntry> All => Entries.Value;

    /// <summary>Every file whose components this build claims to read.</summary>
    internal static List<CorpusEntry> InScope()
    {
        Decoders.EnsureRegistered();
        List<CorpusEntry> result = new List<CorpusEntry>();
        IReadOnlyList<CorpusEntry> all = All;
        for (int i = 0; i < all.Count; i++)
        {
            if (IsInScope(all[i]))
            {
                result.Add(all[i]);
            }
        }

        return result;
    }

    internal static CorpusEntry Get(string id)
    {
        IReadOnlyList<CorpusEntry> all = All;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Id == id)
            {
                return all[i];
            }
        }

        throw new KeyNotFoundException("No corpus entry named " + id + ".");
    }

    internal static bool IsInScope(CorpusEntry entry)
    {
        for (int i = 0; i < entry.ArrayIds.Length; i++)
        {
            if (!ArrayDecoderTable.IsImplemented(EncodingRegistry.ResolveArray(Utf8(entry.ArrayIds[i]))))
            {
                return false;
            }
        }

        for (int i = 0; i < entry.LayoutIds.Length; i++)
        {
            if (!LayoutReaderTable.IsImplemented(EncodingRegistry.ResolveLayout(Utf8(entry.LayoutIds[i]))))
            {
                return false;
            }
        }

        for (int i = 0; i < entry.ExtensionIds.Length; i++)
        {
            if (ExtensionDTypeRegistry.Resolve(Utf8(entry.ExtensionIds[i])) == ExtensionKind.Unknown)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Utf8(string id) => Encoding.UTF8.GetBytes(id);

    private static CorpusEntry[] Load()
    {
        using FileStream stream = System.IO.File.OpenRead(Corpus.ManifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        JsonElement files = document.RootElement.GetProperty("files");
        List<CorpusEntry> entries = new List<CorpusEntry>(files.GetArrayLength());
        foreach (JsonElement file in files.EnumerateArray())
        {
            entries.Add(new CorpusEntry(
                file.GetProperty("id").GetString()!,
                file.GetProperty("row_count").GetInt64(),
                file.GetProperty("dtype").GetString()!,
                !file.TryGetProperty("has_dtype_segment", out JsonElement hasDType) || hasDType.GetBoolean(),
                Strings(file, "array_ids"),
                Strings(file, "layout_ids"),
                Strings(file, "extension_dtype_ids")));
        }

        return entries.ToArray();
    }

    private static string[] Strings(JsonElement file, string name)
    {
        if (!file.TryGetProperty(name, out JsonElement array))
        {
            return [];
        }

        string[] result = new string[array.GetArrayLength()];
        int next = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            result[next++] = item.GetString()!;
        }

        return result;
    }
}
