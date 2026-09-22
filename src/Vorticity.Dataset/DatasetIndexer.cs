using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What one indexing did.</summary>
/// <param name="Version">The version the commit created, or the one it found when it wrote nothing.</param>
/// <param name="Outcome">
/// What the commit made of the fragment: applied, already there — another indexer wrote the same
/// bytes — or dropped, because the object is not the one it was built against any more.
/// </param>
/// <param name="Bytes">The fragment's length.</param>
/// <param name="Reports">What became of every index the policy asked for.</param>
internal sealed record IndexingResult(
    ulong Version, OperationOutcome Outcome, long Bytes, IReadOnlyList<IndexWriteReport> Reports);

/// <summary>Indexes a dataset's objects by fragments, one block range per commit. A fragment is
/// bound to its object by that object's uid, so an object written by another writer cannot take
/// one.</summary>
internal static class DatasetIndexer
{
    /// <summary>
    /// Indexes blocks of one object into a fragment, and commits it. The target must come from the
    /// dataset's own walk, which knows the level and key its leaf sits at, and the rows must be
    /// whole blocks; a partial fragment prunes its own blocks but does not serve as a key source
    /// until fragments cover the object. The dataset moves to the version the commit creates.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> does not come from the dataset's walk.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is not whole blocks of the object.</exception>
    /// <exception cref="VortexUnsupportedException">The object has no identity to bind a fragment to.</exception>
    public static async ValueTask<IndexingResult> IndexAsync(
        VortexDataset dataset,
        PositionedObject target,
        WritePolicy policy,
        RowRange? rows = null,
        VortexWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IndexFragment fragment = await BuildAsync(dataset, target, policy, rows, options, cancellationToken).ConfigureAwait(false);
        CommitResult commit = await dataset
            .CommitAsync([Attach(target, fragment)], cancellationToken).ConfigureAwait(false);
        return new IndexingResult(commit.Version, commit.Outcomes[0], fragment.Bytes.Length, fragment.Reports);
    }

    /// <summary>
    /// Rebuilds one object's index: its fragments dropped and one fragment over the whole object
    /// attached, in a single commit so that no version holds the object with neither index.
    /// </summary>
    /// <remarks>
    /// The drops match by content, so a fragment another indexer attached meanwhile survives, and a
    /// rebuild of an object a compaction replaced drops nothing and attaches nothing.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> does not come from the dataset's walk.</exception>
    /// <exception cref="VortexUnsupportedException">The object has no identity to bind a fragment to.</exception>
    public static async ValueTask<IndexingResult> RebuildAsync(
        VortexDataset dataset,
        PositionedObject target,
        WritePolicy policy,
        VortexWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IndexFragment fragment = await BuildAsync(dataset, target, policy, null, options, cancellationToken).ConfigureAwait(false);
        List<DatasetOperation> operations = [];
        foreach (PageReference old in target.Entry.Fragments)
        {
            operations.Add(new DatasetOperation.DropFragment(target.TreeKey, old) { Level = target.Level });
        }

        operations.Add(Attach(target, fragment));
        CommitResult commit = await dataset.CommitAsync(operations, cancellationToken).ConfigureAwait(false);
        return new IndexingResult(commit.Version, commit.Outcomes[^1], fragment.Bytes.Length, fragment.Reports);
    }

    private static DatasetOperation.AddFragment Attach(PositionedObject target, IndexFragment fragment) =>
        new DatasetOperation.AddFragment(target.TreeKey, target.Entry.Uid, fragment.Bytes) { Level = target.Level };

    private static async ValueTask<IndexFragment> BuildAsync(
        VortexDataset dataset,
        PositionedObject target,
        WritePolicy policy,
        RowRange? rows,
        VortexWriteOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(policy);
        ObjectEntry entry = target.Entry ?? throw new ArgumentNullException(nameof(target));
        if (target.TreeKey.IsEmpty)
        {
            throw new ArgumentException(
                "The object must come from the dataset's own walk, which knows where its leaf is.",
                nameof(target));
        }

        if (entry.Uid == UInt128.Zero)
        {
            throw new VortexUnsupportedException(
                entry.Key,
                ComponentKind.Index,
                "The object carries no identity, being written by another writer: a fragment would " +
                "bind it by the store's token, which its entry does not carry. Rewrite it " +
                "through the dataset, which embeds the index instead.");
        }

        // Read through a plain view: an indexer reads rows, not keys, so the fragments already
        // attached would only cost their reads.
        IndexFragment fragment;
        ObjectLease lease = await dataset
            .RentAsync(entry with { Fragments = [] }, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            VortexFile file = lease.File;
            fragment = await VortexFileIndexer.BuildFragmentAsync(
                file,
                policy,
                rows ?? new RowRange(0, file.RowCount),
                storeToken: null,
                contentHash: entry.Hash == UInt128.Zero ? null : entry.Hash,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        return fragment;
    }
}
