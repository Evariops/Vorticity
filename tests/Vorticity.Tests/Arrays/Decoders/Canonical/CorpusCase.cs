// Corpus plumbing for the canonical decoders' tests.
//
// The 819 golden files are the only oracle in Phase 1 that was not written by us, so every decoder
// in this component is pointed at real bytes. What this file does NOT do is the value-by-value
// conformance sweep of contract §14 - that belongs to the conformance component. It reads exactly
// the files whose root layout is a single `vortex.flat` leaf covering the whole schema, which makes
// the array blob reachable without a layout reader -- these tests hold the decoders apart from the
// layouts -- and compares the decoded rows against the sidecar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

/// <summary>Locates the checked-in golden corpus from the test source's own path.</summary>
internal static class CorpusLocator
{
    private static readonly string Root = Locate();

    internal static string Path(string relative) =>
        System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    internal static bool Exists(string relative) => System.IO.File.Exists(Path(relative));

    private static string Locate([CallerFilePath] string sourcePath = "")
    {
        // .../tests/Vorticity.Tests/Arrays/Decoders/Canonical/CorpusCase.cs
        string? dir = System.IO.Path.GetDirectoryName(sourcePath);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = System.IO.Path.Combine(dir, "Vorticity.Conformance", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"The golden corpus was not found above '{sourcePath}'.");
    }
}

/// <summary>One sidecar, parsed down to what a decoder test needs.</summary>
internal sealed class Sidecar
{
    private Sidecar(long rowCount, IReadOnlyList<int> segmentIds, List<JsonElement> rows)
    {
        RowCount = rowCount;
        SegmentIds = segmentIds;
        Rows = rows;
    }

    internal long RowCount { get; }

    internal IReadOnlyList<int> SegmentIds { get; }

    /// <summary>
    /// The expected values, one per row. Each element keeps its own JsonDocument alive, which is
    /// why the parsed documents are not disposed here.
    /// </summary>
    internal List<JsonElement> Rows { get; }

    /// <summary>
    /// Reads the sidecar for <paramref name="entry"/>, requiring the root layout to be one flat
    /// leaf. Returns null when it is not, so a caller can list candidates generously.
    /// </summary>
    internal static Sidecar? TryLoad(string entry)
    {
        string path = CorpusLocator.Path(entry + ".jsonl");
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        long rowCount = 0;
        List<int> segmentIds = new List<int>();
        List<JsonElement> rows = new List<JsonElement>();
        bool flatRoot = false;

        foreach (string line in System.IO.File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonElement root = JsonDocument.Parse(line).RootElement;
            string kind = root.GetProperty("kind").GetString() ?? string.Empty;
            switch (kind)
            {
                case "header":
                    rowCount = root.GetProperty("row_count").GetInt64();
                    break;

                case "layout":
                {
                    JsonElement tree = root.GetProperty("tree");
                    if (tree.GetProperty("encoding_id").GetString() != "vortex.flat"
                        || tree.GetProperty("children").GetArrayLength() != 0)
                    {
                        return null;
                    }

                    flatRoot = true;
                    foreach (JsonElement id in tree.GetProperty("segment_ids").EnumerateArray())
                    {
                        segmentIds.Add(id.GetInt32());
                    }

                    break;
                }

                case "rows":
                {
                    int from = root.GetProperty("from").GetInt32();
                    if (from != rows.Count)
                    {
                        throw new InvalidDataException($"Sidecar {entry} has rows out of order.");
                    }

                    foreach (JsonElement value in root.GetProperty("v").EnumerateArray())
                    {
                        rows.Add(value);
                    }

                    break;
                }

                default:
                    break;
            }
        }

        if (!flatRoot)
        {
            return null;
        }

        return new Sidecar(rowCount, segmentIds, rows);
    }
}

/// <summary>Opens a corpus file, decodes its single flat leaf, and hands back the canonical root.</summary>
internal sealed class DecodedCorpusFile : IAsyncDisposable
{
    private readonly VortexFile _file;
    private readonly SegmentOwner _owner;

    private DecodedCorpusFile(VortexFile file, SegmentOwner owner, ScanContext scan, int rootIndex, int rowCount)
    {
        _file = file;
        _owner = owner;
        Scan = scan;
        RootIndex = rootIndex;
        RowCount = rowCount;
    }

    internal ScanContext Scan { get; }

    internal int RootIndex { get; }

    internal int RowCount { get; }

    internal DType Schema => _file.Schema;

    internal static async ValueTask<DecodedCorpusFile> OpenAsync(string entry, Sidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);

        VortexFile file = await VortexFile.OpenAsync(CorpusLocator.Path(entry + ".vortex"))
            .ConfigureAwait(false);

        SegmentOwner? owner = null;
        ScanContext? scan = null;
        try
        {
            SegmentSpec spec = file.SegmentSpecs[sidecar.SegmentIds[0]];
            owner = await file.Segments.ReadAsync(spec, CancellationToken.None).ConfigureAwait(false);

            scan = new ScanContext(file);
            ArrayBlobReader.Load(scan.Nodes, owner.Buffer, scan.ArrayEncodings);

            int rowCount = checked((int)file.RowCount);
            int root = scan.Decode.Decode(scan.Nodes.Root, file.Schema, rowCount);
            return new DecodedCorpusFile(file, owner, scan, root, rowCount);
        }
        catch
        {
            scan?.Dispose();
            owner?.Release();
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Scan.Dispose();
        _owner.Release();
        await _file.DisposeAsync().ConfigureAwait(false);
    }
}
