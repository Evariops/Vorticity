// The front door of the order model - docs/12-index-reads.md §8.1, the `Keys` rows.
//
// CHEAPEST SOURCE FIRST, AND SAY WHY THE OTHERS WERE NOT CHOSEN. Four sources exist in the spec and
// the two that deliver rows exist in the code; `Explain` names the chosen one and carries a reason per rejection, so a
// caller who expected an index learns it was absent rather than guessing from a slow walk. The
// order the spec fixes is `SortedColumn` before `SortedRuns` -- one zone-map read and one zone
// decode beats `r` binary searches -- then the key-only sources, which a cursor asking for rows
// cannot take at all.
//
// A COLUMN WITH NO SOURCE IS REFUSED, not emulated. Building the sorted order of an unindexed
// column means holding the column, and constraint 2 of the spec forbids a read whose memory is
// bounded by the data rather than by a batch. The refusal names the policy that would have served.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Keys;

/// <summary>Chooses a key source for one column and opens a cursor over it.</summary>
/// <remarks>Not thread-safe, and meant to be used and discarded, like <c>ScanBuilder</c>.</remarks>
public sealed class KeyCursorBuilder
{
    private readonly VortexFile _file;
    private readonly string _path;
    private bool _distinct;
    private KeySourceKind _forced;

    internal KeyCursorBuilder(VortexFile file, string path)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(path);
        _file = file;
        _path = path;
    }

    /// <summary>
    /// Yields every distinct key once, which admits the key-only sources of docs/12 §3.
    /// </summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Recorded and not yet honoured: the sources that make it cheap, <c>Postings</c> and
    /// <c>Dictionary</c>, arrive with step 15. Over a sorted column or sorted runs the same walk is
    /// <see cref="KeyCursor.NextKeyAsync"/>, one seek per group, which is what this would use.
    /// </remarks>
    public KeyCursorBuilder Distinct()
    {
        _distinct = true;
        return this;
    }

    /// <summary>
    /// Forces a source, for a test or for a caller who measured; refused when it is absent.
    /// </summary>
    /// <param name="source">The source to take.</param>
    /// <returns>This builder.</returns>
    public KeyCursorBuilder WithSource(KeySourceKind source)
    {
        _forced = source;
        return this;
    }

    /// <summary>
    /// What a cursor over this column would be served by, without opening one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the zone-map read this makes.</param>
    /// <returns>The plan, whose <see cref="KeyPlan.Source"/> is <see cref="KeySourceKind.None"/> when nothing serves.</returns>
    public async ValueTask<KeyPlan> ExplainAsync(CancellationToken cancellationToken = default)
    {
        Choice choice = await ChooseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KeySource? source = choice.Source;
            return new KeyPlan(
                _path,
                choice.Kind,
                source?.Runs ?? 0,
                source?.EntryCount,
                HasRows: source is not null,
                choice.Rejected);
        }
        finally
        {
            if (choice.Source is not null)
            {
                await choice.Source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens a cursor over the cheapest source the file offers for this column.</summary>
    /// <param name="cancellationToken">Cancels the reads this makes.</param>
    /// <returns>A cursor, not yet positioned.</returns>
    /// <exception cref="VortexUnsupportedException">No source serves this column.</exception>
    public async ValueTask<KeyCursor> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_forced is KeySourceKind.Postings or KeySourceKind.Dictionary)
        {
            throw new VortexUnsupportedException(
                SourceId(_forced),
                "index",
                $"{_forced} is not implemented yet; a cursor is served by a sorted column or by " +
                "sorted runs (docs/12-index-reads.md §12).");
        }

        Choice choice = await ChooseAsync(cancellationToken).ConfigureAwait(false);
        if (choice.Source is null)
        {
            List<string> reasons = [];
            foreach (KeySourceRejection rejection in choice.Rejected)
            {
                if (rejection.Source is KeySourceKind.SortedColumn or KeySourceKind.SortedRuns)
                {
                    reasons.Add($"{rejection.Source}: {rejection.Reason}");
                }
            }

            throw new VortexUnsupportedException(
                IndexKinds.SortedRuns,
                "index",
                $"'{_path}' has no key source ({string.Join("; ", reasons)}). A cursor over an " +
                "unindexed column would have to hold the column to sort it, which this library " +
                "refuses. Write the file with IndexPolicy.SortedRuns for that column, or add the " +
                "index after the fact (docs/10-indexes.md §8).");
        }

        // `Distinct()` is honoured by the walk: `NextKeyAsync` re-seeks past the duplicates, on
        // either source, until the key-only sources of step 15 make it cheaper.
        _ = _distinct;
        return new KeyCursor(choice.Source);
    }

    /// <summary>
    /// The cheapest source with rows for <paramref name="path"/>, for the scan's exact cover, or
    /// null when none serves.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="path">The column.</param>
    /// <param name="indexes">Whether the scan may use the index directory: without it only the sorted column serves.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// A scan asks this on every filtered execution, so a file that cannot have a source answers
    /// before anything is allocated: no statistics saying the column is sorted, and no index
    /// directory the scan may use.
    /// </remarks>
    internal static async ValueTask<(KeySource? Source, KeySourceKind Kind)> OpenSourceAsync(
        VortexFile file, string path, bool indexes, CancellationToken cancellationToken)
    {
        bool runs = indexes && file.HasIndexDirectory;
        if (StatedSorted(file, path))
        {
            (SortedColumnSource? column, _) =
                await SortedColumnSource.OpenAsync(file, path, cancellationToken).ConfigureAwait(false);
            if (column is not null)
            {
                return (new SortedColumnWalker(column), KeySourceKind.SortedColumn);
            }
        }

        if (runs)
        {
            (SortedRunsSource? source, _) =
                await SortedRunsSource.OpenAsync(file, path, cancellationToken).ConfigureAwait(false);
            if (source is not null)
            {
                return (source, KeySourceKind.SortedRuns);
            }
        }

        return (null, KeySourceKind.None);
    }

    /// <summary>Whether the file statistics say a top-level column is sorted, read without allocating.</summary>
    private static bool StatedSorted(VortexFile file, string path)
    {
        DType schema = file.Schema;
        if (!file.HasFileStatistics || schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            return false;
        }

        int index = schema.IndexOfField(path);
        FileStatistics statistics = file.Statistics;
        return index >= 0
            && index < statistics.FieldCount
            && statistics.GetField(index).TryGetIsSorted(out bool sorted)
            && sorted;
    }

    /// <summary>
    /// The cheapest source that serves, and why each other one was not taken: the sorted column
    /// first, then the sorted runs (docs/12 §3's "cheapest first").
    /// </summary>
    private async ValueTask<Choice> ChooseAsync(CancellationToken cancellationToken)
    {
        List<KeySourceRejection> rejected = [];
        KeySource? chosen = null;
        KeySourceKind kind = KeySourceKind.None;

        if (_forced is KeySourceKind.None or KeySourceKind.SortedColumn)
        {
            (SortedColumnSource? column, string? reason) =
                await SortedColumnSource.OpenAsync(_file, _path, cancellationToken).ConfigureAwait(false);
            if (column is null)
            {
                rejected.Add(new KeySourceRejection(KeySourceKind.SortedColumn, reason!));
            }
            else
            {
                chosen = new SortedColumnWalker(column);
                kind = KeySourceKind.SortedColumn;
            }
        }
        else
        {
            rejected.Add(new KeySourceRejection(KeySourceKind.SortedColumn, $"the caller forced {_forced}"));
        }

        if (_forced is KeySourceKind.None or KeySourceKind.SortedRuns)
        {
            (SortedRunsSource? runs, string? reason) =
                await SortedRunsSource.OpenAsync(_file, _path, cancellationToken).ConfigureAwait(false);
            if (runs is null)
            {
                rejected.Add(new KeySourceRejection(KeySourceKind.SortedRuns, reason!));
            }
            else if (chosen is not null)
            {
                rejected.Add(new KeySourceRejection(
                    KeySourceKind.SortedRuns,
                    $"the sorted column is cheaper than {runs.Runs} runs: one zone-map read and one zone decode (docs/12-index-reads.md §3)"));
                await runs.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                chosen = runs;
                kind = KeySourceKind.SortedRuns;
            }
        }
        else
        {
            rejected.Add(new KeySourceRejection(KeySourceKind.SortedRuns, $"the caller forced {_forced}"));
        }

        rejected.Add(new KeySourceRejection(
            KeySourceKind.Postings,
            "a postings source holds keys without rows, and is not implemented yet (docs/12-index-reads.md §12.6)"));
        rejected.Add(new KeySourceRejection(
            KeySourceKind.Dictionary,
            "the dictionary probe source holds keys without rows, and is not implemented yet (docs/12-index-reads.md §12.6)"));
        return new Choice(chosen, kind, rejected);
    }

    private sealed record Choice(KeySource? Source, KeySourceKind Kind, List<KeySourceRejection> Rejected);

    private static string SourceId(KeySourceKind source) => source switch
    {
        KeySourceKind.SortedRuns => "vorticity.sorted.runs.v1",
        KeySourceKind.Postings => "vorticity.postings.blocks.v1",
        _ => "vorticity.dict.probe.v1",
    };
}

/// <summary>The cursor entry point.</summary>
public static class VortexFileKeyExtensions
{
    /// <summary>Starts building a cursor over one column of <paramref name="file"/>.</summary>
    /// <param name="file">An open file.</param>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <returns>A fresh builder.</returns>
    /// <remarks>
    /// An extension rather than a method on <see cref="VortexFile"/> so that file-open does not
    /// depend on the read path, exactly as <c>Scan()</c> is.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> or <paramref name="path"/> is null.</exception>
    public static KeyCursorBuilder Keys(this VortexFile file, string path) =>
        new KeyCursorBuilder(file, path);
}
