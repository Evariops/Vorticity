using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vorticity.Types.Variants;

/// <summary>
/// The shredding an object's rows pay for, from their census: a path is shredded when the bytes its
/// occurrences no longer take in the residual pass what its typed column takes over every row of the
/// chunks that shred it, nulls included, and the fixed cost of the path in each chunk, in the class
/// most of its values hold. When more paths pay than a chunk's encoding tree may hold, those that save
/// the most are kept.
/// </summary>
/// <remarks>
/// <para>
/// The constants were measured on one chunk of 100 000 rows (<c>vortex-queries --micro shredcost</c>,
/// 2026-10-19), a hundred paths held by every row and by the first half of the rows. A shredded path
/// held by one row adds 154 to 217 bytes to its chunk: its typed column, its validity, their encodings
/// and its name in the tree's dtype, and 4 to 10 tables to the chunk's encoding tree. An occurrence in
/// the residual takes, once compressed, 3.6 bytes for an integer, 3.2 for a double, 2.7 for a text and
/// 0.7 for a boolean; a row of the typed column takes 2.1, 2.0, 1.1 and 0.55, a null row as much as
/// another, since its slot is written. So an integer path held by every row saves 1.5 bytes an
/// occurrence, and one held by half the rows costs more than it saves. Provisional: measured on drawn
/// attributes, to measure again on real mixes, and the null slots drop once rows are sorted by shape.
/// </para>
/// <para>
/// The bytes are the lower bound of what a path saves: a question over a shredded path also reads its
/// typed column alone, where it would parse the residual of every row.
/// </para>
/// </remarks>
internal static class ShreddingChoice
{
    /// <summary>The bytes a shredded path adds to each chunk, its name aside: the upper end of 154 to 217 measured, less the name.</summary>
    internal const int PathBytes = 200;

    /// <summary>The tables a shredded path adds to a chunk's encoding tree: the upper end of 4 to 10 measured.</summary>
    internal const int PathTables = 10;

    /// <summary>The bytes an occurrence takes in the residual, by class: integer, double, text, boolean.</summary>
    private static readonly double[] ResidualBytes = [3.6, 3.2, 2.7, 0.68];

    /// <summary>The bytes a row of a typed column takes, a null row as much as another, by class.</summary>
    private static readonly double[] TypedBytes = [2.1, 2.0, 1.1, 0.55];

    /// <summary>The bytes shredding <paramref name="occurrences"/> of a class over <paramref name="rows"/> rows saves, before the path's fixed cost.</summary>
    internal static double Saving(VariantClass @class, long occurrences, long rows) =>
        (occurrences * ResidualBytes[(int)@class]) - (rows * TypedBytes[(int)@class]);

    /// <summary>
    /// The rows of a chunk of a shredded variant column: long chunks of its own, so a day of 4.3 × 10⁹
    /// rows holds about 4 300 trees rather than one per megabyte.
    /// </summary>
    internal const long ChunkRows = 1_000_000;

    /// <summary>
    /// The tables a chunk's encoding tree may give its paths: half of those a traversal may visit
    /// (<see cref="VortexLimits.MaxFlatBufferTables"/>), the bound a large object's layout keeps too.
    /// </summary>
    internal const int TableBudget = VortexLimits.MaxFlatBufferTables / 2;

    /// <summary>The chunks a shredded variant column of <paramref name="rows"/> rows is written in.</summary>
    internal static long ChunksOf(long rows) => Math.Max(1, (rows + ChunkRows - 1) / ChunkRows);

    /// <summary>
    /// The chunk target, in bytes, that gives the variant column of <paramref name="census"/>'s rows
    /// chunks of about <see cref="ChunkRows"/> rows.
    /// </summary>
    internal static int ChunkTargetBytes(PathCensus census)
    {
        long perRow = census.Rows == 0 ? 1 : Math.Max(1, census.ValueBytes / census.Rows);
        return (int)Math.Clamp(perRow * ChunkRows, 1L << 20, int.MaxValue / 2);
    }

    /// <summary>
    /// The shredding the rows of <paramref name="census"/> pay for, written in chunks of
    /// <see cref="ChunkRows"/>, or null when no path does.
    /// </summary>
    /// <param name="census">The rows' paths, counted.</param>
    /// <param name="tableBudget">The tables a chunk's tree may give its paths.</param>
    internal static VariantShredding? Choose(PathCensus census, int tableBudget = TableBudget)
    {
        ArgumentNullException.ThrowIfNull(census);
        long chunks = ChunksOf(census.Rows);
        List<Candidate> candidates = [];
        Collect(census.Root, [], census.Rows, chunks, candidates);
        if (candidates.Count == 0)
        {
            return null;
        }

        int most = Math.Max(1, tableBudget / PathTables);
        if (candidates.Count > most)
        {
            // The paths that save the most, ties by path, so that two writers of one object choose alike.
            candidates.Sort(static (left, right) => right.Net != left.Net
                ? right.Net.CompareTo(left.Net)
                : string.CompareOrdinal(string.Join('\0', left.Path), string.Join('\0', right.Path)));
            candidates.RemoveRange(most, candidates.Count - most);
        }

        return Build(candidates, 0);
    }

    /// <summary>The paths under <paramref name="node"/> that pay for themselves over the <paramref name="rows"/> rows of <paramref name="chunks"/> chunks.</summary>
    private static void Collect(PathCensus.PathCount node, string[] path, long rows, long chunks, List<Candidate> into)
    {
        long objects = node.Objects;
        (VariantShredding Shredding, long Count) best = Best(node);
        if (node.Keys.Count > 0 && objects >= best.Count)
        {
            foreach ((string name, PathCensus.PathCount key) in node.Keys.OrderBy(static key => key.Key, StringComparer.Ordinal))
            {
                Collect(key, [.. path, name], rows, chunks, into);
            }

            return;
        }

        long nameBytes = path.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(path[^1]);
        double net = Saving(best.Shredding.Class, best.Count, rows) - ((PathBytes + nameBytes) * (double)chunks);
        if (best.Count > 0 && net > 0)
        {
            into.Add(new Candidate(path, best.Shredding, net));
        }
    }

    /// <summary>The class, or the list of a class, most of the node's values hold.</summary>
    private static (VariantShredding Shredding, long Count) Best(PathCensus.PathCount node)
    {
        VariantShredding? best = null;
        long count = 0;
        foreach (VariantClass @class in (ReadOnlySpan<VariantClass>)[VariantClass.Integer, VariantClass.Double, VariantClass.String, VariantClass.Boolean])
        {
            if (node.Of(@class) > count)
            {
                (best, count) = (VariantShredding.Primitive(@class), node.Of(@class));
            }

            if (node.ListsOf(@class) > count)
            {
                (best, count) = (VariantShredding.List(@class), node.ListsOf(@class));
            }
        }

        return (best ?? VariantShredding.Primitive(VariantClass.Integer), count);
    }

    /// <summary>The shredding of the candidates, all of whose paths share their first <paramref name="depth"/> keys.</summary>
    private static VariantShredding Build(List<Candidate> candidates, int depth)
    {
        if (candidates.Count == 1 && candidates[0].Path.Length == depth)
        {
            return candidates[0].Shredding;
        }

        List<(string, VariantShredding)> keys = [];
        foreach (IGrouping<string, Candidate> key in candidates.GroupBy(candidate => candidate.Path[depth], StringComparer.Ordinal))
        {
            keys.Add((key.Key, Build([.. key], depth + 1)));
        }

        return VariantShredding.Object([.. keys]);
    }

    /// <summary>A path that pays: its keys, what it shreds, and the bytes it saves net of its cost.</summary>
    private sealed record Candidate(string[] Path, VariantShredding Shredding, double Net);
}
