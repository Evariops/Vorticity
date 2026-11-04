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
/// Whether <paramref name="Rows"/> is exact. An estimate is that of granules the scan keeps in part: some kept row holds the
/// path, or a row of the same granule the scan leaves out does, the one place where a path may be listed that no kept row
/// holds.
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
/// A path a kept row holds is listed unless <see cref="Complete"/> says otherwise. Counts come from what each object of the
/// scan keeps: exact where it keeps every row of the granules a path lies in, estimated where it keeps a part of them, at
/// the resolution of a granule.
/// </remarks>
public sealed class VariantPaths : IReadOnlyList<VariantPath>
{
    private readonly VariantPath[] _paths;

    internal VariantPaths(string column, VariantPath[] paths, bool complete)
    {
        Column = column;
        _paths = paths;
        Complete = complete;
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

/// <summary>How the paths of a variant column are found among the rows a scan keeps.</summary>
public sealed record PathsOptions
{
    /// <summary>
    /// Whether every count is exact and every path listed: the rows of an object whose directory would estimate, or had
    /// left paths out, are read and counted. Off by default: the directories alone answer, at the cost of a count.
    /// </summary>
    public bool Exact { get; init; }
}
