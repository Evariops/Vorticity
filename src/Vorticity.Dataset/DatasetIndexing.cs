using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>How <see cref="VortexDataset.IndexAsync"/> indexes the objects already written.</summary>
public sealed record IndexingOptions
{
    private readonly int _objectsPerCommit = 64;

    /// <summary>
    /// The objects one commit gives their index fragments to, 64 by default. A fragment is written
    /// into its commit, so more objects a commit make fewer commits, fewer versions for a vacuum to
    /// sweep and a larger commit object, and more work to re-apply when the commit loses a race.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int ObjectsPerCommit
    {
        get => _objectsPerCommit;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _objectsPerCommit = value;
        }
    }

    /// <summary>The options a call without any takes.</summary>
    internal static IndexingOptions Default { get; } = new IndexingOptions();
}

/// <summary>What <see cref="VortexDataset.IndexAsync"/> did.</summary>
public sealed record IndexingResult
{
    /// <summary>The version the dataset reads once the indexing ends.</summary>
    public ulong Version { get; init; }

    /// <summary>The objects that took an index fragment.</summary>
    public long ObjectsIndexed { get; init; }

    /// <summary>
    /// The objects the last walk left without one of the indexes: an object that carries no identity
    /// to bind a fragment to, being written by another writer, or one none of whose blocks the index
    /// can be built for, such as one whose column holds only nulls. A compaction that rewrites the
    /// first kind builds the indexes into them.
    /// </summary>
    public long ObjectsLeft { get; init; }

    /// <summary>The walks over the objects; the last found nothing more to index.</summary>
    public int Walks { get; init; }

    /// <summary>The commits made, the one that added the indexes to the dataset's included.</summary>
    public int Commits { get; init; }

    /// <summary>The bytes of the fragments the commits applied.</summary>
    public long FragmentBytes { get; init; }
}

/// <summary>
/// Gives a dataset indexes: first to every writer of it, through the policy its commits carry, then to
/// the objects already written, as index fragments.
/// </summary>
/// <remarks>
/// The order closes a race. An object written by a writer that has not seen the policy holds no
/// index, and a compaction that merges an object indexed by a fragment into one written without the
/// index undoes the indexing. Once the policy is in the dataset's commits, every write and compaction
/// that builds on a version holding it builds the indexes, whatever its handle's options say. What a
/// compaction planned before the policy writes without them, the next walk finds, and the indexing
/// ends at the first walk that finds nothing to index.
/// </remarks>
internal static class DatasetIndexing
{
    /// <summary>Adds <paramref name="policy"/> to the dataset's, then indexes every object that lacks one of its indexes.</summary>
    /// <exception cref="ArgumentException">The policy names no column, or a composite key.</exception>
    /// <exception cref="VortexSchemaException">The policy names a column the dataset does not have.</exception>
    internal static async ValueTask<IndexingResult> RunAsync(
        VortexDataset dataset, IndexPolicy policy, IndexingOptions options, CancellationToken cancellationToken)
    {
        WritePolicy wanted = policy.ToWritePolicy();
        if (wanted.Keys.Count > 0)
        {
            // A composite key's runs need an encoder, which a handle's own options carry and the
            // dataset's commits cannot: a compaction by a handle without it would drop the index.
            throw new ArgumentException(
                "A composite key is indexed through the dataset's options, which carry its encoder; IndexAsync takes indexes of one column.",
                nameof(policy));
        }

        bool names = false;
        DatasetSchema schema = dataset.Snapshot.Schema;
        foreach ((string path, IndexSpec spec) in wanted.Columns)
        {
            // Refused here rather than left out of every object a writer builds from now on.
            DType column = schema.DType;
            foreach (int field in ToolPaths.Resolve(schema.Columns, path))
            {
                column = column.GetField(field);
            }

            if (IndexWriter.Refusal(spec, column) is { } reason)
            {
                throw new ArgumentException($"A {spec.Kind} index does not apply to column '{path}': {reason}.", nameof(policy));
            }

            names |= IndexKinds.Of(spec.Kind) is not null;
        }

        if (!names)
        {
            throw new ArgumentException("The policy names no index of a column.", nameof(policy));
        }

        CommitResult added = await dataset
            .CommitAsync([new DatasetOperation.AddIndexPolicy(IndexDirectory.PolicyBytes(wanted))], CommitKind.Index, cancellationToken)
            .ConfigureAwait(false);
        int commits = added.Created ? 1 : 0;

        // The fragments are built under the policy's budget and encoder.
        VortexWriteOptions build = VortexWriteOptions.Default with { Indexes = policy };
        HashSet<string> left = new HashSet<string>(StringComparer.Ordinal);
        long indexed = 0;
        long bytes = 0;
        int walks = 0;
        while (true)
        {
            // Each walk reads the latest version: the objects compactions wrote since the last one.
            await dataset.RefreshAsync(cancellationToken).ConfigureAwait(false);
            walks++;
            int lastLeft = 0;
            bool built = false;
            List<DatasetOperation> batch = [];
            List<long> lengths = [];
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (left.Contains(held.Entry.Key))
                {
                    lastLeft++;
                    continue;
                }

                IndexFragment? fragment = await FragmentAsync(dataset, held, wanted, build, cancellationToken).ConfigureAwait(false);
                if (fragment is null)
                {
                    continue;
                }

                if (fragment.Bytes.IsEmpty)
                {
                    left.Add(held.Entry.Key);
                    lastLeft++;
                    continue;
                }

                built = true;
                batch.Add(new DatasetOperation.AddFragment(held.TreeKey, held.Entry.Uid, fragment.Bytes) { Level = held.Level });
                lengths.Add(fragment.Bytes.Length);
                if (batch.Count == options.ObjectsPerCommit)
                {
                    (long applied, long length, bool created) = await CommitAsync(dataset, batch, lengths, cancellationToken).ConfigureAwait(false);
                    (indexed, bytes, commits) = (indexed + applied, bytes + length, commits + (created ? 1 : 0));
                }
            }

            if (batch.Count > 0)
            {
                (long applied, long length, bool created) = await CommitAsync(dataset, batch, lengths, cancellationToken).ConfigureAwait(false);
                (indexed, bytes, commits) = (indexed + applied, bytes + length, commits + (created ? 1 : 0));
            }

            // A walk that built nothing found every object indexed, or left: the next would too.
            if (!built)
            {
                return new IndexingResult
                {
                    Version = dataset.Version,
                    ObjectsIndexed = indexed,
                    ObjectsLeft = lastLeft,
                    Walks = walks,
                    Commits = commits,
                    FragmentBytes = bytes,
                };
            }
        }
    }

    /// <summary>
    /// The fragment that gives <paramref name="held"/> the indexes it lacks: null when it has them all,
    /// and an empty one when it cannot take them, carrying no identity or holding no block an index
    /// can be built for.
    /// </summary>
    private static async ValueTask<IndexFragment?> FragmentAsync(
        VortexDataset dataset, PositionedObject held, WritePolicy wanted, VortexWriteOptions build, CancellationToken cancellationToken)
    {
        ObjectEntry entry = held.Entry;
        ObjectLease lease = await dataset.RentAsync(entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            VortexFile file = lease.File;
            long blockRows = Scanning.SplitPlan.NaturalBatchRows(file.LayoutTree);
            if (blockRows <= 0 || file.RowCount == 0)
            {
                return null;
            }

            // An object of an earlier schema holds the column under the name it had then, or not at
            // all when the column came later, and then has nothing to index.
            ObjectColumns? columns = dataset.Snapshot.Schema.ColumnsOf(file.DType, entry.Key);
            IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
            WritePolicy missing = WritePolicy.None;
            foreach ((string path, IndexSpec spec) in wanted.Columns)
            {
                if ((columns is null ? path : columns.SourcePath(path)) is { } source
                    && IndexKinds.Of(spec.Kind) is { } kind
                    && !Covers(directory, file, source, kind, blockRows))
                {
                    missing = missing.For(source, spec);
                }
            }

            if (missing.Columns.Count == 0)
            {
                return null;
            }

            if (entry.Uid == UInt128.Zero)
            {
                return new IndexFragment(ReadOnlyMemory<byte>.Empty, []);
            }

            IndexFragment fragment = await VortexFileIndexer.BuildFragmentAsync(
                file,
                missing,
                new RowRange(0, file.RowCount),
                storeToken: null,
                contentHash: entry.Hash == UInt128.Zero ? null : entry.Hash,
                build with { BudgetSpares = new HashSet<string>(missing.Columns.Keys, StringComparer.Ordinal) },
                cancellationToken).ConfigureAwait(false);
            foreach (IndexWriteReport report in fragment.Reports)
            {
                if (report.Outcome == IndexOutcome.Built)
                {
                    return fragment;
                }
            }

            return new IndexFragment(ReadOnlyMemory<byte>.Empty, fragment.Reports);
        }
    }

    /// <summary>
    /// Whether the object's own indexes and its fragments together hold an index of
    /// <paramref name="kind"/> on <paramref name="path"/> over every one of its blocks.
    /// </summary>
    private static bool Covers(IndexDirectory? directory, VortexFile file, string path, string kind, long blockRows)
    {
        if (directory is null)
        {
            return false;
        }

        ulong blocks = (ulong)((file.RowCount + blockRows - 1) / blockRows);
        foreach (IndexEntry entry in directory.Entries)
        {
            if (!string.Equals(entry.Kind, kind, StringComparison.Ordinal) || entry.BlockLength != (ulong)blockRows
                || !KeyIndexPruner.TryResolve(file.DType, entry.ColumnPath, out string named, out _)
                || !string.Equals(named, path, StringComparison.Ordinal))
            {
                continue;
            }

            // The runs, from the object and from its fragments, in block order: covered when they
            // leave no block out between the first and the last.
            List<(ulong First, ulong End)> runs = [];
            foreach (IndexRun run in entry.Runs)
            {
                runs.Add((run.FirstBlock, run.EndBlock));
            }

            runs.Sort();
            ulong reached = 0;
            foreach ((ulong first, ulong end) in runs)
            {
                if (first > reached)
                {
                    break;
                }

                reached = Math.Max(reached, end);
            }

            if (reached >= blocks)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Commits a batch of fragments; the objects that took theirs, the bytes of those fragments, and whether a commit was made.</summary>
    private static async ValueTask<(long Applied, long Bytes, bool Created)> CommitAsync(
        VortexDataset dataset, List<DatasetOperation> batch, List<long> lengths, CancellationToken cancellationToken)
    {
        CommitResult commit = await dataset.CommitAsync(batch, CommitKind.Index, cancellationToken).ConfigureAwait(false);
        long applied = 0;
        long bytes = 0;
        for (int i = 0; i < commit.Outcomes.Count; i++)
        {
            if (commit.Outcomes[i] == OperationOutcome.Applied)
            {
                applied++;
                bytes += lengths[i];
            }
        }

        batch.Clear();
        lengths.Clear();
        return (applied, bytes, commit.Created);
    }
}
