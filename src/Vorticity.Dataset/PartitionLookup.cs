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

        (FieldExpr field, FilterLiteral[] values) = (lookup is Compute.VariantFilterExpr fenced ? fenced.Inner : lookup) switch
        {
            ComparisonExpr comparison => (comparison.Field, new[] { comparison.Value }),
            InExpr list => (list.Field, list.Literals),
            _ => (null!, []),
        };
        if (field is null || Identified(version.Schema.DType, field) is not (string identifier, KeyLayout layout))
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
        return new PartitionLookup(dataset, version, identifier, [.. hashes]);
    }

    /// <summary>
    /// The identifier <paramref name="field"/> names, as the postings name it, and the layout its values are hashed in: a
    /// column's own, or a key of the attributes read in its class, whose texts are the bytes of their UTF-8 and whose integers
    /// are eight signed bytes; null for any other field.
    /// </summary>
    private static (string Identifier, KeyLayout Layout)? Identified(DType schema, FieldExpr field)
    {
        if (field is Compute.VariantFieldExpr path)
        {
            return VortexDataset.IdentifierPath(path.Text, schema) is { } key
                ? (key.Text, key.Class == Types.Variants.VariantClass.String ? new KeyLayout(KeyShape.Bytes, 0, default) : new KeyLayout(KeyShape.Signed, sizeof(long), PType.I64))
                : null;
        }

        int index = schema.IndexOfField(field.Path);
        return index >= 0 && KeyLayout.TryOf(schema.GetField(index), out KeyLayout layout) ? (field.Path, layout) : null;
    }

    /// <summary>
    /// The places of the object <paramref name="held"/> that may hold a value sought, ascending, and the rows of a place, its
    /// identifier's unit of blocks (spec C, 5); null when no index of its partition covers it for the identifier, whose own
    /// postings then answer.
    /// </summary>
    internal async ValueTask<(uint[] Blocks, int BlockRows)?> BlocksAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        Partition? known = await LoadingAsync(held, cancellationToken).ConfigureAwait(false);
        if (known is null || known.RankOf(held.Entry.Uid) is not (>= 0 and int rank)
            || !known.Postings.Covers(rank) || known.Index.Objects[rank].Rows != held.Entry.PhysicalRows)
        {
            return null;
        }

        return (known.BlocksOf(rank), known.Index.Objects[rank].BlockRows * known.Postings.UnitBlocks);
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
        // Found by the tree key's bytes: a key of its own is made for a partition met first alone.
        ReadOnlySpan<byte> partition = PartitionSpan(_version, held);
        lock (_partitions)
        {
            if (!_partitions.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(partition, out Task<Partition?>? loading))
            {
                byte[] key = partition.ToArray();
                _partitions[key] = loading = LoadAsync(key, cancellationToken);
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

    /// <summary>The partition of <paramref name="version"/> that <paramref name="held"/>'s rows hold, row-encoded as its tree key leads with it; empty for a dataset without one.</summary>
    internal static byte[] PartitionOf(DatasetSnapshot version, PositionedObject held) => PartitionSpan(version, held).ToArray();

    /// <summary>The bytes of <paramref name="held"/>'s tree key that name its partition (<see cref="PartitionOf"/>).</summary>
    internal static ReadOnlySpan<byte> PartitionSpan(DatasetSnapshot version, in PositionedObject held)
    {
        if (!version.Schema.Partitioned || held.TreeKey.Length <= VortexDataset.UidBytes)
        {
            return [];
        }

        ReadOnlySpan<byte> key = VortexDataset.OrderOf(held.TreeKey).Span;
        return key[..version.Schema.Tree!.PrefixLength(key, 1)];
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

        PartitionPostings.OpenedIndex opened = await PartitionPostings.OpenCachedAsync(
            _dataset.Store, entry, _dataset.PartitionIndexes, cancellationToken).ConfigureAwait(false);
        await using (opened.ConfigureAwait(false))
        {
            PartitionIndex index = opened.Index;
            if (index.Of(_identifier, PartitionIndex.EntryKind.Postings) is not { } postings)
            {
                return null;
            }

            List<(int Rank, uint Block)> found = [];
            List<ulong>? placed = null;
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
                postings.Find(opened.Page, held, hash, found, opened.Restarts(postings, held));
                foreach ((int rank, uint block) in found)
                {
                    (placed ??= []).Add(((ulong)(uint)rank << 32) | block);
                }
            }

            return new Partition(index, postings, entry.RanksIn(index), Placed(placed));
        }
    }

    /// <summary>The postings found, each a rank and a block, ascending and each once.</summary>
    private static ulong[] Placed(List<ulong>? placed)
    {
        if (placed is null)
        {
            return [];
        }

        placed.Sort();
        int kept = 0;
        for (int i = 0; i < placed.Count; i++)
        {
            if (kept == 0 || placed[i] != placed[kept - 1])
            {
                placed[kept++] = placed[i];
            }
        }

        return [.. System.Runtime.InteropServices.CollectionsMarshal.AsSpan(placed)[..kept]];
    }

    /// <summary>
    /// A partition's index as the lookup read it: its directory, the identifier's postings, the ranks the version's entry
    /// covers, a bit a rank, and the postings found of the values sought, a rank and a block each, ascending.
    /// </summary>
    private sealed class Partition(PartitionIndex index, PartitionIndex.Entry postings, ulong[] covered, ulong[] placed)
    {
        /// <summary>The index's directory.</summary>
        internal PartitionIndex Index => index;

        /// <summary>The identifier's postings.</summary>
        internal PartitionIndex.Entry Postings => postings;

        /// <summary>The rank of the object of uid <paramref name="uid"/>, -1 when the version's entry does not cover it.</summary>
        internal int RankOf(UInt128 uid)
        {
            int rank = index.RankOf(uid);
            return rank >= 0 && (covered[rank >> 6] & (1UL << (rank & 63))) != 0 ? rank : -1;
        }

        /// <summary>The blocks of the object of rank <paramref name="rank"/> that hold a value sought, ascending.</summary>
        internal uint[] BlocksOf(int rank)
        {
            int from = Array.BinarySearch(placed, (ulong)(uint)rank << 32);
            from = from >= 0 ? from : ~from;
            int to = from;
            while (to < placed.Length && placed[to] >> 32 == (uint)rank)
            {
                to++;
            }

            if (to == from)
            {
                return [];
            }

            uint[] blocks = new uint[to - from];
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i] = (uint)placed[from + i];
            }

            return blocks;
        }
    }
}
