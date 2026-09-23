using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Keys;

internal sealed partial class SortedRunsSource
{
    /// <summary>
    /// Opens a source over the distinct values of the chunks a dictionary probe claims, or says why
    /// none can be had.
    /// </summary>
    /// <remarks>
    /// A dictionary is a run without rows: the probe names only the chunks stored as a dictionary,
    /// and a chunk's values child holds its distinct values in the order the writer met them, so
    /// each claimed chunk is read, its values child decoded alone, then sorted and deduplicated
    /// into a run whose ordinal stands in for a row. The runs are held by the source rather than by
    /// the file's run cache, because a merge needs every run's head from its first seek and an
    /// evicted dictionary would only have to be read again. A claimed chunk that is not a
    /// dictionary refuses the source whole: a walk that skipped it would miss its keys, which is a
    /// wrong answer and not a slow one.
    /// </remarks>
    private static async ValueTask<(SortedRunsSource? Source, string? Reason)> OpenDictionaryAsync(
        VortexFile file, IndexEntry entry, string path, DType dtype, CancellationToken cancellationToken)
    {
        if (Shape(dtype, out FilterLiteralKind kind, out KeyLayout layout, out DType storage) is { } shape)
        {
            return (null, shape);
        }

        if (entry.BlockLength == 0)
        {
            return (null, "its entry's block length is zero");
        }

        List<(LayoutNode Flat, long Start)>? chunks = Chunks(file.LayoutTree, path);
        if (chunks is null)
        {
            return (null, "the column is not stored as flat chunks, so there is no values child to read");
        }

        List<Run> runs = [];
        long read = 0;
        using ScanContext context = new ScanContext(file, ScanContext.MetadataCapacity);
        foreach (IndexRun meta in entry.Runs)
        {
            long first = checked((long)(meta.FirstBlock * entry.BlockLength));
            long end = Math.Min(checked(first + (long)(meta.BlockCount * entry.BlockLength)), file.RowCount);
            foreach ((LayoutNode flat, long start) in chunks)
            {
                if (start < first || start >= end)
                {
                    continue;
                }

                context.ResetBatch();
                SegmentSpec spec = SpecOf(file, flat);
                read += spec.Length;
                int slot = context.Segments.Add(spec);
                await file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
                Diagnostics.VortexEventSource.RunsRead(1);
                RunSegment? values = Values(context, flat, slot, layout);
                if (values is null)
                {
                    return (null, $"the chunk at row {start} is claimed by the probe and is not a vortex.dict array");
                }

                if (values.Count == 0)
                {
                    continue;
                }

                KeySegment bounds = new KeySegment(
                    (ulong)values.Count, KeyBytesAt(values, layout, 0).ToArray(), KeyBytesAt(values, layout, values.Count - 1).ToArray());
                Run run = new Run(meta, FenceTable.InMemory([bounds], layout), start, flat.RowCount, runs.Count)
                {
                    Probe = values,
                    ProbeIndex = 0,
                };
                runs.Add(run);
            }
        }

        return (
            new SortedRunsSource(file, layout, storage, kind, [.. runs], KeySourceKind.Dictionary)
            {
                DictionaryBytes = read,
            },
            null);
    }

    /// <summary>The spec of a flat node's one segment.</summary>
    private static SegmentSpec SpecOf(VortexFile file, LayoutNode flat)
    {
        ReadOnlySpan<uint> ids = flat.Segments;
        ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
        if (ids.Length != 1 || ids[0] >= (uint)specs.Length)
        {
            throw new VortexFormatException($"A flat layout node names {ids.Length} segments; one was expected.");
        }

        return specs[(int)ids[0]];
    }

    /// <summary>
    /// The chunk's values, sorted and deduplicated, nulls dropped; null when the chunk is not a
    /// dictionary.
    /// </summary>
    private static RunSegment? Values(ScanContext context, LayoutNode flat, int slot, KeyLayout layout)
    {
        VortexBuffer segment = context.Segments.GetBuffer(slot);
        FlatLayoutMetadata metadata = FlatLayoutMetadata.Read(flat.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            context.Decode.LoadBlob(metadata.ArrayEncodingTree, segment);
        }
        else
        {
            context.Decode.LoadBlob(segment);
        }

        ArrayNode root = context.Nodes.Root;
        if (root.Encoding != ArrayEncodingId.Dict || root.ChildCount != 2)
        {
            return null;
        }

        DictMetadata dict = DictMetadata.Read(root.Metadata);
        int length = ArrayDecodeContext.CheckedLength(dict.ValuesLength, "vortex.dict values_len");
        CanonicalNode node = context.Canonical.GetNode(context.Decode.DecodeChild(in root, 1, flat.DType, length));
        while (node.Kind == CanonicalKind.Extension)
        {
            node = context.Canonical.GetNode(node.StorageIndex);
        }

        bool bytes = layout.Shape == KeyShape.Bytes;
        bool shaped = bytes
            ? node.Kind == CanonicalKind.VarBinView
            : node.Kind == CanonicalKind.Primitive && node.PType == layout.PType;
        if (!shaped || node.Length != length)
        {
            throw Malformed("a dictionary's values do not decode to one key of the column's type each");
        }

        // The valid values copied out first, in the writer's order: the canonical node is a view
        // that cannot be held by the comparer.
        ValidityMask validity = ValidityMask.From(context.Canonical, node.Validity);
        int width = layout.Width;
        List<int> starts = new List<int>(length + 1) { 0 };
        List<byte> raw = new List<byte>();
        for (int i = 0; i < length; i++)
        {
            if (!validity.IsValid(i))
            {
                continue;
            }

            raw.AddRange(bytes ? LiteralReader.ViewAt(node, i) : node.Values.Span.Slice(i * width, width));
            starts.Add(raw.Count);
        }

        byte[] unsorted = [.. raw];
        int count = starts.Count - 1;
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        ByKey compare = new ByKey(unsorted, starts, bytes, layout);
        SpanSort.Sort(order.AsSpan(), compare);

        // Adjacent equals are one key: a writer's dictionary has none, a lying one may.
        List<int> kept = new List<int>(count);
        foreach (int i in order)
        {
            if (kept.Count == 0 || compare.Compare(kept[^1], i) != 0)
            {
                kept.Add(i);
            }
        }

        int[] offsets = new int[kept.Count + 1];
        for (int k = 0; k < kept.Count; k++)
        {
            offsets[k + 1] = offsets[k] + (starts[kept[k] + 1] - starts[kept[k]]);
        }

        byte[] keys = new byte[offsets[^1]];
        for (int k = 0; k < kept.Count; k++)
        {
            int at = starts[kept[k]];
            unsorted.AsSpan(at, starts[kept[k] + 1] - at).CopyTo(keys.AsSpan(offsets[k]));
        }

        return new RunSegment(keys, bytes ? offsets : null, null, kept.Count);
    }

    /// <summary>A dictionary's keys, by index into their concatenated bytes, in the key order.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly byte[] _unsorted;
        private readonly List<int> _starts;
        private readonly bool _bytes;
        private readonly KeyLayout _layout;

        internal ByKey(byte[] unsorted, List<int> starts, bool bytes, KeyLayout layout)
        {
            _unsorted = unsorted;
            _starts = starts;
            _bytes = bytes;
            _layout = layout;
        }

        public int Compare(int a, int b)
        {
            ReadOnlySpan<byte> left = _unsorted.AsSpan(_starts[a], _starts[a + 1] - _starts[a]);
            ReadOnlySpan<byte> right = _unsorted.AsSpan(_starts[b], _starts[b + 1] - _starts[b]);
            return _bytes
                ? Math.Sign(left.SequenceCompareTo(right))
                : KeyOrder.Total(LiteralOf(_layout, left), LiteralOf(_layout, right));
        }
    }

    /// <summary>
    /// The chunks this dictionary source read, for a caller that wants the sets themselves rather
    /// than a walk over their union: the block pruner answers an equality from the values child
    /// alone.
    /// </summary>
    internal int Dictionaries => _source == KeySourceKind.Dictionary ? _runs.Length : 0;

    /// <summary>What one segment read cost, summed over the chunks read at open.</summary>
    internal long DictionaryBytes { get; private init; }

    /// <summary>Chunk <paramref name="chunk"/>'s first row and its rows.</summary>
    /// <param name="chunk">The chunk, under <see cref="Dictionaries"/>.</param>
    internal (long FirstRow, long Rows) DictionaryExtent(int chunk) =>
        (_runs[chunk].FirstRow, _runs[chunk].RowLimit);

    /// <summary>Whether chunk <paramref name="chunk"/>'s dictionary holds <paramref name="key"/>.</summary>
    /// <param name="chunk">The chunk, under <see cref="Dictionaries"/>.</param>
    /// <param name="key">The key, encoded as the column's layout encodes one.</param>
    /// <remarks>
    /// The values were sorted and deduplicated at open, so this is a binary search over the
    /// chunk's distinct values -- `O(log d)` and no read at all.
    /// </remarks>
    internal bool DictionaryHolds(int chunk, ReadOnlySpan<byte> key)
    {
        if (_runs[chunk].Probe is not { } probe)
        {
            return false;
        }

        int low = 0;
        int high = probe.Count - 1;
        while (low <= high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            int order = _layout.Compare(KeyBytesAt(probe, _layout, middle), key);
            if (order == 0)
            {
                return true;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    private static ReadOnlySpan<byte> KeyBytesAt(RunSegment segment, KeyLayout layout, int index) =>
        segment.Offsets is { } offsets
            ? segment.Keys.AsSpan(offsets[index], offsets[index + 1] - offsets[index])
            : segment.Keys.AsSpan(index * layout.Width, layout.Width);

    /// <summary>
    /// The flat chunks of a column, each with its first row, or null when the column is not stored
    /// that way: the struct field, through any zoned wrapper, then one flat node or a chunked one of
    /// flat nodes.
    /// </summary>
    private static List<(LayoutNode Flat, long Start)>? Chunks(LayoutTree tree, string path)
    {
        LayoutNode node = tree.Root;
        foreach (string name in path.Split('.'))
        {
            if (!Unwrap(ref node) || node.Encoding != LayoutEncodingId.Struct
                || node.DType.IsDefault || node.DType.Kind != DTypeKind.Struct)
            {
                return null;
            }

            int field = node.DType.IndexOfField(name);
            int child = field + (node.DType.IsNullable ? 1 : 0);
            if (field < 0 || child >= node.ChildCount)
            {
                return null;
            }

            node = node.GetChild(child);
        }

        if (!Unwrap(ref node))
        {
            return null;
        }

        if (node.Encoding == LayoutEncodingId.Flat)
        {
            return [(node, 0)];
        }

        if (node.Encoding != LayoutEncodingId.Chunked)
        {
            return null;
        }

        ReadOnlySpan<long> offsets = node.ChunkOffsets;
        List<(LayoutNode, long)> chunks = new List<(LayoutNode, long)>(node.ChildCount);
        for (int i = 0; i < node.ChildCount; i++)
        {
            LayoutNode chunk = node.GetChild(i);
            if (chunk.Encoding != LayoutEncodingId.Flat)
            {
                return null;
            }

            chunks.Add((chunk, offsets[i]));
        }

        return chunks;
    }

    /// <summary>Walks past zoned wrappers.</summary>
    private static bool Unwrap(ref LayoutNode node)
    {
        for (int guard = 0; guard < VortexLimits.MaxLayoutDepth; guard++)
        {
            if (node.Encoding != LayoutEncodingId.Zoned)
            {
                return true;
            }

            if (node.ChildCount != 2)
            {
                return false;
            }

            node = node.GetChild(0);
        }

        return false;
    }
}
