// The indexer of docs/13-dataset.md §6.4: "an indexer works by (object, block range); each batch is
// one commit; coverage may be partial and the rules of 10 §8 apply unchanged: a pruner uses the
// covered blocks, an exact source waits for complete coverage".
//
// ONE CALL, ONE FRAGMENT, ONE COMMIT. The fragment is built from the object's data -- read with no
// index, the object's own or any fragment's, since the indexer reads rows and not keys -- and handed
// to the commit as BYTES, which the commit that lands writes into its own commit object and names in
// the object's leaf entry. So a rebase costs nothing to get right: whichever version wins, the
// fragment is written by the commit that names it (§8.2).
//
// WHAT A REBASE DECIDES, which is §8.2's rows 3 and 4 and nothing written here. Another indexer
// already attached the same bytes: nothing is written, and the outcome says so. The object was
// replaced meanwhile, by a compaction that embedded its index and dropped its fragments, or by
// anything else with another uid: the fragment is dropped, since it was built against bytes the
// dataset no longer holds.
//
// A FRAGMENT IS BOUND TO ITS OBJECT BY THE OBJECT'S UID (§7), which every object this library wrote
// carries. An object imported from another writer has none: a fragment would bind it by the store's
// token, which its entry does not carry yet, so it is refused with that reason rather than built and
// then refused by every reader.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What one indexing did (§6.4).</summary>
/// <param name="Version">The version the commit created, or the one it found when it wrote nothing.</param>
/// <param name="Outcome">
/// What the commit made of the fragment (§8.2): applied, already there — another indexer wrote the
/// same bytes — or dropped, because the object is not the one it was built against any more.
/// </param>
/// <param name="Bytes">The fragment's length.</param>
/// <param name="Reports">What became of every index the policy asked for.</param>
public sealed record IndexingResult(
    ulong Version, OperationOutcome Outcome, long Bytes, IReadOnlyList<IndexWriteReport> Reports);

/// <summary>Indexes a dataset's objects by fragments, one block range per commit (§6.4).</summary>
public static class DatasetIndexer
{
    /// <summary>Indexes blocks of one object into a fragment, and commits it.</summary>
    /// <param name="dataset">The dataset, which moves to the version the commit creates.</param>
    /// <param name="target">
    /// The object, as the dataset's own walk gives it (<c>Scan().ObjectsAsync()</c>): the walk
    /// knows the level and the key its leaf sits at, which the commit needs to find it again.
    /// </param>
    /// <param name="policy">What to build.</param>
    /// <param name="rows">
    /// The object's rows to index, whole blocks; null for all of them. A partial fragment is used by
    /// a pruner for its blocks, and by a key source only once fragments cover the object (10 §8).
    /// </param>
    /// <param name="options">The budget, block length and scratch for the build; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the reads, the build and the commit.</param>
    /// <returns>What the indexing did.</returns>
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
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(policy);
        ObjectEntry entry = target.Entry ?? throw new ArgumentNullException(nameof(target));
        if (target.TreeKey.IsEmpty)
        {
            throw new ArgumentException(
                "The object must come from the dataset's own walk (Scan().ObjectsAsync()), which knows " +
                "where its leaf is.",
                nameof(target));
        }

        if (entry.Uid == UInt128.Zero)
        {
            throw new VortexUnsupportedException(
                entry.Key,
                "index fragment",
                "The object carries no identity, being written by another writer: a fragment would " +
                "bind it by the store's token, which its entry does not carry (13 §7). Rewrite it " +
                "through the dataset, which embeds the index instead.");
        }

        // The data, read through the object's plain view: an indexer reads rows, not keys, and the
        // fragments already attached would only cost their reads.
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

        DatasetOperation.AddFragment attach = new DatasetOperation.AddFragment(
            target.TreeKey, entry.Uid, fragment.Bytes)
        {
            Level = target.Level,
        };
        CommitResult commit = await dataset.CommitAsync([attach], cancellationToken).ConfigureAwait(false);
        return new IndexingResult(commit.Version, commit.Outcomes[0], fragment.Bytes.Length, fragment.Reports);
    }
}
