// tests/Vorticity.Conformance/forged/manifest.json, read with the same hand-written parser.
//
// A forged fixture is not reference-implementation output, so it is trusted differently: the
// manifest states the source file, its hash, and the exact byte patch, "so the fixture can be
// re-derived rather than trusted". This type carries enough of the record to re-derive it, and
// ForgedFixtureTests does exactly that before any behaviour is asserted on it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vorticity.Conformance.Sidecar;

namespace Vorticity.Conformance.Corpus;

/// <summary>The byte patch that turns a corpus file into a forged fixture.</summary>
internal sealed class ForgedPatch
{
    internal required long Offset { get; init; }

    internal required int Length { get; init; }

    internal required string From { get; init; }

    internal required string To { get; init; }
}

/// <summary>One record of <c>forged/manifest.json</c>.</summary>
internal sealed class ForgedFixture
{
    internal required string Id { get; init; }

    /// <summary>Forged-directory-relative path.</summary>
    internal required string Path { get; init; }

    internal required string Description { get; init; }

    /// <summary>The corpus-relative path of the file the fixture was patched from, if any.</summary>
    internal string? Source { get; init; }

    internal string? SourceSha256 { get; init; }

    internal ForgedPatch? Patch { get; init; }

    internal required string Sha256 { get; init; }

    internal required long SizeBytes { get; init; }

    internal required long RowCount { get; init; }

    /// <summary>The behaviours the manifest declares, verbatim.</summary>
    internal required string[] Expectations { get; init; }

    /// <summary>The absolute path of the fixture.</summary>
    internal string FullPath => System.IO.Path.Combine(
        CorpusCatalog.ForgedRoot, Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public override string ToString() => Id;
}

/// <summary>The forged fixtures, located and parsed once.</summary>
internal static class ForgedCatalog
{
    private static readonly Lazy<ForgedFixture[]> FixturesLazy = new Lazy<ForgedFixture[]>(Load);

    internal static ForgedFixture[] Fixtures => FixturesLazy.Value;

    internal static ForgedFixture Find(string id)
    {
        foreach (ForgedFixture fixture in Fixtures)
        {
            if (string.Equals(fixture.Id, id, StringComparison.Ordinal))
            {
                return fixture;
            }
        }

        throw new InvalidOperationException($"No forged fixture '{id}'.");
    }

    private static ForgedFixture[] Load()
    {
        string path = System.IO.Path.Combine(CorpusCatalog.ForgedRoot, "manifest.json");
        JsonValue manifest = JsonParser.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8));

        string format = manifest.RequireString("format");
        if (!string.Equals(format, "vortex-conformance-forged/1", StringComparison.Ordinal))
        {
            throw new SidecarFormatException($"forged/manifest.json declares format '{format}'.");
        }

        JsonValue files = manifest.Require("files");
        List<ForgedFixture> fixtures = new List<ForgedFixture>(files.Items.Length);
        foreach (JsonValue file in files.Items)
        {
            JsonValue? patch = file.Find("patch");
            fixtures.Add(new ForgedFixture
            {
                Id = file.RequireString("id"),
                Path = file.RequireString("path"),
                Description = file.RequireString("description"),
                Source = file.Find("source")?.Text,
                SourceSha256 = file.Find("source_sha256")?.Text,
                Patch = patch is null || patch.IsNull ? null : new ForgedPatch
                {
                    Offset = patch.RequireInt64("offset"),
                    Length = (int)patch.RequireInt64("length"),
                    From = patch.RequireString("from"),
                    To = patch.RequireString("to"),
                },
                Sha256 = file.RequireString("sha256"),
                SizeBytes = file.RequireInt64("size_bytes"),
                RowCount = file.RequireInt64("row_count"),
                Expectations = Strings(file, "expectations"),
            });
        }

        return fixtures.ToArray();
    }

    private static string[] Strings(JsonValue owner, string key)
    {
        JsonValue? array = owner.Find(key);
        if (array is null || array.Kind != JsonKind.Array)
        {
            return [];
        }

        string[] values = new string[array.Items.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = array.Items[i].Text;
        }

        return values;
    }
}
