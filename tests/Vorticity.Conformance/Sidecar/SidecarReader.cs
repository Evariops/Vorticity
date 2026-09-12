// The sidecar reader, written against corpus/SIDECAR.md and nothing else.
//
// Two properties of that document drive the shape of this class:
//
//   * "Dispatch on the top-level `kind`" - `kind` is ALSO the discriminator inside dtype trees, so
//     the only correct reader parses each line and looks at the top-level member. This one does,
//     and rejects a line whose kind is unknown or out of order rather than skipping it: a sidecar
//     grammar that grew a line kind we ignore is a silent hole in the oracle.
//   * "up to 128 values each; row index = `from` + position in `v`" - the value stream is far too
//     large to hold (108 MB across the corpus), so rows are STREAMED: one line is parsed, compared
//     against the batch that covers it, and dropped. `null_counts` is the line after the last
//     `rows` line, so it only becomes available once the row stream is exhausted, which is exactly
//     the order a scan produces its batches in.
//
// The header's sha256 is checked against the .vortex before anything else is trusted, as the
// document requires: a sidecar regenerated against a different file is otherwise undetectable.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Vorticity.Conformance.Sidecar;

/// <summary>One user metadata segment as the sidecar records it.</summary>
internal readonly record struct SidecarMetadataSegment(string Key, byte[] Value);

/// <summary>One <c>null_counts.by_path</c> entry, in sidecar order.</summary>
internal readonly record struct SidecarNullCount(string Path, long Count);

/// <summary>
/// A streaming reader over one <c>.jsonl</c> sidecar. Construction consumes every line up to the
/// first <c>rows</c> line; the rows themselves are pulled one line at a time.
/// </summary>
internal sealed class SidecarReader : IDisposable
{
    internal const string ExpectedFormat = "vortex-conformance-sidecar/2";
    internal const int MaxValuesPerLine = 128;

    private readonly StreamReader _reader;
    private readonly string _path;
    private JsonValue? _pending;
    private long _nextRow;
    private bool _rowsDone;
    private SidecarNullCount[]? _nullCounts;
    private int _lineNumber;

    private SidecarReader(StreamReader reader, string path)
    {
        _reader = reader;
        _path = path;
        ZoneMaps = [];
        Metadata = [];
    }

    /// <summary>`format` from the header line.</summary>
    internal string Format { get; private set; } = string.Empty;

    /// <summary>`entry_id`: the manifest id this sidecar belongs to.</summary>
    internal string EntryId { get; private set; } = string.Empty;

    /// <summary>`path`: the corpus-relative path of the <c>.vortex</c> file.</summary>
    internal string FilePath { get; private set; } = string.Empty;

    /// <summary>`sha256` of the <c>.vortex</c> file, lower-case hex.</summary>
    internal string Sha256 { get; private set; } = string.Empty;

    /// <summary>`dtype`: the reference implementation's Display rendering of the file dtype.</summary>
    internal string DTypeDisplay { get; private set; } = string.Empty;

    /// <summary>`row_count` from the header.</summary>
    internal long RowCount { get; private set; }

    /// <summary>The `dtype` line's parsed tree.</summary>
    internal JsonValue DTypeTree { get; private set; } = JsonValue.Null;

    /// <summary>The `layout` line's parsed tree.</summary>
    internal JsonValue LayoutTree { get; private set; } = JsonValue.Null;

    /// <summary>User metadata segments, in stored order.</summary>
    internal SidecarMetadataSegment[] Metadata { get; private set; }

    /// <summary>The `file_stats` line, whether or not statistics are present.</summary>
    internal JsonValue FileStats { get; private set; } = JsonValue.Null;

    /// <summary>The `zone_map` lines, in depth-first layout order.</summary>
    internal JsonValue[] ZoneMaps { get; private set; }

    /// <summary>How many rows have been handed out by <see cref="TryReadRows"/> so far.</summary>
    internal long RowsRead => _nextRow;

    /// <summary>
    /// The `null_counts` line's `by_path` entries. Available only once the row stream is exhausted,
    /// because the line follows the last `rows` line.
    /// </summary>
    internal SidecarNullCount[] NullCounts =>
        _nullCounts ?? throw new SidecarFormatException(
            $"{_path}: null_counts was requested before the row stream was exhausted");

    /// <summary>Opens a sidecar and reads everything up to its first `rows` line.</summary>
    /// <param name="sidecarPath">Absolute path of the <c>.jsonl</c>.</param>
    /// <returns>The reader. The caller disposes it.</returns>
    internal static SidecarReader Open(string sidecarPath)
    {
        FileStream stream = new FileStream(
            sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        StreamReader text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 1 << 16);
        SidecarReader reader = new SidecarReader(text, sidecarPath);
        try
        {
            reader.ReadPreamble();
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Verifies the header's `sha256` against the actual bytes of the <c>.vortex</c>, as SIDECAR.md
    /// requires before anything else in the sidecar is trusted.
    /// </summary>
    /// <param name="vortexPath">Absolute path of the paired <c>.vortex</c>.</param>
    /// <exception cref="SidecarFormatException">The hashes differ.</exception>
    internal void VerifyPairing(string vortexPath)
    {
        using FileStream stream = new FileStream(
            vortexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        byte[] hash = SHA256.HashData(stream);
        string hex = Convert.ToHexStringLower(hash);
        if (!string.Equals(hex, Sha256, StringComparison.Ordinal))
        {
            throw new SidecarFormatException(
                $"{_path}: the header's sha256 {Sha256} is not the hash of {vortexPath} ({hex}). " +
                "The sidecar was generated against different bytes.");
        }
    }

    /// <summary>
    /// Pulls the next `rows` line. Returns false once they are exhausted, at which point
    /// <see cref="NullCounts"/> becomes available.
    /// </summary>
    /// <param name="from">The absolute file row index of <c>values[0]</c>.</param>
    /// <param name="values">The line's values, one per row.</param>
    /// <returns><see langword="true"/> when a line was read.</returns>
    internal bool TryReadRows(out long from, out JsonValue[] values)
    {
        if (_rowsDone)
        {
            from = 0;
            values = [];
            return false;
        }

        JsonValue? line = TakePending();
        if (line is null)
        {
            throw new SidecarFormatException($"{_path}: the file ended before its null_counts line");
        }

        string kind = KindOf(line);
        if (string.Equals(kind, "rows", StringComparison.Ordinal))
        {
            from = line.RequireInt64("from");
            JsonValue v = line.Require("v");
            if (v.Kind != JsonKind.Array)
            {
                throw new SidecarFormatException($"{_path} line {_lineNumber}: 'v' is not an array");
            }

            if (from != _nextRow)
            {
                throw new SidecarFormatException(
                    $"{_path} line {_lineNumber}: a rows line starts at {from}, but {_nextRow} rows " +
                    "have been read - the value stream is not contiguous");
            }

            if (v.Items.Length is 0 or > MaxValuesPerLine)
            {
                throw new SidecarFormatException(
                    $"{_path} line {_lineNumber}: a rows line carries {v.Items.Length} values; " +
                    $"SIDECAR.md allows 1..{MaxValuesPerLine}");
            }

            values = v.Items;
            _nextRow += values.Length;
            return true;
        }

        if (string.Equals(kind, "null_counts", StringComparison.Ordinal))
        {
            ReadNullCounts(line);
            from = 0;
            values = [];
            return false;
        }

        throw new SidecarFormatException(
            $"{_path} line {_lineNumber}: unexpected line kind '{kind}' in the row stream");
    }

    /// <summary>
    /// Drains the row stream without comparing anything, so <see cref="NullCounts"/> and the
    /// end-of-file checks still run for a file whose rows were consumed elsewhere.
    /// </summary>
    internal void FinishRows()
    {
        while (TryReadRows(out _, out _))
        {
        }
    }

    public void Dispose() => _reader.Dispose();

    private void ReadPreamble()
    {
        JsonValue header = RequireLine("header");
        Format = header.RequireString("format");
        if (!string.Equals(Format, ExpectedFormat, StringComparison.Ordinal))
        {
            throw new SidecarFormatException(
                $"{_path}: format is '{Format}', not '{ExpectedFormat}'. This reader was written " +
                "against that version of SIDECAR.md and will not guess at another.");
        }

        EntryId = header.RequireString("entry_id");
        FilePath = header.RequireString("path");
        Sha256 = header.RequireString("sha256");
        DTypeDisplay = header.RequireString("dtype");
        RowCount = header.RequireInt64("row_count");

        // The legend is normative and travels with the file; its absence means the header is not
        // the header this reader was written against.
        JsonValue legend = header.Require("legend");
        if (legend.Kind != JsonKind.Object)
        {
            throw new SidecarFormatException($"{_path}: the header carries no legend object");
        }

        DTypeTree = RequireLine("dtype").Require("tree");
        LayoutTree = RequireLine("layout").Require("tree");

        JsonValue metadata = RequireLine("metadata");
        Metadata = ReadMetadata(metadata);

        FileStats = RequireLine("file_stats");

        List<JsonValue> zones = new List<JsonValue>();
        while (true)
        {
            JsonValue? next = PeekPending();
            if (next is null)
            {
                // A zero-row file still carries null_counts, so running out of lines here is a
                // truncated sidecar and not a legal shape.
                throw new SidecarFormatException($"{_path}: the sidecar ends after file_stats");
            }

            if (!string.Equals(KindOf(next), "zone_map", StringComparison.Ordinal))
            {
                break;
            }

            zones.Add(TakePending()!);
        }

        ZoneMaps = zones.ToArray();
    }

    private SidecarMetadataSegment[] ReadMetadata(JsonValue line)
    {
        JsonValue segments = line.Require("segments");
        if (segments.Kind != JsonKind.Array)
        {
            throw new SidecarFormatException($"{_path}: metadata.segments is not an array");
        }

        SidecarMetadataSegment[] result = new SidecarMetadataSegment[segments.Items.Length];
        for (int i = 0; i < segments.Items.Length; i++)
        {
            JsonValue segment = segments.Items[i];
            string key = segment.RequireString("key");

            // SIDECAR.md: `b64` is "" for a present-but-empty segment, and the key is simply absent
            // when there is no such segment. Those are different states and neither is null.
            JsonValue? b64 = segment.Find("b64");
            byte[] value = b64 is null || b64.IsNull ? [] : Convert.FromBase64String(b64.Text);
            result[i] = new SidecarMetadataSegment(key, value);
        }

        return result;
    }

    private void ReadNullCounts(JsonValue line)
    {
        JsonValue byPath = line.Require("by_path");
        if (byPath.Kind != JsonKind.Object)
        {
            throw new SidecarFormatException($"{_path}: null_counts.by_path is not an object");
        }

        SidecarNullCount[] counts = new SidecarNullCount[byPath.Keys.Length];
        for (int i = 0; i < byPath.Keys.Length; i++)
        {
            JsonValue count = byPath.Values[i];
            if (count.Kind != JsonKind.Number ||
                !long.TryParse(count.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                throw new SidecarFormatException(
                    $"{_path}: null_counts['{byPath.Keys[i]}'] is not an integer");
            }

            counts[i] = new SidecarNullCount(byPath.Keys[i], parsed);
        }

        _nullCounts = counts;
        _rowsDone = true;

        if (PeekPending() is JsonValue extra)
        {
            throw new SidecarFormatException(
                $"{_path} line {_lineNumber}: '{KindOf(extra)}' follows null_counts, which must be last");
        }
    }

    private JsonValue RequireLine(string kind)
    {
        JsonValue? line = TakePending();
        if (line is null)
        {
            throw new SidecarFormatException($"{_path}: the sidecar ends where a '{kind}' line was expected");
        }

        string actual = KindOf(line);
        if (!string.Equals(actual, kind, StringComparison.Ordinal))
        {
            throw new SidecarFormatException(
                $"{_path} line {_lineNumber}: expected a '{kind}' line, found '{actual}'");
        }

        return line;
    }

    private JsonValue? PeekPending() => _pending ??= ReadLine();

    private JsonValue? TakePending()
    {
        JsonValue? value = PeekPending();
        _pending = null;
        return value;
    }

    private JsonValue? ReadLine()
    {
        while (true)
        {
            string? text = _reader.ReadLine();
            if (text is null)
            {
                return null;
            }

            _lineNumber++;
            if (text.Length == 0)
            {
                continue;
            }

            return JsonParser.Parse(text);
        }
    }

    private string KindOf(JsonValue line)
    {
        if (line.Kind != JsonKind.Object)
        {
            throw new SidecarFormatException($"{_path} line {_lineNumber}: a line is not a JSON object");
        }

        // Deliberately the TOP-LEVEL member, by name, from the parsed object: `kind` also
        // discriminates dtype nodes, and a reader that scans the text for it mis-dispatches
        // (SIDECAR.md, "Line order").
        return line.RequireString("kind");
    }
}
