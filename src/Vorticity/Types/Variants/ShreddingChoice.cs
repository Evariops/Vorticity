using System;
using System.Collections.Generic;
using System.Linq;

namespace Vorticity.Types.Variants;

/// <summary>
/// The shredding an object's rows take, from their census (spec E, 2): every path whose dominant class the object holds
/// <see cref="LeastValues"/> values of or more has its typed column, a stream of its own, dense, or sparse where the
/// compressor finds nine nulls in ten (<c>vortex.sparse</c>); a rarer path stays in the residual, where the path
/// directory says which granules hold it. When more paths qualify than a chunk's encoding tree may hold, those held most
/// are kept.
/// </summary>
/// <remarks>
/// <para>
/// No saving in bytes decides: a sparse stream of <c>n</c> values takes about <c>6 n</c> bytes, its positions and its
/// values, where the residual took about <c>3.6 n</c> for integers, and a question on the path then reads its values
/// alone, where it parsed the residual of every row that holds it. A shredded path adds 154 to 217 bytes to each chunk and
/// 4 to 10 tables to the chunk's encoding tree (measured on one chunk of 100 000 rows, <c>vortex-queries --micro
/// shredcost</c>); half the tables a traversal may visit leave about 50 000 paths a chunk.
/// </para>
/// </remarks>
internal static class ShreddingChoice
{
    /// <summary>The values of its dominant class an object holds of a path under which the path stays in the residual.</summary>
    internal const long LeastValues = 2_048;

    /// <summary>The tables a shredded path adds to a chunk's encoding tree: the upper end of 4 to 10 measured.</summary>
    internal const int PathTables = 10;

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
    /// The shredding the rows of <paramref name="census"/> take, written in chunks of <see cref="ChunkRows"/>, or null when
    /// no path qualifies.
    /// </summary>
    /// <param name="census">The rows' paths, counted.</param>
    /// <param name="tableBudget">The tables a chunk's tree may give its paths.</param>
    /// <param name="required">
    /// First-level keys shredded in their class wherever a row holds one of that class, however few, and first to keep: a
    /// dataset's identifiers among the attributes, whose index a key in the residual could not have, and the keys its
    /// questions ask (spec E, 5.1).
    /// </param>
    internal static VariantShredding? Choose(PathCensus census, int tableBudget = TableBudget, IReadOnlyDictionary<string, VariantClass>? required = null)
    {
        ArgumentNullException.ThrowIfNull(census);
        List<Candidate> candidates = [];
        Collect(census.Root, [], candidates, required);
        if (candidates.Count == 0)
        {
            return null;
        }

        int most = Math.Max(1, tableBudget / PathTables);
        if (candidates.Count > most)
        {
            // The paths held most, ties by path, so that two writers of one object choose alike.
            candidates.Sort(static (left, right) => right.Held != left.Held
                ? right.Held.CompareTo(left.Held)
                : string.CompareOrdinal(string.Join('\0', left.Path), string.Join('\0', right.Path)));
            candidates.RemoveRange(most, candidates.Count - most);
        }

        return Build(candidates, 0);
    }

    /// <summary>The paths under <paramref name="node"/> that hold <see cref="LeastValues"/> values of their dominant class or more.</summary>
    private static void Collect(PathCensus.PathCount node, string[] path, List<Candidate> into, IReadOnlyDictionary<string, VariantClass>? required)
    {
        // A required key is shredded in its class wherever a row holds one; a key that holds none of that class is judged
        // as any other.
        if (path.Length == 1 && required is not null && required.TryGetValue(path[0], out VariantClass wanted) && node.Of(wanted) > 0)
        {
            into.Add(new Candidate(path, VariantShredding.Primitive(wanted), long.MaxValue));
            return;
        }

        (VariantShredding best, long count) = Best(node);
        if (node.Keys.Count > 0 && node.Objects >= count)
        {
            foreach ((string name, PathCensus.PathCount key) in node.Keys.OrderBy(static key => key.Key, StringComparer.Ordinal))
            {
                Collect(key, [.. path, name], into, path.Length == 0 ? required : null);
            }

            return;
        }

        if (count >= LeastValues)
        {
            into.Add(new Candidate(path, best, count));
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

    /// <summary>A path that qualifies: its keys, what it shreds, and the values of it the object holds.</summary>
    private sealed record Candidate(string[] Path, VariantShredding Shredding, long Held);
}
