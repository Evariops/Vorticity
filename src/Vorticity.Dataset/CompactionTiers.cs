using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.RowEncoding;

namespace Vorticity.Dataset;

/// <summary>
/// The plan of a dataset organized in tiers (<see cref="LayoutOrganization.Tiered"/>) with a partition or a key, partition by
/// partition: level 0 holds every partition's objects not yet consolidated, of every size class, which <c>k</c> objects of
/// one class merge into one of the class below; level 1 holds the consolidated ones, disjoint on (partition, key), which a
/// calm partition's level 0 joins at its consolidation.
/// </summary>
/// <remarks>
/// <para>
/// A plan reads level 0's leaves, whose objects the partition leading their tree keys keeps together, and of level 1 the
/// objects of a partition it consolidates, found by a walk that passes over the subtrees of the others. A size class is
/// <see cref="ObjectTally.ClassOf"/> under the layout's top and fan-out.
/// </para>
/// <para>
/// Jobs, most urgent first, no two on one partition: the merges of <c>k</c> objects of a class, the class of the smallest
/// objects first, which retires the most objects a byte; then the consolidations of the calm partitions, the one whose
/// rows arrived first first; then the purges of marks and the fragments, when no merge is due.
/// </para>
/// </remarks>
internal static partial class CompactionPolicy
{
    /// <summary>
    /// Plans up to <paramref name="count"/> jobs of a tiered dataset; with <paramref name="consolidateAll"/>, every partition
    /// whose level 0 holds an object is consolidated, calm or not, whatever its share of its level 1, as
    /// <see cref="VortexDataset.ConsolidateAsync"/> asks.
    /// </summary>
    private static async ValueTask<CompactionPlan> PlanTiersAsync(
        PlannedVersion dataset, CompactionOptions settings, int count, bool consolidateAll, CancellationToken cancellationToken)
    {
        DatasetLayout layout = dataset.Header.Layout ?? new DatasetLayout();
        long top = layout.ObjectBytes;
        int fanout = layout.MergeFanout;
        long calmAfter = layout.CalmAfter is { } calm ? (long)calm.TotalSeconds : -1;
        long now = dataset.Options.TimeProvider.GetUtcNow().ToUnixTimeSeconds();

        // Level 0's objects, partition by partition, as the tree holds them.
        List<TierPartition> partitions = [];
        long[] objects = new long[dataset.Levels.Count];
        long[] bytes = new long[dataset.Levels.Count];
        if (dataset.Levels.Count > 0)
        {
            await foreach (TreeEntry entry in dataset.Levels[0].EnumerateAsync(dataset.Pages, cancellationToken).ConfigureAwait(false))
            {
                CompactionInput input = new CompactionInput(0, entry.Key, ObjectEntry.FromBytes(entry.Value));
                ReadOnlySpan<byte> partition = dataset.PartitionOf(entry.Key.Span);
                if (partitions.Count == 0 || !partitions[^1].Value.AsSpan().SequenceEqual(partition))
                {
                    partitions.Add(new TierPartition(partition.ToArray()));
                }

                partitions[^1].Add(input, ObjectTally.ClassOf(input.Entry.Bytes, top, fanout));
                objects[0]++;
                bytes[0] += input.Entry.Bytes;
            }
        }

        for (int level = 1; level < objects.Length; level++)
        {
            objects[level] = dataset.Levels[level].Entries;
        }

        // The merges due, by partition: the class of the smallest objects first; none when every partition consolidates,
        // which merges its level 0 whole.
        List<(int Class, CompactionJob Job)> merges = [];
        long lag = 0;
        foreach (TierPartition partition in partitions)
        {
            lag += partition.Lag(fanout);
            if (!consolidateAll && partition.Due(fanout) is ({ } inputs, int size))
            {
                merges.Add((size, Tier(dataset, inputs, top)));
            }
        }

        merges.Sort(static (left, right) => right.Class.CompareTo(left.Class));
        List<CompactionJob> jobs = [];
        HashSet<int> busy = [];
        foreach ((int _, CompactionJob job) in merges)
        {
            if (jobs.Count < count && busy.Add(PartitionIndex(partitions, dataset, job.Inputs[0])))
            {
                jobs.Add(job);
            }
        }

        // The consolidations due: calm partitions, the earliest arrived first, whose level 0 weighs enough against the
        // consolidated objects it overlaps; any partition, when asked.
        ImmutableArray<PartitionCompaction>.Builder states = ImmutableArray.CreateBuilder<PartitionCompaction>(partitions.Count);
        List<TierPartition> calmOnes = [];
        for (int p = 0; p < partitions.Count; p++)
        {
            TierPartition partition = partitions[p];
            bool isCalm = calmAfter >= 0 && now - partition.Latest >= calmAfter;
            states.Add(new PartitionCompaction
            {
                Partition = dataset.Describe(partition.Value),
                Objects = partition.Objects.Count,
                Bytes = partition.Bytes,
                Lag = partition.Lag(fanout),
                Calm = isCalm,
                NeverCalm = calmAfter > 0 && partition.ClassZero > 2 * fanout && now - partition.EarliestOfClassZero >= 48 * calmAfter,
                TooFine = isCalm && partition.Bytes < top / ((long)fanout * fanout),
            });

            if (dataset.Keyed && (isCalm || consolidateAll))
            {
                calmOnes.Add(partition);
            }
        }

        calmOnes.Sort(static (left, right) => left.Latest.CompareTo(right.Latest));
        foreach (TierPartition partition in calmOnes)
        {
            if (jobs.Count >= count)
            {
                break;
            }

            int index = partitions.IndexOf(partition);
            if (busy.Contains(index))
            {
                continue;
            }

            if (await ConsolidationAsync(dataset, partition, top, layout.LateShare, consolidateAll, cancellationToken).ConfigureAwait(false) is { } job)
            {
                busy.Add(index);
                jobs.Add(job);
            }
        }

        // The purges of marks and the fragments, when no merge is due, by the levels' tallies.
        List<CompactionInput> fragmented = [];
        if (jobs.Count == 0 && await TalliesAsync(dataset, cancellationToken).ConfigureAwait(false) is { } tallies)
        {
            fragmented = await FragmentedAsync(dataset, tallies, settings.MaxFragments, cancellationToken).ConfigureAwait(false);
            if (settings.PurgeMarks
                && await MostMarkedAsync(dataset, tallies, Marking.Of(dataset), [], cancellationToken).ConfigureAwait(false) is { } marked)
            {
                jobs.Add(Purge(marked, Marking.Of(dataset), settings, CompactionStyle.Tiered, FirstRow(dataset, marked)));
            }
            else if (fragmented.Count > 0)
            {
                jobs.Add(new CompactionJob(
                    fragmented[0].Level, fragmented[0].Level, CompactionStyle.Tiered, CompactionTrigger.Fragments, fragmented, 0, 0));
            }
        }

        return new CompactionPlan
        {
            Version = dataset.Version,
            ObjectsByLevel = [.. objects],
            BytesByLevel = [.. bytes],
            Lag = lag,
            IsClustered = dataset.Keyed,
            Style = CompactionStyle.Tiered,
            Job = jobs.Count > 0 ? jobs[0] : null,
            Jobs = [.. jobs],
            FragmentedObjects = fragmented.Count,
            Partitions = states.MoveToImmutable(),
        };
    }

    /// <summary>The partition a job's first input lies in, by its place among the plan's.</summary>
    private static int PartitionIndex(List<TierPartition> partitions, PlannedVersion dataset, CompactionInput input)
    {
        ReadOnlySpan<byte> partition = dataset.PartitionOf(input.Key.Span);
        for (int p = 0; p < partitions.Count; p++)
        {
            if (partitions[p].Value.AsSpan().SequenceEqual(partition))
            {
                return p;
            }
        }

        return -1;
    }

    /// <summary>
    /// The merge of <paramref name="inputs"/>, objects of one class of one partition at level 0, into level 0: on the key,
    /// the outputs cut at the top without cutting a key; concatenated in their order without a key.
    /// </summary>
    private static CompactionJob Tier(PlannedVersion dataset, List<CompactionInput> inputs, long top) =>
        new CompactionJob(0, 0, CompactionStyle.Tiered, CompactionTrigger.Tier, inputs, top, FirstRow(dataset, inputs[0]));

    /// <summary>
    /// The consolidation of <paramref name="partition"/>: its level 0 merged with the objects of its level 1 that the span of
    /// its keys meets, into level 1, disjoint, cut at the top; moved there whole when they are disjoint and meet none. Null
    /// when its level 0 weighs less than a <paramref name="share"/>-th of what it meets, unless asked.
    /// </summary>
    private static async ValueTask<CompactionJob?> ConsolidationAsync(
        PlannedVersion dataset, TierPartition partition, long top, int share, bool asked, CancellationToken cancellationToken)
    {
        // The span of the level 0's keys, from its least first key to its greatest last.
        List<CompactionInput> fresh = partition.Objects;
        ReadOnlySpan<byte> low = default;
        ReadOnlySpan<byte> high = default;
        for (int i = 0; i < fresh.Count; i++)
        {
            ReadOnlySpan<byte> first = fresh[i].Key.Span[..^VortexDataset.UidBytes];
            ReadOnlySpan<byte> last = fresh[i].Entry.LastKey.Span;
            if (last.IsEmpty)
            {
                return null;
            }

            low = i == 0 || first.SequenceCompareTo(low) < 0 ? first : low;
            high = i == 0 || last.SequenceCompareTo(high) > 0 ? last : high;
        }

        // The consolidated objects that meet it: of its partition, which leads their keys, starting at or before its end and
        // ending at or past its start. An object's last key lies past its tree key, so a subtree is passed over only when it
        // ends before the partition or starts past the span.
        byte[] spanLow = low.ToArray();
        byte[] spanHigh = high.ToArray();
        byte[] value = partition.Value;
        List<CompactionInput> met = [];
        long metBytes = 0;
        if (dataset.Levels.Count > 1)
        {
            await foreach (PositionedEntry held in dataset.Levels[1].WalkAsync(
                    dataset.Pages,
                    0,
                    long.MaxValue,
                    node => TreePage.Compare(node.MaxKey.Span[..Math.Min(node.MaxKey.Length, value.Length)], value) >= 0
                        && TreePage.Compare(node.MinKey.Span[..^VortexDataset.UidBytes], spanHigh) <= 0,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(held.Entry.Value);
                if (held.Entry.Key.Span[..^VortexDataset.UidBytes].SequenceCompareTo(spanHigh) <= 0
                    && (entry.LastKey.IsEmpty || entry.LastKey.Span.SequenceCompareTo(spanLow) >= 0))
                {
                    met.Add(new CompactionInput(1, held.Entry.Key, entry));
                    metBytes += entry.Bytes;
                }
            }
        }

        if (!asked && met.Count > 0 && (long)share * partition.Bytes < metBytes)
        {
            return null;
        }

        List<CompactionInput> inputs = [.. fresh, .. met];
        LevelRange range = LevelRange.Of(1, inputs, dataset.Key);
        return new CompactionJob(0, 1, CompactionStyle.Tiered, CompactionTrigger.Consolidation, inputs, top, 0)
        {
            Range = range,
            Moves = met.Count == 0 && Disjoint(fresh),
        };
    }

    /// <summary>Whether objects hold keys no two of which meet: each one's first key past the last key of the one before.</summary>
    private static bool Disjoint(List<CompactionInput> inputs)
    {
        List<CompactionInput> ordered = [.. inputs];
        ordered.Sort(static (left, right) => left.Key.Span.SequenceCompareTo(right.Key.Span));
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Key.Span[..^VortexDataset.UidBytes].SequenceCompareTo(ordered[i - 1].Entry.LastKey.Span) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One partition's objects at level 0, as a plan counts them.</summary>
    private sealed class TierPartition(byte[] value)
    {
        // The objects of each size class, in the order of the tree.
        private readonly List<List<CompactionInput>> _classes = [];

        /// <summary>The partition, row-encoded; empty for a dataset of one.</summary>
        internal byte[] Value { get; } = value;

        /// <summary>Its objects at level 0, in the order of the tree.</summary>
        internal List<CompactionInput> Objects { get; } = [];

        /// <summary>Their bytes.</summary>
        internal long Bytes { get; private set; }

        /// <summary>When the latest of their rows arrived.</summary>
        internal long Latest { get; private set; }

        /// <summary>Its objects of class 0, which no merge takes any more.</summary>
        internal int ClassZero => _classes.Count > 0 ? _classes[0].Count : 0;

        /// <summary>When the earliest of its objects of class 0 arrived; the current time's past when it holds none.</summary>
        internal long EarliestOfClassZero
        {
            get
            {
                long earliest = long.MaxValue;
                if (_classes.Count > 0)
                {
                    foreach (CompactionInput input in _classes[0])
                    {
                        earliest = Math.Min(earliest, input.Entry.Arrival);
                    }
                }

                return earliest;
            }
        }

        internal void Add(CompactionInput input, int size)
        {
            while (_classes.Count <= size)
            {
                _classes.Add([]);
            }

            _classes[size].Add(input);
            Objects.Add(input);
            Bytes += input.Entry.Bytes;
            Latest = Math.Max(Latest, input.Entry.Arrival);
        }

        /// <summary>The objects past <c>k − 1</c> in a class above 0: what its merges owe.</summary>
        internal long Lag(int fanout)
        {
            long lag = 0;
            for (int c = 1; c < _classes.Count; c++)
            {
                lag += Math.Max(0, _classes[c].Count - (fanout - 1));
            }

            return lag;
        }

        /// <summary>
        /// The merge due: the <paramref name="fanout"/> earliest arrived objects of the class of the smallest objects that
        /// holds as many, and the class; null when none does.
        /// </summary>
        internal (List<CompactionInput> Inputs, int Class)? Due(int fanout)
        {
            for (int c = _classes.Count - 1; c >= 1; c--)
            {
                if (_classes[c].Count >= fanout)
                {
                    List<CompactionInput> earliest = [.. _classes[c]];
                    earliest.Sort(static (left, right) =>
                        left.Entry.Arrival != right.Entry.Arrival
                            ? left.Entry.Arrival.CompareTo(right.Entry.Arrival)
                            : left.Key.Span.SequenceCompareTo(right.Key.Span));
                    return (earliest.GetRange(0, fanout), c);
                }
            }

            return null;
        }
    }
}

/// <summary>What a tiered dataset's partition holds at level 0, and what its compaction owes it.</summary>
public sealed record PartitionCompaction
{
    /// <summary>The partition's value, as text; empty for a dataset of one partition.</summary>
    public string Partition { get; init; } = string.Empty;

    /// <summary>Its objects at level 0, not yet consolidated.</summary>
    public long Objects { get; init; }

    /// <summary>Their bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>The objects past <c>k − 1</c> of a size class above 0: what the merges owe it.</summary>
    public long Lag { get; init; }

    /// <summary>Whether no row arrived in it for <see cref="DatasetLayout.CalmAfter"/>: its consolidation is due.</summary>
    public bool Calm { get; init; }

    /// <summary>
    /// Whether it held more than <c>2k</c> objects of class 0 for more than 48 times <see cref="DatasetLayout.CalmAfter"/>:
    /// it never calms, and a partition by a value that closes, a key led by the arrival order, or levels would serve it.
    /// </summary>
    public bool NeverCalm { get; init; }

    /// <summary>Whether, calm, it holds fewer bytes than the top over <c>k²</c>: its partition is finer than its rows.</summary>
    public bool TooFine { get; init; }
}
