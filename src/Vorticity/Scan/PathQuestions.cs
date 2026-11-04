using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types.Variants;

namespace Vorticity.Scanning;

/// <summary>A variant column whose paths a question asks for, and the tally they go to.</summary>
/// <param name="Field">The column.</param>
/// <param name="Tally">Where its paths are tallied.</param>
internal sealed record PathColumn(FieldExpr Field, PathTally Tally);

/// <summary>
/// A question for the paths of variant columns among the rows a scan keeps (<see cref="IRowsCapability.PathsAsync"/>): the
/// columns, whether only exact counts are taken, and the memory a census holds its counts under.
/// </summary>
/// <param name="Columns">The columns, each with its tally.</param>
/// <param name="Exact">Whether every count is exact: the rows a directory would estimate, or had left paths of out, are counted.</param>
/// <param name="Memory">What a census reserves its counts under; null for none.</param>
internal sealed record PathsQuestion(PathColumn[] Columns, bool Exact, QueryMemory? Memory)
{
    /// <summary>What the question reads, counted as it reads; one for every copy of it.</summary>
    internal PathsReads Reads { get; } = new PathsReads();
}

/// <summary>What a question for the paths reads, counted as it reads (<see cref="PathsReading"/>).</summary>
internal sealed class PathsReads
{
    internal int Directories;
    internal int Unopened;
    internal int EdgeGranules;
    internal long EdgeRows;
    internal int Censuses;
    internal long CensusRows;

    /// <summary>What was read so far.</summary>
    internal PathsReading Reading() => new PathsReading(Directories, Unopened, EdgeGranules, EdgeRows, Censuses, CensusRows);
}

/// <summary>The paths of variant columns among the rows a scan keeps: the question asked of a source, and the census any source answers it by.</summary>
internal static class PathQuestions
{
    /// <summary>The bytes a census holds for each key it counts, its counts and its name, reserved under the question's memory.</summary>
    private const long KeyBytes = 512;

    /// <summary>
    /// The paths of each of <paramref name="columns"/> among the rows <paramref name="query"/> keeps, asked of
    /// <paramref name="source"/>, in the order of the columns.
    /// </summary>
    /// <exception cref="ArgumentException">A column is not a variant column of the scan.</exception>
    internal static async ValueTask<VariantPaths[]> AskAsync(
        ScanSource source, LogicalQuery query, IReadOnlyList<ColumnSym> columns, PathsOptions? options, ScanCounters counters, CancellationToken cancellationToken)
    {
        bool exact = options?.Exact ?? false;
        FieldMaskBuilder mask = new FieldMaskBuilder();
        string[] names = new string[columns.Count];
        PathColumn[] asked = new PathColumn[columns.Count];
        for (int c = 0; c < asked.Length; c++)
        {
            ColumnSym column = columns[c];
            if (column.Field is not ColumnFieldExpr || column.Type.Kind != VortexTypeKind.Variant || column.FieldPath.Length == 0)
            {
                throw new ArgumentException($"'{column.Field.Path}' is not a variant column of the scan: the paths are those of a variant column.", nameof(columns));
            }

            mask.Include(column.FieldPath);
            names[c] = column.Field.Path;
            asked[c] = new PathColumn(column.Field, new PathTally());
        }

        LogicalQuery paths = query with { Shape = new PathsShape(names, exact), Reads = mask.Build() };
        // The columns read whole, never through the keys a filter on them reads alone.
        ScanSpec spec = paths.Spec().Reading(Array.ConvertAll(asked, column => column.Field));
        PathsReads reads = new PathsReads();
        if (!spec.MatchesNothing)
        {
            using QueryMemory memory = new QueryMemory(source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
            PathsQuestion question = new PathsQuestion(asked, exact, memory);
            await source.Rows.PathsAsync(spec, question, counters, cancellationToken).ConfigureAwait(false);
            reads = question.Reads;
        }

        VariantPaths[] answers = new VariantPaths[asked.Length];
        for (int c = 0; c < answers.Length; c++)
        {
            answers[c] = asked[c].Tally.ToPaths(names[c], reads.Reading());
        }

        return answers;
    }

    /// <summary>
    /// The bytes censuses that count <paramref name="keys"/> keys hold, reserved under the question's memory past the
    /// <paramref name="held"/> already reserved: the bytes now held.
    /// </summary>
    internal static long Hold(PathsQuestion question, long keys, long held)
    {
        if (keys * KeyBytes <= held)
        {
            return held;
        }

        question.Memory?.Hold((keys * KeyBytes) - held, "census of a variant's paths");
        return keys * KeyBytes;
    }

    /// <summary>
    /// Counts into the tallies of <paramref name="columns"/>, each a column of <paramref name="question"/>, the paths of the rows of
    /// <paramref name="batches"/> their selections keep, each column found in a batch by its field; its counts held under the
    /// question's memory while they grow.
    /// </summary>
    internal static async ValueTask CountAsync(
        IAsyncEnumerable<RecordBatch> batches, PathsQuestion question, IReadOnlyList<PathColumn> columns, CancellationToken cancellationToken)
    {
        PathCensus[] census = new PathCensus[columns.Count];
        for (int c = 0; c < census.Length; c++)
        {
            census[c] = new PathCensus();
        }

        question.Reads.Censuses++;
        long held = 0;
        try
        {
            await foreach (RecordBatch batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                long keys = 0;
                question.Reads.CensusRows += batch.SelectedRows;
                for (int c = 0; c < census.Length; c++)
                {
                    int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, columns[c].Field, batch.RowCount);
                    census[c].Add(PathCensus.Prepare(batch.Arena, node, batch.RowCount), batch.SelectionWords);
                    keys += census[c].HeldKeys;
                }

                held = Hold(question, keys, held);
            }

            for (int c = 0; c < census.Length; c++)
            {
                columns[c].Tally.AddCensus(census[c]);
            }
        }
        finally
        {
            question.Memory?.LetGo(held);
            foreach (PathCensus counted in census)
            {
                counted.Release();
            }
        }
    }
}
