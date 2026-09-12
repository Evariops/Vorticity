// docs/02-format.md §5.1 and spec/REFERENCE.md, "The array blob, read off the writer rather than
// the prose"; the reference is vortex-array-0.86.1/src/serde.rs
// (`TryFrom<ByteBuffer> for ArrayRef` and `from_flatbuffer_and_segment_with_overrides`).
//
// The four details the prose gets wrong, all handled below:
//   * the writer emits a leading ZERO-LENGTH max-alignment buffer that contributes no bytes and no
//     `Buffer` entry - do not look for it on read;
//   * `padding` is computed by the writer from the blob's offset WITHIN THE FILE, but the padding
//     it actually wrote is recorded per buffer, so the reader just accumulates from zero;
//   * the padding before the FlatBuffer is recorded NOWHERE, which is why the FlatBuffer is located
//     from the end and never by walking forward past the last data buffer;
//   * the FlatBuffer is built with `finish_minimal`: there is NO file identifier to look for.
using System;
using System.Buffers.Binary;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Arrays;

/// <summary>
/// Turns a segment into a populated <see cref="ArrayNodeArena"/>, zero-copy for the data buffers.
/// </summary>
public static class ArrayBlobReader
{
    /// <summary>
    /// The normal case: <paramref name="segment"/> is
    /// <c>[padding][buffer 0]...[Array flatbuffer][u32 fb length]</c>.
    /// </summary>
    /// <param name="arena">The arena to populate. It is <see cref="ArrayNodeArena.Reset"/> first.</param>
    /// <param name="segment">The whole array-blob segment.</param>
    /// <param name="encodings">
    /// Maps the <c>u16</c> spec index to a resolved id, i.e. the file's <c>array_specs</c> mapped
    /// through <see cref="EncodingRegistry.ResolveArray"/> at open (contract §2.3).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="VortexFormatException">The blob is malformed in any way.</exception>
    /// <exception cref="VortexUnsupportedException">A buffer declares a compression we do not implement.</exception>
    public static void Load(
        ArrayNodeArena arena,
        VortexBuffer segment,
        ReadOnlySpan<ArrayEncodingId> encodings)
    {
        ArgumentNullException.ThrowIfNull(arena);

        // 1. The u32 trailer must be there at all.
        if (segment.Length < 4)
        {
            ArraysThrow.Format(
                $"Array blob is {segment.Length} bytes; at least 4 are needed for the FlatBuffer " +
                "length trailer.");
        }

        ReadOnlySpan<byte> bytes = segment.Span;
        uint fbLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]);

        // 2. `4 + fbLength` in 64 bits: a u32 close to 4 GiB would wrap a 32-bit sum back into range.
        if ((long)fbLength + 4 > segment.Length)
        {
            ArraysThrow.Format(
                $"Array blob declares a {fbLength}-byte FlatBuffer but only " +
                $"{segment.Length - 4} bytes precede its length trailer.");
        }

        // 3. Located from the END. The padding in front of it is recorded nowhere.
        int regionLength = segment.Length - 4 - (int)fbLength;
        ReadOnlySpan<byte> flatBuffer = bytes.Slice(regionLength, (int)fbLength);
        LoadCore(arena, flatBuffer, segment.Slice(0, regionLength), encodings);
    }

    /// <summary>
    /// The <c>vortex.flat</c> inlined variant: the <c>Array</c> FlatBuffer came from the layout's
    /// <c>array_encoding_tree</c> metadata and <paramref name="segment"/> is read from offset 0 as
    /// the buffer region.
    /// </summary>
    /// <param name="arena">The arena to populate. It is <see cref="ArrayNodeArena.Reset"/> first.</param>
    /// <param name="arrayTree">The <c>Array</c> FlatBuffer bytes, copied into the arena.</param>
    /// <param name="segment">The segment holding the data buffers.</param>
    /// <param name="encodings">Spec index to resolved id, as for the other overload.</param>
    /// <remarks>
    /// The segment bytes are byte-identical to the normal case - the writer still appends the
    /// FlatBuffer and the <c>u32</c> - so this is the same walk with a different FlatBuffer source
    /// and not a second algorithm. Verified against
    /// <c>corpus/containers/flat_inline_array_node.vortex</c>, whose five inlined trees are byte
    /// equal to the tails of their own segments.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="VortexFormatException">The blob is malformed in any way.</exception>
    /// <exception cref="VortexUnsupportedException">A buffer declares a compression we do not implement.</exception>
    public static void Load(
        ArrayNodeArena arena,
        ReadOnlySpan<byte> arrayTree,
        VortexBuffer segment,
        ReadOnlySpan<ArrayEncodingId> encodings)
    {
        ArgumentNullException.ThrowIfNull(arena);
        LoadCore(arena, arrayTree, segment, encodings);
    }

    private static void LoadCore(
        ArrayNodeArena arena,
        ReadOnlySpan<byte> flatBuffer,
        VortexBuffer region,
        ReadOnlySpan<ArrayEncodingId> encodings)
    {
        arena.Reset();

        if (flatBuffer.Length < 4)
        {
            ArraysThrow.Format(
                $"An Array FlatBuffer of {flatBuffer.Length} bytes cannot hold a root offset.");
        }

        // The arena keeps its own copy so that node metadata and statistics - which are (offset,
        // length) pairs into these bytes - never depend on the caller's span staying alive or
        // staying put. It is one memcpy of at most a few hundred bytes (1184 is the largest in the
        // golden corpus) and it is what makes the inlined variant safe without pinning.
        Span<byte> tree = arena.BeginTree(flatBuffer.Length);
        flatBuffer.CopyTo(tree);
        ReadOnlySpan<byte> treeSpan = arena.TreeSpan;

        // Forward-only uoffsets exclude cycles but NOT sharing: two parents may resolve to one
        // child table, so the node graph is a DAG and depth alone cannot bound the walk
        // (docs/03-architecture.md §6). This budget is what stops it.
        int tableBudget = VortexLimits.MaxFlatBufferTables;
        ArrayView view = ArrayView.Root(treeSpan, ref tableBudget);

        ResolveBuffers(arena, view, region);

        // Two more budgets, on the work the ARENA is made to do. The FlatBuffers table budget
        // bounds table VISITS at a million; a million 32-byte records plus their buffer index lists
        // is tens of megabytes conjured out of a 1.5 KB file, so depth and table count together are
        // not enough. Both numbers below are derived from the FlatBuffer's own byte accounting
        // rather than invented, so neither can reject a well-formed file:
        //
        //   * a node costs at least 8 bytes - four for its own soffset and four for the slot that
        //     points at it (its parent's children vector, or the Array table's `root`) - so a tree
        //     cannot hold more than length/8 + 1 of them;
        //   * a buffer index costs 2 bytes in its node's `buffers` vector, so a tree cannot hold
        //     more than length/2 + 1 of them.
        //
        // Measured over all 2236 array blobs in the golden corpus the worst case uses 13.6% of the
        // node budget and 6.2% of the index budget. Exceeding either means the graph is a
        // shared-child DAG being re-expanded, not a tree.
        int nodeBudget = (flatBuffer.Length / 8) + 1;
        int indexBudget = (flatBuffer.Length / 2) + 1;

        int root = arena.ReserveNodes(1);
        Fill(arena, view.Root_, root, 1, encodings, treeSpan, ref nodeBudget, ref indexBudget);
        arena.SetRoot(root);
    }

    private static void ResolveBuffers(ArrayNodeArena arena, ArrayView view, VortexBuffer region)
    {
        ReadOnlySpan<BufferSpec> specs = view.Buffers;
        ReadOnlySpan<byte> regionBytes = region.Span;

        // `offset` starts at 0 relative to the SEGMENT, not at the segment's file offset. The
        // writer computed padding from the file offset but recorded what it actually wrote, so the
        // reader accumulates - reaching for the file offset here is off-by-padding on every
        // multi-buffer array (spec/REFERENCE.md).
        long offset = 0;
        for (int i = 0; i < specs.Length; i++)
        {
            ref readonly BufferSpec spec = ref specs[i];

            if (spec.Compression != (byte)BufferCompression.None)
            {
                ThrowBufferCompression(i, spec.Compression);
            }

            // Class I: an unchecked u8 exponent can demand 2^255 (docs/08-semantics.md §6).
            int alignmentExponent = spec.AlignmentExponent;
            _ = VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);

            long start = offset + spec.Padding;
            long end = start + spec.Length;
            if (end > regionBytes.Length)
            {
                ArraysThrow.Format(
                    $"Array buffer {i} spans [{start}, {end}) of a {regionBytes.Length}-byte " +
                    "buffer region.");
            }

            // FromPinned rather than region.Slice: Slice keeps the SEGMENT's declared alignment
            // exponent, and every consumer that reinterprets these bytes needs the BUFFER's.
            // The bytes are owner-backed (mapped pages or an aligned native block), so they are
            // pinned in the sense FromPinned requires.
            arena.AddGlobalBuffer(
                VortexBuffer.FromPinned(regionBytes.Slice((int)start, (int)spec.Length), alignmentExponent));

            offset = end;
        }
    }

    private static void Fill(
        ArrayNodeArena arena,
        ArrayNodeView node,
        int slot,
        int depth,
        ReadOnlySpan<ArrayEncodingId> encodings,
        ReadOnlySpan<byte> treeSpan,
        ref int nodeBudget,
        ref int indexBudget)
    {
        // Semantic array depth, a separate budget from the FlatBuffers table count (contract §1.5).
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Array");

        ushort specIndex = node.Encoding;
        if (specIndex >= encodings.Length)
        {
            // Not an unknown component - a structurally inconsistent file. There is no id text to
            // name, so lazy resolution has nothing to defer (contract §2.3).
            ArraysThrow.Format(
                $"Array node names encoding spec {specIndex}; the footer declares " +
                $"{encodings.Length}.");
        }

        ReadOnlySpan<byte> metadata = node.Metadata;
        int metadataOffset = -1;
        if (!metadata.IsEmpty)
        {
            metadataOffset = InTree(treeSpan, metadata, "ArrayNode.metadata");
        }

        ReadOnlySpan<ushort> bufferIndices = node.BufferIndices;
        int childCount = node.ChildCount;

        Charge(ref nodeBudget, 1 + childCount, "nodes");
        Charge(ref indexBudget, bufferIndices.Length, "buffer references");

        int globalBuffers = arena.GlobalBufferCount;
        int firstBuffer = arena.NodeBufferIndexCount;
        for (int i = 0; i < bufferIndices.Length; i++)
        {
            int global = bufferIndices[i];

            // Class I. Validated at LOAD, not at use: a node with BufferCount == 0 legitimately has
            // an absent vector, and an index past the global list must never reach GetBuffer.
            if (global >= globalBuffers)
            {
                ArraysThrow.Format(
                    $"Array node buffer index {global} is outside the blob's {globalBuffers} " +
                    "declared buffers.");
            }

            arena.AddNodeBufferIndex(global);
        }

        int statsIndex = -1;
        if (node.HasStats)
        {
            statsIndex = arena.AddStats(ReadStats(node.Stats, treeSpan));
        }

        int firstChild = arena.ReserveNodes(childCount);

        arena.SetRecord(
            slot,
            new ArrayNodeRecord(
                specIndex,
                encodings[specIndex],
                metadataOffset,
                metadata.Length,
                firstChild,
                childCount,
                firstBuffer,
                bufferIndices.Length,
                statsIndex));

        for (int i = 0; i < childCount; i++)
        {
            Fill(
                arena,
                node.GetChild(i),
                firstChild + i,
                depth + 1,
                encodings,
                treeSpan,
                ref nodeBudget,
                ref indexBudget);
        }
    }

    private static ArrayStatsRecord ReadStats(ArrayStatsView view, ReadOnlySpan<byte> treeSpan)
    {
        ArrayStatsRecord record = default;

        ReadOnlySpan<byte> min = view.MinBytes;
        if (!min.IsEmpty)
        {
            record.MinOffset = InTree(treeSpan, min, "ArrayStats.min");
            record.MinLength = min.Length;
        }

        record.MinPrecision = view.MinPrecision;

        ReadOnlySpan<byte> max = view.MaxBytes;
        if (!max.IsEmpty)
        {
            record.MaxOffset = InTree(treeSpan, max, "ArrayStats.max");
            record.MaxLength = max.Length;
        }

        record.MaxPrecision = view.MaxPrecision;

        ReadOnlySpan<byte> sum = view.SumBytes;
        if (!sum.IsEmpty)
        {
            record.SumOffset = InTree(treeSpan, sum, "ArrayStats.sum");
            record.SumLength = sum.Length;
        }

        // Six `= null` fields: absent means UNKNOWN, so presence and value are separate bits.
        if (view.TryGetIsSorted(out bool isSorted))
        {
            record.Flags |= ArrayStatsRecord.FlagIsSorted;
            if (isSorted)
            {
                record.Flags |= ArrayStatsRecord.ValueIsSorted;
            }
        }

        if (view.TryGetIsStrictSorted(out bool isStrictSorted))
        {
            record.Flags |= ArrayStatsRecord.FlagIsStrictSorted;
            if (isStrictSorted)
            {
                record.Flags |= ArrayStatsRecord.ValueIsStrictSorted;
            }
        }

        if (view.TryGetIsConstant(out bool isConstant))
        {
            record.Flags |= ArrayStatsRecord.FlagIsConstant;
            if (isConstant)
            {
                record.Flags |= ArrayStatsRecord.ValueIsConstant;
            }
        }

        if (view.TryGetNullCount(out ulong nullCount))
        {
            record.Flags |= ArrayStatsRecord.FlagNullCount;
            record.NullCount = nullCount;
        }

        if (view.TryGetUncompressedSizeInBytes(out ulong uncompressed))
        {
            record.Flags |= ArrayStatsRecord.FlagUncompressedSize;
            record.UncompressedSizeInBytes = uncompressed;
        }

        if (view.TryGetNanCount(out ulong nanCount))
        {
            record.Flags |= ArrayStatsRecord.FlagNanCount;
            record.NanCount = nanCount;
        }

        return record;
    }

    /// <summary>Byte offset of <paramref name="slice"/> inside the arena's FlatBuffer copy.</summary>
    private static int InTree(ReadOnlySpan<byte> treeSpan, ReadOnlySpan<byte> slice, string what)
    {
        long offset = SchemaThrow.InBufferOffset(treeSpan, slice);
        if (offset < 0 || offset + slice.Length > treeSpan.Length)
        {
            ArraysThrow.Format($"{what} escapes the Array FlatBuffer.");
        }

        return (int)offset;
    }

    private static void Charge(ref int budget, int units, string what)
    {
        budget -= units;
        if (budget < 0)
        {
            ThrowWorkBudget(what);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowWorkBudget(string what) =>
        throw new VortexFormatException(
            $"The array node tree expands to more {what} than its FlatBuffer has room to declare; " +
            "it is a shared-child DAG, not a tree.");

    /// <summary>
    /// Refuses a compressed buffer. NOT a deferral, and not effort: buffer-level compression is
    /// declared by the schema and implemented by nothing.
    /// </summary>
    /// <remarks>
    /// Vortex 0.86.1 writes <c>Compression::None</c>, never reads this field at all, and depends on
    /// no lz4 implementation; the schema names an algorithm without saying whether the bytes are a
    /// raw LZ4 block or a frame, and records no decompressed length anywhere. A decoder could only
    /// be written by inventing both, so we refuse instead - which is also the safer of the two
    /// behaviours, since the reference would read these bytes AS DATA and return silent garbage.
    /// See docs/08-semantics.md §7.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowBufferCompression(int index, byte compression) =>
        throw new VortexUnsupportedException(
            compression == (byte)BufferCompression.LZ4
                ? "lz4"
                : compression.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VortexComponentKind.Compression,
            $"Array buffer {index} declares compression, which no Vortex release implements: " +
            "the format defines neither its framing nor a decompressed length, so the bytes " +
            "cannot be decoded without inventing both.");
}
