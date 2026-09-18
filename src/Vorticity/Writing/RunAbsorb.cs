// The runs an append merges back - docs/13-dataset.md §6.1, docs/11-write-strategy.md §3.8 as amended.
//
// AT MOST K RUNS PER ENTRY. An append keeps the runs that end by its first block and adds its own:
// one merged run, and the last chunk's when its rows are not whole blocks. When the kept runs and
// those two would pass K (4), the kept runs are read back -- index bytes, never a data segment --
// laid raw in a scratch, and merged into the append's run, so a lookup still probes at most K runs.
// The old runs' bytes become dead weight in the file, as an old postscript does.
//
// ONLY A CONTIGUOUS TAIL IS MERGED. A merged run claims every block of its range, so the runs read
// back must end at the append's first block with no gap between them: a gap is blocks no run
// covers -- a partial index -- and a merged run over it would say "absent" where it knows nothing.
// A run before a gap stays as it is.
//
// WHAT IT COSTS, HONESTLY. Merging everything past K rewrites the entry's index every K - 1
// appends: over n appends of one size, the index bytes rewritten grow as n² / K. The in-place
// append is the single-file mode; a workload of many appends is the dataset's (13 §5), whose
// compaction is tiered across objects.
using System;
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

/// <summary>An old entry whose tail of runs an append merges back.</summary>
/// <param name="Entry">The old entry, restricted to the runs the append keeps.</param>
/// <param name="Count">How many of those runs, at the end, were read back.</param>
/// <param name="Runs">Those runs, raw, in block order.</param>
internal sealed record AbsorbedEntry(IndexEntry Entry, int Count, List<RawRun> Runs);

/// <summary>Reads back the runs an append merges.</summary>
internal static class RunAbsorb
{
    /// <summary>
    /// For every locating entry whose kept runs and the append's would pass
    /// <see cref="KeyIndexBuilder.MaxRuns"/>, its contiguous tail of runs, laid raw in
    /// <paramref name="scratch"/>.
    /// </summary>
    /// <param name="file">The file the append continues.</param>
    /// <param name="entries">Its directory's entries.</param>
    /// <param name="boundary">The append's first block.</param>
    /// <param name="scratch">Where the runs are laid.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
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
        ISegmentSource source = file.IndexSourceOf(run);
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

                if (!Lay(context, requests, slots, run, table.WideRows, entries, keyType, layout, hasRows, firstRow, types, writer))
                {
                    return null;
                }
            }
        }
        catch (VortexFormatException)
        {
            return null;
        }

        return writer.Finish();
    }

    private static bool Lay(
        ScanContext context, SegmentRequestSet requests, int[] slots, IndexRun run, bool wide, int entries,
        DType keyType, KeyLayout layout, bool hasRows, long firstRow, DTypeArena types, RawRunWriter writer)
    {
        context.ResetBatch();
        CanonicalNode keys = context.Canonical.GetNode(Decode(context, requests.GetBuffer(slots[0]), keyType, entries));
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
            CanonicalNode rows = context.Canonical.GetNode(
                Decode(context, requests.GetBuffer(slots[1]), types.Primitive(width, Nullability.NonNullable), entries));
            if (rows.Kind != CanonicalKind.Primitive || rows.PType != width || rows.Length != entries)
            {
                return false;
            }

            for (int i = 0; i < entries; i++)
            {
                long relative = wide ? checked((long)rows.Values.Cast<ulong>()[i]) : rows.Values.Cast<uint>()[i];
                writer.Add(KeyAt(keys, layout, i), firstRow + relative);
            }

            return true;
        }

        DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode offsets = context.Canonical.GetNode(Decode(context, requests.GetBuffer(slots[1]), u32, entries + 1));
        if (offsets.Kind != CanonicalKind.Primitive || offsets.PType != PType.U32 || offsets.Length != entries + 1)
        {
            return false;
        }

        ReadOnlySpan<uint> starts = offsets.Values.Cast<uint>();
        int listLength = checked((int)starts[entries]);
        CanonicalNode lists = context.Canonical.GetNode(Decode(context, requests.GetBuffer(slots[2]), u32, listLength));
        if (lists.Kind != CanonicalKind.Primitive || lists.PType != PType.U32 || lists.Length != listLength)
        {
            return false;
        }

        ReadOnlySpan<uint> blocks = lists.Values.Cast<uint>();
        uint firstBlock = checked((uint)run.FirstBlock);
        uint[] absolute = new uint[256];
        for (int i = 0; i < entries; i++)
        {
            int from = checked((int)starts[i]);
            int to = checked((int)starts[i + 1]);
            if (from > to || to > listLength)
            {
                return false;
            }

            if (absolute.Length < to - from)
            {
                absolute = new uint[to - from];
            }

            for (int b = from; b < to; b++)
            {
                absolute[b - from] = checked(firstBlock + blocks[b]);
            }

            writer.BeginKey(KeyAt(keys, layout, i));
            writer.AddBlocks(absolute.AsSpan(0, to - from));
            writer.EndKey();
        }

        return true;
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
