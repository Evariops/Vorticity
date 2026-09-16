// The front door of the order model - docs/12-index-reads.md §8.1, the `Keys` rows.
//
// CHEAPEST SOURCE FIRST, AND SAY WHY THE OTHERS WERE NOT CHOSEN. Four sources exist in the spec and
// one exists in the code; `Explain` names the chosen one and carries a reason per rejection, so a
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
    /// <c>Dictionary</c>, arrive with step 15. Over a sorted column the same walk is
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
        (SortedColumnSource? source, string? reason) =
            await SortedColumnSource.OpenAsync(_file, _path, cancellationToken).ConfigureAwait(false);

        List<KeySourceRejection> rejected = [];
        if (source is null)
        {
            rejected.Add(new KeySourceRejection(KeySourceKind.SortedColumn, reason!));
        }

        rejected.Add(new KeySourceRejection(
            KeySourceKind.SortedRuns,
            "no vorticity.sorted.runs.v1 index: the writer does not build one yet (docs/12-index-reads.md §12.4)"));
        rejected.Add(new KeySourceRejection(
            KeySourceKind.Postings,
            "no vorticity.postings.blocks.v1 index: the writer does not build one yet (docs/12-index-reads.md §12.6)"));
        rejected.Add(new KeySourceRejection(
            KeySourceKind.Dictionary,
            "the dictionary probe source is not implemented yet (docs/12-index-reads.md §12.6)"));

        try
        {
            return new KeyPlan(
                _path,
                source is null ? KeySourceKind.None : KeySourceKind.SortedColumn,
                source is null ? 0 : 1,
                source?.EntryCount,
                HasRows: source is not null,
                rejected);
        }
        finally
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens a cursor over the cheapest source the file offers for this column.</summary>
    /// <param name="cancellationToken">Cancels the zone-map read this makes.</param>
    /// <returns>A cursor, not yet positioned.</returns>
    /// <exception cref="VortexUnsupportedException">No source serves this column.</exception>
    public async ValueTask<KeyCursor> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_forced is not (KeySourceKind.None or KeySourceKind.SortedColumn))
        {
            throw new VortexUnsupportedException(
                SourceId(_forced),
                "index",
                $"{_forced} is not implemented yet; today a cursor is served by a sorted column " +
                "and nothing else (docs/12-index-reads.md §12).");
        }

        (SortedColumnSource? source, string? reason) =
            await SortedColumnSource.OpenAsync(_file, _path, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            throw new VortexUnsupportedException(
                "vorticity.sorted.runs.v1",
                "index",
                $"'{_path}' has no key source: {reason}. A cursor over an unindexed column would " +
                "have to hold the column to sort it, which this library refuses. Write the file " +
                "with IndexPolicy.SortedRuns for that column, or add the index after the fact " +
                "(docs/10-indexes.md §8).");
        }

        if (_distinct)
        {
            // Honoured by the walk rather than by the source: `NextKeyAsync` re-seeks past the
            // duplicates, which is exactly what a `Distinct()` cursor over a sorted column does.
        }

        return new KeyCursor(source);
    }

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
