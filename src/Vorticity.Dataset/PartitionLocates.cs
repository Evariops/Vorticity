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
/// Where the values a filter seeks of located columns lie over the consolidated partitions a scan reaches (spec D, 7): each
/// partition's index read once, a page of it a value sought, and for each object it covers the granules that hold one, in
/// place of the object's own index of where its values lie (<see cref="ColumnLocate"/>), which a walk reads object by
/// object. A question over thirty days reads thirty pages a value, where it read an index of each object. A filter whose
/// leaves on those columns are equalities and sets alone asks it: a page of hashes answers no prefix.
/// </summary>
internal sealed class PartitionLocates
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;

    // The columns the filter seeks values of, by place, in the walk's order, and the hashes of each one's values, ascending.
    private readonly int[] _fields;
    private readonly ulong[][] _hashes;

    // Each partition's index as read for the values sought, by the partition's row-encoded value; null for none.
    private readonly Dictionary<byte[], Task<Partition?>> _partitions = new Dictionary<byte[], Task<Partition?>>(Buffers.ByteKeys.Instance);

    private PartitionLocates(VortexDataset dataset, DatasetSnapshot version, int[] fields, ulong[][] hashes)
    {
        _dataset = dataset;
        _version = version;
        _fields = fields;
        _hashes = hashes;
    }

    /// <summary>
    /// The lookup of the values <paramref name="filter"/> seeks of <paramref name="columns"/>, as the walk asks them
    /// (<see cref="SheetPruning.Columns"/>), over the partition indexes of <paramref name="version"/>; null when it keeps none,
    /// or the filter asks a prefix of one of those columns, or a value whose bytes cannot be told.
    /// </summary>
    internal static PartitionLocates? Of(VortexDataset dataset, DatasetSnapshot version, VortexExpr filter, List<int> columns)
    {
        if (version.Levels.PartitionIndexes.IsEmpty || columns.Count == 0)
        {
            return null;
        }

        List<ulong>[] sought = new List<ulong>[columns.Count];
        for (int i = 0; i < sought.Length; i++)
        {
            sought[i] = [];
        }

        if (!Seek(filter, version.Schema.DType, columns, sought))
        {
            return null;
        }

        ulong[][] hashes = new ulong[sought.Length][];
        for (int i = 0; i < hashes.Length; i++)
        {
            sought[i].Sort();
            hashes[i] = [.. sought[i]];
        }

        return new PartitionLocates(dataset, version, [.. columns], hashes);
    }

    /// <summary>
    /// Adds to <paramref name="sought"/>, by the place of their column among <paramref name="columns"/>, the hashes of the
    /// values the equalities and sets of <paramref name="filter"/> seek; false when a leaf on one of those columns is a prefix
    /// or a value whose bytes cannot be told.
    /// </summary>
    private static bool Seek(VortexExpr filter, DType schema, List<int> columns, List<ulong>[] sought) =>
        filter switch
        {
            ComparisonExpr { Op: ComparisonOp.Equal } equal when Place(equal.Field, schema, columns) is int at =>
                Add([equal.Value], schema.GetField(columns[at]), sought[at]),
            InExpr set when Place(set.Field, schema, columns) is int at => Add(set.Literals, schema.GetField(columns[at]), sought[at]),
            StringMatchExpr { Op: StringMatchOp.StartsWith } prefix => Place(prefix.Field, schema, columns) is null,
            LogicalExpr logical => Seek(logical.Left, schema, columns, sought) && Seek(logical.Right, schema, columns, sought),
            _ => true,
        };

    /// <summary>Adds the hashes of the bytes the column's sheet keeps <paramref name="literals"/> by; false when one cannot be told.</summary>
    private static bool Add(FilterLiteral[] literals, DType dtype, List<ulong> into)
    {
        if (CountsAnswer.ValuesOf(literals, dtype) is not { } values)
        {
            return false;
        }

        foreach (byte[] value in values)
        {
            into.Add(XxHash3.HashToUInt64(value));
        }

        return true;
    }

    /// <summary>The place among <paramref name="columns"/> of the top-level column <paramref name="field"/> names; null for another.</summary>
    private static int? Place(FieldExpr field, DType schema, List<int> columns) =>
        field is ColumnFieldExpr && field.Path.IndexOf('.') < 0 && schema.IndexOfField(field.Path) is int index and >= 0 && columns.IndexOf(index) is int at and >= 0
            ? at
            : null;

    /// <summary>
    /// Where the values sought lie in the object <paramref name="held"/>, a source a column of those the walk asks, in its
    /// order; null when no index of its partition covers it for every one of them, its own indexes then read.
    /// </summary>
    internal async ValueTask<IValueGranules?[]?> LocatesAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        byte[] key = PartitionLookup.PartitionOf(_version, held);
        Partition? known;
        while (true)
        {
            Task<Partition?> load;
            lock (_partitions)
            {
                if (!_partitions.TryGetValue(key, out load!))
                {
                    _partitions[key] = load = LoadAsync(key, cancellationToken);
                }
            }

            // The lanes of a question share the lookup: a load another lane's caller cancelled is not this one's failure,
            // and is begun again.
            try
            {
                known = await load.WaitAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_partitions)
                {
                    if (_partitions.TryGetValue(key, out Task<Partition?>? failed) && ReferenceEquals(failed, load))
                    {
                        _partitions.Remove(key);
                    }
                }
            }
        }

        if (known is null || !known.Entry.Covers(held.Entry.Uid) || !known.Ranks.TryGetValue(held.Entry.Uid, out int rank))
        {
            return null;
        }

        PartitionIndex.Covered covered = known.Index.Objects[rank];
        if (covered.Rows != held.Entry.PhysicalRows || covered.BlockRows <= 0)
        {
            return null;
        }

        int granules = (int)((covered.Rows + covered.BlockRows - 1) / covered.BlockRows);
        IValueGranules?[] sources = new IValueGranules?[_fields.Length];
        for (int c = 0; c < sources.Length; c++)
        {
            if (known.Columns[c] is not { } column || !column.Entry.Covers(rank))
            {
                return null;
            }

            sources[c] = new Found(granules, column.Granules.TryGetValue(rank, out Dictionary<ulong, List<uint>>? byHash) ? byHash : null);
        }

        return sources;
    }

    /// <summary>
    /// The index of <paramref name="partition"/>, its directory and where the values sought lie, by column and object rank:
    /// the pages they lie in read once each, in one request a page, each against its checksum; null when the partition keeps
    /// no index.
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
            using PartitionPostings.OpenedIndex opened = await PartitionPostings.OpenCachedAsync(
                reader, entry.ObjectKey, entry.Uid, entry.Bytes, _dataset.PartitionIndexes, cancellationToken).ConfigureAwait(false);
            PartitionIndex index = opened.Index;
            Dictionary<UInt128, int> ranks = new Dictionary<UInt128, int>(index.Objects.Count);
            for (int rank = 0; rank < index.Objects.Count; rank++)
            {
                ranks[index.Objects[rank].Uid] = rank;
            }

            Column?[] columns = new Column?[_fields.Length];
            List<(int Rank, uint Block)> found = [];
            for (int c = 0; c < columns.Length; c++)
            {
                if (index.Of(_version.Schema.Columns[_fields[c]].Name, PartitionIndex.EntryKind.Locate) is not { } located)
                {
                    continue;
                }

                Dictionary<int, Dictionary<ulong, List<uint>>> granules = [];
                foreach (ulong hash in _hashes[c])
                {
                    int at = located.PageOf(hash);
                    if (at < 0)
                    {
                        continue;
                    }

                    PartitionIndex.Page held = located.Pages[at];
                    if (!await opened.LoadAsync(held, cancellationToken).ConfigureAwait(false))
                    {
                        throw new VortexFormatException($"A page of where the values of '{located.Name}' lie in the index '{entry.ObjectKey}' fails its checksum.");
                    }

                    found.Clear();
                    located.Find(opened.Page, held, hash, found);
                    foreach ((int rank, uint granule) in found)
                    {
                        if (!granules.TryGetValue(rank, out Dictionary<ulong, List<uint>>? byHash))
                        {
                            granules[rank] = byHash = [];
                        }

                        if (!byHash.TryGetValue(hash, out List<uint>? of))
                        {
                            byHash[hash] = of = [];
                        }

                        of.Add(granule);
                    }
                }

                columns[c] = new Column(located, granules);
            }

            return new Partition(entry, index, ranks, columns);
        }
    }

    /// <summary>A partition's index as the lookup read it: its entry, its directory, the objects' ranks, and each column sought, by its place in the walk's order.</summary>
    private sealed record Partition(PartitionIndexEntry Entry, PartitionIndex Index, Dictionary<UInt128, int> Ranks, Column?[] Columns);

    /// <summary>A located column of a partition's index: its entry, and the granules of the values sought, by object rank and value hash.</summary>
    private sealed record Column(PartitionIndex.Entry Entry, Dictionary<int, Dictionary<ulong, List<uint>>> Granules);

    /// <summary>
    /// Where the values sought lie in one object, by their hashes, as its partition's index says: a hash it does not hold
    /// lies nowhere there; it tells no prefix.
    /// </summary>
    private sealed class Found(int granules, Dictionary<ulong, List<uint>>? byHash) : IValueGranules
    {
        public int Granules => granules;

        public void OrValue(ReadOnlySpan<byte> value, Span<ulong> into)
        {
            if (byHash is null || !byHash.TryGetValue(XxHash3.HashToUInt64(value), out List<uint>? held))
            {
                return;
            }

            foreach (uint granule in held)
            {
                if (granule == PartitionIndex.Everywhere)
                {
                    Arrays.Decoders.Canonical.BitmapKernels.FillRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(into), 0, granules, true);
                    return;
                }

                if (granule < (uint)granules)
                {
                    into[(int)(granule >> 6)] |= 1UL << (int)(granule & 63);
                }
            }
        }

        public bool TryOrPrefix(ReadOnlySpan<byte> prefix, Span<ulong> into) => false;
    }
}
