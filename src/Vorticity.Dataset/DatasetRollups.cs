using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;

namespace Vorticity.Dataset;

/// <summary>
/// The job that writes rollups (<see cref="RollupEntry"/>): over the deepest level of a dataset with a key, the runs of
/// consecutive objects, then of rollups, rank by rank, that no rollup the version holds sums yet, each summed from what
/// its children keep, written to the store and put by a commit that checks it summed what the version holds.
/// </summary>
/// <remarks>
/// <para>
/// A run is cut by its items' tree keys, as the pages of a tree are: it ends after an item whose key hashes, under the
/// dataset's seed and the rank, below 2^64 / N, N the fanout; at rank 1, it also ends where the clustering value changes,
/// so that a rollup of rank 1 holds one value. A run is a function of the keys alone: an object put or taken away moves the
/// end of its own run and no other, and the rollups of the others stay. The last run of a rank, which no end closed yet,
/// waits.
/// </para>
/// <para>
/// A run of objects of which one has rows a delete marked, which its counts still hold, or columns of an earlier schema,
/// gets no rollup, nor do the runs above it, until a job rewrites the object.
/// </para>
/// </remarks>
internal static class DatasetRollups
{
    /// <summary>A run of a rank: its first item among the rank below's, how many, and whether an end closed it.</summary>
    private readonly record struct Run(int First, int Count, bool Closed);

    /// <summary>
    /// Writes the rollups the latest version lacks, rank by rank, a commit a rank; the rollups put. None for a dataset with
    /// no key, by periods, on a sealed store, with a fanout of 0, or with no level past 0. With <paramref name="refreshed"/>,
    /// the handle stands on the latest version already, which the first rank reads as it is: a compaction loop's turn.
    /// </summary>
    internal static async ValueTask<int> RollUpAsync(VortexDataset dataset, CancellationToken cancellationToken, bool refreshed = false)
    {
        int fanout = dataset.Options.RollupFanout;
        if (fanout < 2 || dataset.Snapshot.Schema.Key is null || dataset.Snapshot.Levels.ByPeriods || dataset.Store is SealedObjectStore { Seals: true })
        {
            return 0;
        }

        int put = 0;
        for (int rank = 1; ; rank++)
        {
            if (rank > 1 || !refreshed)
            {
                await dataset.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            DatasetSnapshot version = dataset.Snapshot;
            if (fanout < 2 || version.Schema.Key is not { } key || version.Levels.ByPeriods || dataset.Store is SealedObjectStore { Seals: true }
                || Bottom(version.Levels) is not int bottom)
            {
                return put;
            }

            // The deepest level and the rollups as the last call left them: nothing new to sum, nothing walked.
            (PageReference, PageReference) roots = (version.Levels[bottom].Root, version.Levels.Rollups.Root);
            if (rank == 1 && dataset.RolledUp == roots)
            {
                return put;
            }

            // The objects of the deepest level, in the order of their tree keys, and the runs of every rank over them.
            List<PositionedObject> objects = [];
            await foreach (PositionedEntry walked in version.Levels[bottom].WalkAsync(version.Pages, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
            {
                objects.Add(new PositionedObject(ObjectEntry.FromBytes(walked.Entry.Value), walked.FirstRow) { Level = bottom, TreeKey = walked.Entry.Key });
            }

            List<List<Run>> ranks = Ranks(objects, version.Schema.Cut, key, version.Header.Seed, fanout);
            if (rank > ranks.Count)
            {
                return put;
            }

            // What the tree holds of this rank, and of the one below, by the first tree key each covers.
            Dictionary<string, RollupEntry> held = await RollupsAsync(version, bottom, rank, cancellationToken).ConfigureAwait(false);
            Dictionary<string, RollupEntry> below = rank > 1 ? await RollupsAsync(version, bottom, rank - 1, cancellationToken).ConfigureAwait(false) : [];
            List<DatasetOperation> operations = [];
            foreach (Run run in ranks[rank - 1])
            {
                (int first, int last) = Span(ranks, rank, run);
                ReadOnlyMemory<byte> firstKey = objects[first].TreeKey;
                ReadOnlyMemory<byte> lastKey = objects[last].TreeKey;
                if (!run.Closed
                    || (held.TryGetValue(Convert.ToHexString(firstKey.Span), out RollupEntry? kept)
                        && kept.LastKey.Span.SequenceEqual(lastKey.Span) && kept.Children == run.Count))
                {
                    continue;
                }

                RollupEntry? written = rank == 1
                    ? await FromObjectsAsync(dataset, version, key, bottom, objects, run, cancellationToken).ConfigureAwait(false)
                    : await FromRollupsAsync(dataset, version, key, bottom, rank, objects, ranks, run, below, cancellationToken).ConfigureAwait(false);
                if (written is not null)
                {
                    operations.Add(new DatasetOperation.PutRollup(written));
                }
            }

            if (operations.Count > 0)
            {
                CommitResult result = await dataset.CommitAsync(operations, CommitKind.Rollup, cancellationToken).ConfigureAwait(false);
                foreach (OperationOutcome outcome in result.Outcomes)
                {
                    put += outcome == OperationOutcome.Applied ? 1 : 0;
                }
            }

            if (rank == ranks.Count)
            {
                // The version this handle stands on now, past its own commits.
                DatasetSnapshot after = dataset.Snapshot;
                dataset.RolledUp = (after.Levels[bottom].Root, after.Levels.Rollups.Root);
                return put;
            }
        }
    }

    /// <summary>The deepest level past 0 that holds objects, or null for none.</summary>
    private static int? Bottom(DatasetLevels levels)
    {
        for (int level = levels.Count - 1; level >= 1; level--)
        {
            if (!levels[level].IsEmpty)
            {
                return level;
            }
        }

        return null;
    }

    /// <summary>
    /// The runs of every rank: rank 1 over <paramref name="objects"/>, cut where the clustering value changes and by their
    /// keys' hashes; rank k + 1 over rank k's runs, by the keys of their first objects. The ranks go up while the one below
    /// holds two runs or more.
    /// </summary>
    private static List<List<Run>> Ranks(List<PositionedObject> objects, ValueCut? values, ClusteringKey key, ulong seed, int fanout)
    {
        List<List<Run>> ranks = [];
        if (objects.Count == 0)
        {
            return ranks;
        }

        // Each object's clustering value, which a run of rank 1 holds one of.
        FilterLiteral?[] images = new FilterLiteral?[objects.Count];
        for (int i = 0; values is not null && i < images.Length; i++)
        {
            images[i] = values.Single(objects[i].Summaries(key));
        }

        List<ReadOnlyMemory<byte>> keys = objects.ConvertAll(static held => held.TreeKey);
        ranks.Add(Cut(keys, seed, 1, fanout, i => i + 1 < images.Length && !SameValue(images[i], images[i + 1])));
        while (ranks[^1].Count >= 2)
        {
            List<Run> lower = ranks[^1];
            List<ReadOnlyMemory<byte>> firsts = lower.ConvertAll(run => keys[FirstObject(ranks, ranks.Count, run.First)]);
            List<Run> upper = Cut(firsts, seed, ranks.Count + 1, fanout, static _ => false);

            // A run over an open run of the rank below is open too: only the last can be.
            if (upper.Count > 0 && !lower[^1].Closed && upper[^1].Closed)
            {
                upper[^1] = upper[^1] with { Closed = false };
            }

            ranks.Add(upper);
        }

        return ranks;
    }

    /// <summary>
    /// The runs of <paramref name="keys"/>, tree keys: each ends after a key whose clustering key, without the uid that ends
    /// it, hashes under the rank's seed below 2^64 / fanout, or where <paramref name="ends"/> says. An object rewritten with
    /// the same rows keeps its run's end.
    /// </summary>
    private static List<Run> Cut(List<ReadOnlyMemory<byte>> keys, ulong seed, int rank, int fanout, Func<int, bool> ends)
    {
        ulong threshold = ulong.MaxValue / (ulong)fanout;
        long ranked = unchecked((long)(seed ^ (0x9E3779B97F4A7C15UL * (ulong)rank)));
        List<Run> runs = [];
        int start = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            ReadOnlySpan<byte> order = keys[i].Length > VortexDataset.UidBytes ? keys[i].Span[..^VortexDataset.UidBytes] : keys[i].Span;
            if (XxHash3.HashToUInt64(order, ranked) < threshold || ends(i))
            {
                runs.Add(new Run(start, i - start + 1, true));
                start = i + 1;
            }
        }

        if (start < keys.Count)
        {
            runs.Add(new Run(start, keys.Count - start, false));
        }

        return runs;
    }

    /// <summary>The first object of item <paramref name="item"/> of rank <paramref name="rank"/>'s items: an object at rank 1, a run of the rank below past it.</summary>
    private static int FirstObject(List<List<Run>> ranks, int rank, int item)
    {
        for (int r = rank - 1; r >= 1; r--)
        {
            item = ranks[r - 1][item].First;
        }

        return item;
    }

    /// <summary>The first and the last objects a run of rank <paramref name="rank"/> covers.</summary>
    private static (int First, int Last) Span(List<List<Run>> ranks, int rank, Run run)
    {
        int first = run.First;
        int last = run.First + run.Count - 1;
        for (int r = rank - 1; r >= 1; r--)
        {
            first = ranks[r - 1][first].First;
            Run tail = ranks[r - 1][last];
            last = tail.First + tail.Count - 1;
        }

        return (first, last);
    }

    private static bool SameValue(FilterLiteral? left, FilterLiteral? right) =>
        left is { } one && right is { } other ? KeyCursor.Compare(one, other) == 0 : left is null && right is null;

    /// <summary>The rollups of level <paramref name="level"/> and rank <paramref name="rank"/> the version holds, by the hex of the first tree key each covers.</summary>
    private static async ValueTask<Dictionary<string, RollupEntry>> RollupsAsync(DatasetSnapshot version, int level, int rank, CancellationToken cancellationToken)
    {
        Dictionary<string, RollupEntry> rollups = new Dictionary<string, RollupEntry>(StringComparer.Ordinal);
        if (version.Levels.Rollups.IsEmpty)
        {
            return rollups;
        }

        await foreach (TreeEntry entry in DatasetCommitter.RangeAsync(
            version.Levels.Rollups, RollupEntry.PrefixOf(level, rank), RollupEntry.PrefixOf(level, rank + 1), version.Pages, cancellationToken).ConfigureAwait(false))
        {
            RollupEntry rollup = RollupEntry.FromBytes(entry.Key.Span, entry.Value.Span);
            if (rollup.Level == level && rollup.Rank == rank)
            {
                rollups[Convert.ToHexString(rollup.FirstKey.Span)] = rollup;
            }
        }

        return rollups;
    }

    /// <summary>
    /// The rollup of rank 1 over <paramref name="run"/>'s objects, its object written; null when one of them keeps no counts,
    /// has rows a delete marked, or columns of an earlier schema.
    /// </summary>
    private static async ValueTask<RollupEntry?> FromObjectsAsync(
        VortexDataset dataset, DatasetSnapshot version, ClusteringKey key, int level, List<PositionedObject> objects, Run run, CancellationToken cancellationToken)
    {
        RollupBuilder builder = new RollupBuilder(level, 1, key, version.Schema.Cut);
        for (int i = run.First; i < run.First + run.Count; i++)
        {
            PositionedObject held = objects[i];
            if (held.Entry.HasDeletions || held.Entry.Contents is not { Counts.Count: > 0 } contents || !version.Schema.Holds(held.Entry))
            {
                return null;
            }

            List<(int Field, ColumnCounts Counts, ColumnSketches? Sketches)> counts = [];
            foreach ((int field, IndexSegment _) in contents.Counts)
            {
                if (await dataset.ReadCountsAsync(held.Entry, field, sketches: true, cancellationToken).ConfigureAwait(false) is ({ } kept, var sketches))
                {
                    counts.Add((field, kept, sketches));
                }
            }

            builder.AddObject(held, counts);
        }

        return await WriteAsync(dataset, builder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The rollup of rank <paramref name="rank"/> over <paramref name="run"/>'s runs of the rank below, from their rollups'
    /// objects, its own written; null when one of them has no rollup the version holds.
    /// </summary>
    private static async ValueTask<RollupEntry?> FromRollupsAsync(
        VortexDataset dataset, DatasetSnapshot version, ClusteringKey key, int level, int rank, List<PositionedObject> objects, List<List<Run>> ranks, Run run,
        Dictionary<string, RollupEntry> below, CancellationToken cancellationToken)
    {
        RollupBuilder builder = new RollupBuilder(level, rank, key, version.Schema.Cut);
        for (int i = run.First; i < run.First + run.Count; i++)
        {
            // The child's rollup, by the first object its run covers, over as many children as its run, and all its regions.
            ReadOnlyMemory<byte> first = objects[FirstObject(ranks, rank - 1, i)].TreeKey;
            if (!below.TryGetValue(Convert.ToHexString(first.Span), out RollupEntry? child) || child.Children != ranks[rank - 2][i].Count)
            {
                return null;
            }

            byte[] bytes = new byte[child.Bytes];
            ObjectRead read = await dataset.Store.ReadAsync(child.Key, 0, bytes, null, cancellationToken).ConfigureAwait(false);
            if (read.BytesRead != bytes.Length)
            {
                return null;
            }

            List<(int, ColumnCounts, ColumnSketches?, ColumnCounts?, ColumnSketches?)> columns = [];
            foreach (RollupColumn column in child.Columns)
            {
                if (Region(bytes, column.Counts) is not { } totals)
                {
                    return null;
                }

                columns.Add((
                    column.Field,
                    ColumnCounts.From(totals),
                    column.Sketches is { } sketches && Region(bytes, sketches) is { } sketchBytes ? ColumnSketches.From(sketchBytes) : null,
                    column.BucketCounts is { } bucketed && Region(bytes, bucketed) is { } bucketBytes ? ColumnCounts.From(bucketBytes) : null,
                    column.BucketSketches is { } bucketSketches && Region(bytes, bucketSketches) is { } bucketSketchBytes ? ColumnSketches.From(bucketSketchBytes) : null));
            }

            builder.AddRollup(child, columns);
        }

        return await WriteAsync(dataset, builder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A region's bytes, or null when they fail their checksum.</summary>
    private static byte[]? Region(byte[] bytes, IndexSegment region) =>
        region.Offset + region.Length <= (ulong)bytes.Length && region.Holds(bytes.AsSpan((int)region.Offset, (int)region.Length))
            ? bytes.AsSpan((int)region.Offset, (int)region.Length).ToArray()
            : null;

    /// <summary>The rollup <paramref name="builder"/> summed, its object written under a new key.</summary>
    private static async ValueTask<RollupEntry?> WriteAsync(VortexDataset dataset, RollupBuilder builder, CancellationToken cancellationToken)
    {
        Span<byte> random = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        UInt128 uid = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(random);
        if (builder.Finish(CommitKey.ForRollup(uid), uid) is not ({ } bytes, { } entry))
        {
            return null;
        }

        await dataset.Store.PutIfAbsentAsync(entry.Key, bytes, cancellationToken).ConfigureAwait(false);
        return entry;
    }
}
