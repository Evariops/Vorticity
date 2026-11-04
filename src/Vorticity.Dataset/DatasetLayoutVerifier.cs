using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The layout's invariants over a version's objects (spec A, 3.1), what a full <see cref="VortexDataset.VerifyAsync"/>
/// checks past the store's promises: each object's rows against the layout, its entry against its rows, the levels
/// against the organization. Each problem is named, as the store's checks name theirs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>I1: an object holds the rows of one value of the partition.</item>
/// <item>I2: its rows lie in the order of the dataset's tree key, the partition then the key's components.</item>
/// <item>I3: under a key, an object that keeps a table of contents keeps the sparse index of the key's prefix, which holds
/// each granule's first key; an imported file waits for its first merge.</item>
/// <item>I4: its tree key is its first row's key then its uid, and its entry holds its last row's key; under a partition
/// alone, its tree key leads with its partition.</item>
/// <item>I5: under levels, every level above 0 is disjoint on the tree key; under tiers, the objects of level 1 of one
/// partition are.</item>
/// <item>I7: an object that keeps a table of contents keeps the geometry of its columns' chunks, and the values of each of
/// its object dictionaries lie in the order of their type.</item>
/// </list>
/// I6 is I1 for the objects merges write; the bytes they hold past <see cref="DatasetLayout.ObjectBytes"/>, a key's rows,
/// are not counted here. Each object's key columns are decoded once; an object's rows are read whole, as the store's
/// checks hash it whole.
/// </remarks>
internal static class DatasetLayoutVerifier
{
    /// <summary>The problems the objects of <paramref name="dataset"/>'s version hold against its layout; none for a dataset without one.</summary>
    internal static async ValueTask<List<string>> CheckAsync(VortexDataset dataset, CancellationToken cancellationToken)
    {
        List<string> problems = [];
        DatasetSchema schema = dataset.Snapshot.Schema;
        if (schema.Layout is not { } plan || schema.Tree is not { } tree)
        {
            return problems;
        }

        SortKey? indexed = plan.Keyed ? SortKey.Of(plan, partition: false, components: plan.Components.Count) : null;
        List<(int Level, byte[] First, byte[] Last, byte[] Partition, string Key)> placed = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            // An object whose bytes, or whose vector of deleted rows, do not read is one the store's checks name.
            byte[]? first;
            try
            {
                first = await CheckObjectAsync(dataset, held, plan, tree, indexed, problems, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception unreadable) when (unreadable is TornCommitException or CommitFormatException or ObjectNotFoundException
                or VortexFormatException or VortexEncryptionException)
            {
                continue;
            }

            if (first is not null && plan.Keyed)
            {
                int partition = plan.Partition is null ? 0 : tree.PrefixLength(first, 1);
                placed.Add((held.Level, first, held.Entry.LastKey.ToArray(), first[..Math.Max(partition, 0)], held.Entry.Key));
            }
        }

        CheckLevels(placed, plan.Organization, problems);
        return problems;
    }

    /// <summary>
    /// I1 to I4 and I7 over the object <paramref name="held"/>, its rows read once in a stream, the key before kept alone:
    /// its first row's tree key, or null when it holds none.
    /// </summary>
    private static async ValueTask<byte[]?> CheckObjectAsync(
        VortexDataset dataset, PositionedObject held, LayoutPlan plan, SortKey tree, SortKey? indexed, List<string> problems,
        CancellationToken cancellationToken)
    {
        ObjectEntry entry = held.Entry;
        ObjectContents? contents = entry.Contents;

        // I3: the sparse index of the key's prefix, read first, so that each granule's first key is met as its row passes.
        GranuleKeys? index = null;
        GranuleKeys.KeyReader granuleKeys = default;
        if (indexed is not null && contents is not null)
        {
            index = await dataset.KeyIndexAsync(entry, contents, cancellationToken).ConfigureAwait(false);
            if (index is null)
            {
                problems.Add(Invariant($"'{entry.Key}': it keeps no sparse index of its key (I3)"));
            }
            else if (index.Components != plan.Prefix)
            {
                problems.Add(Invariant($"'{entry.Key}': its sparse index holds {index.Components} components of the key, the layout indexes {plan.Prefix} (I3)"));
                index.Dispose();
                index = null;
            }
        }

        // Given back once the object's rows are checked against it.
        using GranuleKeys? read = index;

        byte[]? first = null;
        byte[] previous = [];
        int previousLength = 0;
        byte[] lastPrefix = [];
        long rows = 0;
        int partition = 0;
        bool misplaced = false;
        bool unordered = false;
        bool unindexed = false;
        ObjectLease lease = await dataset.RentAsync(entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in lease.File.Scan().ToBatchesAsync(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                {
                    using RowKeys encoded = tree.Encode(batch);
                    using RowKeys? keyed = index is null ? null : indexed!.Encode(batch);
                    for (int i = 0; i < batch.RowCount; i++, rows++)
                    {
                        ReadOnlySpan<byte> key = encoded.Row(i);
                        if (first is null)
                        {
                            first = key.ToArray();
                            partition = plan.Partition is null ? 0 : tree.PrefixLength(first, 1);
                        }
                        else
                        {
                            // I1: the partition every row holds; I2: the rows in the tree key's order.
                            if (partition > 0 && !misplaced && !tree.SamePrefix(first, key, 1))
                            {
                                problems.Add(Invariant($"'{entry.Key}': row {rows} lies in another partition than its first row (I1)"));
                                misplaced = true;
                            }

                            if (!unordered && previous.AsSpan(0, previousLength).SequenceCompareTo(key) > 0)
                            {
                                problems.Add(Invariant($"'{entry.Key}': row {rows} lies before the row ahead of it in the key's order (I2)"));
                                unordered = true;
                            }
                        }

                        if (previous.Length < key.Length)
                        {
                            previous = new byte[Math.Max(key.Length, previous.Length * 2)];
                        }

                        key.CopyTo(previous);
                        previousLength = key.Length;
                        if (keyed is not null)
                        {
                            ReadOnlySpan<byte> full = keyed.Row(i);
                            ReadOnlySpan<byte> prefix = full[..indexed!.PrefixLength(full, plan.Prefix)];
                            if (!unindexed && rows % index!.GranuleRows == 0 && (rows / index.GranuleRows >= index.Granules || !granuleKeys.Key(index, (int)(rows / index.GranuleRows)).SequenceEqual(Coded(index, indexed, prefix))))
                            {
                                problems.Add(Invariant($"'{entry.Key}': its sparse index's key of granule {rows / index.GranuleRows} is not the key of the granule's first row (I3)"));
                                unindexed = true;
                            }

                            if (i == batch.RowCount - 1)
                            {
                                lastPrefix = prefix.ToArray();
                            }
                        }
                    }
                }
            }
        }

        if (first is null)
        {
            return null;
        }

        granuleKeys.Dispose();
        if (index is not null && (index.Rows != rows || !index.LastKey.SequenceEqual(Coded(index, indexed!, lastPrefix))))
        {
            problems.Add(Invariant($"'{entry.Key}': its sparse index holds {index.Rows} rows and another last key, the object {rows} (I3)"));
        }

        // I4: the tree key of its first row, the key of its last in its entry; the partition alone leads it otherwise.
        ReadOnlySpan<byte> order = VortexDataset.OrderOf(held.TreeKey).Span;
        if (plan.Keyed)
        {
            if (!order.SequenceEqual(first))
            {
                problems.Add(Invariant($"'{entry.Key}': its tree key is not its first row's key (I4)"));
            }

            if (!entry.LastKey.Span.SequenceEqual(previous.AsSpan(0, previousLength)))
            {
                problems.Add(Invariant($"'{entry.Key}': its entry's last key is not its last row's (I4)"));
            }
        }
        else if (partition > 0 && !order.StartsWith(first.AsSpan(0, partition)))
        {
            problems.Add(Invariant($"'{entry.Key}': its tree key does not lead with its partition (I4)"));
        }

        // I7: the geometry of its chunks, and its object dictionaries' values in their type's order.
        if (contents is not null)
        {
            if (contents.Marks.Count == 0)
            {
                problems.Add(Invariant($"'{entry.Key}': its table of contents names no geometry of its chunks (I7)"));
            }

            ObjectDictionary?[] dictionaries = await dataset.DictionariesAsync(entry, contents, cancellationToken).ConfigureAwait(false);
            for (int field = 0; field < dictionaries.Length; field++)
            {
                if (dictionaries[field] is { } dictionary && !Ordered(dictionary))
                {
                    problems.Add(Invariant($"'{entry.Key}': the values of column {field}'s object dictionary are not in their type's order (I7)"));
                }
            }
        }

        return first;
    }

    /// <summary>
    /// I5: under levels, every level above 0 disjoint on the tree key, each object's first key past the last key of the one
    /// before it; under tiers, the objects of level 1 of one partition.
    /// </summary>
    private static void CheckLevels(List<(int Level, byte[] First, byte[] Last, byte[] Partition, string Key)> placed, LayoutOrganization organization, List<string> problems)
    {
        placed.Sort(static (left, right) => left.Level != right.Level ? left.Level.CompareTo(right.Level) : left.First.AsSpan().SequenceCompareTo(right.First));
        for (int i = 1; i < placed.Count; i++)
        {
            (int level, byte[] first, _, byte[] partition, string key) = placed[i];
            (int before, _, byte[] last, byte[] beforePartition, string beforeKey) = placed[i - 1];
            bool judged = level == before && (organization == LayoutOrganization.Leveled
                ? level > 0
                : level == 1 && partition.AsSpan().SequenceEqual(beforePartition));
            if (judged && first.AsSpan().SequenceCompareTo(last) <= 0)
            {
                problems.Add(Invariant($"'{key}' starts at or before the last key of '{beforeKey}' on level {level} (I5)"));
            }
        }
    }

    /// <summary>Whether a dictionary's values lie in their type's order, each past the one before.</summary>
    private static bool Ordered(ObjectDictionary dictionary)
    {
        for (int code = 1; code < dictionary.Count; code++)
        {
            ReadOnlySpan<byte> before = dictionary.Value(code - 1);
            ReadOnlySpan<byte> value = dictionary.Value(code);
            int order = dictionary.Kind != DTypeKind.Primitive
                ? before.SequenceCompareTo(value)
                : dictionary.PType switch
                {
                    PType.I32 => BitConverter.ToInt32(before).CompareTo(BitConverter.ToInt32(value)),
                    PType.U32 => BitConverter.ToUInt32(before).CompareTo(BitConverter.ToUInt32(value)),
                    PType.I64 => BitConverter.ToInt64(before).CompareTo(BitConverter.ToInt64(value)),
                    _ => BitConverter.ToUInt64(before).CompareTo(BitConverter.ToUInt64(value)),
                };
            if (order >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="key"/>, a row-encoded key of <paramref name="indexed"/>'s components, coded as <paramref name="index"/>
    /// codes its keys; as it is when its components cannot be coded so, which then holds no key of the index.
    /// </summary>
    private static ReadOnlySpan<byte> Coded(GranuleKeys index, SortKey indexed, ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<int> widths = indexed.Widths[..Math.Min(index.Components, indexed.Count)];
        return index.Coding == KeyCoding.Rows || !UnpaddedKeys.Codes(widths) ? key : UnpaddedKeys.Narrow(key, widths);
    }
}
