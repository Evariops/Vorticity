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
                int slot = context.Segments.Add(SpecOf(file, flat));
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

        return (new SortedRunsSource(file, layout, storage, kind, [.. runs], KeySourceKind.Dictionary), null);
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

    /// <summary>The bytes of chunks a dictionary probe asks for in one round trip: enough to ride a store's latency, few enough to hold.</summary>
    private const long ProbeBatchBytes = 32L << 20;

    /// <summary>
    /// Which of <paramref name="keys"/> the dictionaries of the chunks of <paramref name="path"/> hold, for a pruner: the
    /// chunks the file's dictionary probe claims and <paramref name="wanted"/> keeps, asked with each one's first row and
    /// rows. They are read together, <see cref="ProbeBatchBytes"/> at a time, a round trip a batch however many chunks it
    /// holds, and a chunk's values child is decoded alone and its values looked up among the keys, sorted once, as they
    /// lie: nothing is copied nor sorted per chunk, where a walk over the values needs them sorted and deduplicated. A
    /// null key is never held. A chunk that is not a dictionary claims nothing, and its blocks stay live. Null when the
    /// file has no probe of the column, or one whose runs leave blocks uncovered.
    /// </summary>
    internal static async ValueTask<DictionaryClaims?> ClaimDictionariesAsync(
        VortexFile file, string path, byte[]?[] keys, Func<long, long, bool> wanted, CancellationToken cancellationToken)
    {
        if (!file.HasIndexDirectory || await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false) is not { } directory)
        {
            return null;
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind == IndexKinds.DictProbe
                && KeyIndexPruner.TryResolve(file.DType, entry.ColumnPath, out string resolved, out DType dtype)
                && string.Equals(resolved, path, StringComparison.Ordinal)
                && entry.BlockLength > 0
                && Uncovered(file, entry) is null
                && Shape(dtype, out _, out KeyLayout layout, out _) is null
                && Chunks(file.LayoutTree, path) is { } chunks)
            {
                return await ClaimAsync(file, entry, layout, chunks, keys, wanted, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async ValueTask<DictionaryClaims> ClaimAsync(
        VortexFile file, IndexEntry entry, KeyLayout layout, List<(LayoutNode Flat, long Start)> chunks, byte[]?[] keys,
        Func<long, long, bool> wanted, CancellationToken cancellationToken)
    {
        // The chunks the entry's runs claim and the caller wants, in the file's order: the runs are contiguous, from the
        // first block, which Uncovered checked.
        List<(LayoutNode Flat, long Start)> asked = [];
        int r = 0;
        foreach ((LayoutNode flat, long start) in chunks)
        {
            while (r < entry.Runs.Count && Math.Min(checked((long)(entry.Runs[r].EndBlock * entry.BlockLength)), file.RowCount) <= start)
            {
                r++;
            }

            if (r < entry.Runs.Count && start >= checked((long)(entry.Runs[r].FirstBlock * entry.BlockLength)) && wanted(start, (long)flat.RowCount))
            {
                asked.Add((flat, start));
            }
        }

        // The keys in the layout's order, each by its place among those asked.
        List<int> sorted = [];
        for (int k = 0; k < keys.Length; k++)
        {
            if (keys[k] is not null)
            {
                sorted.Add(k);
            }
        }

        sorted.Sort((a, b) => layout.Compare(keys[a], keys[b]));
        int[] order = [.. sorted];
        DictionaryClaims claims = new DictionaryClaims(keys.Length);

        // A batch's segments in one context, each chunk decoded alone in another, both the process's: the
        // pruning of a scan of one part of an object probes a chunk or two.
        ScanContext reading = ScanContexts.Shared.Rent(file);
        ScanContext decoding = ScanContexts.Shared.Rent(file);
        try
        {
            int[] slots = new int[asked.Count];
            for (int at = 0; at < asked.Count;)
            {
                reading.ResetBatch();
                int from = at;
                long bytes = 0;
                for (; at < asked.Count && (at == from || bytes < ProbeBatchBytes); at++)
                {
                    SegmentSpec spec = SpecOf(file, asked[at].Flat);
                    slots[at] = reading.Segments.Add(spec);
                    bytes += spec.Length;
                }

                claims.Read += Scanning.ScanCounters.Unread(reading.Segments, file.Segments, out long unread);
                claims.Bytes += unread;
                await file.Segments.ReadManyAsync(reading.Segments, cancellationToken).ConfigureAwait(false);
                Diagnostics.VortexEventSource.RunsRead(at - from);
                for (int c = from; c < at; c++)
                {
                    decoding.ResetBatch();
                    Claim(decoding, asked[c].Flat, asked[c].Start, reading.Segments.GetBuffer(slots[c]), layout, keys, order, claims);
                }
            }
        }
        finally
        {
            decoding.ResetBatch();
            reading.ResetBatch();
            ScanContexts.Shared.Return(decoding);
            ScanContexts.Shared.Return(reading);
        }

        return claims;
    }

    /// <summary>Adds a chunk to <paramref name="claims"/> with the keys its dictionary holds, when it is one.</summary>
    private static void Claim(
        ScanContext context, LayoutNode flat, long start, VortexBuffer segment, KeyLayout layout, byte[]?[] keys, int[] order, DictionaryClaims claims)
    {
        if (!DictionaryValues(context, flat, segment, layout, out CanonicalNode node, out int length))
        {
            return;
        }

        claims.Open(start, (long)flat.RowCount);
        ValidityMask validity = ValidityMask.From(context.Canonical, node.Validity);
        bool bytes = layout.Shape == KeyShape.Bytes;
        int width = layout.Width;
        int held = 0;
        for (int i = 0; i < length && held < order.Length; i++)
        {
            if (!validity.IsValid(i))
            {
                continue;
            }

            ReadOnlySpan<byte> value = bytes ? LiteralReader.ViewAt(node, i) : node.Values.Span.Slice(i * width, width);
            int low = 0;
            int high = order.Length - 1;
            while (low <= high)
            {
                int middle = (int)(((uint)low + (uint)high) >> 1);
                int compared = layout.Compare(keys[order[middle]], value);
                if (compared == 0)
                {
                    // Keys asked twice, as a zero and its other, are held together.
                    for (int equal = middle; equal >= 0 && layout.Compare(keys[order[equal]], value) == 0; equal--)
                    {
                        held += claims.Hold(order[equal]) ? 1 : 0;
                    }

                    for (int equal = middle + 1; equal < order.Length && layout.Compare(keys[order[equal]], value) == 0; equal++)
                    {
                        held += claims.Hold(order[equal]) ? 1 : 0;
                    }

                    break;
                }

                if (compared < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
        }
    }

    /// <summary>
    /// The values child of a chunk's segment decoded alone into <paramref name="node"/>, an extension's storage, when the
    /// chunk is a dictionary; false otherwise.
    /// </summary>
    private static bool DictionaryValues(ScanContext context, LayoutNode flat, VortexBuffer segment, KeyLayout layout, out CanonicalNode node, out int length)
    {
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
            node = default;
            length = 0;
            return false;
        }

        DictMetadata dict = DictMetadata.Read(root.Metadata);
        length = ArrayDecodeContext.CheckedLength(dict.ValuesLength, "vortex.dict values_len");
        node = context.Canonical.GetNode(context.Decode.DecodeChild(in root, 1, flat.DType, length));
        while (node.Kind == CanonicalKind.Extension)
        {
            node = context.Canonical.GetNode(node.StorageIndex);
        }

        bool shaped = layout.Shape == KeyShape.Bytes
            ? node.Kind == CanonicalKind.VarBinView
            : node.Kind == CanonicalKind.Primitive && node.PType == layout.PType;
        if (!shaped || node.Length != length)
        {
            throw Malformed("a dictionary's values do not decode to one key of the column's type each");
        }

        return true;
    }

    /// <summary>
    /// The chunk's values, sorted and deduplicated, nulls dropped; null when the chunk is not a
    /// dictionary.
    /// </summary>
    private static RunSegment? Values(ScanContext context, LayoutNode flat, int slot, KeyLayout layout)
    {
        if (!DictionaryValues(context, flat, context.Segments.GetBuffer(slot), layout, out CanonicalNode node, out int length))
        {
            return null;
        }

        bool bytes = layout.Shape == KeyShape.Bytes;

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
