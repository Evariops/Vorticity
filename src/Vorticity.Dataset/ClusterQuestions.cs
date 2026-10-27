using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity.Dataset;

/// <summary>
/// The questions a cluster's nodes answer by their parts (spec F, 2): each an aggregation the application builds, under a
/// name, over a dataset and a range of its rows, from arguments it codes itself. Every node holds the same questions under
/// the same names: only a name, its arguments, the dataset's location and version and a range travel between nodes, never
/// an expression.
/// </summary>
/// <remarks>
/// A question is an aggregation whose every aggregate a state answers whole, the condition of
/// <see cref="Aggregation{TResult}.PartsAsync"/>: a count, a sum, a least, a greatest or a mean of a number column, a
/// distribution or a quantile, an approximate distinct count. Another is refused when it is first run. Its parts' partials
/// merge to the bits of the whole question; a partial of another question throws
/// <see cref="System.IO.InvalidDataException"/> when the merge reads it.
/// </remarks>
public sealed class ClusterQuestions
{
    /// <summary>The range the coordinator builds the whole question over, in which every row of a dataset lies.</summary>
    internal static readonly RowRange Every = new RowRange(0, long.MaxValue);

    private readonly ConcurrentDictionary<string, Question> _questions = new ConcurrentDictionary<string, Question>(StringComparer.Ordinal);

    /// <summary>
    /// Adds the question <paramref name="name"/>, of one result a group, which <paramref name="build"/> builds over a dataset,
    /// the range of its rows a part reads, and the question's arguments: <c>dataset.Scan&lt;T&gt;().Rows(rows)…Select(g =&gt; g.Count())</c>.
    /// </summary>
    /// <typeparam name="TResult">The type of the question's results.</typeparam>
    /// <param name="name">The name every node knows the question by.</param>
    /// <param name="build">Builds the question; it applies the range it is given to its scan.</param>
    /// <returns>These questions, for another.</returns>
    /// <exception cref="ArgumentException">A question of that name is there already.</exception>
    public ClusterQuestions Add<TResult>(string name, Func<VortexDataset, RowRange, ReadOnlyMemory<byte>, Aggregation<TResult>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return Add(name, typeof(TResult), (dataset, rows, arguments) => build(dataset, rows, arguments).Query);
    }

    /// <summary>
    /// Adds the question <paramref name="name"/>, of several results a group read through a record, which
    /// <paramref name="build"/> builds over a dataset, the range of its rows a part reads, and the question's arguments:
    /// <c>dataset.Scan&lt;T&gt;().Rows(rows)…Select(g =&gt; (g.Key, g.Count()))</c>.
    /// </summary>
    /// <param name="name">The name every node knows the question by.</param>
    /// <param name="build">Builds the question; it applies the range it is given to its scan.</param>
    /// <returns>These questions, for another.</returns>
    /// <exception cref="ArgumentException">A question of that name is there already.</exception>
    public ClusterQuestions Add(string name, Func<VortexDataset, RowRange, ReadOnlyMemory<byte>, Aggregation> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return Add(name, null, (dataset, rows, arguments) => build(dataset, rows, arguments).Query);
    }

    /// <summary>
    /// The question <paramref name="name"/> built over <paramref name="rows"/> of <paramref name="dataset"/>, whose results
    /// are of type <paramref name="results"/>, or read through a record when null.
    /// </summary>
    /// <exception cref="ArgumentException">No question has that name, or its results are of another type.</exception>
    /// <exception cref="NotSupportedException">The question is not an aggregation.</exception>
    internal AggregationQuery Build(string name, Type? results, VortexDataset dataset, RowRange rows, ReadOnlyMemory<byte> arguments)
    {
        Question question = Of(name);
        if (question.Results != results)
        {
            throw new ArgumentException(
                question.Results is null ? $"The question '{name}' gives several results a group: run it untyped." : $"The question '{name}' gives results of {question.Results.Name}.",
                nameof(name));
        }

        return AggregationPartials.Of(question.Build(dataset, rows, arguments));
    }

    /// <summary>The partial of the part <paramref name="rows"/> of the question <paramref name="name"/> over <paramref name="dataset"/>.</summary>
    /// <exception cref="ArgumentException">No question has that name.</exception>
    /// <exception cref="NotSupportedException">The question is not an aggregation whose every aggregate a state answers whole.</exception>
    internal ValueTask<AggregationPartial> PartialAsync(
        string name, VortexDataset dataset, RowRange rows, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken) =>
        AggregationPartials.ExportAsync(AggregationPartials.Of(Of(name).Build(dataset, rows, arguments)).Partial(), cancellationToken);

    private ClusterQuestions Add(string name, Type? results, Func<VortexDataset, RowRange, ReadOnlyMemory<byte>, ResultQuery> build)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_questions.TryAdd(name, new Question(results, build)))
        {
            throw new ArgumentException($"A question named '{name}' is there already.", nameof(name));
        }

        return this;
    }

    private Question Of(string name) =>
        _questions.TryGetValue(name, out Question? question) ? question : throw new ArgumentException($"No question is named '{name}'.", nameof(name));

    /// <summary>A question: the type of its results, null for a record's, and how it is built.</summary>
    private sealed record Question(Type? Results, Func<VortexDataset, RowRange, ReadOnlyMemory<byte>, ResultQuery> Build);
}
