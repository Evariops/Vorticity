using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

/// <summary>An old entry, restricted to the runs the append keeps, and the raw tail it merges back.</summary>
internal sealed record AbsorbedEntry(IndexEntry Entry, int Count, List<RawRun> Runs);

/// <summary>
/// Reads back the runs an append merges. Only a contiguous tail ending at the append's first block
/// qualifies: a merged run claims every block of its range, so a gap would make it deny blocks it
/// knows nothing about.
/// </summary>
internal static class RunAbsorb
{
    /// <summary>
    /// For every locating entry whose kept runs and the append's would pass
    /// <see cref="KeyIndexBuilder.MaxRuns"/>, its contiguous tail of runs, laid raw in the scratch.
    /// </summary>
    internal static async ValueTask<List<AbsorbedEntry>> ReadAsync(
        VortexFile file, IReadOnlyList<IndexEntry> entries, int boundary, RunScratch scratch,
        CancellationToken cancellationToken)
    {
        List<AbsorbedEntry> absorbed = [];
        foreach (IndexEntry entry in entries)
        {
            int stride = KeyRunOptions.StrideOf(entry.Kind);
            if (stride == 0)
            {
                continue;
            }

            List<IndexRun> kept = [];
            foreach (IndexRun run in entry.Runs)
            {
                if (run.EndBlock <= (ulong)boundary)
                {
                    kept.Add(run);
                }
            }

            // The append adds a merged run and, perhaps, the last chunk's.
            if (kept.Count + 2 <= KeyIndexBuilder.MaxRuns)
            {
                continue;
            }

            int count = 0;
            ulong end = (ulong)boundary;
            for (int i = kept.Count - 1; i >= 0 && kept[i].EndBlock == end; i--)
            {
                count++;
                end = kept[i].FirstBlock;
            }

            if (count == 0)
            {
                continue;
            }

            IndexEntry restricted = entry with { Runs = kept };
            List<RawRun> runs = [];
            bool readable = true;
            for (int i = kept.Count - count; i < kept.Count && readable; i++)
            {
                RawRun? raw = await ReadRunAsync(file, entry, kept[i], stride, scratch, cancellationToken).ConfigureAwait(false);
                if (raw is null)
                {
                    readable = false;
                }
                else
                {
                    runs.Add(raw);
                }
            }

            // A run that cannot be read back stays where it is, and so do the others.
            if (readable)
            {
                absorbed.Add(new AbsorbedEntry(restricted, count, runs));
            }
        }

        return absorbed;
    }

    /// <summary>One run's entries, laid raw with file rows and blocks; null when it does not read back.</summary>
    private static async ValueTask<RawRun?> ReadRunAsync(
        VortexFile file, IndexEntry entry, IndexRun run, int stride, RunScratch scratch,
        CancellationToken cancellationToken)
    {
        byte[] keyDType = KeyRunOptions.KeyDType(run);
        if (keyDType.Length == 0)
        {
            return null;
        }

        bool hasRows = stride == KeyRunOptions.SortedStride;
        DTypeArena types = new DTypeArena();
        DType keyType;
        try
        {
            keyType = DTypeFlatBuffers.Read(keyDType, types);
        }
        catch (VortexFormatException)
        {
            return null;
        }

        if (!KeyLayout.TryOf(keyType, out KeyLayout layout)
            || !FenceTable.TryOpen(run, stride, layout, out FenceTable? table, out _))
        {
            return null;
        }

        long firstRow = checked((long)(run.FirstBlock * entry.BlockLength));
        using RawRunWriter writer = new RawRunWriter(
            scratch, hasRows, layout.Width, checked((int)run.FirstBlock), checked((int)run.BlockCount));
        using ScanContext context = file.CreateIndexContext(run);
        ISegmentReader source = file.IndexSourceOf(run);
        uint[] absolute = ArrayPool<uint>.Shared.Rent(256);
        try
        {
            for (long s = 0; s < table!.SegmentCount; s++)
            {
                Fence fence = await table.GetAsync(source, s, cancellationToken).ConfigureAwait(false);
                if (fence.Bounds.Entries > int.MaxValue)
                {
                    return null;
                }

                int entries = (int)fence.Bounds.Entries;
                using SegmentRequestSet requests = new SegmentRequestSet(stride);
                int[] slots = new int[stride];
                for (int a = 0; a < stride; a++)
                {
                    IndexSegment payload = fence.Regions[a];
                    slots[a] = requests.Add(new SegmentSpec(payload.Offset, payload.Length, payload.AlignmentExponent, 0, 0));
                }

                await source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
                for (int a = 0; a < stride; a++)
                {
                    if (!fence.Regions[a].Holds(requests.GetBuffer(slots[a]).Span))
                    {
                        return null;
                    }
                }

                if (!DecodeSegment(context, requests, slots, table.WideRows, entries, keyType, layout, hasRows, types, out SegmentNodes nodes))
                {
                    return null;
                }

                for (int from = 0; from < entries;)
                {
                    from = Lay(context, nodes, entries, run, layout, hasRows, table.WideRows, firstRow, from, ref absolute, writer);
                    if (from < 0)
                    {
                        return null;
                    }

                    if (writer.Full)
                    {
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (VortexFormatException)
        {
            return null;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(absolute);
        }

        return await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A decoded segment's nodes in its context's arena: its keys, its rows or list offsets, and its lists.</summary>
    private readonly record struct SegmentNodes(int Keys, int Values, int Lists);

    /// <summary>Decodes a segment into the context's arena; false when its arrays do not have the run's shape.</summary>
    private static bool DecodeSegment(
        ScanContext context, SegmentRequestSet requests, int[] slots, bool wide, int entries,
        DType keyType, KeyLayout layout, bool hasRows, DTypeArena types, out SegmentNodes nodes)
    {
        nodes = default;
        context.ResetBatch();
        int keysNode = Decode(context, requests.GetBuffer(slots[0]), keyType, entries);
        CanonicalNode keys = context.Canonical.GetNode(keysNode);
        bool fits = keys.Length == entries && (layout.Shape == KeyShape.Bytes
            ? keys.Kind == CanonicalKind.VarBinView
            : keys.Kind == CanonicalKind.Primitive && keys.PType == layout.PType);
        if (!fits)
        {
            return false;
        }

        if (hasRows)
        {
            PType width = wide ? PType.U64 : PType.U32;
            // The keys' arena is kept: the rows are decoded beside them.
            int rowsNode = Decode(context, requests.GetBuffer(slots[1]), types.Primitive(width, Nullability.NonNullable), entries);
            CanonicalNode rows = context.Canonical.GetNode(rowsNode);
            if (rows.Kind != CanonicalKind.Primitive || rows.PType != width || rows.Length != entries)
            {
                return false;
            }

            nodes = new SegmentNodes(keysNode, rowsNode, -1);
            return true;
        }

        DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
        int offsetsNode = Decode(context, requests.GetBuffer(slots[1]), u32, entries + 1);
        CanonicalNode offsets = context.Canonical.GetNode(offsetsNode);
        if (offsets.Kind != CanonicalKind.Primitive || offsets.PType != PType.U32 || offsets.Length != entries + 1)
        {
            return false;
        }

        int listLength = checked((int)offsets.Values.Cast<uint>()[entries]);
        int listsNode = Decode(context, requests.GetBuffer(slots[2]), u32, listLength);
        CanonicalNode lists = context.Canonical.GetNode(listsNode);
        if (lists.Kind != CanonicalKind.Primitive || lists.PType != PType.U32 || lists.Length != listLength)
        {
            return false;
        }

        nodes = new SegmentNodes(keysNode, offsetsNode, listsNode);
        return true;
    }

    /// <summary>
    /// Lays a decoded segment's entries from <paramref name="from"/> on, until the writer's window is
    /// full or they end, and returns where it stopped; -1 when a block list lies outside the lists.
    /// </summary>
    private static int Lay(
        ScanContext context, SegmentNodes nodes, int entries, IndexRun run, KeyLayout layout, bool hasRows, bool wide,
        long firstRow, int from, ref uint[] absolute, RawRunWriter writer)
    {
        CanonicalNode keys = context.Canonical.GetNode(nodes.Keys);
        if (hasRows)
        {
            CanonicalNode rows = context.Canonical.GetNode(nodes.Values);
            for (int i = from; i < entries; i++)
            {
                long relative = wide ? checked((long)rows.Values.Cast<ulong>()[i]) : rows.Values.Cast<uint>()[i];
                if (writer.Add(KeyAt(keys, layout, i), firstRow + relative))
                {
                    return i + 1;
                }
            }

            return entries;
        }

        CanonicalNode offsets = context.Canonical.GetNode(nodes.Values);
        CanonicalNode lists = context.Canonical.GetNode(nodes.Lists);
        ReadOnlySpan<uint> starts = offsets.Values.Cast<uint>();
        ReadOnlySpan<uint> blocks = lists.Values.Cast<uint>();
        int listLength = checked((int)starts[entries]);
        uint firstBlock = checked((uint)run.FirstBlock);
        for (int i = from; i < entries; i++)
        {
            int start = checked((int)starts[i]);
            int end = checked((int)starts[i + 1]);
            if (start > end || end > listLength)
            {
                return -1;
            }

            if (absolute.Length < end - start)
            {
                ArrayPool<uint>.Shared.Return(absolute);
                absolute = ArrayPool<uint>.Shared.Rent(end - start);
            }

            for (int b = start; b < end; b++)
            {
                absolute[b - start] = checked(firstBlock + blocks[b]);
            }

            writer.BeginKey(KeyAt(keys, layout, i));
            writer.AddBlocks(absolute.AsSpan(0, end - start));
            if (writer.EndKey())
            {
                return i + 1;
            }
        }

        return entries;
    }

    private static ReadOnlySpan<byte> KeyAt(CanonicalNode keys, KeyLayout layout, int index) =>
        layout.Shape == KeyShape.Bytes
            ? LiteralReader.ViewAt(keys, index)
            : keys.Values.Span.Slice(index * layout.Width, layout.Width);

    private static int Decode(ScanContext context, VortexBuffer blob, DType dtype, int length)
    {
        context.Decode.LoadBlob(blob);
        ArrayNode root = context.Nodes.Root;
        return context.Decode.DecodeRoot(in root, dtype, length);
    }
}
