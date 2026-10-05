using System;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>
/// The columns of <typeparamref name="TRecord"/> as symbols, for the lambdas of a scan:
/// <c>r => r.Day &gt;= 900 &amp;&amp; r.City == "Paris"</c>.
/// </summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <remarks>The generator adds one property per member; <see cref="Column{T}(int)"/> is what they call.</remarks>
public readonly struct Probe<TRecord>
{
    private readonly string[]? _path;

    // A chosen row of a group, whose columns are results; null for the rows themselves.
    private readonly Aggregating.AggregateNode<long>? _chosen;

    internal Probe(RecordBinding binding, string[]? path = null, Aggregating.AggregateNode<long>? chosen = null)
    {
        Binding = binding;
        _path = path;
        _chosen = chosen;
    }

    internal RecordBinding Binding { get; }

    /// <summary>The rows where this nested record is null; none for the scan's own record, which is never null.</summary>
    /// <exception cref="InvalidOperationException">The probe is a group's chosen row, whose columns are results.</exception>
    public Predicate IsNull => _path is null ? Predicate.None : new Predicate(new NullCheckExpr(new FieldExpr(Rows()), isNull: true));

    /// <summary>The rows where this nested record is present; all of them for the scan's own record.</summary>
    /// <exception cref="InvalidOperationException">The probe is a group's chosen row, whose columns are results.</exception>
    public Predicate IsNotNull => _path is null ? Predicate.All : new Predicate(new NullCheckExpr(new FieldExpr(Rows()), isNull: false));

    /// <summary>The column of member <paramref name="index"/>.</summary>
    /// <typeparam name="T">The member's .NET type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The column's symbol.</returns>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> does not map to the column.</exception>
    public Sym<T> Column<T>(int index)
    {
        RecordBinding binding = Bound();
        binding.Require<T>(index);
        ColumnSym column = new ColumnSym(
            new FieldExpr(binding.Paths[index]), binding.TypeOf(index), binding.Extensions, binding, index, binding.IndexPaths[index]);

        // A column of a chosen row is that row's value of it, a result of the group.
        return _chosen is null ? new Sym<T>(column) : new Sym<T>(new Aggregating.ChosenColumnNode<T>(_chosen, column));
    }

    /// <summary>The column of the member named <paramref name="name"/>.</summary>
    /// <typeparam name="T">The member's .NET type.</typeparam>
    /// <param name="name">The member's name in the record.</param>
    /// <returns>The column's symbol.</returns>
    public Sym<T> Column<T>(string name) => Column<T>(Bound().MemberIndex(name));

    /// <summary>The nested record held by member <paramref name="index"/>, whose columns are symbols in turn.</summary>
    /// <typeparam name="TNested">The nested record type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The nested probe.</returns>
    public Probe<TNested> Struct<TNested>(int index)
        where TNested : IVortexRecord<TNested>
    {
        RecordBinding binding = Bound();
        return new Probe<TNested>(binding.NestedFor<TNested>(index), binding.Paths[index], _chosen);
    }

    private RecordBinding Bound() =>
        Binding ?? throw new InvalidOperationException("A probe exists only inside the lambda a scan runs; default(Probe) has no columns.");

    private string[] Rows() =>
        _chosen is null
            ? _path!
            : throw new InvalidOperationException("A chosen row's columns are results of its group: a filter on rows reads the rows themselves.");
}
