// Access to the golden corpus for the layout tests: the manifest's per-file layout ids and
// aggregate specs, plus the sidecar's `layout` and `zone_map` lines, which are a free oracle for
// everything this component derives - the encoding id, the pushed-down dtype, the row count, the
// segment ids and the metadata length of every node.
//
// JsonDocument rather than the serializer, so nothing here needs reflection and the AOT analyzer
// stays quiet. Every JsonElement is materialized into a plain object before the document is
// disposed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vorticity.Tests.Layouts;

/// <summary>One record from <c>corpus/manifest.json</c>, reduced to what a layout test needs.</summary>
internal sealed class LayoutCorpusEntry
{
    internal required string Id { get; init; }

    internal required string Path { get; init; }

    internal required string Sidecar { get; init; }

    internal required long RowCount { get; init; }

    internal required string[] LayoutIds { get; init; }

    internal required string[] ArrayIds { get; init; }

    /// <summary>The manifest's dtype display, e.g. <c>{ints=i64, strs=utf8}</c>.</summary>
    internal required string DType { get; init; }

    internal required int ZoneMaps { get; init; }

    /// <summary>False for <c>types/no_dtype_segment</c>, whose schema the caller must supply.</summary>
    internal required bool HasDTypeSegment { get; init; }
}

/// <summary>A dtype as the sidecar spells it, materialized out of its JsonDocument.</summary>
internal sealed class SidecarDType
{
    internal required string Kind { get; init; }

    internal bool Nullable { get; init; }

    internal string? PType { get; init; }

    internal int Precision { get; init; }

    internal int Scale { get; init; }

    internal uint Size { get; init; }

    internal string? ExtensionId { get; init; }

    internal SidecarDType? Element { get; init; }

    internal SidecarDType? Key { get; init; }

    internal SidecarDType? Value { get; init; }

    internal SidecarDType? Storage { get; init; }

    internal string[] FieldNames { get; init; } = [];

    internal SidecarDType[] Fields { get; init; } = [];
}

/// <summary>One node of the sidecar's <c>layout</c> line.</summary>
internal sealed class SidecarLayoutNode
{
    internal required string Name { get; init; }

    internal required string EncodingId { get; init; }

    internal required SidecarDType DType { get; init; }

    internal required long RowCount { get; init; }

    internal required uint[] SegmentIds { get; init; }

    internal required int MetadataBytes { get; init; }

    internal required SidecarLayoutNode[] Children { get; init; }
}

/// <summary>One <c>zone_map</c> line, in the sidecar's depth-first order.</summary>
internal sealed class SidecarZoneMap
{
    internal required long ZoneLength { get; init; }

    internal required int ZoneCount { get; init; }

    internal required string[] Aggregates { get; init; }

    internal required SidecarDType ZonesDType { get; init; }
}

/// <summary>The golden corpus, located and parsed once.</summary>
internal static class LayoutCorpus
{
    private static readonly Lazy<string> RootLazy = new Lazy<string>(() => FindRoot());
    private static readonly Lazy<LayoutCorpusEntry[]> EntriesLazy = new Lazy<LayoutCorpusEntry[]>(Load);

    /// <summary>Absolute path of <c>tests/Vorticity.Conformance/corpus</c>.</summary>
    internal static string Root => RootLazy.Value;

    /// <summary>Every manifest record, in manifest order.</summary>
    internal static LayoutCorpusEntry[] Entries => EntriesLazy.Value;

    internal static string FullPath(LayoutCorpusEntry entry) =>
        System.IO.Path.Combine(Root, entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    internal static string SidecarPath(LayoutCorpusEntry entry) =>
        System.IO.Path.Combine(Root, entry.Sidecar.Replace('/', System.IO.Path.DirectorySeparatorChar));

    internal static LayoutCorpusEntry Find(string id)
    {
        foreach (LayoutCorpusEntry entry in Entries)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"No corpus entry '{id}'.");
    }

    /// <summary>The sidecar's <c>layout</c> line, as a tree.</summary>
    internal static SidecarLayoutNode ReadLayout(LayoutCorpusEntry entry)
    {
        foreach (string line in SidecarLines(entry))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("kind").GetString() == "layout")
            {
                return ReadLayoutNode(document.RootElement.GetProperty("tree"));
            }
        }

        throw new InvalidOperationException($"Sidecar for '{entry.Id}' has no layout line.");
    }

    /// <summary>Every <c>zone_map</c> line, in sidecar (depth-first) order.</summary>
    internal static SidecarZoneMap[] ReadZoneMaps(LayoutCorpusEntry entry)
    {
        List<SidecarZoneMap> maps = new List<SidecarZoneMap>();
        foreach (string line in SidecarLines(entry))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? kind = root.GetProperty("kind").GetString();
            if (kind == "rows" || kind == "null_counts")
            {
                break;
            }

            if (kind != "zone_map")
            {
                continue;
            }

            JsonElement aggregates = root.GetProperty("aggregates");
            string[] names = new string[aggregates.GetArrayLength()];
            int i = 0;
            foreach (JsonElement aggregate in aggregates.EnumerateArray())
            {
                names[i++] = aggregate.GetString()!;
            }

            maps.Add(new SidecarZoneMap
            {
                ZoneLength = root.GetProperty("zone_len").GetInt64(),
                ZoneCount = root.GetProperty("nzones").GetInt32(),
                Aggregates = names,
                ZonesDType = ReadDType(root.GetProperty("zones").GetProperty("dtype")),
            });
        }

        return maps.ToArray();
    }

    /// <summary>Lines of one sidecar, stopping before the bulk <c>rows</c> section.</summary>
    private static IEnumerable<string> SidecarLines(LayoutCorpusEntry entry)
    {
        using StreamReader reader = new StreamReader(SidecarPath(entry));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            yield return line;
        }
    }

    private static SidecarLayoutNode ReadLayoutNode(JsonElement node)
    {
        JsonElement segments = node.GetProperty("segment_ids");
        uint[] segmentIds = new uint[segments.GetArrayLength()];
        int s = 0;
        foreach (JsonElement segment in segments.EnumerateArray())
        {
            segmentIds[s++] = segment.GetUInt32();
        }

        SidecarLayoutNode[] children = [];
        if (node.TryGetProperty("children", out JsonElement childArray))
        {
            children = new SidecarLayoutNode[childArray.GetArrayLength()];
            int c = 0;
            foreach (JsonElement child in childArray.EnumerateArray())
            {
                children[c++] = ReadLayoutNode(child);
            }
        }

        return new SidecarLayoutNode
        {
            Name = node.GetProperty("name").GetString()!,
            EncodingId = node.GetProperty("encoding_id").GetString()!,
            DType = ReadDType(node.GetProperty("dtype")),
            RowCount = node.GetProperty("row_count").GetInt64(),
            SegmentIds = segmentIds,
            MetadataBytes = node.GetProperty("metadata_bytes").GetInt32(),
            Children = children,
        };
    }

    private static SidecarDType ReadDType(JsonElement dtype)
    {
        string kind = dtype.GetProperty("kind").GetString()!;
        bool nullable = dtype.TryGetProperty("nullable", out JsonElement n) && n.GetBoolean();

        switch (kind)
        {
            case "primitive":
                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    PType = dtype.GetProperty("ptype").GetString(),
                };

            case "decimal":
                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    Precision = dtype.GetProperty("precision").GetInt32(),
                    Scale = dtype.GetProperty("scale").GetInt32(),
                };

            case "list":
                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    Element = ReadDType(dtype.GetProperty("element")),
                };

            case "fixed_size_list":
                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    Size = dtype.GetProperty("size").GetUInt32(),
                    Element = ReadDType(dtype.GetProperty("element")),
                };

            case "map":
                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    Key = ReadDType(dtype.GetProperty("key")),
                    Value = ReadDType(dtype.GetProperty("value")),
                };

            case "extension":
                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    ExtensionId = dtype.GetProperty("id").GetString(),
                    Storage = ReadDType(dtype.GetProperty("storage")),
                };

            case "struct":
            {
                JsonElement fields = dtype.GetProperty("fields");
                int count = fields.GetArrayLength();
                string[] names = new string[count];
                SidecarDType[] types = new SidecarDType[count];
                int i = 0;
                foreach (JsonElement field in fields.EnumerateArray())
                {
                    names[i] = field.GetProperty("name").GetString()!;
                    types[i] = ReadDType(field.GetProperty("dtype"));
                    i++;
                }

                return new SidecarDType
                {
                    Kind = kind,
                    Nullable = nullable,
                    FieldNames = names,
                    Fields = types,
                };
            }

            default:
                return new SidecarDType { Kind = kind, Nullable = nullable };
        }
    }

    private static string FindRoot([CallerFilePath] string callerFilePath = "")
    {
        // Two independent starting points so the corpus is found both from the real test project's
        // output directory and from a check project whose bin/ is outside the repo.
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

    private static LayoutCorpusEntry[] Load()
    {
        byte[] json = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Root, "manifest.json"));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement files = document.RootElement.GetProperty("files");

        List<LayoutCorpusEntry> entries = new List<LayoutCorpusEntry>(files.GetArrayLength());
        foreach (JsonElement file in files.EnumerateArray())
        {
            entries.Add(new LayoutCorpusEntry
            {
                Id = file.GetProperty("id").GetString()!,
                Path = file.GetProperty("path").GetString()!,
                Sidecar = file.GetProperty("sidecar").GetString()!,
                RowCount = file.GetProperty("row_count").GetInt64(),
                LayoutIds = Strings(file.GetProperty("layout_ids")),
                ArrayIds = Strings(file.GetProperty("array_ids")),
                DType = file.GetProperty("dtype").GetString()!,
                ZoneMaps = file.GetProperty("zone_maps").GetInt32(),
                HasDTypeSegment = file.GetProperty("has_dtype_segment").GetBoolean(),
            });
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The corpus manifest lists no files.");
        }

        return entries.ToArray();
    }

    private static string[] Strings(JsonElement array)
    {
        string[] values = new string[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            values[i++] = value.GetString()!;
        }

        return values;
    }

}
