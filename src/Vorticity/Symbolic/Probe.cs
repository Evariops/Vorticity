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
    internal Probe(RecordBinding binding)
    {
        Binding = binding;
    }

    internal RecordBinding Binding { get; }

    /// <summary>The column of member <paramref name="index"/>.</summary>
    /// <typeparam name="T">The member's .NET type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The column's symbol.</returns>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> does not map to the column.</exception>
    public Sym<T> Column<T>(int index)
    {
        RecordBinding binding = Bound();
        binding.Require<T>(index);
        return new Sym<T>(new ColumnSym(
            new FieldExpr(binding.Paths[index]), binding.TypeOf(index), binding.Extensions, binding, index, binding.IndexPaths[index]));
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
        where TNested : IVortexRecord<TNested> =>
        new Probe<TNested>(Bound().NestedFor<TNested>(index));

    private RecordBinding Bound() =>
        Binding ?? throw new InvalidOperationException("A probe exists only inside the lambda a scan runs; default(Probe) has no columns.");
}
