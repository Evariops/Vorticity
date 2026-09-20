// Serializing a canonical array back to the blob format - docs/02-format.md §5.1.
//
//     [padding] [buffer 0] [padding] [buffer 1] ... [Array flatbuffer] [u32 flatbuffer length]
//
// The reader's own header comment lists four details the prose gets wrong, and three of them are
// this file's obligations rather than the reader's:
//
//   * `padding` is what was ACTUALLY written before each buffer, and the reader accumulates it from
//     zero within the segment. So the padding computed here has to place each buffer at its
//     alignment relative to the SEGMENT START, and the segment itself is then written at a
//     64-byte file offset -- which is what turns "aligned within the blob" into "aligned in the
//     mapped file", the thing that makes the read zero-copy.
//   * the padding in front of the FlatBuffer is recorded nowhere, because the FlatBuffer is located
//     from the end. It can therefore be whatever the 8-byte alignment needs.
//   * the FlatBuffer is finished MINIMALLY: no file identifier.
//
// The fourth -- the reference's leading zero-length max-alignment buffer -- is deliberately not
// reproduced. It contributes no bytes and no Buffer entry, so a reader that accumulates from zero
// cannot tell whether it was there, and emitting it would be cargo cult.
//
// VALIDITY IS WHERE A WRITER QUIETLY LOSES DATA. A nullable array with no validity child reads back
// as AllValid, so AllValid may be omitted and ALL-INVALID MAY NOT: an all-null column written
// without its bitmap comes back as a column of zeros that claims every row is present. The
// asymmetry is the reason Validity is switched on rather than tested for "has a bitmap".
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Compute;
using Vorticity.Buffers;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

/// <summary>Turns a canonical array into the bytes of one <c>vortex.flat</c> segment.</summary>
internal static class ArrayBlobWriter
{
    private const int ViewSize = 16;

    /// <summary>
    /// Serializes the canonical node at <paramref name="nodeIndex"/>.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The node to write.</param>
    /// <param name="encodings">The file's array-encoding dictionary, extended as needed.</param>
    /// <param name="compress">Whether to let ColumnCompressor pick an encoding for the column.</param>
    /// <param name="stats">
    /// What the ingest pass measured over this chunk's rows, when the caller has it; a default
    /// summary makes every candidate measure the column itself, as it did before stage 2 of
    /// docs/11-write-strategy.md §8.
    /// </param>
    /// <returns>The blob.</returns>
    /// <exception cref="NotSupportedException">The canonical form has no writer.</exception>
    internal static BlobLease Write(
        CanonicalArena arena, int nodeIndex, EncodingDictionary encodings, bool compress = false,
        ChunkStats stats = default)
    {
        List<PendingBuffer> buffers = [];
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        try
        {
        // The compressed forms are chosen and MATERIALIZED before the builder starts, because both
        // of them add canonical nodes to the arena -- the gathered values child -- and a
        // FlatBuffers table cannot be open while that happens.
        // A constant is expanded where it is read. The uncompressed path reads rows and nothing
        // else, so it expands here; the compressed one asks first what the element alone decides.
        int root = compress
            ? WriteCompressed(builder, arena, nodeIndex, buffers, encodings, stats)
            : WriteNode(
                builder, arena, Materialize(arena, nodeIndex), buffers, encodings,
                compress: false, stats);

        // The Buffer vector records what the layout below will actually write, so the paddings have
        // to be settled before the table that carries them is built.
        BufferSpec[] specs = new BufferSpec[buffers.Count];
        long offset = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            PendingBuffer pending = buffers[i];
            int alignment = 1 << pending.AlignmentExponent;
            long aligned = Align(offset, alignment);
            int padding = (int)(aligned - offset);

            specs[i] = new BufferSpec(
                (ushort)padding, (byte)pending.AlignmentExponent, (byte)BufferCompression.None,
                (uint)pending.Length);

            offset = aligned + pending.Length;
        }

        int table = ArrayWriter.Write(builder, root, specs);
        ReadOnlySpan<byte> flatBuffer = builder.Finish(table);

        // The FlatBuffer is 8-byte aligned within the blob; the padding before it is recorded
        // nowhere because the reader finds it from the end.
        long flatStart = Align(offset, 8);
        long total = flatStart + flatBuffer.Length + sizeof(uint);

        // Rented, not allocated (W-4). The rental is longer than `total` and its tail holds
        // whatever the last renter wrote, so the padding between buffers has to be cleared rather
        // than assumed zero the way a fresh array allowed -- a file is byte-exact or it is wrong.
        //
        // ASSEMBLING THE BLOB COSTS NOTHING WORTH AVOIDING, and the alternative is a real design:
        // handing the sink each buffer and each run of padding in turn, so the bytes go out from
        // where they already are. Doubling the copy below reads 0,999 on a million-row `table_mixed`
        // write and 1,006 on a `varbin` one -- a per cent at the outside, against many small writes
        // to a FileStream and a native view that is only memory through a manager.
        int exact = checked((int)total);
        byte[] blob = ArrayPool<byte>.Shared.Rent(exact);
        long cursor = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            int padding = specs[i].Padding;
            if (padding > 0)
            {
                blob.AsSpan((int)cursor, padding).Clear();
                cursor += padding;
            }

            buffers[i].Span.CopyTo(blob.AsSpan((int)cursor));
            cursor += buffers[i].Length;
        }

        // The gap before the FlatBuffer is the other run of bytes nothing writes.
        if (flatStart > cursor)
        {
            blob.AsSpan((int)cursor, (int)(flatStart - cursor)).Clear();
        }

        flatBuffer.CopyTo(blob.AsSpan((int)flatStart));
        BinaryPrimitives.WriteUInt32LittleEndian(
            blob.AsSpan((int)(total - sizeof(uint))), (uint)flatBuffer.Length);

        return new BlobLease(blob, exact);
        }
        finally
        {
            // AFTER THE COPY INTO `blob` AND NOWHERE EARLIER. A pooled array handed back while the
            // blob still had to read it would be handed to the next renter and overwritten, and the
            // file would be wrong in a way no exception reports. In a `finally` so that a throw
            // between the rent and the copy does not quietly drain the pool either.
            foreach (PendingBuffer pending in buffers)
            {
                if (pending.Rented && pending.Bytes is byte[] rented)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }

    /// <summary>
    /// Writes the column under whichever scheme ColumnCompressor chose.
    /// </summary>
    /// <remarks>
    /// Compression is applied to the TOP of a column and nowhere else. A cascade -- dictionary
    /// codes that are themselves bit-packed, which is where the reference's ratios come from --
    /// needs the integer kernels this build does not have yet, and applying a scheme inside a
    /// struct or a list would change shapes the reader derives top-down. One level is what can be
    /// done correctly today (docs/90-registry.md).
    /// <para>
    /// <c>stats</c> is the chunk's ingest statistics when this node is one of the file's own
    /// columns, and a default summary for a child a scheme invented — which the chooser then
    /// measures itself (docs/11-write-strategy.md §3.4.4 turns those into cascade context in a later
    /// stage).
    /// </para>
    /// </remarks>
    private static int WriteCompressed(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings,
        ChunkStats stats = default,
        Cascade cascade = default)
    {
        // Z1b-c2b: a constant node is materialized HERE, before the compressor looks at it, and the
        // first attempt did it lower down -- inside WriteNode -- which produced a file of 33 676
        // bytes against 996. The compressor never saw the column, so the encoding that made a
        // constant 4 096-row i64 cost under a kilobyte was simply not chosen. Materializing above
        // `Choose` puts the writer back on exactly the path it took before the switch existed, which
        // is what makes the bytes identical rather than merely close.
        //
        // What a constant can be decided to be WITHOUT being expanded is asked first, because the
        // expansion is most of what writing a constant column costs. A progression is the answer
        // for an integer constant and needs neither the rows nor a walk; every other constant falls
        // through to the line below and takes the route it always took.
        BlockStats summary = stats.Stats;
        ColumnPlan plan = ColumnCompressor.ChooseConstant(
            arena, nodeIndex, encodings.Target, in summary, cascade);
        if (plan.Scheme == ColumnScheme.None)
        {
            nodeIndex = Materialize(arena, nodeIndex);
            plan = ColumnCompressor.Choose(
                arena, nodeIndex, encodings.Target, in summary, cascade, stats);
        }

        // THE BYTES THE PLAN ACTUALLY PRODUCED, handed back to the column for docs/11 §3.4.3's plan
        // memory: every buffer this node and its subtree appended, measured against what the chooser
        // priced the plan at. Buffer bytes rather than the blob's, because the blob is one per root
        // field and a struct's children are priced one by one; the framing they leave out is the
        // same framing chunk after chunk, which is all a tolerance needs.
        //
        // A DICTIONARY IS HELD TO ITS OWN LAYER, not to its subtree: the plan priced codes plus
        // entries, and the children then take schemes of their own -- on `dict_u8_codes` the codes
        // zstd to 220 bytes and the values to 150, against a layer of 66 738. Measured by the
        // subtree, the prediction "broke" on every chunk of every dictionary column, the distinct
        // table was never expected to serve, and each chunk walked for the dictionary the table
        // had already built. `WriteChosen` reports the layer as built; the children's bytes are
        // the children's.
        int firstBuffer = buffers.Count;
        int written = WriteChosen(
            builder, arena, nodeIndex, in plan, buffers, encodings, stats, out long dictionaryLayer);
        long produced = dictionaryLayer;
        if (produced < 0)
        {
            produced = 0;
            for (int i = firstBuffer; i < buffers.Count; i++)
            {
                produced += buffers[i].Length;
            }
        }

        stats.Remember(in plan, produced);
        return written;
    }

    /// <summary>Writes the node the way <paramref name="plan"/> says to.</summary>
    // `dictionaryLayer`: for a dictionary, the bytes of the layer the plan priced -- codes at width
    // plus entries as built -- which is what plan memory holds it to; -1 for every other plan,
    // whose buffers are their own measure.
    private static int WriteChosen(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        in ColumnPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings,
        ChunkStats stats,
        out long dictionaryLayer)
    {
        dictionaryLayer = -1;
        if (plan.Scheme == ColumnScheme.None)
        {
            // Not the end of it: the column itself resisted every scheme, but a struct field or a
            // list's elements underneath it may not.
            return WriteNode(builder, arena, nodeIndex, buffers, encodings, compress: true, stats);
        }

        if (plan.Scheme == ColumnScheme.BitPacked)
        {
            return WriteBitPacked(builder, arena, nodeIndex, plan.BitPack!, buffers, encodings);
        }

        if (plan.Scheme == ColumnScheme.Fsst)
        {
            return WriteFsst(builder, arena, nodeIndex, plan.Fsst!, buffers, encodings);
        }

        if (plan.Scheme == ColumnScheme.Zstd)
        {
            return WriteZstd(builder, arena, nodeIndex, plan.Zstd!, buffers, encodings);
        }

        if (plan.Scheme == ColumnScheme.Alp)
        {
            return WriteAlp(builder, arena, nodeIndex, plan.Alp!, buffers, encodings);
        }

        if (plan.Scheme == ColumnScheme.Sequence)
        {
            return WriteSequence(builder, arena, nodeIndex, plan.Sequence!, encodings);
        }

        // THE VALUES CHILD COMES FROM THE TABLE WHEN THE TABLE CHOSE THE PLAN (docs/11 §3.5): the
        // entries laid out in code order from the keys it owns, no gather over the chunk, and for
        // strings the key heap as the data buffer. A run-end, or a dictionary the reference chooser
        // walked for, is still the original column gathered down to its representative rows -- a
        // dictionary of strings then shares the data buffers it came from. Either way the child is
        // itself a column, and a dictionary of long strings is exactly the shape FSST wants
        // underneath: compressed rather than written flat.
        int values = plan.Table is not null
            ? plan.Table.BuildValues(arena, arena.GetNode(nodeIndex), plan.Entries)
            : CanonicalFilter.Apply(arena, nodeIndex, plan.Gather);

        if (plan.Scheme == ColumnScheme.RunEnd)
        {
            return WriteRunEnd(builder, arena, nodeIndex, values, plan, buffers, encodings);
        }

        // Measured on the values child as built and the rows the codes index, with the formula the
        // plan was priced by: the number plan memory compares the prediction to.
        dictionaryLayer = ColumnCompressor.DictionaryLayerBytes(
            arena, arena.GetNode(values), arena.GetNode(nodeIndex).Length);
        return WriteDict(builder, arena, nodeIndex, values, plan, buffers, encodings);
    }

    /// <summary>
    /// Writes <c>fastlanes.bitpacked</c> under whichever transform the plan chose:
    /// <c>fastlanes.for</c>, which adds a reference back, or <c>vortex.zigzag</c>, which
    /// un-interleaves the sign bit.
    /// </summary>
    /// <remarks>
    /// The nesting is not arbitrary. Neither wrapper has a validity of its own -- both take their
    /// child's -- so the validity child belongs to the bitpacked node underneath, which is where
    /// this puts it, AFTER the patch children, because the decoder derives the validity child's
    /// position from the metadata (2 with patches, 0 without) and never from the child count.
    ///
    /// The frame subtraction is on RAW BITS, unsigned, and that is what makes a signed column work:
    /// a column spanning -1000 to 1000 has a span of 2000 and needs 11 bits, which a signed
    /// per-value subtraction would have overflowed on. The reader's own kernel is `wrapping_add`
    /// for exactly this reason.
    ///
    /// The PATCH VALUES are in the encoded domain -- after the transform, before the packing --
    /// because that is the buffer the decoder overwrites: it applies patches to the unpacked
    /// values and only then hands them to the wrapper.
    /// </remarks>
    private static int WriteBitPacked(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        BitPackPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        PType ptype = node.PType;
        int width = ptype.ByteWidth();
        int length = node.Length;

        byte[] packed = Pack(
            arena, node, plan, ptype, length, out int[] patchIndices, out ulong[] patchValues);
        buffers.Add(new PendingBuffer(packed, Exponent(width)));
        Span<ushort> packedBuffer = stackalloc ushort[1];
        packedBuffer[0] = (ushort)(buffers.Count - 1);

        Span<int> children = stackalloc int[3];
        int childCount = 0;
        PatchesMetadata patches = default;
        bool patched = patchIndices.Length > 0;
        if (patched)
        {
            PType indicesPType = FsstPlan.IndexPType(length);
            patches = PatchesMetadata.Create((ulong)patchIndices.Length, 0, indicesPType);
            children[0] = WriteIndexArray(builder, buffers, encodings, patchIndices, indicesPType);
            children[1] = WriteRawPrimitive(
                builder, buffers, encodings, LittleEndian(patchValues, width), ToUnsigned(ptype));
            childCount = 2;
        }

        childCount += Validity(builder, arena, node, buffers, encodings, children[childCount..]);

        int bitpacked = Node(
            builder, encodings, "fastlanes.bitpacked"u8,
            BitPackedBytes((uint)plan.BitWidth, patched, in patches),
            children[..childCount], packedBuffer);

        Span<int> wrapper = stackalloc int[1];
        wrapper[0] = bitpacked;
        return plan.Transform == BitPackTransform.ZigZag
            ? Node(builder, encodings, "vortex.zigzag"u8, default, wrapper, [])
            : Node(
                builder, encodings, "fastlanes.for"u8, ReferenceBytes(plan.Reference, ptype),
                wrapper, []);
    }

    /// <summary>The patch values, truncated to the element width and written little-endian.</summary>
    private static byte[] LittleEndian(ulong[] values, int width)
    {
        byte[] bytes = new byte[values.Length * width];
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> destination = bytes.AsSpan(i * width, width);
            switch (width)
            {
                case 1:
                    destination[0] = unchecked((byte)values[i]);
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(destination, unchecked((ushort)values[i]));
                    break;
                case 4:
                    BinaryPrimitives.WriteUInt32LittleEndian(destination, unchecked((uint)values[i]));
                    break;
                default:
                    BinaryPrimitives.WriteUInt64LittleEndian(destination, values[i]);
                    break;
            }
        }

        return bytes;
    }

    /// <summary>Applies the transform and bit-packs, one 1024-element block at a time.</summary>
    /// <remarks>
    /// A patched row is packed like any other and its low bits are simply lost to the mask; the
    /// patch child puts the value back. Writing a zero there instead would cost a branch per row
    /// to produce bytes nothing reads.
    /// </remarks>
    /// <summary>
    /// The patches the pack finds for <paramref name="plan"/> over <paramref name="node"/>: the rows
    /// whose transformed value does not fit the width, and those values in the encoded domain.
    /// </summary>
    /// <remarks>
    /// FOR THE TESTS THAT USED TO READ THEM OFF THE PLAN. The gather moved from the chooser into the
    /// pack (docs/11-write-strategy.md §3.5), and the assertions that pin its positions and its
    /// domain — a patch value is the offset from the reference, not the raw value; a null row is
    /// never a patch — moved with it rather than being dropped. The packed bytes are computed and
    /// discarded, which is what the assertions cost.
    /// </remarks>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">The integer column chunk.</param>
    /// <param name="plan">Its bit-packing plan.</param>
    internal static (int[] Indices, ulong[] Values) Patches(
        CanonicalArena arena, CanonicalNode node, BitPackPlan plan)
    {
        Pack(arena, node, plan, node.PType, node.Length, out int[] indices, out ulong[] values);
        return (indices, values);
    }

    private static byte[] Pack(
        CanonicalArena arena, CanonicalNode node, BitPackPlan plan, PType ptype, int length,
        out int[] patchIndices, out ulong[] patchValues)
    {
        // THE PATCHES ARE FOUND HERE, NOT BY A WALK OF THEIR OWN (docs/11-write-strategy.md §3.5):
        // every row is transformed below anyway, and an exception is a transformed value that does
        // not fit the width. The chooser counted them from its histogram, which sizes the arrays;
        // the loop fills them; a count that disagrees is an exception rather than a short array
        // padded with row 0 in silence.
        int exceptions = checked((int)plan.Exceptions);
        patchIndices = exceptions == 0 ? [] : new int[exceptions];
        patchValues = exceptions == 0 ? [] : new ulong[exceptions];
        int found = 0;

        int bitWidth = plan.BitWidth;
        int blocks = (length + FastLanes.BlockSize - 1) / FastLanes.BlockSize;

        // UNINITIALIZED, because `PackBlock` clears its own destination before it ORs into it, so
        // every byte returned here is written twice and zeroed once for nothing. WRITE-ARCHITECTURE
        // §3.6 charges `ZeroMemoryNative` **17,0 %** of a `fastlanes_bitpacked` write and **17,6 %**
        // of a `dict_nullable_codes` one, and this array is the first of its two sources. The two
        // early returns below are the empty array in both cases -- `BlockByteLength(0)` is 0, and
        // `length == 0` gives no blocks -- so nothing is ever returned unwritten.
        byte[] destination = GC.AllocateUninitializedArray<byte>(
            checked((int)((long)blocks * FastLanes.BlockByteLength(bitWidth))));
        // WIDTH ZERO STILL WALKS: a column packed at zero bits is a constant with exceptions, and the
        // exceptions are exactly what the loop below has to find. Only an empty column has nothing
        // to look at. The packed bytes are empty either way -- `BlockByteLength(0)` is 0 -- and the
        // pack itself is skipped block by block below.
        if (length == 0)
        {
            return destination;
        }

        ReadOnlySpan<byte> values = node.Values.Span;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int blockBytes = FastLanes.BlockByteLength(bitWidth);
        int elementBits = ptype.ByteWidth() * 8;

        // BOTH SCRATCH BUFFERS ARE RENTED AND LIVE FOR THE WHOLE COLUMN. `block` was allocated per
        // column, but the NARROW copy inside `PackInto` was allocated per 1024-ROW BLOCK -- a fresh
        // `byte[1024]`, `ushort[1024]` or `uint[1024]` for every block of every bit-packed column
        // in the file. The narrow buffer is sized in BYTES here so one rental serves whichever
        // width the column turns out to be.
        // An exception is a transformed value at or above 2^width; at the element's own width
        // nothing can be one, and the chooser then counted none.
        ulong limit = bitWidth >= 64 ? ulong.MaxValue : 1UL << bitWidth;
        bool patching = exceptions > 0;

        ulong[] block = ArrayPool<ulong>.Shared.Rent(FastLanes.BlockSize);
        byte[] narrow = ArrayPool<byte>.Shared.Rent(FastLanes.BlockSize * sizeof(uint));
        try
        {
            Span<ulong> wide = block.AsSpan(0, FastLanes.BlockSize);
            for (int b = 0; b < blocks; b++)
            {
                int start = b * FastLanes.BlockSize;
                int count = Math.Min(FastLanes.BlockSize, length - start);

                // ONLY THE TAIL OF THE LAST BLOCK NEEDS CLEARING. The loop below writes every one
                // of the block's 1 024 slots when the block is full, which is every block but the
                // last; clearing the whole 8 KiB each time was zeroing bytes that were about to be
                // overwritten -- a megabyte of `wide` cleared per 128 blocks, for one partial block
                // at the end of the column.
                if (count < FastLanes.BlockSize)
                {
                    wide[count..].Clear();
                }

                // TWO METHODS, NEITHER INLINED: with the masked loop left in this one, the lanes'
                // call moved its code enough to cost `chunked_mixed_validity` 14 % on the blocks
                // that still take it, bytes unchanged.
                if (mask.AllValid)
                {
                    TransformValid(values, ptype, start, count, plan, elementBits, wide);
                }
                else
                {
                    TransformMasked(values, in mask, ptype, start, count, plan, elementBits, wide);
                }

                // THE PATCHES ARE GATHERED FROM `wide`, IN A LOOP OF THEIR OWN, and not from the
                // transform loop above with a branch per row: stage R5b-2 put the test inside that
                // loop and a trace of `chunked` showed `Pack` at four times its former share, the
                // same effect a never-taken branch had on the bounds loop (R7c). The block's 1 024
                // transformed values are in L1, a second walk over them costs their compares and
                // nothing else, and it runs only when the chooser counted an exception at all. A
                // null row is zero, zero always fits, so a null is never a patch.
                if (patching)
                {
                    found = FindPatches(wide[..count], limit, start, patchIndices, patchValues, found);
                }

                if (blockBytes > 0)
                {
                    PackInto(wide, narrow, bitWidth, ptype, destination.AsSpan(b * blockBytes, blockBytes));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(narrow);
            ArrayPool<ulong>.Shared.Return(block);
        }

        if (found != exceptions)
        {
            // The count came from the histogram and the gather from the transform; they read the
            // same rows under the same map, so they agree or one of the two is wrong -- and a
            // short array of patches would otherwise put row 0 back into the file in silence.
            throw new InvalidOperationException(
                $"The width histogram counted {exceptions} values above {bitWidth} bits and the " +
                $"pack found {found}.");
        }

        return destination;
    }

    /// <summary>
    /// The transform of an all-valid block applied on the load (docs/11-write-strategy.md §4.2):
    /// two 64-bit values or four 32-bit ones per step, widened into <paramref name="wide"/>.
    /// </summary>
    /// <remarks>
    /// THE ELEMENT WIDTH'S OWN ARITHMETIC IS THE MASK. <see cref="BitPackPlan.Encode"/> computes
    /// in 64 bits and masks to the element width; a 32-bit lane wraps at 32 bits and its arithmetic
    /// shift by 31 is the sign word, so its result, zero-extended, is the same number. Only the
    /// low word of the reference takes part, which is all the mask kept of it. The narrower widths
    /// and the tail are left to the scalar loop.
    /// </remarks>
    /// <param name="values">The column's values, from its first row.</param>
    /// <param name="ptype">The column's element type.</param>
    /// <param name="start">The block's first row.</param>
    /// <param name="count">The block's rows.</param>
    /// <param name="plan">The transform and its reference.</param>
    /// <param name="elementBits">The element width, for the scalar tail.</param>
    /// <param name="wide">The block's transformed values.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TransformValid(
        ReadOnlySpan<byte> values, PType ptype, int start, int count, BitPackPlan plan,
        int elementBits, Span<ulong> wide)
    {
        int done = TransformLanes(values, ptype.ByteWidth(), start, count, plan.Transform, plan.Reference, wide);
        switch (ptype.ByteWidth())
        {
            case 1: TransformRows<byte>(values, done, start, count, plan, elementBits, wide); return;
            case 2: TransformRows<ushort>(values, done, start, count, plan, elementBits, wide); return;
            case 4: TransformRows<uint>(values, done, start, count, plan, elementBits, wide); return;
            default: TransformRows<ulong>(values, done, start, count, plan, elementBits, wide); return;
        }
    }

    /// <summary>
    /// Rows <c>[done, count)</c> of the block, the width resolved once: the lanes' tail, or the
    /// whole block for the widths the lanes do not serve.
    /// </summary>
    private static void TransformRows<T>(
        ReadOnlySpan<byte> values, int done, int start, int count, BitPackPlan plan,
        int elementBits, Span<ulong> wide)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> block = MemoryMarshal.Cast<byte, T>(values).Slice(start, count);
        for (int i = done; i < block.Length; i++)
        {
            wide[i] = BitPackPlan.Encode(
                ulong.CreateTruncating(block[i]), plan.Transform, plan.Reference, elementBits);
        }
    }

    /// <summary>
    /// The exceptions of one transformed block, found eight values at a time: an unsigned
    /// <c>v ≥ 2^width</c> mask over two loads, and a scalar walk only of the eight that hold one.
    /// </summary>
    /// <remarks>
    /// Exceptions are rare by construction — the chooser priced every one — so the common step is
    /// two compares, an OR and a test. A found array that would overflow is left to the caller's
    /// count check, which names both counts.
    /// </remarks>
    /// <returns>The patches found so far, this block's included.</returns>
    private static int FindPatches(
        ReadOnlySpan<ulong> block, ulong limit, int start, int[] indices, ulong[] values, int found)
    {
        const int Step = 8;
        if (!Vector128.IsHardwareAccelerated)
        {
            return CollectPatches(block, limit, start, indices, values, found);
        }

        Vector128<ulong> bound = Vector128.Create(limit - 1);
        ref ulong first = ref MemoryMarshal.GetReference(block);
        int i = 0;
        for (; i + Step <= block.Length; i += Step)
        {
            Vector128<ulong> any =
                Vector128.GreaterThan(Vector128.LoadUnsafe(ref first, (nuint)i), bound)
                | Vector128.GreaterThan(Vector128.LoadUnsafe(ref first, (nuint)(i + 2)), bound)
                | Vector128.GreaterThan(Vector128.LoadUnsafe(ref first, (nuint)(i + 4)), bound)
                | Vector128.GreaterThan(Vector128.LoadUnsafe(ref first, (nuint)(i + 6)), bound);
            if (any == Vector128<ulong>.Zero)
            {
                continue;
            }

            found = CollectPatches(block.Slice(i, Step), limit, start + i, indices, values, found);
        }

        return CollectPatches(block[i..], limit, start + i, indices, values, found);
    }

    /// <summary>The scalar walk: every value at or above the limit, in order.</summary>
    private static int CollectPatches(
        ReadOnlySpan<ulong> block, ulong limit, int start, int[] indices, ulong[] values, int found)
    {
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] < limit)
            {
                continue;
            }

            if (found < indices.Length)
            {
                indices[found] = start + i;
                values[found] = block[i];
            }

            found++;
        }

        return found;
    }

    /// <summary>The transform of a block that may hold nulls, row by row.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TransformMasked(
        ReadOnlySpan<byte> values, in ValidityMask mask, PType ptype, int start, int count,
        BitPackPlan plan, int elementBits, Span<ulong> wide)
    {
        for (int i = 0; i < count; i++)
        {
            int row = start + i;

            // A null row encodes as zero under either transform: its value is never read back and
            // a stable zero compresses better than whatever the buffer held.
            wide[i] = mask.IsValid(row)
                ? BitPackPlan.Encode(
                    CompressedValues.ReadUnsigned(values, ToUnsigned(ptype), row),
                    plan.Transform, plan.Reference, elementBits)
                : 0;
        }
    }

    /// <summary>The lanes of <see cref="TransformValid"/>, for 32- and 64-bit elements.</summary>
    /// <returns>The rows written: a prefix of the block.</returns>
    private static int TransformLanes(
        ReadOnlySpan<byte> values, int byteWidth, int start, int count,
        BitPackTransform transform, ulong reference, Span<ulong> wide)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        bool frame = transform == BitPackTransform.Frame;
        ref ulong destination = ref MemoryMarshal.GetReference(wide);
        int i = 0;
        if (byteWidth == sizeof(ulong))
        {
            ref ulong source = ref Unsafe.Add(
                ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, ulong>(values)), start);
            Vector128<ulong> origin = Vector128.Create(reference);
            for (; i + 2 <= count; i += 2)
            {
                Vector128<ulong> value = Vector128.LoadUnsafe(ref source, (nuint)i);
                Vector128<ulong> encoded = frame
                    ? value - origin
                    : (value << 1) ^ Vector128.ShiftRightArithmetic(value.AsInt64(), 63).AsUInt64();
                encoded.StoreUnsafe(ref destination, (nuint)i);
            }
        }
        else if (byteWidth == sizeof(uint))
        {
            ref uint source = ref Unsafe.Add(
                ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, uint>(values)), start);
            Vector128<uint> origin = Vector128.Create(unchecked((uint)reference));
            for (; i + 4 <= count; i += 4)
            {
                Vector128<uint> value = Vector128.LoadUnsafe(ref source, (nuint)i);
                Vector128<uint> encoded = frame
                    ? value - origin
                    : (value << 1) ^ Vector128.ShiftRightArithmetic(value.AsInt32(), 31).AsUInt32();
                Vector128.WidenLower(encoded).StoreUnsafe(ref destination, (nuint)i);
                Vector128.WidenUpper(encoded).StoreUnsafe(ref destination, (nuint)(i + 2));
            }
        }

        return i;
    }

    private static void PackInto(
        ReadOnlySpan<ulong> block, byte[] scratch, int bitWidth, PType ptype, Span<byte> destination)
    {
        switch (ptype.ByteWidth())
        {
            case 1:
            {
                Span<byte> narrow = scratch.AsSpan(0, FastLanes.BlockSize);
                for (int i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (byte)block[i];
                }

                FastLanes.PackBlock<byte>(narrow, bitWidth, destination);
                return;
            }

            case 2:
            {
                Span<ushort> narrow = MemoryMarshal.Cast<byte, ushort>(
                    scratch.AsSpan(0, FastLanes.BlockSize * sizeof(ushort)));
                for (int i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (ushort)block[i];
                }

                FastLanes.PackBlock<ushort>(narrow, bitWidth, MemoryMarshal.Cast<byte, ushort>(destination));
                return;
            }

            case 4:
            {
                Span<uint> narrow = MemoryMarshal.Cast<byte, uint>(
                    scratch.AsSpan(0, FastLanes.BlockSize * sizeof(uint)));
                for (int i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (uint)block[i];
                }

                FastLanes.PackBlock<uint>(narrow, bitWidth, MemoryMarshal.Cast<byte, uint>(destination));
                return;
            }

            default:
                FastLanes.PackBlock<ulong>(block, bitWidth, MemoryMarshal.Cast<byte, ulong>(destination));
                return;
        }
    }

    /// <remarks>
    /// ARITHMETIC RATHER THAN A SWITCH, and the enum is what makes it legitimate: U8..U64 are 0..3
    /// and I8..I64 are 4..7, so the signed block sits exactly <c>I8</c> above the unsigned one --
    /// the same fact <see cref="PTypeExtensions.IsSignedInteger"/> is already written from. Four
    /// mappings enumerated by hand said nothing this does not, and left the other seven PTypes to a
    /// <c>_</c> arm that read as a fallthrough.
    /// </remarks>
    private static PType ToUnsigned(PType ptype) =>
        ptype.IsSignedInteger() ? (PType)(ptype - PType.I8) : ptype;

    /// <remarks>
    /// WHAT THIS FAMILY OF COPIES COSTS, measured: doubling all twelve of them, together with the
    /// zero bitmap an all-null validity writes, moves the write allocation axes by 32 to 192 bytes
    /// each and by 712 on the widest of them -- a tenth of a per cent. A node's metadata is a
    /// handful of protobuf fields, and there are as many of these per file as there are nodes, not
    /// as there are rows.
    /// </remarks>
    private static byte[] BitPackedBytes(uint bitWidth, bool hasPatches, in PatchesMetadata patches)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            BitPackedMetadata value = hasPatches
                ? new BitPackedMetadata(bitWidth, 0, in patches)
                : new BitPackedMetadata(bitWidth, 0);
            BitPackedMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The FoR reference: a bare protobuf ScalarValue, without its dtype.
    /// </summary>
    /// <remarks>
    /// An EMPTY metadata decodes to a null reference, which the reference implementation's own
    /// validate_parts rejects with "Reference value cannot be null" -- so this is one of the places
    /// where writing nothing is not the same as writing the default.
    /// </remarks>
    /// <summary>Nodes a metadata store has to hold, which is two.</summary>
    /// <remarks>
    /// PERF-AUDIT-v2.md W-3. `new ScalarStore()` is five allocations, not one -- the store, a
    /// `ScalarNode[16]`, an `int[16]`, a `byte[64]` and a `DType[4]` -- and its default capacity is
    /// sized for a file's statistics, not for the two integers these two helpers put in it. Measured
    /// at **992 bytes** per node written with a frame of reference or a sequence, which is 1,8 % of
    /// `table_wide`'s whole write, 1,1 % of `table_mixed`'s and 4,7 % of a file that is nothing but
    /// a sequence column. At four it is **416 bytes**.
    ///
    /// FOUR AND NOT TWO because the constructor floors it there anyway. The store still grows if
    /// something ever puts more in it, so this is a size hint and not an invariant.
    ///
    /// THE REST OF THE POINT IS DELIBERATELY NOT DONE. Reusing one store instead of sizing it would
    /// take the remaining 416 bytes, and the audit proposes a field with `Clear()` -- but the only
    /// correct home for it is `VortexFileWriter`, because `[ThreadStatic]` is banned in this project
    /// (RS0030, and the reason is exactly this library: an `await` can separate taking the state
    /// from finishing with it). Getting a field from there to here means a parameter through
    /// `WriteCompressed`, which has six callers of its own, and about thirty signatures in all. That
    /// is not a trade worth making for the 0,3 to 0,5 % of a write that is left.
    /// </remarks>
    private const int Scalars = 4;

    private static byte[] ReferenceBytes(ulong reference, PType ptype)
    {
        ScalarStore store = new ScalarStore(Scalars);
        ScalarValue value = ptype.IsSignedInteger()
            ? store.Int64(unchecked((long)SignExtend(reference, ptype)))
            : store.UInt64(reference);
        return ScalarProtobuf.SerializeValue(value);
    }

    /// <summary>Widens a narrow signed value's raw bits back to 64 bits.</summary>
    /// <remarks>
    /// THE WIDTH IS THE ANSWER, not the tag: shifting the value up to the top of a 64-bit word and
    /// back down arithmetically is what sign extension IS, and it is written once instead of once
    /// per narrow signed type. The three cases a switch spelled out were I8, I16 and I32 -- that
    /// is <c>ByteWidth</c> 1, 2 and 4 -- and everything else returned its bits unchanged, which
    /// here is the shift of zero that <c>I64</c> produces.
    /// </remarks>
    private static ulong SignExtend(ulong bits, PType ptype)
    {
        if (!ptype.IsSignedInteger())
        {
            return bits;
        }

        int shift = 64 - (ptype.ByteWidth() * 8);
        return unchecked((ulong)((long)(bits << shift) >> shift));
    }

    /// <summary>
    /// Writes <c>vortex.fsst</c>: three buffers and two or three children.
    /// </summary>
    /// <remarks>
    /// The shape is the one FsstDecoder reads, and its unusual property is worth restating at the
    /// writing end: THE ROW BOUNDARIES ARE ON THE DECODED SIDE. `codes_offsets` bounds each row's
    /// codes, but the reader decompresses the whole stream in one pass and then cuts the result
    /// with `uncompressed_lengths` - so the lengths are not a hint, they are the only thing that
    /// says where a value ends, and they must account for the decoded heap exactly.
    ///
    /// The validity child goes last and is present only for a nullable dtype, matching the
    /// decoder's `DecodeValidity(node, 2, ...)`.
    /// </remarks>
    private static int WriteFsst(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        FsstPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        FsstSymbols table = plan.Table;
        byte[] symbols = new byte[table.Count * 8];
        byte[] symbolLengths = new byte[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(symbols.AsSpan(i * 8), table.SymbolBits(i));
            symbolLengths[i] = table.SymbolLength(i);
        }

        buffers.Add(new PendingBuffer(symbols, Exponent(8)));
        int symbolBuffer = buffers.Count - 1;
        buffers.Add(new PendingBuffer(symbolLengths, 0));
        int lengthBuffer = buffers.Count - 1;
        // No copy: `FsstPlan` now keeps a code array of exactly `CodeLength` bytes, because the
        // oversized one it used to carry was a rental it had to let go of anyway. This line was
        // PERF-AUDIT §4.3's `WriteFsst (:469)`.
        buffers.Add(new PendingBuffer(plan.Codes, 0));
        int codeBuffer = buffers.Count - 1;

        PType lengthsPType = FsstPlan.IndexPType(FsstPlan.MaxOf(plan.Lengths));
        PType offsetsPType = FsstPlan.IndexPType(plan.CodeLength);

        int uncompressed = WriteIndexArray(builder, buffers, encodings, plan.Lengths, lengthsPType);
        int offsets = WriteIndexArray(builder, buffers, encodings, plan.Offsets, offsetsPType);

        Span<int> children = stackalloc int[3];
        children[0] = uncompressed;
        children[1] = offsets;
        Span<int> validity = stackalloc int[1];
        int childCount = 2 + Validity(builder, arena, node, buffers, encodings, validity);
        if (childCount == 3)
        {
            children[2] = validity[0];
        }

        Span<ushort> indices = stackalloc ushort[3];
        indices[0] = (ushort)symbolBuffer;
        indices[1] = (ushort)lengthBuffer;
        indices[2] = (ushort)codeBuffer;

        byte[] metadata = FsstBytes(lengthsPType, offsetsPType);
        return Node(builder, encodings, "vortex.fsst"u8, metadata, children[..childCount], indices);
    }

    /// <summary>
    /// Writes <c>vortex.zstd</c>: one frame buffer, one optional validity child.
    /// </summary>
    /// <remarks>
    /// One frame, not several. The format allows a column to be split across frames so a slice can
    /// decompress only the part it wants, and nothing in this writer slices - a second frame would
    /// be unread structure. The decoder walks frames until their cumulative value count covers the
    /// rows it needs, so a single frame carrying every value is the degenerate case it already
    /// handles.
    ///
    /// No dictionary buffer: `DictionarySize` is 0, which is what the decoder checks to decide
    /// whether a dictionary buffer is present at all.
    /// </remarks>
    private static int WriteZstd(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        ZstdPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        // THE FRAME IS THE POOL'S, AND OWNERSHIP MOVES HERE. `ZstdPlan` kept the buffer it
        // compressed into rather than copying it out; from this line the plan's `Frame` must not be
        // read again, and `Write` is what hands it back after the blob has been laid out.
        buffers.Add(new PendingBuffer(plan.Frame, plan.FrameLength, 0, rented: true));
        int frameBuffer = buffers.Count - 1;

        Span<int> children = stackalloc int[1];
        int childCount = Validity(builder, arena, node, buffers, encodings, children);

        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)frameBuffer;

        byte[] metadata = ZstdBytes(plan);
        return Node(builder, encodings, "vortex.zstd"u8, metadata, children[..childCount], indices);
    }

    private static byte[] ZstdBytes(ZstdPlan plan)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            Span<ZstdFrameMetadata> frames = stackalloc ZstdFrameMetadata[1];
            frames[0] = new ZstdFrameMetadata((ulong)plan.UncompressedSize, (ulong)plan.ValueCount);
            ZstdMetadata value = new ZstdMetadata(dictionarySize: 0, frameCount: 1);
            ZstdMetadata.Write(ref writer, in value, frames);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] FsstBytes(PType lengthsPType, PType offsetsPType)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            FsstMetadata value = new FsstMetadata(lengthsPType, offsetsPType);
            FsstMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Writes <c>vortex.alp</c>: no buffers, and one, three or four children.
    /// </summary>
    /// <remarks>
    /// There is NO validity child, and that is not an omission. The decoder takes the array's
    /// validity off the ENCODED child - `decompress_unchunked_core` does - so an ALP array's
    /// nullability rides on the integers, and writing a validity child here would leave the real
    /// one unread.
    ///
    /// The encoded child goes through the compressor like any other column, which is where the
    /// saving actually lands: ALP turns doubles into small integers, and frame-of-reference plus
    /// bit-packing turns small integers into few bits.
    /// </remarks>
    private static int WriteAlp(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        AlpPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;

        // The encoded integers carry the float column's own validity, so they are added to the
        // arena as a node rather than written as a bare buffer.
        // The dtype arena is the column's own: a DType carries the arena it belongs to, and a
        // node whose dtype came from a different one would not compare equal downstream.
        DType encodedType = node.DType.Arena.Primitive(plan.EncodedPType, node.DType.Nullability);
        // Uninitialized: the CopyTo on the next line fills it whole (WRITE-ARCHITECTURE.md §3.6).
        VortexBuffer encodedBuffer = arena.AllocateUninitialized(
            plan.Encoded.Length, plan.EncodedPType.ByteWidth(), out Span<byte> destination);
        plan.Encoded.CopyTo(destination);

        // The integers live in the arena from here on, so the pool can have its array back. Every
        // float column is priced with ALP, and one that loses now costs the pool a rental instead
        // of the heap a column.
        plan.Release();
        int encodedNode = arena.AddPrimitive(
            encodedType, rows, node.Validity, plan.EncodedPType, encodedBuffer);

        Span<int> children = stackalloc int[3];
        children[0] = WriteCompressed(builder, arena, encodedNode, buffers, encodings);
        int childCount = 1;

        PatchesMetadata patches = default;
        if (plan.PatchIndices.Length > 0)
        {
            PType indicesPType = FsstPlan.IndexPType(rows);
            patches = PatchesMetadata.Create((ulong)plan.PatchIndices.Length, 0, indicesPType);
            children[1] = WriteIndexArray(builder, buffers, encodings, plan.PatchIndices, indicesPType);
            children[2] = WriteRawPrimitive(
                builder, buffers, encodings, plan.PatchValues, node.DType.PType);
            childCount = 3;
        }

        byte[] metadata = AlpBytes(plan.ExponentE, plan.ExponentF, plan.PatchIndices.Length > 0, patches);
        return Node(
            builder, encodings, "vortex.alp"u8, metadata, children[..childCount], []);
    }

    private static byte[] AlpBytes(byte e, byte f, bool hasPatches, in PatchesMetadata patches)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            AlpMetadata value = hasPatches
                ? new AlpMetadata(e, f, in patches)
                : new AlpMetadata(e, f);
            AlpMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Writes <c>vortex.sequence</c>: no children, no buffers, the whole column in the metadata.
    /// </summary>
    /// <remarks>
    /// The one encoding here that REPLACES a node rather than wrapping one, so it has no overhead
    /// to weigh against: the primitive node and its buffer both disappear.
    ///
    /// There is no validity child because there is nowhere to put one - the encoding has neither
    /// children nor buffers - which is why the plan refuses any column with a null. A nullable
    /// dtype still round-trips: the decoder derives AllValid from the nullability, which is what
    /// the column had.
    /// </remarks>
    private static int WriteSequence(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        SequencePlan plan,
        EncodingDictionary encodings)
    {
        // A progression writes no buffer, so this is the one scheme a constant reaches without
        // being expanded, and an unexpanded node has no physical type of its own yet. Its dtype's
        // is the one the expansion would have given it.
        CanonicalNode node = arena.GetNode(nodeIndex);
        PType ptype = node.Kind == CanonicalKind.Constant ? node.DType.PType : node.PType;
        return Node(builder, encodings, "vortex.sequence"u8, SequenceBytes(plan, ptype), [], []);
    }

    /// <summary>
    /// The serialized <c>vortex.sequence</c> metadata, exposed so a test can assert the exact
    /// bytes against a file the reference wrote.
    /// </summary>
    /// <param name="plan">The base and step.</param>
    /// <param name="ptype">The column's physical type.</param>
    /// <remarks>
    /// The wire tag of the multiplier is part of the contract - upstream reads its physical type
    /// from the proto tag rather than from the dtype - and no round trip through our own reader can
    /// see a wrong one, because we read either tag happily and produce the same values.
    /// </remarks>
    internal static byte[] SequenceMetadataBytesForTests(SequencePlan plan, PType ptype) =>
        SequenceBytes(plan, ptype);

    private static byte[] SequenceBytes(SequencePlan plan, PType ptype)
    {
        ScalarStore store = new ScalarStore(Scalars);

        // Both fields are bare ScalarValues, and the base is interpreted against the array's own
        // dtype - so its signedness must match the column's, exactly as the frame of reference's
        // does. The multiplier's does not: the wire preserves the STEP's signedness, and the
        // decoder reads its physical type from the proto tag rather than from the dtype.
        ScalarValue baseValue = ptype.IsSignedInteger()
            ? store.Int64(unchecked((long)plan.BaseBits))
            : store.UInt64(plan.BaseBits);
        ScalarValue multiplier = plan.StepIsUnsigned
            ? store.UInt64((ulong)plan.Step)
            : store.Int64((long)plan.Step);

        ProtoWriter writer = new ProtoWriter();
        try
        {
            SequenceMetadata.Write(ref writer, new SequenceMetadata(baseValue, multiplier));
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Writes raw little-endian bytes as a non-nullable primitive node.</summary>
    private static int WriteRawPrimitive(
        FlatBufferBuilder builder, List<PendingBuffer> buffers, EncodingDictionary encodings,
        byte[] bytes, PType ptype)
    {
        buffers.Add(new PendingBuffer(bytes, Exponent(ptype.ByteWidth())));
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)(buffers.Count - 1);
        return Node(builder, encodings, "vortex.primitive"u8, default, [], indices);
    }

    private static int WriteRunEnd(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        int values,
        in ColumnPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        int length = arena.GetNode(nodeIndex).Length;
        PType endsPType = IndexPType(length);

        byte[] metadata = RunEndBytes(endsPType, (ulong)plan.Codes.Length);
        int ends = WriteIndexColumn(
            builder, arena, nodeIndex, plan.Codes, endsPType, buffers, encodings,
            Cascade.RunEndEnds());
        int valuesNode = WriteCompressed(
            builder, arena, values, buffers, encodings, cascade: Cascade.ValuesChild());

        Span<int> children = stackalloc int[2];
        children[0] = ends;
        children[1] = valuesNode;
        return Node(builder, encodings, "vortex.runend"u8, metadata, children, []);
    }

    private static int WriteDict(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        int values,
        in ColumnPlan plan,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        int entries = arena.GetNode(values).Length;
        PType codesPType = IndexPType(entries);

        // is_nullable_codes = false: a null row is a code pointing at a null DICTIONARY ENTRY, not
        // a null code. Nullness is part of the value the compressor deduplicated, so at most one
        // entry is null and every row still has a code.
        byte[] metadata = DictBytes((uint)entries, codesPType);

        // The codes are the table's own buffer when the table chose the plan -- one code per row,
        // written by the probe as the rows arrived, never copied (docs/11 §3.5) -- and the plan's
        // array when the reference chooser walked for them.
        ReadOnlySpan<int> codeOfRow = plan.Table is not null
            ? plan.Table.Codes[..plan.Rows]
            : plan.Codes.AsSpan(0, plan.Rows);
        int codes = WriteIndexColumn(
            builder, arena, nodeIndex, codeOfRow, codesPType, buffers, encodings,
            Cascade.DictionaryCodes(entries));

        // The codes are in the arena now, narrowed to the width the file carries, so a rental the
        // chooser handed over goes back to the pool rather than becoming a row vector of garbage.
        if (plan.CodesRented)
        {
            ArrayPool<int>.Shared.Return(plan.Codes);
        }
        int valuesNode = WriteCompressed(
            builder, arena, values, buffers, encodings, cascade: Cascade.ValuesChild());

        Span<int> children = stackalloc int[2];
        children[0] = codes;
        children[1] = valuesNode;
        return Node(builder, encodings, "vortex.dict"u8, metadata, children, []);
    }

    /// <summary>The narrowest unsigned type that indexes <paramref name="count"/> values.</summary>
    private static PType IndexPType(int count) => count switch
    {
        <= byte.MaxValue => PType.U8,
        <= ushort.MaxValue => PType.U16,
        _ => PType.U32,
    };

    /// <summary>Writes an int array as a non-nullable primitive node of the narrowest width.</summary>
    /// <summary>
    /// Writes a column of indices - dictionary codes, run ends - THROUGH THE COMPRESSOR.
    /// </summary>
    /// <remarks>
    /// This is the cascade docs/90-registry.md has been calling "where the rest lives": codes are
    /// the one part of a dictionary that costs a byte per ROW rather than per distinct value, and
    /// they are the most bit-packable data in the file by construction - non-negative, dense, and
    /// bounded by the entry count. A column with 1153 distinct values carries u16 codes and needs
    /// eleven bits.
    ///
    /// Legal because the reader derives the codes child's dtype from the metadata's
    /// `codes_ptype` and then decodes it like any other child: `fastlanes.for` over
    /// `fastlanes.bitpacked` produces exactly the u16 primitive the dict decoder then checks for.
    ///
    /// The recursion terminates on the arithmetic rather than on a depth counter: every scheme
    /// must beat the level above it by its own overhead - 512 bytes for a dictionary, 256 for a
    /// bit-packing - so the sizes strictly decrease.
    /// </remarks>
    private static int WriteIndexColumn(
        FlatBufferBuilder builder, CanonicalArena arena, int parentIndex, ReadOnlySpan<int> values,
        PType ptype, List<PendingBuffer> buffers, EncodingDictionary encodings,
        Cascade cascade = default)
    {
        int width = ptype.ByteWidth();

        // Uninitialized: `WriteIndices` writes every byte of it (WRITE-ARCHITECTURE.md §3.6).
        VortexBuffer buffer = arena.AllocateUninitialized(
            values.Length * width, width, out Span<byte> destination);
        WriteIndices(values, width, destination);

        return WriteIndexBuffer(
            builder, arena, arena.GetNode(parentIndex).DType.Arena, buffer, ptype, values.Length,
            buffers, encodings, cascade);
    }

    /// <summary>
    /// Writes an index buffer that already exists - a varbin's offsets, a list's offsets or
    /// sizes - through the compressor, without copying it.
    /// </summary>
    /// <remarks>
    /// docs/90-registry.md used to say offsets and sizes "are index machinery, and they are small".
    /// The first half is true and the second is not: a list of 8193 rows carries 8193 offsets and
    /// 8193 sizes, 64 kB between them, against elements that may be a fraction of that. They are
    /// also the most compressible data in the file - offsets are monotone by construction, and a
    /// list of fixed-width rows has offsets that are an exact arithmetic progression and sizes that
    /// are constant, which is to say both are `vortex.sequence` and cost nothing at all. The
    /// reference bit-packs them; `types/list_i32_nonnull_r8193` was 1.95x our size because of it.
    ///
    /// Validity bitmaps are still written raw. They genuinely are small - one bit per row - and
    /// they are the one child the compressor has no scheme for.
    /// </remarks>
    private static int WriteIndexBuffer(
        FlatBufferBuilder builder, CanonicalArena arena, DTypeArena types, VortexBuffer values,
        PType ptype, int count, List<PendingBuffer> buffers, EncodingDictionary encodings,
        Cascade cascade = default)
    {
        // The dtype arena is the column's own: a DType carries the arena it belongs to, and a node
        // whose dtype came from a different one would not compare equal downstream.
        int node = arena.AddPrimitive(
            types.Primitive(ptype, Nullability.NonNullable), count, Arrays.Validity.NonNullable,
            ptype, values);
        return WriteCompressed(builder, arena, node, buffers, encodings, cascade: cascade);
    }

    /// <summary>Writes an index per element, filling EVERY byte of <paramref name="destination"/>.</summary>
    /// <remarks>
    /// THE 8-BYTE CASE IS NOT DEAD CODE, it is the eligibility argument. Its callers hand widths of
    /// 1, 2 or 4 today -- <c>IndexPType</c> tops out at <c>u32</c>, and the varbin path refuses a
    /// heap above <c>int.MaxValue</c> before it picks a type -- but this function now writes into
    /// buffers from <c>CanonicalArena.AllocateUninitialized</c>, whose contract is "provably writes
    /// every byte, not probably" (its own docstring). A <c>default</c> arm writing four bytes into
    /// an eight-byte slot would put pooled bytes from another file into a column the day a caller
    /// passes <c>u64</c>, and the only oracle for that is the Rust cross-check.
    /// </remarks>
    /// <remarks>
    /// The width switch is inside the loop and it does not matter: doubling this whole call costs
    /// 0,2 ms of a 14,3 ms `dict_u8_codes` write, one and a half per cent, under the bar the plan
    /// sets for the per-row family. The index arrays are short -- one entry a patch, one a
    /// dictionary entry -- so what would be a per-row switch elsewhere is a per-entry one here.
    /// </remarks>
    private static void WriteIndices(ReadOnlySpan<int> values, int width, Span<byte> destination)
    {
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> at = destination.Slice(i * width, width);
            switch (width)
            {
                case 1:
                    at[0] = (byte)values[i];
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)values[i]);
                    break;
                case 8:
                    BinaryPrimitives.WriteUInt64LittleEndian(at, (ulong)(uint)values[i]);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(at, (uint)values[i]);
                    break;
            }
        }
    }

    private static int WriteIndexArray(
        FlatBufferBuilder builder, List<PendingBuffer> buffers, EncodingDictionary encodings,
        int[] values, PType ptype)
    {
        int width = ptype.ByteWidth();
        byte[] bytes = new byte[values.Length * width];
        for (int i = 0; i < values.Length; i++)
        {
            switch (width)
            {
                case 1:
                    bytes[i] = (byte)values[i];
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)values[i]);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)values[i]);
                    break;
            }
        }

        buffers.Add(new PendingBuffer(bytes, Exponent(width)));
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)(buffers.Count - 1);
        return Node(builder, encodings, "vortex.primitive"u8, default, [], indices);
    }

    private static byte[] RunEndBytes(PType endsPType, ulong runCount)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            RunEndMetadata value = new RunEndMetadata(endsPType, runCount, 0);
            RunEndMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] DictBytes(uint valuesLength, PType codesPType)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            DictMetadata value = new DictMetadata(valuesLength, codesPType, false, null);
            DictMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <remarks>
    /// <paramref name="compress"/> says whether the children that are columns in their own right -
    /// a list's elements, a fixed-size list's elements, a struct's fields, an extension's storage -
    /// may be compressed. Validity bitmaps and a list's offsets and sizes never are: they are index
    /// machinery, and they are small.
    /// </remarks>
    private static int WriteNode(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings,
        bool compress,
        ChunkStats stats = default)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);

        // EXHAUSTIVE BY CONSTRUCTION (PERF-AUDIT-v2.md §2.4bis, Z1b-c1), and this is the site where
        // it matters most. Every kind is NAMED and the `_` arm throws; it used to be
        // `default: WriteExtension`, so a tenth kind was written as `vortex.ext` -- a COHERENT file
        // carrying the wrong array, which byte-exact WrittenSizeTests cannot catch because the
        // bytes agree with themselves. IDE0072 -- error, see .editorconfig -- now fails the build
        // when a named kind is missing.
        return node.Kind switch
        {
            CanonicalKind.Null => Node(builder, encodings, "vortex.null"u8, default, [], []),
            CanonicalKind.Bool => WriteBool(builder, arena, node, buffers, encodings),
            CanonicalKind.Primitive => WritePrimitive(builder, arena, node, buffers, encodings),
            CanonicalKind.Decimal => WriteDecimal(builder, arena, node, buffers, encodings),
            CanonicalKind.VarBinView => WriteVarBinView(builder, arena, node, buffers, encodings),

            // A map node IS a ListView wearing the map dtype - see MapDecoder - so the kind alone
            // does not say which id to write. Emitting `vortex.listview` under a map schema
            // produces a file THIS READER REFUSES, correctly: "vortex.listview produces a List
            // dtype; it was asked for Map".
            CanonicalKind.ListView => node.DType.Kind == DTypeKind.Map
                ? WriteMap(builder, arena, node, buffers, encodings, compress, stats)
                : WriteListView(builder, arena, node, buffers, encodings, compress, stats),

            CanonicalKind.FixedSizeList =>
                WriteFixedSizeList(builder, arena, node, buffers, encodings, compress, stats),

            // THE DTYPE DECIDES, as it does for a map above: a Struct wearing a VARIANT dtype is a
            // variant column in this library's canonical form, and writing it as `vortex.struct`
            // would produce a file whose array says "two fields" and whose schema says "variant" --
            // which every reader, this one included, refuses.
            CanonicalKind.Struct => node.DType.Kind == DTypeKind.Variant
                ? WriteParquetVariant(builder, arena, node, buffers, encodings, compress, stats)
                : WriteStruct(builder, arena, node, buffers, encodings, compress, stats),

            CanonicalKind.Extension =>
                WriteExtension(builder, arena, node, buffers, encodings, compress, stats),
            // Materialized above, before the compressor (see WriteCompressed). Reaching it here
            // would mean a constant arrived by a path that skips `Choose`, and the bytes would not
            // be the ones the corpus was written with.
            CanonicalKind.Constant => throw new UnreachableException(
                "A constant node reached WriteNode; it is materialized in WriteCompressed."),
            _ => throw new UnreachableException($"CanonicalKind {(byte)node.Kind} is not defined."),
        };
    }


    /// <summary>Expands a constant node back to its materialized form; anything else unchanged.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The node about to be compressed and written.</param>
    /// <returns>A node index the rest of the writer already knows how to handle.</returns>
    /// <remarks>
    /// PERF-AUDIT-v2.md Z1b-c2b, AND THE CHOICE IS THE POINT. The design note says the writer
    /// "converts"; converting to WHAT was the ambiguity. Emitting `vortex.constant` on the wire
    /// would make the file smaller -- a real gain, and a different one: Z1b's claim is the memory a
    /// reader holds, not the size of a file. It would also move `WrittenSizeTests`, whose whole
    /// value is being byte-exact, and folding a size change into a memory refactor is how a
    /// regression hides behind an improvement.
    /// <para>
    /// So the element is expanded back to the form the writer already emits, and the file does not
    /// depend on how the read stored the column: the canonicalizer keeps the element and the writer
    /// tiles it to the buffer a primitive would have arrived in.
    /// `ConstantFormTests.WritingBackIsDeterministic` is what makes that a measured fact rather
    /// than a claim.
    /// </para>
    /// <para>
    /// THE EXPANSION ITSELF IS THE ARENA'S, not a second copy here. This used to tile the element
    /// with the element's own LENGTH as the buffer alignment, which is a power of two for every
    /// primitive and is 3 or 14 for a string -- so the day the constant form learned strings, the
    /// writer refused its own file. One expansion, in one place, is what stops the reader's form
    /// and the writer's from drifting apart again.
    /// </para>
    /// </remarks>
    internal static int Materialize(CanonicalArena arena, int nodeIndex) =>
        arena.GetNode(nodeIndex).Kind == CanonicalKind.Constant
            ? arena.MaterializeConstant(nodeIndex)
            : nodeIndex;

    /// <remarks>Writes a child that is a column in its own right, compressing it when asked.</remarks>
    private static int WriteChild(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings,
        bool compress,
        ChunkStats stats = default) =>
        compress
            ? WriteCompressed(builder, arena, nodeIndex, buffers, encodings, stats)
            : WriteNode(builder, arena, nodeIndex, buffers, encodings, compress: false, stats);

    private static int WriteBool(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        // The bit offset rides in the metadata, so a bitmap sliced off a byte boundary is written
        // without re-shifting it.
        byte[] metadata = BoolBytes((uint)node.BitOffset);
        int bits = Buffer(buffers, node.Bits, alignmentExponent: 0);
        Span<int> children = stackalloc int[1];
        int count = Validity(builder, arena, node, buffers, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)bits;
        return Node(builder, encodings, "vortex.bool"u8, metadata, children[..count], bufferIndices);
    }

    private static int WritePrimitive(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        int width = node.PType.ByteWidth();
        int values = Buffer(buffers, node.Values, Exponent(width));

        Span<int> children = stackalloc int[1];
        int count = Validity(builder, arena, node, buffers, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)values;
        return Node(builder, encodings, "vortex.primitive"u8, default, children[..count], bufferIndices);
    }

    private static int WriteDecimal(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        byte[] metadata = DecimalBytes(node.Storage);
        int width = DecimalStorage.ByteWidth(node.Storage);
        int values = Buffer(buffers, node.Values, Exponent(width));

        Span<int> children = stackalloc int[1];
        int count = Validity(builder, arena, node, buffers, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)values;
        return Node(builder, encodings, "vortex.decimal"u8, metadata, children[..count], bufferIndices);
    }

    /// <remarks>
    /// Chooses between the two serializations of a binary column, which is a SIZE decision and not
    /// a compression one.
    ///
    /// A canonical VarBinView costs 16 bytes of view per row whatever the values are; `vortex.varbin`
    /// costs one offset per row plus a contiguous heap, and inlines nothing. Four-byte offsets beat
    /// sixteen-byte views by twelve bytes a row, and inlining can save at most the twelve bytes a
    /// value of that length would have occupied in the heap - so varbin is never worse than
    /// varbinview and is usually much better. The reference reaches the same conclusion: every
    /// plain binary and utf8 column in the corpus is written `vortex.varbin`.
    ///
    /// What it costs US is a copy: the view form can hand the existing data buffers straight to the
    /// writer, and this has to gather the values into one heap. That is paid once at write time for
    /// a saving every reader keeps.
    /// </remarks>
    private static int WriteVarBinView(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        int dataCount = node.DataBufferCount;
        long viewForm = ((long)node.Length * ViewSize) + DataBufferBytes(node, dataCount);
        if (TryWriteVarBin(builder, arena, node, buffers, encodings, viewForm, out int varbin))
        {
            return varbin;
        }

        // Data buffers first, views LAST: the reader reaches for views at index `dataBufferCount`.
        ushort[] indices = new ushort[dataCount + 1];
        for (int i = 0; i < dataCount; i++)
        {
            indices[i] = (ushort)Buffer(buffers, node.GetDataBuffer(i), alignmentExponent: 0);
        }

        indices[dataCount] = (ushort)Buffer(buffers, node.Views, Exponent(ViewSize));

        Span<int> children = stackalloc int[1];
        int count = Validity(builder, arena, node, buffers, encodings, children);
        return Node(builder, encodings, "vortex.varbinview"u8, default, children[..count], indices);
    }

    private static long DataBufferBytes(CanonicalNode node, int dataCount)
    {
        long total = 0;
        for (int i = 0; i < dataCount; i++)
        {
            total += node.GetDataBuffer(i).Length;
        }

        return total;
    }

    /// <summary>Writes the column as <c>vortex.varbin</c> when that is smaller.</summary>
    private static bool TryWriteVarBin(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, long viewForm, out int result)
    {
        result = 0;
        int rows = node.Length;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        long heapBytes = 0;
        for (int i = 0; i < rows; i++)
        {
            if (mask.IsValid(i))
            {
                heapBytes += ViewLength(node, i);
            }
        }

        if (heapBytes > int.MaxValue)
        {
            return false;
        }

        PType offsetsPType = FsstPlan.IndexPType(heapBytes);
        long varbinForm = (((long)rows + 1) * offsetsPType.ByteWidth()) + heapBytes;
        if (varbinForm >= viewForm)
        {
            return false;
        }

        // Uninitialized and written ONCE. `heapBytes` is the sum of the valid values' lengths, so
        // the loop below fills exactly this array; it was allocated zeroed and then copied whole a
        // second time by a `ToArray()` that served nothing -- WRITE-ARCHITECTURE.md §3.6 measures
        // that pair at `fsst` allocating 103 MB to write 52.
        byte[] heap = GC.AllocateUninitializedArray<byte>(Math.Max((int)heapBytes, 1));
        int[] offsets = GC.AllocateUninitializedArray<int>(rows + 1);
        int written = 0;
        for (int i = 0; i < rows; i++)
        {
            offsets[i] = written;
            if (!mask.IsValid(i))
            {
                // A null row is zero-length: offsets stay monotone and the reader never looks at
                // the bytes, because validity already told it not to.
                continue;
            }

            ReadOnlySpan<byte> value = ViewBytes(node, i);
            value.CopyTo(heap.AsSpan(written));
            written += value.Length;
        }

        offsets[rows] = written;

        int heapBuffer = buffers.Count;
        buffers.Add(new PendingBuffer(heap, written, 0, rented: false));

        Span<int> children = stackalloc int[2];
        int width = offsetsPType.ByteWidth();
        VortexBuffer offsetBuffer = arena.AllocateUninitialized(
            offsets.Length * width, width, out Span<byte> offsetBytes);
        WriteIndices(offsets, width, offsetBytes);
        children[0] = WriteIndexBuffer(
            builder, arena, node.DType.Arena, offsetBuffer, offsetsPType, offsets.Length,
            buffers, encodings);
        int count = 1 + Validity(builder, arena, node, buffers, encodings, children[1..]);

        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)heapBuffer;
        result = Node(
            builder, encodings, "vortex.varbin"u8, VarBinBytes(offsetsPType),
            children[..count], indices);
        return true;
    }

    private static byte[] VarBinBytes(PType offsetsPType)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            VarBinMetadata value = new VarBinMetadata(offsetsPType);
            VarBinMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static int ViewLength(CanonicalNode node, int row) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(node.Views.Span.Slice(row * ViewSize, 4));

    private static ReadOnlySpan<byte> ViewBytes(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * ViewSize, ViewSize);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= 12)
        {
            return view.Slice(4, (int)size);
        }

        uint buffer = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer((int)buffer).Span.Slice((int)offset, (int)size);
    }

    /// <summary>Writes <c>vortex.map</c>: empty metadata, no buffers, one <c>vortex.listview</c> child.</summary>
    /// <remarks>
    /// The inner listview carries the validity, which is where <c>MapDecoder</c>
    /// reads it from, so the pair is symmetric rather than merely compatible. Upstream requires the
    /// entries child to use the listview encoding specifically, so this wrapper is the only shape a
    /// conformant map can take.
    /// </remarks>
    private static int WriteMap(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, bool compress,
        ChunkStats stats = default)
    {
        int entries = WriteListView(builder, arena, node, buffers, encodings, compress, stats);
        Span<int> children = stackalloc int[1];
        children[0] = entries;
        return Node(builder, encodings, "vortex.map"u8, default, children, []);
    }

    /// <summary>
    /// What a test installs to see every list chunk's elements beside the summary the chooser is
    /// handed for them (docs/11 §3.2.4): the arena, the elements node, and the summary, absent when
    /// the chooser measures. It flows with the async write, as <c>ColumnCompressor.Differential</c>
    /// does, so two tests writing at once never see each other's chunks.
    /// </summary>
    internal static readonly System.Threading.AsyncLocal<Action<CanonicalArena, int, BlockStats>?> ElementsHanded =
        new System.Threading.AsyncLocal<Action<CanonicalArena, int, BlockStats>?>();

    /// <summary>The cursor for a list's elements, shown to the audit when one is installed.</summary>
    private static ChunkStats ElementsOf(CanonicalArena arena, int elements, bool compress, ChunkStats stats)
    {
        if (!compress)
        {
            return default;
        }

        ChunkStats cursor = stats.Elements(arena.GetNode(elements).Length);
        ElementsHanded.Value?.Invoke(arena, elements, cursor.Stats);
        return cursor;
    }

    /// <remarks>
    /// THE ELEMENTS ARE A COLUMN WITH STATISTICS OF THEIR OWN (docs/11 §3.2.4, step 28): the
    /// ingest summarized them into the list's blocks, so the chooser reads them as it reads a
    /// struct field's — when the blocks name exactly the elements this chunk holds, which the
    /// cursor checks, and measures them otherwise.
    /// </remarks>
    private static int WriteListView(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, bool compress,
        ChunkStats stats = default)
    {
        CanonicalNode elements = arena.GetNode(node.ElementsIndex);
        byte[] metadata = ListViewBytes(
            (ulong)elements.Length, node.OffsetPType, node.SizePType);

        // Children in the reader's order: elements, offsets, sizes, then validity.
        Span<int> children = stackalloc int[4];
        children[0] = WriteChild(
            builder, arena, node.ElementsIndex, buffers, encodings, compress,
            ElementsOf(arena, node.ElementsIndex, compress, stats));
        children[1] = compress
            ? WriteIndexBuffer(
                builder, arena, node.DType.Arena, node.Offsets, node.OffsetPType, node.Length,
                buffers, encodings)
            : WritePrimitiveBuffer(builder, buffers, encodings, node.Offsets, node.OffsetPType);
        children[2] = compress
            ? WriteIndexBuffer(
                builder, arena, node.DType.Arena, node.Sizes, node.SizePType, node.Length,
                buffers, encodings)
            : WritePrimitiveBuffer(builder, buffers, encodings, node.Sizes, node.SizePType);

        int count = 3 + Validity(builder, arena, node, buffers, encodings, children[3..]);
        return Node(builder, encodings, "vortex.listview"u8, metadata, children[..count], []);
    }

    private static int WriteFixedSizeList(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, bool compress,
        ChunkStats stats = default)
    {
        Span<int> children = stackalloc int[2];
        children[0] = WriteChild(
            builder, arena, node.ElementsIndex, buffers, encodings, compress,
            ElementsOf(arena, node.ElementsIndex, compress, stats));
        int count = 1 + Validity(builder, arena, node, buffers, encodings, children[1..]);
        return Node(
            builder, encodings, "vortex.fixed_size_list"u8, default, children[..count], []);
    }

    /// <summary>
    /// Writes a variant column as <c>vortex.parquet.variant</c>: the unshredded metadata and value.
    /// </summary>
    /// <remarks>
    /// ONE SPELLING OUT, TWO IN. Both `vortex.variant` and `vortex.parquet.variant` decode to
    /// `Struct{metadata, value}`, and the information that distinguished them -- whether the value
    /// arrived as a typed scalar or as bytes -- is gone by then, because the canonical form is the
    /// bytes. So everything goes out as the parquet spelling, which is the one that stores exactly
    /// what this form holds. A file round-trips to a DIFFERENT ENCODING and the same values, which
    /// is what `vortex.varbin` already does (it reads as a VarBinView and writes as one).
    /// </remarks>
    private static int WriteParquetVariant(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, bool compress,
        ChunkStats stats = default)
    {
        if (node.FieldCount != 2)
        {
            throw new InvalidOperationException(
                $"A variant column is a two-field struct of {{metadata, value}}; this one has " +
                $"{node.FieldCount} fields.");
        }

        CanonicalNode valueNode = arena.GetNode(arena.GetNode(node.Index).GetFieldIndex(1));
        bool valueNullable = valueNode.DType.Nullability == Nullability.Nullable;

        // Children in the reader's order: validity when the array carries one, then metadata, then
        // value. The metadata says how many to expect.
        Span<int> children = stackalloc int[3];
        Span<int> validity = stackalloc int[1];
        int validityCount = Validity(builder, arena, node, buffers, encodings, validity);
        if (validityCount == 1)
        {
            children[0] = validity[0];
        }

        children[validityCount] = WriteChild(
            builder, arena, arena.GetNode(node.Index).GetFieldIndex(0), buffers, encodings, compress,
            stats.Field(0));
        children[validityCount + 1] = WriteChild(
            builder, arena, arena.GetNode(node.Index).GetFieldIndex(1), buffers, encodings, compress,
            stats.Field(1));

        byte[] metadata = ParquetVariantBytes(valueNullable);
        return Node(
            builder, encodings, "vortex.parquet.variant"u8, metadata,
            children[..(validityCount + 2)], []);
    }

    private static byte[] ParquetVariantBytes(bool valueNullable)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            ParquetVariantMetadata value = new ParquetVariantMetadata(
                hasValue: true, hasTypedValue: false, valueNullable: valueNullable);
            ParquetVariantMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static int WriteStruct(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, bool compress,
        ChunkStats stats = default)
    {
        // A struct puts its validity FIRST, unlike every other canonical kind
        // (vortex-array's slot_to_child: `nullable.then_some(0)`).
        int fields = node.FieldCount;
        int[] children = new int[fields + 1];
        Span<int> validity = stackalloc int[1];
        int validityCount = Validity(builder, arena, node, buffers, encodings, validity);

        for (int i = 0; i < fields; i++)
        {
            children[validityCount + i] = WriteChild(
                builder, arena, arena.GetNode(node.Index).GetFieldIndex(i), buffers, encodings,
                compress, stats.Field(i));
        }

        if (validityCount == 1)
        {
            children[0] = validity[0];
        }

        return Node(
            builder, encodings, "vortex.struct"u8, default,
            children.AsSpan(0, fields + validityCount), []);
    }

    private static int WriteExtension(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, bool compress,
        ChunkStats stats = default)
    {
        if (node.Kind != CanonicalKind.Extension)
        {
            throw new NotSupportedException($"A writer cannot serialize a {node.Kind} array.");
        }

        Span<int> children = stackalloc int[1];
        children[0] = WriteChild(
            builder, arena, node.StorageIndex, buffers, encodings, compress, stats.Field(0));
        return Node(builder, encodings, "vortex.ext"u8, default, children, []);
    }

    /// <summary>Writes a bare <c>vortex.primitive</c> node over an existing buffer.</summary>
    private static int WritePrimitiveBuffer(
        FlatBufferBuilder builder, List<PendingBuffer> buffers, EncodingDictionary encodings,
        VortexBuffer values, PType ptype)
    {
        int index = Buffer(buffers, values, Exponent(ptype.ByteWidth()));
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)index;
        return Node(builder, encodings, "vortex.primitive"u8, default, [], indices);
    }

    /// <summary>
    /// Emits the validity child, when one has to exist.
    /// </summary>
    /// <returns><c>1</c> when a child was written, <c>0</c> when the reader can derive the validity.</returns>
    private static int Validity(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings, Span<int> destination)
    {
        switch (node.Validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                // A nullable array with no validity child reads back as AllValid, which is exactly
                // what this is. Writing a bitmap of ones would be bytes for nothing.
                return 0;

            case ValidityKind.AllInvalid:
            {
                // NOT omittable: without the child the reader derives AllValid and every null row
                // comes back as a zero that claims to be present.
                //
                // Allocated rather than taken from a shared array of zeros or from the pool: an
                // all-null column is one node's worth of bitmap, and doubling this allocation
                // alongside the twelve metadata copies moves the widest write allocation axis by
                // 712 bytes of 709 152.
                int bytes = CanonicalSupport.BitmapByteCount(node.Length);
                byte[] zeros = new byte[Math.Max(bytes, 1)];
                destination[0] = BitmapNode(builder, buffers, encodings, zeros);
                return 1;
            }

            default:
                // Never compressed: a validity bitmap is index machinery, and it is small.
                destination[0] = WriteNode(
                    builder, arena, node.Validity.CanonicalNodeIndex, buffers, encodings, compress: false);
                return 1;
        }
    }

    private static int BitmapNode(
        FlatBufferBuilder builder, List<PendingBuffer> buffers, EncodingDictionary encodings,
        byte[] bits)
    {
        byte[] metadata = BoolBytes(0);
        buffers.Add(new PendingBuffer(bits, 0));
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)(buffers.Count - 1);
        return Node(builder, encodings, "vortex.bool"u8, metadata, [], indices);
    }

    private static int Node(
        FlatBufferBuilder builder,
        EncodingDictionary encodings,
        ReadOnlySpan<byte> idUtf8,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<int> children,
        ReadOnlySpan<ushort> bufferIndices) =>
        ArrayWriter.WriteNode(
            builder, encodings.Intern(idUtf8), metadata, children, bufferIndices, statsOffset: 0);

    /// <summary>
    /// Records a buffer the arena already owns, as a VIEW: the blob copies it once, not twice.
    /// </summary>
    private static int Buffer(List<PendingBuffer> buffers, VortexBuffer buffer, int alignmentExponent)
    {
        buffers.Add(new PendingBuffer(buffer, alignmentExponent));
        return buffers.Count - 1;
    }

    private static byte[] BoolBytes(uint bitOffset)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            BoolMetadata value = new BoolMetadata(bitOffset);
            BoolMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] DecimalBytes(DecimalStorageType storage)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            DecimalMetadata value = new DecimalMetadata(storage);
            DecimalMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] ListViewBytes(ulong elementsLength, PType offsets, PType sizes)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            ListViewMetadata value = new ListViewMetadata(elementsLength, offsets, sizes);
            ListViewMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The exponent of a power-of-two width, capped at the format's own ceiling.</summary>
    private static int Exponent(int width)
    {
        int exponent = System.Numerics.BitOperations.TrailingZeroCount((uint)Math.Max(width, 1));
        return Math.Min(exponent, VortexLimits.MaxAlignmentExponent);
    }

    private static long Align(long offset, int alignment) =>
        (offset + alignment - 1) & ~((long)alignment - 1);

    /// <summary>One serialized array, in a buffer the caller gives back.</summary>
    /// <remarks>
    /// <para>
    /// PERF-AUDIT-v2.md W-4. A blob was <c>new byte[total]</c>, allocated and dropped once per
    /// column per chunk — 20 of them and 305 kio for a 65 536-row rewrite, 950 and 5,1 Mio for a
    /// million-row <c>varbinview</c>. None of it survives the <c>WriteAsync</c> that consumes it,
    /// so none of it needs to be allocated.
    /// </para>
    /// <para>
    /// THE LENGTH IS SEPARATE FROM THE ARRAY, for the same reason
    /// <see cref="PendingBuffer.Length"/> is: a rented array is at least as long as asked for and
    /// usually longer, so <c>Bytes.Length</c> is not the number of bytes that belong in the file.
    /// Every consumer takes <see cref="Memory"/> or <see cref="Length"/> and never the array's own.
    /// </para>
    /// </remarks>
    internal readonly struct BlobLease : IDisposable
    {
        private readonly byte[]? _bytes;

        internal BlobLease(byte[] bytes, int length)
        {
            _bytes = bytes;
            Length = length;
        }

        /// <summary>Bytes of the rental that belong in the file.</summary>
        internal int Length { get; }

        /// <summary>The blob, exactly <see cref="Length"/> bytes of it.</summary>
        internal ReadOnlyMemory<byte> Memory =>
            _bytes is null ? default : _bytes.AsMemory(0, Length);

        /// <summary>Hands the rental back. Safe on a default lease, and safe twice.</summary>
        public void Dispose()
        {
            if (_bytes is not null)
            {
                ArrayPool<byte>.Shared.Return(_bytes);
            }
        }
    }

    /// <summary>One buffer waiting to be laid into the blob, and whether the pool owns it.</summary>
    /// <remarks>
    /// THE LENGTH IS SEPARATE FROM THE ARRAY because of <see cref="Rented"/>: an array from
    /// `ArrayPool` is at least as long as asked for and usually longer, so `Bytes.Length` stops
    /// being the number of bytes that belong in the file the moment one of these is pooled.
    /// PERF-AUDIT-v2.md W-5.
    /// </remarks>
    /// <summary>One buffer waiting to be placed in the blob: either bytes, or a view onto them.</summary>
    /// <remarks>
    /// A VIEW, WHEREVER THE BYTES ALREADY EXIST — docs/11-write-strategy.md §3.5, and
    /// WRITE-ARCHITECTURE.md §3.6 is the measurement that asks for it. A canonical column's values,
    /// views, data buffers and validity bits are all already in the arena, correctly laid out, and
    /// they were being copied into a fresh <c>byte[]</c> with <c>ToArray()</c> and then copied AGAIN
    /// into the blob. The array served nothing: the blob is assembled before <c>Write</c> returns,
    /// while the arena that owns the bytes is still alive by construction.
    /// <para>
    /// The bytes form stays for the buffers a scheme actually PRODUCES — a packed block, an FSST
    /// symbol table, a zstd frame — which have no home but their own array.
    /// </para>
    /// </remarks>
    private readonly struct PendingBuffer
    {
        private readonly byte[]? _bytes;
        private readonly VortexBuffer _view;

        internal PendingBuffer(byte[] bytes, int alignmentExponent)
            : this(bytes, bytes.Length, alignmentExponent, rented: false)
        {
        }

        internal PendingBuffer(byte[] bytes, int length, int alignmentExponent, bool rented)
        {
            _bytes = bytes;
            Length = length;
            AlignmentExponent = alignmentExponent;
            Rented = rented;
        }

        internal PendingBuffer(VortexBuffer view, int alignmentExponent)
        {
            _bytes = null;
            _view = view;
            Length = view.Length;
            AlignmentExponent = alignmentExponent;
            Rented = false;
        }

        /// <summary>The pooled array to hand back, or <see langword="null"/> for a view.</summary>
        internal byte[]? Bytes => _bytes;

        /// <summary>Bytes that belong in the blob.</summary>
        internal int Length { get; }

        internal int AlignmentExponent { get; }

        /// <summary>Whether <c>Write</c> must hand <see cref="Bytes"/> back to the pool.</summary>
        internal bool Rented { get; }

        /// <summary>What goes into the blob, whichever form it is in.</summary>
        internal ReadOnlySpan<byte> Span =>
            _bytes is not null ? _bytes.AsSpan(0, Length) : _view.Span[..Length];
    }
}
