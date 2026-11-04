using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Vorticity;

/// <summary>
/// The class a question reads the values of a variant path as: what a typed read of the path takes, <c>AsInt64()</c> an
/// integer of any width, <c>AsDouble()</c> a float or a double.
/// </summary>
public enum VariantPathClass : byte
{
    /// <summary>The variant null, a value that is null: not a row without the path.</summary>
    Null = 0,

    /// <summary>A boolean, which <c>AsBool()</c> reads.</summary>
    Boolean = 1,

    /// <summary>An integer of any width, which <c>AsInt64()</c> reads.</summary>
    Integer = 2,

    /// <summary>A float or a double, which <c>AsDouble()</c> reads.</summary>
    Double = 3,

    /// <summary>A string, which <c>AsString()</c> reads.</summary>
    String = 4,

    /// <summary>A timestamp, which <c>AsTimestamp()</c> reads.</summary>
    Timestamp = 5,

    /// <summary>An object, whose keys are paths in turn.</summary>
    Object = 6,

    /// <summary>A list, whose elements are no paths of their own.</summary>
    List = 7,

    /// <summary>Any other value: a decimal, a date, a time, a uuid, bytes.</summary>
    Other = 8,
}

/// <summary>
/// One path of a variant column and one class of the values it holds there, among the rows a scan keeps: how many of
/// those rows hold a value of that class at that path.
/// </summary>
/// <param name="Keys">The keys from the variant's root, one an object's level; none for the root itself.</param>
/// <param name="Class">The class of the values.</param>
/// <param name="Rows">The rows that hold one; estimated from the granules that hold the path when <paramref name="Exact"/> is false.</param>
/// <param name="Exact">
/// Whether <paramref name="Rows"/> is exact. An estimate is that of a path the granules the scan leaves out hold too, its
/// values spread over its granules; or, where the scan keeps more granules in part than it reads again, of a path such a
/// granule holds, which a row of it the scan leaves out may hold alone.
/// </param>
public readonly record struct VariantPath(IReadOnlyList<string> Keys, VariantPathClass Class, long Rows, bool Exact)
{
    /// <summary>The path as a filter writes it, its keys bracketed and quoted: <c>['http.route']</c>, <c>['cart']['items']</c>; empty for the root.</summary>
    public string Path
    {
        get
        {
            StringBuilder text = new StringBuilder();
            foreach (string key in Keys)
            {
                text.Append("['").Append(key.Replace("'", "\\'", StringComparison.Ordinal)).Append("']");
            }

            return text.ToString();
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{(Keys.Count == 0 ? "(root)" : Path)} {Class}: {Rows} row{(Rows == 1 ? string.Empty : "s")}{(Exact ? string.Empty : " (estimated)")}";
}

/// <summary>
/// The paths of a variant column among the rows a scan keeps: every path and class a kept row holds, in the ordinal order
/// of their keys and then of their class.
/// </summary>
/// <remarks>
/// A path a kept row holds is listed unless <see cref="Complete"/> says otherwise, and a path no kept row holds is not: the
/// granules the scan keeps in part, the edges of a window, are read again and their kept rows counted. Where it keeps most
/// granules in part, a filter on a column the rows are not sorted by, they are not, and a path their other rows hold may be
/// listed, estimated. Counts come from each object's path directory: exact unless a path lies in granules the scan leaves
/// out too, estimated then from the granules it lies in.
/// </remarks>
public sealed class VariantPaths : IReadOnlyList<VariantPath>
{
    private readonly VariantPath[] _paths;

    internal VariantPaths(string column, VariantPath[] paths, bool complete, PathsReading reading)
    {
        Column = column;
        _paths = paths;
        Complete = complete;
        Reading = reading;
        Exact = Array.TrueForAll(paths, path => path.Exact);
    }

    /// <summary>The variant column.</summary>
    public string Column { get; }

    /// <summary>
    /// Whether every path a kept row holds is listed: false when an object's directory had left rare paths out under its
    /// size bound, and no read of its rows replaced it (<see cref="PathsOptions.Exact"/>).
    /// </summary>
    public bool Complete { get; }

    /// <summary>Whether every count is exact, and so no path is listed that no kept row holds.</summary>
    public bool Exact { get; }

    /// <summary>What the question read to answer, every column it asked together.</summary>
    public PathsReading Reading { get; }

    /// <inheritdoc/>
    public int Count => _paths.Length;

    /// <inheritdoc/>
    public VariantPath this[int index] => _paths[index];

    /// <inheritdoc/>
    public IEnumerator<VariantPath> GetEnumerator() => ((IEnumerable<VariantPath>)_paths).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _paths.GetEnumerator();

    /// <inheritdoc/>
    public override string ToString() => $"{_paths.Length} paths of {Column}{(Complete ? string.Empty : ", some left out")}{(Exact ? string.Empty : ", estimated")}";
}

/// <summary>
/// What a question for the paths of variant columns read to answer: the path directories that answered, those of a dataset
/// read from its table of contents without opening their object among them; the granules the scan keeps in part whose rows
/// were read again and counted, so that no path is listed that no kept row holds; and the objects or files whose kept rows
/// were counted, where no directory answered or every count is asked exact.
/// </summary>
/// <param name="Directories">The path directories that answered for a column of an object or a file.</param>
/// <param name="Unopened">Of them, those read with no open of their object.</param>
/// <param name="EdgeGranules">The granules kept in part whose rows were read again.</param>
/// <param name="EdgeRows">Their rows.</param>
/// <param name="Censuses">The objects or files whose kept rows were counted.</param>
/// <param name="CensusRows">The rows those counted.</param>
public sealed record PathsReading(int Directories, int Unopened, int EdgeGranules, long EdgeRows, int Censuses, long CensusRows);

/// <summary>How the paths of a variant column are found among the rows a scan keeps.</summary>
public sealed record PathsOptions
{
    /// <summary>
    /// Whether every count is exact and every path listed: the rows of an object whose directory would estimate, or had
    /// left paths out, are read and counted. Off by default: the directories alone answer, at the cost of a count.
    /// </summary>
    public bool Exact { get; init; }
}
