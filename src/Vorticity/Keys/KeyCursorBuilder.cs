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
    /// The cursor's steps become <see cref="KeyCursor.NextKeyAsync"/>, one seek per group, and its
    /// entry is a key's first. The sources are taken cheapest first for a walk of keys: the sorted
    /// column, then the postings keys -- a few hundred bytes a chunk, and never a data segment --
    /// then the dictionaries, whose chunks are read for their values child, then the sorted runs,
    /// which hold an entry per row. A cursor served by postings or a dictionary has no rows
    /// (<see cref="KeyCursor.HasRows"/>).
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
                HasRows: source?.HasRows ?? false,
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
    /// <exception cref="InvalidOperationException">A source of keys without rows was forced on a cursor that is not <see cref="Distinct"/>.</exception>
    public async ValueTask<KeyCursor> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_forced is KeySourceKind.Postings or KeySourceKind.Dictionary && !_distinct)
        {
            throw new InvalidOperationException(
                $"{_forced} holds keys without rows and serves only a Distinct() cursor " +
                "(docs/12-index-reads.md §3).");
        }

        Choice choice = await ChooseAsync(cancellationToken).ConfigureAwait(false);
        if (choice.Source is null)
        {
            List<string> reasons = [];
            foreach (KeySourceRejection rejection in choice.Rejected)
            {
                reasons.Add($"{rejection.Source}: {rejection.Reason}");
            }

            // A walk of keys is refused naming the cheapest structure that would serve it (§5.4);
            // a walk of rows, the one that serves rows.
            throw new VortexUnsupportedException(
                _distinct ? IndexKinds.PostingsBlocks : IndexKinds.SortedRuns,
                "index",
                $"'{_path}' has no key source ({string.Join("; ", reasons)}). A cursor over an " +
                "unindexed column would have to hold the column to sort it, which this library " +
                "refuses. Write the file with " +
                (_distinct ? "IndexPolicy.Postings" : "IndexPolicy.SortedRuns") +
                " for that column, or add the index after the fact (docs/10-indexes.md §8).");
        }

        return new KeyCursor(choice.Source, _distinct);
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
    /// <remarks>
    /// Rows: the sorted column, then the sorted runs; the key-only sources are rejected by name.
    /// Keys (<see cref="Distinct"/>): the sorted column, the postings, the dictionaries, the sorted
    /// runs. A later source is opened only to say why it lost, which is what `Explain` reports.
    /// </remarks>
    private async ValueTask<Choice> ChooseAsync(CancellationToken cancellationToken)
    {
        KeySourceKind[] order = _distinct
            ? [KeySourceKind.SortedColumn, KeySourceKind.Postings, KeySourceKind.Dictionary, KeySourceKind.SortedRuns]
            : [KeySourceKind.SortedColumn, KeySourceKind.SortedRuns, KeySourceKind.Postings, KeySourceKind.Dictionary];
        List<KeySourceRejection> rejected = [];
        KeySource? chosen = null;
        KeySourceKind kind = KeySourceKind.None;
        foreach (KeySourceKind candidate in order)
        {
            if (_forced != KeySourceKind.None && _forced != candidate)
            {
                rejected.Add(new KeySourceRejection(candidate, $"the caller forced {_forced}"));
                continue;
            }

            if (!_distinct && candidate is KeySourceKind.Postings or KeySourceKind.Dictionary)
            {
                rejected.Add(new KeySourceRejection(
                    candidate,
                    "it holds keys without rows, which only a Distinct() cursor takes (docs/12-index-reads.md §3)"));
                continue;
            }

            if (chosen is not null && candidate == KeySourceKind.Dictionary)
            {
                // Opening a dictionary source reads the column's chunks: not to say why it lost.
                rejected.Add(new KeySourceRejection(candidate, $"{kind} is cheaper and was taken first; the dictionaries were not read"));
                continue;
            }

            (KeySource? opened, string? reason) = await OpenAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                rejected.Add(new KeySourceRejection(candidate, reason!));
            }
            else if (chosen is not null)
            {
                rejected.Add(new KeySourceRejection(candidate, $"{kind} is cheaper and was taken first (docs/12-index-reads.md §3)"));
                await opened.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                chosen = opened;
                kind = candidate;
            }
        }

        return new Choice(chosen, kind, rejected);
    }

    private async ValueTask<(KeySource? Source, string? Reason)> OpenAsync(
        KeySourceKind candidate, CancellationToken cancellationToken)
    {
        if (candidate == KeySourceKind.SortedColumn)
        {
            (SortedColumnSource? column, string? reason) =
                await SortedColumnSource.OpenAsync(_file, _path, cancellationToken).ConfigureAwait(false);
            return column is null ? (null, reason) : (new SortedColumnWalker(column), null);
        }

        (SortedRunsSource? runs, string? why) =
            await SortedRunsSource.OpenAsync(_file, _path, candidate, cancellationToken).ConfigureAwait(false);
        return (runs, why);
    }

    private sealed record Choice(KeySource? Source, KeySourceKind Kind, List<KeySourceRejection> Rejected);
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
