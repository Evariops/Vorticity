// Access to the golden corpus and its manifest. 819 files written by Vortex 0.86.1, each with the
// row count, dtype, postscript size and metadata keys this component must reproduce.
//
// The manifest is 1.5 MB of JSON: parsed once, with JsonDocument rather than the serializer, so
// nothing here needs reflection and the AOT analyzer stays quiet.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vorticity.Tests.File;

/// <summary>One record from <c>corpus/manifest.json</c>.</summary>
internal sealed class CorpusEntry
{
    internal required string Id { get; init; }

    internal required string Path { get; init; }

    internal required long RowCount { get; init; }

    internal required string DType { get; init; }

    internal required int PostscriptBytes { get; init; }

    internal required bool HasDTypeSegment { get; init; }

    internal required bool HasFileStatistics { get; init; }

    internal required long SizeBytes { get; init; }

    internal required string[] MetadataKeys { get; init; }

    internal required int[] MetadataLengths { get; init; }
}

/// <summary>The golden corpus, located and parsed once.</summary>
internal static class CorpusManifest
{
    private static readonly Lazy<string> RootLazy = new Lazy<string>(() => FindRoot());
    private static readonly Lazy<CorpusEntry[]> EntriesLazy = new Lazy<CorpusEntry[]>(Load);

    /// <summary>Absolute path of <c>tests/Vorticity.Conformance/corpus</c>.</summary>
    internal static string Root => RootLazy.Value;

    /// <summary>Every manifest record, in manifest order.</summary>
    internal static CorpusEntry[] Entries => EntriesLazy.Value;

    /// <summary>The absolute path of one corpus file.</summary>
    /// <param name="entry">The manifest record.</param>
    /// <returns>The path.</returns>
    internal static string FullPath(CorpusEntry entry) =>
        System.IO.Path.Combine(Root, entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>The bytes of one corpus file.</summary>
    /// <param name="id">The manifest id, e.g. <c>types/no_dtype_segment</c>.</param>
    /// <returns>The file bytes.</returns>
    internal static byte[] Bytes(string id) => System.IO.File.ReadAllBytes(FullPath(Find(id)));

    /// <summary>Looks up one record by id.</summary>
    /// <param name="id">The manifest id.</param>
    /// <returns>The record.</returns>
    internal static CorpusEntry Find(string id)
    {
        foreach (CorpusEntry entry in Entries)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"No corpus entry '{id}'.");
    }

    private static string FindRoot([CallerFilePath] string callerFilePath = "")
    {
        // Two independent starting points so the corpus is found both from the test project's
        // output directory and from a project built outside the repository, whose output is not
        // under it.
        string?[] starts = [System.IO.Path.GetDirectoryName(callerFilePath), AppContext.BaseDirectory];
        foreach (string? start in starts)
        {
            string? directory = start;
            while (!string.IsNullOrEmpty(directory))
            {
                string candidate = System.IO.Path.Combine(
                    directory, "tests", "Vorticity.Conformance", "corpus");
                if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "manifest.json")))
                {
                    return candidate;
                }

                directory = System.IO.Path.GetDirectoryName(directory);
            }
        }

        throw new InvalidOperationException(
            "Could not locate tests/Vorticity.Conformance/corpus from " +
            $"'{callerFilePath}' or '{AppContext.BaseDirectory}'.");
    }

    private static CorpusEntry[] Load()
    {
        byte[] json = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Root, "manifest.json"));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement files = document.RootElement.GetProperty("files");

        List<CorpusEntry> entries = new List<CorpusEntry>(files.GetArrayLength());
        foreach (JsonElement file in files.EnumerateArray())
        {
            JsonElement metadata = file.GetProperty("metadata_segments");
            int count = metadata.GetArrayLength();
            string[] keys = new string[count];
            int[] lengths = new int[count];
            int i = 0;
            foreach (JsonElement segment in metadata.EnumerateArray())
            {
                keys[i] = segment.GetProperty("key").GetString()!;
                lengths[i] = segment.GetProperty("len").GetInt32();
                i++;
            }

            entries.Add(new CorpusEntry
            {
                Id = file.GetProperty("id").GetString()!,
                Path = file.GetProperty("path").GetString()!,
                RowCount = file.GetProperty("row_count").GetInt64(),
                DType = file.GetProperty("dtype").GetString()!,
                PostscriptBytes = file.GetProperty("postscript_bytes").GetInt32(),
                HasDTypeSegment = file.GetProperty("has_dtype_segment").GetBoolean(),
                HasFileStatistics = file.GetProperty("has_file_statistics").GetBoolean(),
                SizeBytes = file.GetProperty("size_bytes").GetInt64(),
                MetadataKeys = keys,
                MetadataLengths = lengths,
            });
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The corpus manifest lists no files.");
        }

        return entries.ToArray();
    }

    /// <summary>Formats a count for an assertion message, culture-invariantly.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The rendered value.</returns>
    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
