using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// A lookup of an identifier's values over the consolidated partitions a scan reaches (D13): each partition's index read
/// once, a page of it a value sought, and for each object it covers the blocks that may hold one. An object the index
/// covers whose blocks hold none of the values is passed over with no read of it at all, which a lookup object by object
/// read the postings of: a question over thirty days reads thirty pages, where it read the postings of every object.
/// </summary>
internal sealed class PartitionLookup
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;
    private readonly string _identifier;
    private readonly ulong[] _hashes;
    private readonly Dictionary<byte[], Task<Partition?>> _partitions = new Dictionary<byte[], Task<Partition?>>(Buffers.ByteKeys.Instance);

    private PartitionLookup(VortexDataset dataset, DatasetSnapshot version, string identifier, ulong[] hashes)
    {
        _dataset = dataset;
        _version = version;
        _identifier = identifier;
        _hashes = hashes;
    }

    /// <summary>
    /// The lookup of <paramref name="lookup"/>, an equality or a list of values of a declared identifier's column
    /// (<see cref="DatasetScanBuilder.LookupOf"/>), over the partition indexes of <paramref name="version"/>; null when the
    /// version keeps none, or a value has no key the identifier's postings hash.
    /// </summary>
    internal static PartitionLookup? Of(VortexDataset dataset, DatasetSnapshot version, VortexExpr lookup)
    {
        if (version.Levels.PartitionIndexes.IsEmpty)
        {
            return null;
        }

        (FieldExpr field, FilterLiteral[] values) = lookup switch
        {
            ComparisonExpr comparison => (comparison.Field, new[] { comparison.Value }),
            InExpr list => (list.Field, list.Literals),
            _ => (null!, []),
        };
        int index = field is null ? -1 : version.Schema.DType.IndexOfField(field.Path);
        if (index < 0 || !KeyLayout.TryOf(version.Schema.DType.GetField(index), out KeyLayout layout))
        {
            return null;
        }

        // Each value's hash as the identifier's hashed postings key it, the bytes its layout gives it; a float zero's other.
        List<ulong> hashes = [];
        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        foreach (FilterLiteral value in values)
        {
            if (!layout.TryEncode(value, scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
            {
                return null;
            }

            hashes.Add(XxHash3.HashToUInt64(key));
            if (hasOtherZero)
            {
                hashes.Add(XxHash3.HashToUInt64(zero[..layout.Width]));
            }
        }

        hashes.Sort();
        return new PartitionLookup(dataset, version, field!.Path, [.. hashes]);
    }

    /// <summary>
    /// The places of the object <paramref name="held"/> that may hold a value sought, ascending, and the rows of a place, its
    /// identifier's unit of blocks (spec C, 5); null when no index of its partition covers it for the identifier, whose own
    /// postings then answer.
    /// </summary>
    internal async ValueTask<(uint[] Blocks, int BlockRows)?> BlocksAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        Partition? known = await LoadingAsync(held, cancellationToken).ConfigureAwait(false);
        if (known is null || !known.Entry.Covers(held.Entry.Uid) || !known.Ranks.TryGetValue(held.Entry.Uid, out int rank)
            || !known.Postings.Covers(rank) || known.Index.Objects[rank].Rows != held.Entry.PhysicalRows)
        {
            return null;
        }

        return (known.Blocks.TryGetValue(rank, out SortedSet<uint>? blocks) ? [.. blocks] : [],
            known.Index.Objects[rank].BlockRows * known.Postings.UnitBlocks);
    }

    /// <summary>
    /// Begins reading the index of the partition of <paramref name="held"/>, when no read of it began: a walk that asks each
    /// object's partition before it awaits any reads the indexes side by side, thirty days in one wave, where each awaited
    /// in turn cost two round trips, its directory then its page.
    /// </summary>
    internal void Ahead(PositionedObject held, CancellationToken cancellationToken) => _ = LoadingAsync(held, cancellationToken);

    /// <summary>The read of the index of the partition of <paramref name="held"/>, begun once.</summary>
    private Task<Partition?> LoadingAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        byte[] partition = PartitionOf(held);
        lock (_partitions)
        {
            if (!_partitions.TryGetValue(partition, out Task<Partition?>? loading))
            {
                _partitions[partition] = loading = LoadAsync(partition, cancellationToken);
            }

            return loading;
        }
    }

    /// <summary>Waits for the reads of the partitions' indexes begun ahead, whatever they found: a scan that stopped lets them go.</summary>
    internal async ValueTask SettleAsync()
    {
        Task<Partition?>[] loads;
        lock (_partitions)
        {
            loads = [.. _partitions.Values];
        }

        foreach (Task<Partition?> load in loads)
        {
            try
            {
                await load.ConfigureAwait(false);
            }
            catch (Exception) when (load.IsFaulted || load.IsCanceled)
            {
            }
        }
    }

    /// <summary>The partition <paramref name="held"/>'s rows hold, row-encoded as its tree key leads with it; empty for a dataset without one.</summary>
    private byte[] PartitionOf(PositionedObject held) => PartitionOf(_version, held);

    /// <summary>The partition of <paramref name="version"/> that <paramref name="held"/>'s rows hold, row-encoded as its tree key leads with it; empty for a dataset without one.</summary>
    internal static byte[] PartitionOf(DatasetSnapshot version, PositionedObject held)
    {
        if (!version.Schema.Partitioned || held.TreeKey.Length <= VortexDataset.UidBytes)
        {
            return [];
        }

        ReadOnlySpan<byte> key = VortexDataset.OrderOf(held.TreeKey).Span;
        return key[..version.Schema.Tree!.PrefixLength(key, 1)].ToArray();
    }

    /// <summary>
    /// The index of <paramref name="partition"/>, its directory and the postings of the values sought, by object rank: the
    /// pages they lie in read once each, in one request a page, each against its checksum; null when the partition keeps no
    /// index of the identifier.
    /// </summary>
    private async Task<Partition?> LoadAsync(byte[] partition, CancellationToken cancellationToken)
    {
        if (await _version.PartitionIndexAsync(partition, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return null;
        }

        ISegmentReader reader = await SealedObjectStore.OpenDataAsync(_dataset.Store, entry.ObjectKey, entry.Uid, entry.Bytes, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            using PartitionPostings.OpenedIndex opened = await PartitionPostings.OpenAsync(reader, entry.Bytes, cancellationToken).ConfigureAwait(false);
            PartitionIndex index = opened.Index;
            if (index.Of(_identifier, PartitionIndex.EntryKind.Postings) is not { } postings)
            {
                return null;
            }

            Dictionary<UInt128, int> ranks = new Dictionary<UInt128, int>(index.Objects.Count);
            for (int rank = 0; rank < index.Objects.Count; rank++)
            {
                ranks[index.Objects[rank].Uid] = rank;
            }

            Dictionary<int, SortedSet<uint>> blocks = [];
            List<(int Rank, uint Block)> found = [];
            foreach (ulong hash in _hashes)
            {
                int at = postings.PageOf(hash);
                if (at < 0)
                {
                    continue;
                }

                PartitionIndex.Page held = postings.Pages[at];
                if (!await opened.LoadAsync(held, cancellationToken).ConfigureAwait(false))
                {
                    throw new VortexFormatException($"A page of the postings of '{_identifier}' in the index '{entry.ObjectKey}' fails its checksum.");
                }

                found.Clear();
                postings.Find(opened.Page, held, hash, found);
                foreach ((int rank, uint block) in found)
                {
                    if (!blocks.TryGetValue(rank, out SortedSet<uint>? of))
                    {
                        blocks[rank] = of = [];
                    }

                    of.Add(block);
                }
            }

            return new Partition(entry, index, postings, ranks, blocks);
        }
    }

    /// <summary>A partition's index as the lookup read it: its entry, its directory, the identifier's postings, the objects' ranks, the blocks found by rank.</summary>
    private sealed record Partition(
        PartitionIndexEntry Entry,
        PartitionIndex Index,
        PartitionIndex.Entry Postings,
        Dictionary<UInt128, int> Ranks,
        Dictionary<int, SortedSet<uint>> Blocks);
}
