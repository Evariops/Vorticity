using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity;

public sealed partial class Scan
{
    /// <summary>
    /// Groups the scan's rows by <paramref name="keys"/>, columns named as a filter names them, for a program that
    /// learns its columns at run time: <c>file.Scan().Where("Status &gt;= 500").GroupBy("Service").Select("Service", "count()")</c>.
    /// </summary>
    /// <param name="keys">The key's columns, by top-level name or <c>.</c>-separated path.</param>
    /// <returns>The grouped scan, whose <see cref="GroupedScan.Select"/> names the results.</returns>
    /// <exception cref="VortexSchemaException">A key names no column of the file.</exception>
    /// <remarks>The same engine as the typed path's, and its plan: synopses, indexes, lanes and spills alike.</remarks>
    public GroupedScan GroupBy(params ReadOnlySpan<string> keys)
    {
        ColumnSym[] columns = new ColumnSym[keys.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = ColumnOf(keys[i]);
        }

        return new GroupedScan(this, columns);
    }

    /// <summary>
    /// The aggregates of every row the scan keeps, as a scan of one row: <c>file.Scan().Aggregate("count()", "avg(Celsius)")</c>.
    /// </summary>
    /// <param name="results">The aggregates, written as <see cref="GroupedScan.Select"/> takes them.</param>
    /// <returns>A scan of the result.</returns>
    /// <exception cref="ArgumentException">A result is not an aggregate this path knows, or names its results twice.</exception>
    public Scan Aggregate(params ReadOnlySpan<string> results) => new GroupedScan(this, []).Select(results);

    /// <summary>The column a tool path names, as the engine reads it.</summary>
    internal ColumnSym ColumnOf(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string[] segments = _source.Schema.IndexOfName(path) >= 0 ? [path] : path.Split('.');
        int[] fieldPath = ToolPaths.Resolve(_source.Schema, segments, path);
        VortexType type = ToolPaths.TypeOf(_source.Schema, segments, path);
        return new ColumnSym((FieldExpr)Expr.Field(path), type, _source.Session.Options.Extensions, null, -1, fieldPath);
    }

    /// <summary>The scan as the aggregation engine sees it.</summary>
    internal AggregationHost Host => _host ??= new ToolHost(this);

    private AggregationHost? _host;

    private sealed class ToolHost(Scan scan) : AggregationHost
    {
        internal override ScanSource Source => scan._source;

        internal override ScanCounters Counters => scan._counters;

        internal override ScanMetrics Metrics => scan.Metrics;

        internal override ScanSpec Spec() => scan.Spec();

        internal override void Begin() => scan.Begin();

        internal override void End() => scan.End();
    }
}

/// <summary>
/// The rows of a scan for a program that learns its columns at run time, grouped by a key: its results named as
/// text, read as a scan of the groups. <c>Select("Service", "count()", "sum(Bytes)", "avg(Duration)")</c>.
/// </summary>
/// <remarks>
/// A result is a column of the key, by the name <see cref="Scan.GroupBy"/> took it under, or an aggregate:
/// <c>count()</c>, <c>count_distinct(c)</c>, <c>sum(c)</c>, <c>min(c)</c>, <c>max(c)</c> or <c>avg(c)</c>, of a
/// column <c>c</c> named as a filter names it. A sum of integers is a 64-bit integer, signed or not, and a sum of
/// floats a double; a mean is a double; a minimum, a maximum and a key keep their column's type. Each result
/// column is named by the text that asked for it.
/// </remarks>
public sealed class GroupedScan
{
    private readonly Scan _scan;
    private readonly ColumnSym[] _keys;

    internal GroupedScan(Scan scan, ColumnSym[] keys)
    {
        _scan = scan;
        _keys = keys;
    }

    /// <summary>The groups' results, computed when the returned scan is read.</summary>
    /// <param name="results">The results, in order: columns of the key and aggregates, written as text.</param>
    /// <returns>A scan of the groups, a row each, its columns named by <paramref name="results"/>.</returns>
    /// <exception cref="ArgumentException">A result is neither a column of the key nor an aggregate this path knows, or is asked twice.</exception>
    /// <exception cref="VortexSchemaException">An aggregate names no column of the file, or a column it cannot take.</exception>
    public Scan Select(params ReadOnlySpan<string> results)
    {
        if (results.IsEmpty)
        {
            throw new ArgumentException("A selection names at least one result.", nameof(results));
        }

        ColumnShape[] keys = new ColumnShape[_keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new ColumnShape(_keys[i]);
        }

        IResultNode[] nodes = new IResultNode[results.Length];
        SymNode[] computed = new SymNode[results.Length];
        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Length; i++)
        {
            string text = results[i]?.Trim() ?? throw new ArgumentException($"Result {i + 1} is null.", nameof(results));
            if (!named.Add(text))
            {
                throw new ArgumentException($"'{text}' is asked twice: each result names a column of the groups.", nameof(results));
            }

            nodes[i] = Result(text, keys);
            computed[i] = (SymNode)nodes[i];
        }

        AggregationQuery query = new AggregationQuery(_scan.Host, new AggregationPlan(computed, keys), nodes, keys);

        // The result's columns named by the texts that asked for them, at the types the engine gives them.
        VortexField[] fields = new VortexField[nodes.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new VortexField(results[i].Trim(), query.Schema[i].Type);
        }

        AggregationQuery typed = query.Typed(VortexSchema.Create(fields), typeof(Scan));
        return new Scan(new ResultScanSource(typed), [], typed.Counters);
    }

    /// <summary>The result one text asks for: a column of the key, or an aggregate of the group.</summary>
    private IResultNode Result(string text, ColumnShape[] keys)
    {
        int open = text.IndexOf('(');
        if (open < 0)
        {
            for (int k = 0; k < _keys.Length; k++)
            {
                if (string.Equals(_keys[k].Field.Path, text, StringComparison.Ordinal))
                {
                    return (IResultNode)Typed(keys[k], new KeyOf(k, text));
                }
            }

            throw new ArgumentException(
                $"'{text}' is neither a column of the key ({string.Join(", ", Array.ConvertAll(_keys, key => key.Field.Path))}) nor an aggregate such as count() or sum(column).",
                nameof(text));
        }

        if (!text.EndsWith(')'))
        {
            throw new ArgumentException($"'{text}' opens a parenthesis it does not close.", nameof(text));
        }

        string function = text[..open].Trim().ToLowerInvariant();
        string argument = text[(open + 1)..^1].Trim();
        if (function == "count" && argument is "" or "*")
        {
            return (IResultNode)Aggregators.Count().Node;
        }

        if (argument.Length == 0)
        {
            throw new ArgumentException($"'{text}' names no column: {function}(column).", nameof(text));
        }

        ColumnShape shape = new ColumnShape(_scan.ColumnOf(argument));
        SymNode node = function switch
        {
            "sum" => Sum(shape),
            "avg" => Aggregators.Average(shape).Node,
            "min" => Typed(shape, new ExtremeOf(max: false)),
            "max" => Typed(shape, new ExtremeOf(max: true)),
            "count_distinct" => Aggregators.CountDistinct(shape).Node,
            _ => throw new ArgumentException(
                $"'{function}' is not an aggregate of the tool path: count, count_distinct, sum, min, max or avg.", nameof(text)),
        };
        return (IResultNode)node;
    }

    /// <summary>A sum at the widest type of its kind: a signed integer's a long, an unsigned one's an unsigned long, a float's a double.</summary>
    private static SymNode Sum(ColumnShape shape) => shape.Kind == StorageKind.Primitive
        ? shape.PType switch
        {
            PType.I8 or PType.I16 or PType.I32 or PType.I64 => Aggregators.Sum<long>(shape).Node,
            PType.U8 or PType.U16 or PType.U32 or PType.U64 => Aggregators.Sum<ulong>(shape).Node,
            _ => Aggregators.Sum<double>(shape).Node,
        }
        : throw new VortexSchemaException($"'{shape.Path}' is {shape.Type}: the tool path sums integers and floats.");

    /// <summary>What a result is at a column's own .NET type, which the tool path picks from the column.</summary>
    private interface IAtType
    {
        SymNode At<T>(ColumnShape shape);
    }

    /// <summary>A component of the key at its column's type.</summary>
    private readonly struct KeyOf(int component, string name) : IAtType
    {
        public SymNode At<T>(ColumnShape shape) => new KeyNode<T>(component, name);
    }

    /// <summary>A minimum or a maximum at its column's type.</summary>
    private readonly struct ExtremeOf(bool max) : IAtType
    {
        public SymNode At<T>(ColumnShape shape) => Aggregators.Extreme<T>(shape, max).Node;
    }

    /// <summary>
    /// <paramref name="result"/> at the .NET type of a column's values: a timestamp's an instant, text a string, bytes
    /// an array, a boolean a bool, a decimal a <see cref="VortexDecimal"/>, any other primitive its own.
    /// </summary>
    private static SymNode Typed<TResult>(ColumnShape shape, TResult result)
        where TResult : IAtType
    {
        if (shape.Type.Kind == VortexTypeKind.Extension && shape.Type.ExtensionId == ExtensionIds.Timestamp)
        {
            return result.At<DateTimeOffset>(shape);
        }

        return shape.Kind switch
        {
            StorageKind.Bool => result.At<bool>(shape),
            StorageKind.Decimal or StorageKind.Decimal256 => result.At<VortexDecimal>(shape),
            StorageKind.Bytes => shape.Type.Kind == VortexTypeKind.Binary ? result.At<byte[]>(shape) : result.At<string>(shape),
            StorageKind.Primitive => shape.PType switch
            {
                PType.I8 => result.At<sbyte>(shape),
                PType.I16 => result.At<short>(shape),
                PType.I32 => result.At<int>(shape),
                PType.I64 => result.At<long>(shape),
                PType.U8 => result.At<byte>(shape),
                PType.U16 => result.At<ushort>(shape),
                PType.U32 => result.At<uint>(shape),
                PType.U64 => result.At<ulong>(shape),
                PType.F16 => result.At<Half>(shape),
                PType.F32 => result.At<float>(shape),
                _ => result.At<double>(shape),
            },
            _ => throw new VortexSchemaException($"'{shape.Path}' is {shape.Type}, which the tool path does not group or rank."),
        };
    }
}
