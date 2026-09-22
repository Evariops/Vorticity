using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Vorticity.Samples;

/// <summary>Short forms of a write report, so that a file of fifty chunks prints on a line.</summary>
internal static class WriteReportText
{
    /// <summary>Each distinct chunk size with how many chunks have it, in order of first appearance: <c>32768 x24, 8192 x24, 16384, 576</c>.</summary>
    internal static string Chunks(ImmutableArray<int> chunkRows) =>
        string.Join(", ", chunkRows.GroupBy(rows => rows).Select(g => g.Count() == 1 ? $"{g.Key}" : $"{g.Key} x{g.Count()}"));

    /// <summary>How many chunks got each encoding, most frequent first: <c>RunEnd x49, Sequence x1</c>.</summary>
    internal static string Encodings(ImmutableArray<string> encodings) =>
        string.Join(", ", encodings.GroupBy(e => e).OrderByDescending(g => g.Count()).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}"));

    /// <summary>The report's bytes by kind.</summary>
    internal static string Bytes(WriteBytes bytes) =>
        $"data {bytes.Data}, statistics {bytes.Statistics}, zone maps {bytes.ZoneMaps}, indexes {bytes.Indexes}, footer {bytes.Footer}";
}
