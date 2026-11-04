using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>
    /// The paths of a variant column among the rows the scan keeps, each with its class and the rows that hold it:
    /// <c>await scan.Where(r =&gt; r.Service == "checkout").PathsAsync(r =&gt; r.Attributes)</c>.
    /// </summary>
    /// <param name="column">The variant column.</param>
    /// <param name="options">Whether every count is exact; the default answers from the path directories, at the cost of a count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The paths, in the ordinal order of their keys, then of their class.</returns>
    /// <remarks>
    /// A path a kept row holds is always listed, unless <see cref="VariantPaths.Complete"/> says an object's directory left
    /// some out. Each object answers from its path directory where it has one, which names the granules each path lies in:
    /// the count of an object the scan keeps whole is exact, that of one it keeps in part estimated from its kept
    /// granules, and an object without a directory has its kept rows counted. The objects the scan reads are those a count
    /// of it reads.
    /// </remarks>
    /// <exception cref="ArgumentException">The column is not a variant column of the scan.</exception>
    public async ValueTask<VariantPaths> PathsAsync(Func<Probe<TRecord>, Sym<Variant>> column, PathsOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        return (await PathsOfAsync([Variant(column(new Probe<TRecord>(Binding)).Node)], options, cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>The paths of a nullable variant column among the rows the scan keeps; a null row holds none.</summary>
    /// <param name="column">The variant column.</param>
    /// <param name="options">Whether every count is exact; the default answers from the path directories, at the cost of a count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The paths, in the ordinal order of their keys, then of their class.</returns>
    /// <remarks>See <see cref="PathsAsync(Func{Probe{TRecord}, Sym{Variant}}, PathsOptions?, CancellationToken)"/>.</remarks>
    /// <exception cref="ArgumentException">The column is not a variant column of the scan.</exception>
    public async ValueTask<VariantPaths> PathsAsync(Func<Probe<TRecord>, Sym<Variant?>> column, PathsOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        return (await PathsOfAsync([Variant(column(new Probe<TRecord>(Binding)).Node)], options, cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// The paths of several variant columns among the rows the scan keeps, found in one pass:
    /// <c>await scan.PathsAsync(r =&gt; (r.Attributes, r.Resource))</c>.
    /// </summary>
    /// <param name="columns">A tuple of variant columns.</param>
    /// <param name="options">Whether every count is exact; the default answers from the path directories, at the cost of a count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The paths of each column, in the order of the tuple.</returns>
    /// <exception cref="ArgumentException">An element of the tuple is not a variant column of the scan.</exception>
    public async ValueTask<IReadOnlyList<VariantPaths>> PathsAsync(Func<Probe<TRecord>, ITuple> columns, PathsOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ITuple tuple = columns(new Probe<TRecord>(Binding));
        ColumnSym[] asked = new ColumnSym[tuple.Length];
        for (int i = 0; i < asked.Length; i++)
        {
            asked[i] = tuple[i] is ISymbol symbol
                ? Variant(symbol.Node)
                : throw new ArgumentException($"Element {i + 1} of the tuple is not a column of the scan.", nameof(columns));
        }

        return await PathsOfAsync(asked, options, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<VariantPaths[]> PathsOfAsync(ColumnSym[] columns, PathsOptions? options, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            return await PathQuestions.AskAsync(_source, Query(RowsShape.All), columns, options, _counters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            End();
        }
    }

    /// <summary>The column a symbol stands for, when it is a column of the scan; its type is checked by the question.</summary>
    private static ColumnSym Variant(SymNode node) =>
        node as ColumnSym
        ?? throw new ArgumentException($"'{node}' is not a column of the scan: the paths are those of a variant column, before any path into it or aggregate.");
}

public sealed partial class Scan
{
    /// <summary>The paths of the variant column at <paramref name="column"/> among the rows the scan keeps, each with its class and the rows that hold it.</summary>
    /// <param name="column">The variant column's path, <c>.</c>-separated for a nested one.</param>
    /// <param name="options">Whether every count is exact; the default answers from the path directories, at the cost of a count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The paths, in the ordinal order of their keys, then of their class.</returns>
    /// <remarks>See <see cref="Scan{TRecord}.PathsAsync(Func{Probe{TRecord}, Sym{Variant}}, PathsOptions?, CancellationToken)"/>.</remarks>
    /// <exception cref="ArgumentException">The path names no variant column of the scan.</exception>
    public async ValueTask<VariantPaths> PathsAsync(string column, PathsOptions? options = null, CancellationToken cancellationToken = default) =>
        (await PathsAsync([column], options, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>The paths of several variant columns among the rows the scan keeps, found in one pass.</summary>
    /// <param name="columns">The variant columns' paths, <c>.</c>-separated for a nested one.</param>
    /// <param name="options">Whether every count is exact; the default answers from the path directories, at the cost of a count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The paths of each column, in the order given.</returns>
    /// <exception cref="ArgumentException">A path names no variant column of the scan.</exception>
    public async ValueTask<IReadOnlyList<VariantPaths>> PathsAsync(IReadOnlyList<string> columns, PathsOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ColumnSym[] asked = new ColumnSym[columns.Count];
        for (int i = 0; i < asked.Length; i++)
        {
            asked[i] = ColumnOf(columns[i]);
        }

        Begin();
        try
        {
            return await PathQuestions.AskAsync(_source, Query(RowsShape.All), asked, options, _counters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            End();
        }
    }
}
