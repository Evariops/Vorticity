using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
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
/// <remarks>
/// A segment is the buffers in order, each preceded by the padding that puts it on its alignment,
/// then the Array flatbuffer and its length as a trailing <c>u32</c>. Padding is counted from the
/// start of the segment rather than of the file, and the segment itself lands on a 64-byte file
/// offset, which is what carries a buffer's alignment through into the mapped file and makes the
/// read zero-copy; the padding in front of the flatbuffer is recorded nowhere, since the reader
/// locates the flatbuffer from the end, so it is only what 8-byte alignment needs.
/// <para>
/// Validity is the asymmetry to respect: a nullable array with no validity child reads back as
/// all-valid, so an all-valid bitmap may be omitted and an all-invalid one may not — omitting that
/// one turns an all-null column into zeros claiming to be present. Hence validity is switched on
/// its kind rather than tested for the presence of a bitmap.
/// </para>
/// </remarks>
internal static class ArrayBlobWriter
{
    private const int ViewSize = 16;

    /// <summary>
    /// Serializes the canonical node at <paramref name="nodeIndex"/>.
    /// </summary>
    /// <param name="blob">The workspace the blob is assembled in, the caller's for as long as it writes.</param>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The node to write.</param>
    /// <param name="encodings">The file's array-encoding dictionary, extended as needed.</param>
    /// <param name="compress">Whether to let ColumnCompressor pick an encoding for the column.</param>
    /// <param name="stats">
    /// What the ingest pass found over this chunk's rows, when the caller has it; a default summary
    /// leaves every candidate to walk the column itself.
    /// </param>
    /// <returns>The blob.</returns>
    /// <exception cref="NotSupportedException">The canonical form has no writer.</exception>
    internal static BlobLease Write(
        Workspace blob, CanonicalArena arena, int nodeIndex, EncodingDictionary encodings,
        bool compress = false, ChunkStats stats = default)
    {
        List<PendingBuffer> buffers = blob.Buffers;
        FlatBufferBuilder builder = blob.Builder;
        builder.Clear();
        try
        {
        // The compressed forms are chosen and materialized before the builder starts, because both
        // of them add canonical nodes to the arena -- the gathered values child -- and a
        // FlatBuffers table cannot be open while that happens.
        // A constant is expanded where it is read. The uncompressed path reads rows and nothing
        // else, so it expands here; the compressed one asks first what the element alone decides.
        int root = compress
            ? WriteCompressed(blob, arena, nodeIndex, encodings, stats)
            : WriteNode(blob, arena, Materialize(arena, nodeIndex), encodings, compress: false, stats);

        // The Buffer vector records what the layout below will actually write, so the paddings have
        // to be settled before the table that carries them is built.
        Span<BufferSpec> specs = blob.Specs(buffers.Count);
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

        // Rented, not allocated. The rental is longer than `total` and its tail holds whatever the
        // last renter wrote, so the padding between buffers has to be cleared rather than assumed
        // zero the way a fresh array allowed -- a file is byte-exact or it is wrong.
        //
        // The blob is assembled here rather than handed to the sink buffer by buffer and padding
        // run by padding run: the copy below is not what a write costs, while streaming it would
        // mean many small writes to a FileStream and a native view that is only memory through a
        // manager.
        int exact = checked((int)total);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(exact);
        long cursor = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            int padding = specs[i].Padding;
            if (padding > 0)
            {
                bytes.AsSpan((int)cursor, padding).Clear();
                cursor += padding;
            }

            buffers[i].CopyTo(bytes.AsSpan((int)cursor, buffers[i].Length));
            cursor += buffers[i].Length;
        }

        // The gap before the FlatBuffer is the other run of bytes nothing writes.
        if (flatStart > cursor)
        {
            bytes.AsSpan((int)cursor, (int)(flatStart - cursor)).Clear();
        }

        flatBuffer.CopyTo(bytes.AsSpan((int)flatStart));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan((int)(total - sizeof(uint))), (uint)flatBuffer.Length);

        return new BlobLease(bytes, exact);
        }
        finally
        {
            // After the copy into `bytes` and nowhere earlier. A pooled array handed back while the
            // blob still had to read it would be handed to the next renter and overwritten, and the
            // file would be wrong in a way no exception reports. In a `finally` so that a throw
            // between the rent and the copy does not quietly drain the pool either, and so that
            // the next blob starts from an empty list whatever this one did.
            foreach (PendingBuffer pending in buffers)
            {
                if (pending.Rented && pending.Bytes is byte[] rented)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            buffers.Clear();
        }
    }

    /// <summary>
    /// Writes the column under whichever scheme ColumnCompressor chose.
    /// </summary>
    /// <remarks>
    /// Compression is applied to the top of a column and nowhere else. A cascade -- dictionary
    /// codes that are themselves bit-packed, which is where the reference's ratios come from --
    /// would need integer kernels this build does not carry, and applying a scheme inside a struct
    /// or a list would change shapes the reader derives top-down.
    /// <para>
    /// <c>stats</c> is the chunk's ingest statistics when this node is one of the file's own
    /// columns, and a default summary for a child a scheme invented, which the chooser then walks
    /// for itself.
    /// </para>
    /// </remarks>
    private static int WriteCompressed(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        EncodingDictionary encodings,
        ChunkStats stats = default,
        Cascade cascade = default)
    {
        // A constant node is materialized here, before the compressor looks at it. Expanding it
        // lower down -- inside WriteNode -- hides the column from the compressor, and a constant
        // integer column then goes out flat instead of taking the encoding that reduces it to a
        // handful of bytes. Materializing above `Choose` keeps the writer on the same path it takes
        // for any other column, which is what makes the bytes identical rather than merely close.
        //
        // What a constant can be decided to be without being expanded is asked first, because the
        // expansion is most of what writing a constant column costs. A progression is the answer
        // for an integer constant and needs neither the rows nor a walk; every other constant falls
        // through to the line below and takes the ordinary route.
        BlockStats summary = stats.Stats;
        ColumnPlan plan = ColumnCompressor.ChooseConstant(
            arena, nodeIndex, encodings.Target, in summary, cascade);
        if (plan.Scheme == ColumnScheme.None)
        {
            nodeIndex = Materialize(arena, nodeIndex);
            plan = ColumnCompressor.Choose(
                arena, nodeIndex, encodings.Target, in summary, cascade, stats, blob);

            // A trial that came in under an exact plan's price has beaten the plan's layer, not
            // what the plan writes once its children take their own schemes. Size first is about
            // the bytes written, so the exact plan is written for measure only and the smaller of
            // the two is the one kept.
            if (plan.DisplacedExact && KeepsExact(blob, arena, nodeIndex, in plan, encodings, in summary, cascade, stats))
            {
                plan.Release();
                plan = ColumnCompressor.Choose(
                    arena, nodeIndex, encodings.Target, in summary, cascade, stats.Dry(), blob);
            }
        }

        // The bytes the plan actually produced, handed back to the column for its plan memory:
        // every buffer this node and its subtree appended, against what the chooser priced the plan
        // at. Buffer bytes rather than the blob's, because the blob is one per root field and a
        // struct's children are priced one by one; the framing they leave out is the same framing
        // chunk after chunk, which is all a tolerance needs.
        //
        // A dictionary is held to its own layer, not to its subtree: the plan priced codes plus
        // entries, and the children then take schemes of their own that can shrink them by orders
        // of magnitude. Judged by the subtree, a dictionary column's prediction would miss on every
        // chunk, the distinct table would never be trusted to serve, and each chunk would walk for
        // a dictionary the table had already built. `WriteChosen` reports the layer as built; the
        // children's bytes are the children's.
        List<PendingBuffer> buffers = blob.Buffers;
        int firstBuffer = buffers.Count;
        int written = WriteChosen(blob, arena, nodeIndex, in plan, encodings, stats, out long dictionaryLayer);
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

    /// <summary>
    /// Whether the plan every other profile writes for this column takes fewer bytes than the
    /// size-first <paramref name="trial"/> that displaced an exact plan: that plan is written into
    /// the measuring workspace and dropped, and set against the trial's buffers and the validity it
    /// writes beside them.
    /// </summary>
    private static bool KeepsExact(
        Workspace blob, CanonicalArena arena, int nodeIndex, in ColumnPlan trial, EncodingDictionary encodings,
        in BlockStats summary, Cascade cascade, ChunkStats stats)
    {
        (Workspace measure, EncodingDictionary measured) = blob.Measure(encodings.Target);
        try
        {
            ChunkStats dry = stats.Dry();
            ColumnPlan exact = ColumnCompressor.Choose(arena, nodeIndex, encodings.Target, in summary, cascade, dry, measure);
            WriteChosen(measure, arena, nodeIndex, in exact, measured, dry, out _);
            long exactBytes = QueuedBytes(measure);
            measure.Discard();

            Span<int> children = stackalloc int[1];
            Validity(measure, arena, arena.GetNode(nodeIndex), measured, children);
            return exactBytes < trial.PredictedBytes + QueuedBytes(measure);
        }
        finally
        {
            measure.Discard();
        }
    }

    private static long QueuedBytes(Workspace blob)
    {
        long bytes = 0;
        foreach (PendingBuffer pending in blob.Buffers)
        {
            bytes += pending.Length;
        }

        return bytes;
    }

    /// <summary>Writes the node the way <paramref name="plan"/> says to.</summary>
    // `dictionaryLayer`: for a dictionary, the bytes of the layer the plan priced -- codes at width
    // plus entries as built -- which is what plan memory holds it to; -1 for every other plan,
    // whose buffers are their own measure.
    private static int WriteChosen(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        in ColumnPlan plan,
        EncodingDictionary encodings,
        ChunkStats stats,
        out long dictionaryLayer)
    {
        dictionaryLayer = -1;
        if (plan.Scheme == ColumnScheme.None)
        {
            // Not the end of it: the column itself resisted every scheme, but a struct field or a
            // list's elements underneath it may not.
            return WriteNode(blob, arena, nodeIndex, encodings, compress: true, stats);
        }

        if (plan.Scheme == ColumnScheme.BitPacked)
        {
            return WriteBitPacked(blob, arena, nodeIndex, plan.BitPack.GetValueOrDefault(), encodings);
        }

        if (plan.Scheme == ColumnScheme.Fsst)
        {
            return WriteFsst(blob, arena, nodeIndex, plan.Fsst!, encodings);
        }

        if (plan.Scheme == ColumnScheme.Zstd)
        {
            return WriteZstd(blob, arena, nodeIndex, plan.Zstd.GetValueOrDefault(), encodings);
        }

        if (plan.Scheme == ColumnScheme.Alp)
        {
            AlpPlan alp = plan.Alp.GetValueOrDefault();
            return WriteAlp(blob, arena, nodeIndex, in alp, encodings);
        }

        if (plan.Scheme == ColumnScheme.AlpRd)
        {
            return WriteAlpRd(blob, arena, nodeIndex, plan.AlpRd!, encodings);
        }

        if (plan.Scheme == ColumnScheme.Sequence)
        {
            return WriteSequence(blob, arena, nodeIndex, plan.Sequence.GetValueOrDefault(), encodings);
        }

        // The values child comes from the table when the table chose the plan: the entries laid out
        // in code order from the keys it owns, no gather over the chunk, and for strings the key
        // heap as the data buffer. A run-end, or a dictionary the reference chooser
        // walked for, is still the original column gathered down to its representative rows -- a
        // dictionary of strings then shares the data buffers it came from. Either way the child is
        // itself a column, and a dictionary of long strings is exactly the shape FSST wants
        // underneath: compressed rather than written flat.
        int values = plan.Table is not null
            ? plan.Table.BuildValues(arena, arena.GetNode(nodeIndex), plan.Entries)
            : CanonicalFilter.Apply(arena, nodeIndex, plan.Gather);

        if (plan.Scheme == ColumnScheme.RunEnd)
        {
            return WriteRunEnd(blob, arena, nodeIndex, values, plan, encodings);
        }

        // Computed over the values child as built and the rows the codes index, with the formula
        // the plan was priced by: the number plan memory compares the prediction to.
        dictionaryLayer = ColumnCompressor.DictionaryLayerBytes(
            arena, arena.GetNode(values), arena.GetNode(nodeIndex).Length);
        return WriteDict(blob, arena, nodeIndex, values, plan, encodings);
    }

    /// <summary>
    /// Writes <c>fastlanes.bitpacked</c> under whichever transform the plan chose:
    /// <c>fastlanes.for</c>, which adds a reference back, or <c>vortex.zigzag</c>, which
    /// un-interleaves the sign bit.
    /// </summary>
    /// <remarks>
    /// The nesting is not arbitrary. Neither wrapper has a validity of its own -- both take their
    /// child's -- so the validity child belongs to the bitpacked node underneath, which is where
    /// this puts it, after the patch children, because the decoder derives the validity child's
    /// position from the metadata (2 with patches, 0 without) and never from the child count.
    ///
    /// The frame subtraction is on raw bits, unsigned, and that is what makes a signed column work:
    /// a column spanning -1000 to 1000 has a span of 2000 and needs 11 bits, which a signed
    /// per-value subtraction would have overflowed on. The reader's own kernel adds back with
    /// wrapping arithmetic for exactly this reason.
    ///
    /// The patch values are in the encoded domain -- after the transform, before the packing --
    /// because that is the buffer the decoder overwrites: it applies patches to the unpacked
    /// values and only then hands them to the wrapper.
    /// </remarks>
    private static int WriteBitPacked(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        in BitPackPlan plan,
        EncodingDictionary encodings)
    {
        int bitpacked = WritePacked(blob, arena, nodeIndex, in plan, encodings);
        Span<int> wrapper = stackalloc int[1];
        wrapper[0] = bitpacked;
        return plan.Transform == BitPackTransform.ZigZag
            ? Node(blob, encodings, "vortex.zigzag"u8, default, wrapper, [])
            : Node(
                blob, encodings, "fastlanes.for"u8,
                ReferenceBytes(blob, plan.Reference, arena.GetNode(nodeIndex).PType), wrapper, []);
    }

    /// <summary>
    /// The <c>fastlanes.bitpacked</c> node alone, with its patches and its validity under it: what
    /// <see cref="WriteBitPacked"/> wraps, and what an encoding whose children it sized itself
    /// writes bare.
    /// </summary>
    private static int WritePacked(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        in BitPackPlan plan,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        PType ptype = node.PType;
        int width = ptype.ByteWidth();
        int length = node.Length;

        // The packed bytes are the pool's and go into the blob as a rental, queued before they are
        // written so that the blob's `finally` hands them back whatever happens next. The patches
        // are rented at the count the chooser priced and handed back once their buffers hold them.
        int packedBytes = PackedBytes(length, plan.BitWidth);
        byte[] packed = ArrayPool<byte>.Shared.Rent(packedBytes);
        Span<ushort> packedBuffer = stackalloc ushort[1];
        packedBuffer[0] = (ushort)Add(blob, new PendingBuffer(packed, packedBytes, Exponent(width), rented: true));

        int exceptions = checked((int)plan.Exceptions);
        int[] patchIndices = exceptions == 0 ? [] : ArrayPool<int>.Shared.Rent(exceptions);
        ulong[] patchValues = exceptions == 0 ? [] : ArrayPool<ulong>.Shared.Rent(exceptions);
        Span<int> children = stackalloc int[3];
        int childCount = 0;
        PatchesMetadata patches = default;
        bool patched = exceptions > 0;
        try
        {
            Pack(
                arena, node, plan, ptype, length, packed.AsSpan(0, packedBytes),
                patchIndices.AsSpan(0, exceptions), patchValues.AsSpan(0, exceptions));
            if (patched)
            {
                PType indicesPType = FsstPlan.IndexPType(length);
                patches = PatchesMetadata.Create((ulong)exceptions, 0, indicesPType);
                children[0] = WriteIndexArray(blob, encodings, patchIndices.AsSpan(0, exceptions), indicesPType);
                children[1] = WriteRawPrimitive(
                    blob, encodings, LittleEndian(patchValues.AsSpan(0, exceptions), width));
                childCount = 2;
            }
        }
        finally
        {
            if (patched)
            {
                ArrayPool<int>.Shared.Return(patchIndices);
                ArrayPool<ulong>.Shared.Return(patchValues);
            }
        }

        childCount += Validity(blob, arena, node, encodings, children[childCount..]);

        return Node(
            blob, encodings, "fastlanes.bitpacked"u8,
            BitPackedBytes(blob, (uint)plan.BitWidth, patched, in patches),
            children[..childCount], packedBuffer);
    }

    /// <summary>
    /// The patch values, truncated to the element width and written little-endian, in a rental the
    /// blob hands back.
    /// </summary>
    private static PendingBuffer LittleEndian(ReadOnlySpan<ulong> values, int width)
    {
        int length = values.Length * width;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
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

        return new PendingBuffer(bytes, length, Exponent(width), rented: true);
    }

    /// <summary>
    /// The patches the pack finds for <paramref name="plan"/> over <paramref name="node"/>: the rows
    /// whose transformed value does not fit the width, and those values in the encoded domain.
    /// </summary>
    /// <remarks>
    /// Exposed so tests can pin the patches' positions and their domain — a patch value is the
    /// offset from the reference, not the raw value, and a null row is never a patch. The pack finds
    /// them as it encodes, so reaching them from outside means packing the column and discarding the
    /// bytes, which is what the assertions cost.
    /// </remarks>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">The integer column chunk.</param>
    /// <param name="plan">Its bit-packing plan.</param>
    internal static (int[] Indices, ulong[] Values) Patches(
        CanonicalArena arena, CanonicalNode node, in BitPackPlan plan)
    {
        int exceptions = checked((int)plan.Exceptions);
        int[] indices = new int[exceptions];
        ulong[] values = new ulong[exceptions];
        int bytes = PackedBytes(node.Length, plan.BitWidth);
        byte[] packed = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            Pack(arena, node, plan, node.PType, node.Length, packed.AsSpan(0, bytes), indices, values);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
        }

        return (indices, values);
    }

    /// <summary>The bytes <paramref name="length"/> rows take packed at <paramref name="bitWidth"/> bits: whole blocks.</summary>
    private static int PackedBytes(int length, int bitWidth)
    {
        int blocks = (length + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        return checked((int)((long)blocks * FastLanes.BlockByteLength(bitWidth)));
    }

    /// <summary>Applies the transform and bit-packs, one block at a time.</summary>
    /// <remarks>
    /// A patched row is packed like any other and its low bits are simply lost to the mask; the
    /// patch child puts the value back. Writing a zero there instead would cost a branch per row
    /// to produce bytes nothing reads.
    /// </remarks>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="node">The integer column chunk.</param>
    /// <param name="plan">Its plan: the transform, the width, the exceptions counted.</param>
    /// <param name="ptype">The column's element type.</param>
    /// <param name="length">Its rows.</param>
    /// <param name="destination">
    /// Exactly <see cref="PackedBytes"/> bytes. It is not cleared first, and need not be:
    /// <c>PackBlock</c> clears its own destination before it ORs into it, so every byte would be
    /// written twice and zeroed once for nothing -- and zeroing is a large share of what a
    /// bit-packed or dictionary-coded write spends.
    /// </param>
    /// <param name="patchIndices">The rows the exceptions are at, exactly as many as the plan counted.</param>
    /// <param name="patchValues">Their values in the encoded domain, as many.</param>
    private static void Pack(
        CanonicalArena arena, CanonicalNode node, in BitPackPlan plan, PType ptype, int length,
        Span<byte> destination, Span<int> patchIndices, Span<ulong> patchValues)
    {
        // The patches are found here rather than by a walk of their own: every row is transformed
        // below anyway, and an exception is a transformed value that does not fit the width. The
        // chooser counted them from its histogram, which sizes the spans; the loop fills them; a
        // count that disagrees is an exception rather than a short array padded with row 0 in
        // silence.
        int exceptions = patchIndices.Length;
        int found = 0;

        int bitWidth = plan.BitWidth;
        int blocks = (length + FastLanes.BlockSize - 1) / FastLanes.BlockSize;

        // Width zero still walks: a column packed at zero bits is a constant with exceptions, and
        // the exceptions are exactly what the loop below has to find. Only an empty column has
        // nothing to look at. The packed bytes are empty either way -- `BlockByteLength(0)` is 0 --
        // and the pack itself is skipped block by block below.
        if (length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> values = node.Values.Span;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int blockBytes = FastLanes.BlockByteLength(bitWidth);
        int elementBits = ptype.ByteWidth() * 8;

        // Both scratch buffers are rented once and live for the whole column rather than for one
        // block, and the narrow one is sized in bytes so a single rental serves whichever element
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

                // Only the tail of the last block needs clearing: the loop below writes every slot
                // of a full block, which is every block but the last, so clearing the whole block
                // each time would zero bytes that are about to be overwritten.
                if (count < FastLanes.BlockSize)
                {
                    wide[count..].Clear();
                }

                // Two methods, neither inlined: with both loops in one body the masked path's code
                // crowds the lanes' and slows the blocks that take them, for identical bytes.
                if (mask.AllValid)
                {
                    TransformValid(values, ptype, start, count, plan, elementBits, wide);
                }
                else
                {
                    TransformMasked(values, in mask, ptype, start, count, plan, elementBits, wide);
                }

                // The patches are gathered from `wide` in a loop of their own, and not from the
                // transform loop above with a branch per row: a per-row test there costs the pack
                // several times over, even when the branch is never taken. The block's transformed
                // values are still in cache, a second walk over them costs their compares and
                // nothing else, and it runs only when the chooser counted an exception at all. A
                // null row is zero, zero always fits, so a null is never a patch.
                if (patching)
                {
                    found = FindPatches(wide[..count], limit, start, patchIndices, patchValues, found);
                }

                if (blockBytes > 0)
                {
                    PackInto(wide, narrow, bitWidth, ptype, destination.Slice(b * blockBytes, blockBytes));
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
    }

    /// <summary>
    /// The transform of an all-valid block applied on the load: two 64-bit values or four 32-bit
    /// ones per step, widened into <paramref name="wide"/>.
    /// </summary>
    /// <remarks>
    /// The element width's own arithmetic is the mask. <see cref="BitPackPlan.Encode"/> computes
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
        ReadOnlySpan<byte> values, PType ptype, int start, int count, in BitPackPlan plan,
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
        ReadOnlySpan<byte> values, int done, int start, int count, in BitPackPlan plan,
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
        ReadOnlySpan<ulong> block, ulong limit, int start, Span<int> indices, Span<ulong> values, int found)
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
        ReadOnlySpan<ulong> block, ulong limit, int start, Span<int> indices, Span<ulong> values, int found)
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
        in BitPackPlan plan, int elementBits, Span<ulong> wide)
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
    /// Arithmetic rather than a switch, and the enum is what makes it legitimate: U8..U64 are 0..3
    /// and I8..I64 are 4..7, so the signed block sits exactly <c>I8</c> above the unsigned one --
    /// the same fact <see cref="PTypeExtensions.IsSignedInteger"/> is already written from. Naming
    /// the four mappings by hand would say nothing more and would leave every other PType to a
    /// <c>_</c> arm that reads as a fallthrough.
    /// </remarks>
    private static PType ToUnsigned(PType ptype) =>
        ptype.IsSignedInteger() ? (PType)(ptype - PType.I8) : ptype;

    private static ReadOnlySpan<byte> BitPackedBytes(
        Workspace blob, uint bitWidth, bool hasPatches, in PatchesMetadata patches)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        BitPackedMetadata value = hasPatches
            ? new BitPackedMetadata(bitWidth, 0, in patches)
            : new BitPackedMetadata(bitWidth, 0);
        BitPackedMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <summary>The frame-of-reference value: a bare protobuf ScalarValue, without its dtype.</summary>
    /// <remarks>
    /// An empty metadata decodes to a null reference, which the reference implementation rejects
    /// outright -- so this is one of the places where writing nothing is not the same as writing
    /// the default.
    /// </remarks>
    private static ReadOnlySpan<byte> ReferenceBytes(Workspace blob, ulong reference, PType ptype)
    {
        ScalarStore store = blob.Scalars();
        ScalarValue value = ptype.IsSignedInteger()
            ? store.Int64(unchecked((long)SignExtend(reference, ptype)))
            : store.UInt64(reference);
        ref ProtoWriter writer = ref blob.Metadata();
        ScalarProtobuf.WriteValue(ref writer, value);
        return writer.WrittenSpan;
    }

    /// <summary>Widens a narrow signed value's raw bits back to 64 bits.</summary>
    /// <remarks>
    /// The width is the answer, not the tag: shifting the value up to the top of a 64-bit word and
    /// back down arithmetically is what sign extension is, written once instead of once per narrow
    /// signed type. <c>I64</c> falls out of it as a shift of zero, and an unsigned type returns its
    /// bits unchanged.
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
    /// writing end: the row boundaries are on the decoded side. `codes_offsets` bounds each row's
    /// codes, but the reader decompresses the whole stream in one pass and then cuts the result
    /// with `uncompressed_lengths` - so the lengths are not a hint, they are the only thing that
    /// says where a value ends, and they must account for the decoded heap exactly.
    ///
    /// The validity child goes last and is present only for a nullable dtype, matching the
    /// decoder's `DecodeValidity(node, 2, ...)`.
    /// </remarks>
    private static int WriteFsst(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        FsstPlan plan,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        FsstSymbols table = plan.Table;
        int symbolBytes = table.Count * 8;
        byte[] symbols = ArrayPool<byte>.Shared.Rent(symbolBytes);
        int symbolBuffer = Add(blob, new PendingBuffer(symbols, symbolBytes, Exponent(8), rented: true));
        byte[] symbolLengths = ArrayPool<byte>.Shared.Rent(table.Count);
        int lengthBuffer = Add(blob, new PendingBuffer(symbolLengths, table.Count, 0, rented: true));
        for (int i = 0; i < table.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(symbols.AsSpan(i * 8), table.SymbolBits(i));
            symbolLengths[i] = table.SymbolLength(i);
        }

        // No copy: the code stream's rental passes from the plan to the blob, which hands it back
        // once laid out. The row tables are copied at their widths, and the plan gives them back.
        int codeBuffer = Add(blob, new PendingBuffer(plan.TakeCodes(), plan.CodeLength, 0, rented: true));
        PType lengthsPType;
        PType offsetsPType;
        int uncompressed;
        int offsets;
        try
        {
            lengthsPType = FsstPlan.IndexPType(FsstPlan.MaxOf(plan.Lengths));
            offsetsPType = FsstPlan.IndexPType(plan.CodeLength);
            uncompressed = WriteIndexArray(blob, encodings, plan.Lengths, lengthsPType);
            offsets = WriteIndexArray(blob, encodings, plan.Offsets, offsetsPType);
        }
        finally
        {
            plan.Release();
        }

        Span<int> children = stackalloc int[3];
        children[0] = uncompressed;
        children[1] = offsets;
        Span<int> validity = stackalloc int[1];
        int childCount = 2 + Validity(blob, arena, node, encodings, validity);
        if (childCount == 3)
        {
            children[2] = validity[0];
        }

        Span<ushort> indices = stackalloc ushort[3];
        indices[0] = (ushort)symbolBuffer;
        indices[1] = (ushort)lengthBuffer;
        indices[2] = (ushort)codeBuffer;

        return Node(
            blob, encodings, "vortex.fsst"u8, FsstBytes(blob, lengthsPType, offsetsPType),
            children[..childCount], indices);
    }

    /// <summary>
    /// Writes <c>vortex.zstd</c>: a buffer per frame, one optional validity child.
    /// </summary>
    /// <remarks>
    /// A frame per block of rows, so that a read of some rows decompresses the frames of their
    /// blocks and not the column: the decoder walks the frames' value counts to the first frame it
    /// needs and stops at the last.
    ///
    /// No dictionary buffer: `DictionarySize` is 0, which is what the decoder checks to decide
    /// whether a dictionary buffer is present at all.
    /// </remarks>
    private static int WriteZstd(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        in ZstdPlan plan,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);

        // The frames are the pool's, cut from one rental, and ownership moves here: `ZstdPlan` keeps
        // the buffer it compressed into rather than copying it out, so from this line the plan's
        // `Data` must not be read again. Only the last frame's buffer says it is rented, so that
        // `Write` hands the array back once, after the blob has taken every frame.
        int frames = plan.FrameCount;
        Span<ushort> stack = stackalloc ushort[64];
        using Scratch<ushort> scratch = new Scratch<ushort>(frames, stack);
        Span<ushort> indices = scratch.Span;
        for (int i = 0; i < frames; i++)
        {
            (int start, int length, _, _) = plan.FrameAt(i);
            indices[i] = (ushort)Add(blob, new PendingBuffer(plan.Data, start, length, 0, rented: i == frames - 1));
        }

        Span<int> children = stackalloc int[1];
        int childCount = Validity(blob, arena, node, encodings, children);

        int written = Node(
            blob, encodings, "vortex.zstd"u8, ZstdBytes(blob, in plan), children[..childCount], indices);
        plan.ReleaseFrames();
        return written;
    }

    private static ReadOnlySpan<byte> ZstdBytes(Workspace blob, in ZstdPlan plan)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        Span<ZstdFrameMetadata> stack = stackalloc ZstdFrameMetadata[16];
        using Scratch<ZstdFrameMetadata> scratch = new Scratch<ZstdFrameMetadata>(plan.FrameCount, stack);
        Span<ZstdFrameMetadata> frames = scratch.Span;
        for (int i = 0; i < frames.Length; i++)
        {
            (_, _, int uncompressed, int values) = plan.FrameAt(i);
            frames[i] = new ZstdFrameMetadata((ulong)uncompressed, (ulong)values);
        }

        ZstdMetadata value = new ZstdMetadata(dictionarySize: 0, frameCount: frames.Length);
        ZstdMetadata.Write(ref writer, in value, frames);
        return writer.WrittenSpan;
    }

    private static ReadOnlySpan<byte> FsstBytes(Workspace blob, PType lengthsPType, PType offsetsPType)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        FsstMetadata value = new FsstMetadata(lengthsPType, offsetsPType);
        FsstMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <summary>
    /// Writes <c>vortex.alp</c>: no buffers, and one, three or four children.
    /// </summary>
    /// <remarks>
    /// There is no validity child, and that is not an omission: the decoder takes the array's
    /// validity off the encoded child, so an ALP array's nullability rides on the integers, and
    /// writing a validity child here would leave the real one unread.
    ///
    /// The encoded child goes through the compressor like any other column, which is where the
    /// saving actually lands: ALP turns doubles into small integers, and frame-of-reference plus
    /// bit-packing turns small integers into few bits.
    /// </remarks>
    private static int WriteAlp(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        in AlpPlan plan,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;
        Span<int> children = stackalloc int[3];
        int childCount = 1;
        int patchCount = plan.PatchCount;
        PatchesMetadata patches = default;

        // The plan is a value this call consumes: each rental goes back once, here or through
        // the blob it is handed to, and the finally gives back whatever neither took.
        bool encodedBack = false;
        bool valuesHanded = false;
        try
        {
            // The encoded integers carry the float column's own validity, so they are added to the
            // arena as a node rather than written as a bare buffer.
            // The dtype arena is the column's own: a DType carries the arena it belongs to, and a
            // node whose dtype came from a different one would not compare equal downstream.
            DType encodedType = node.DType.Arena.Primitive(plan.EncodedPType, node.DType.Nullability);
            // Uninitialized: the CopyTo on the next line fills it whole.
            VortexBuffer encodedBuffer = arena.AllocateUninitialized(
                plan.Encoded.Length, plan.EncodedPType.ByteWidth(), out Span<byte> destination);
            plan.Encoded.CopyTo(destination);

            // The integers live in the arena from here on, so the pool can have its array back
            // before writing them rents anything. Every float column is priced with ALP, and one
            // that loses now costs the pool a rental instead of the heap a column.
            plan.ReturnEncoded();
            encodedBack = true;
            int encodedNode = arena.AddPrimitive(
                encodedType, rows, node.Validity, plan.EncodedPType, encodedBuffer);

            children[0] = WriteCompressed(blob, arena, encodedNode, encodings);
            if (patchCount > 0)
            {
                // The indices are copied at their width, the values go into the blob as they are:
                // their rental passes to it, and the blob hands it back once laid out.
                PType indicesPType = FsstPlan.IndexPType(rows);
                patches = PatchesMetadata.Create((ulong)patchCount, 0, indicesPType);
                children[1] = WriteIndexArray(blob, encodings, plan.PatchIndices, indicesPType);
                (byte[] values, int length) = plan.PatchValueRental;
                valuesHanded = true;
                children[2] = WriteRawPrimitive(
                    blob, encodings,
                    new PendingBuffer(values, length, Exponent(node.DType.PType.ByteWidth()), rented: true));
                childCount = 3;
            }
        }
        finally
        {
            plan.Release(encodedBack, valuesHanded);
        }

        return Node(
            blob, encodings, "vortex.alp"u8,
            AlpBytes(blob, plan.ExponentE, plan.ExponentF, patchCount > 0, patches),
            children[..childCount], []);
    }

    private static ReadOnlySpan<byte> AlpBytes(
        Workspace blob, byte e, byte f, bool hasPatches, in PatchesMetadata patches)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        AlpMetadata value = hasPatches
            ? new AlpMetadata(e, f, in patches)
            : new AlpMetadata(e, f);
        AlpMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <summary>
    /// Writes <c>vortex.alprd</c>: no buffers, and the left parts, the right parts and, when some
    /// high bits missed the dictionary, the patch indices and values.
    /// </summary>
    /// <remarks>
    /// The shape is the one both readers take, and the one the reference writes. The left parts are
    /// the codes, <c>u16</c> as the reference reads them, and they carry the column's validity,
    /// which is where a reader finds it; the right parts are the low bits at the float's own width,
    /// never null. Both are bare <c>fastlanes.bitpacked</c> at the widths the cut gave them: the
    /// codes need the bits of the dictionary's length and the right parts the bits below the cut,
    /// which the plan established, so running them through the chooser would only rediscover that
    /// at the price of a dictionary probe over every right part. A patch replaces a row's high bits
    /// outright, so its values are raw <c>u16</c> patterns, not codes.
    /// </remarks>
    private static int WriteAlpRd(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        AlpRdPlan plan,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;
        DTypeArena types = node.DType.Arena;
        int width = plan.Width;
        PType rightPType = width == sizeof(ulong) ? PType.U64 : PType.U32;

        // Uninitialized: each CopyTo fills its buffer whole.
        VortexBuffer leftBuffer = arena.AllocateUninitialized(
            rows * sizeof(ushort), sizeof(ushort), out Span<byte> leftBytes);
        MemoryMarshal.AsBytes(plan.Codes).CopyTo(leftBytes);
        VortexBuffer rightBuffer = arena.AllocateUninitialized(rows * width, width, out Span<byte> rightBytes);
        plan.Right.CopyTo(rightBytes);

        // The parts live in the arena from here on, so the pool can have them back before writing
        // them rents anything.
        plan.ReleaseParts();
        int leftNode = arena.AddPrimitive(
            types.Primitive(PType.U16, node.DType.Nullability), rows, node.Validity, PType.U16, leftBuffer);
        int rightNode = arena.AddPrimitive(
            types.Primitive(rightPType, Nullability.NonNullable), rows, Arrays.Validity.NonNullable,
            rightPType, rightBuffer);

        Span<int> children = stackalloc int[4];
        int childCount = 2;
        int exceptions = plan.ExceptionCount;
        PatchesMetadata patches = default;
        try
        {
            BitPackPlan codes = BitPackPlan.Fitting(AlpRdPlan.LeftBitWidth(plan.DictionaryLength));
            BitPackPlan low = BitPackPlan.Fitting(plan.RightBitWidth);
            children[0] = WritePacked(blob, arena, leftNode, in codes, encodings);
            children[1] = WritePacked(blob, arena, rightNode, in low, encodings);
            if (exceptions > 0)
            {
                PType indicesPType = FsstPlan.IndexPType(rows);
                patches = PatchesMetadata.Create((ulong)exceptions, 0, indicesPType);
                children[2] = WriteIndexArray(blob, encodings, plan.ExceptionRows, indicesPType);
                (byte[] values, int length) = plan.TakeExceptionValues();
                children[3] = WriteRawPrimitive(
                    blob, encodings, new PendingBuffer(values, length, Exponent(sizeof(ushort)), rented: true));
                childCount = 4;
            }
        }
        finally
        {
            plan.Release();
        }

        return Node(
            blob, encodings, "vortex.alprd"u8, AlpRdBytes(blob, plan, exceptions > 0, in patches),
            children[..childCount], []);
    }

    private static ReadOnlySpan<byte> AlpRdBytes(
        Workspace blob, AlpRdPlan plan, bool hasPatches, in PatchesMetadata patches)
    {
        ReadOnlySpan<ushort> patterns = plan.Dictionary;
        Span<uint> dictionary = stackalloc uint[AlpRdPlan.MaxDictionarySize];
        for (int i = 0; i < patterns.Length; i++)
        {
            dictionary[i] = patterns[i];
        }

        PatchesMetadata? optional = hasPatches ? patches : null;
        AlpRdMetadata value = new AlpRdMetadata(
            (uint)plan.RightBitWidth, (uint)patterns.Length, patterns.Length, PType.U16, in optional);
        ref ProtoWriter writer = ref blob.Metadata();
        AlpRdMetadata.Write(ref writer, in value, dictionary[..patterns.Length]);
        return writer.WrittenSpan;
    }

    /// <summary>
    /// Writes <c>vortex.sequence</c>: no children, no buffers, the whole column in the metadata.
    /// </summary>
    /// <remarks>
    /// The one encoding here that replaces a node rather than wrapping one, so it has no overhead
    /// to weigh against: the primitive node and its buffer both disappear.
    ///
    /// There is no validity child because there is nowhere to put one - the encoding has neither
    /// children nor buffers - which is why the plan refuses any column with a null. A nullable
    /// dtype still round-trips: the decoder derives AllValid from the nullability, which is what
    /// the column had.
    /// </remarks>
    private static int WriteSequence(
        Workspace blob,
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
        ref ProtoWriter writer = ref blob.Metadata();
        WriteSequenceMetadata(ref writer, blob.Scalars(), plan, ptype);
        return Node(blob, encodings, "vortex.sequence"u8, writer.WrittenSpan, [], []);
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
    internal static byte[] SequenceMetadataBytesForTests(SequencePlan plan, PType ptype)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            WriteSequenceMetadata(ref writer, new ScalarStore(), plan, ptype);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteSequenceMetadata(
        ref ProtoWriter writer, ScalarStore store, SequencePlan plan, PType ptype)
    {
        // Both fields are bare ScalarValues, and the base is interpreted against the array's own
        // dtype - so its signedness must match the column's, exactly as the frame of reference's
        // does. The multiplier's does not: the wire preserves the step's own signedness, and the
        // decoder reads its physical type from the proto tag rather than from the dtype.
        ScalarValue baseValue = ptype.IsSignedInteger()
            ? store.Int64(unchecked((long)plan.BaseBits))
            : store.UInt64(plan.BaseBits);
        ScalarValue multiplier = plan.StepIsUnsigned
            ? store.UInt64((ulong)plan.Step)
            : store.Int64((long)plan.Step);
        SequenceMetadata.Write(ref writer, new SequenceMetadata(baseValue, multiplier));
    }

    /// <summary>Writes raw little-endian bytes as a non-nullable primitive node.</summary>
    /// <param name="blob">The workspace.</param>
    /// <param name="encodings">The file's encodings.</param>
    /// <param name="values">The bytes, aligned to their element width; a rental passes to the blob.</param>
    private static int WriteRawPrimitive(Workspace blob, EncodingDictionary encodings, PendingBuffer values)
    {
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)Add(blob, values);
        return Node(blob, encodings, "vortex.primitive"u8, default, [], indices);
    }

    private static int WriteRunEnd(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        int values,
        in ColumnPlan plan,
        EncodingDictionary encodings)
    {
        int length = arena.GetNode(nodeIndex).Length;
        PType endsPType = IndexPType(length);

        int ends = WriteIndexColumn(
            blob, arena, nodeIndex, plan.Codes, endsPType, encodings, Cascade.RunEndEnds());
        int valuesNode = WriteCompressed(blob, arena, values, encodings, cascade: Cascade.ValuesChild());

        Span<int> children = stackalloc int[2];
        children[0] = ends;
        children[1] = valuesNode;
        return Node(
            blob, encodings, "vortex.runend"u8, RunEndBytes(blob, endsPType, (ulong)plan.Codes.Length),
            children, []);
    }

    private static int WriteDict(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        int values,
        in ColumnPlan plan,
        EncodingDictionary encodings)
    {
        int entries = arena.GetNode(values).Length;
        PType codesPType = IndexPType(entries);

        // The codes are the table's own buffer when the table chose the plan -- one code per row,
        // written by the probe as the rows arrived, never copied -- and the plan's array when the
        // reference chooser walked for them.
        ReadOnlySpan<int> codeOfRow = plan.Table is not null
            ? plan.Table.Codes[..plan.Rows]
            : plan.Codes.AsSpan(0, plan.Rows);
        int codes = WriteIndexColumn(
            blob, arena, nodeIndex, codeOfRow, codesPType, encodings, Cascade.DictionaryCodes(entries));

        // The codes are in the arena now, narrowed to the width the file carries, so a rental the
        // chooser handed over goes back to the pool rather than becoming a row vector of garbage.
        if (plan.CodesRented)
        {
            ArrayPool<int>.Shared.Return(plan.Codes);
        }
        int valuesNode = WriteCompressed(blob, arena, values, encodings, cascade: Cascade.ValuesChild());

        Span<int> children = stackalloc int[2];
        children[0] = codes;
        children[1] = valuesNode;

        // is_nullable_codes = false: a null row is a code pointing at a null dictionary entry, not
        // a null code. Nullness is part of the value the compressor deduplicated, so at most one
        // entry is null and every row still has a code.
        return Node(
            blob, encodings, "vortex.dict"u8, DictBytes(blob, (uint)entries, codesPType), children, []);
    }

    /// <summary>The narrowest unsigned type that indexes <paramref name="count"/> values.</summary>
    private static PType IndexPType(int count) => count switch
    {
        <= byte.MaxValue => PType.U8,
        <= ushort.MaxValue => PType.U16,
        _ => PType.U32,
    };

    /// <summary>
    /// Writes a column of indices - dictionary codes, run ends - through the compressor.
    /// </summary>
    /// <remarks>
    /// Codes are the one part of a dictionary that costs a byte per row rather than per distinct
    /// value, and they are the most bit-packable data in the file by construction - non-negative,
    /// dense, and bounded by the entry count. A column with a thousand-odd distinct values carries
    /// u16 codes and needs eleven bits.
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
        Workspace blob, CanonicalArena arena, int parentIndex, ReadOnlySpan<int> values,
        PType ptype, EncodingDictionary encodings, Cascade cascade = default)
    {
        int width = ptype.ByteWidth();

        // Uninitialized: `WriteIndices` writes every byte of it.
        VortexBuffer buffer = arena.AllocateUninitialized(
            values.Length * width, width, out Span<byte> destination);
        WriteIndices(values, width, destination);

        return WriteIndexBuffer(
            blob, arena, arena.GetNode(parentIndex).DType.Arena, buffer, ptype, values.Length,
            encodings, cascade);
    }

    /// <summary>
    /// Writes an index buffer that already exists - a varbin's offsets, a list's offsets or
    /// sizes - through the compressor, without copying it.
    /// </summary>
    /// <remarks>
    /// Offsets and sizes are index machinery, but they are not small: a list carries one of each
    /// per row, which between them can outweigh elements that are a fraction of a word. They are
    /// also the most compressible data in the file - offsets are monotone by construction, and a
    /// list of fixed-width rows has offsets that are an exact arithmetic progression and sizes that
    /// are constant, which is to say both are `vortex.sequence` and cost nothing at all.
    ///
    /// Validity bitmaps are still written raw. They genuinely are small - one bit per row - and
    /// they are the one child the compressor has no scheme for.
    /// </remarks>
    private static int WriteIndexBuffer(
        Workspace blob, CanonicalArena arena, DTypeArena types, VortexBuffer values,
        PType ptype, int count, EncodingDictionary encodings, Cascade cascade = default)
    {
        // The dtype arena is the column's own: a DType carries the arena it belongs to, and a node
        // whose dtype came from a different one would not compare equal downstream.
        int node = arena.AddPrimitive(
            types.Primitive(ptype, Nullability.NonNullable), count, Arrays.Validity.NonNullable,
            ptype, values);
        return WriteCompressed(blob, arena, node, encodings, cascade: cascade);
    }

    /// <summary>Writes an index per element, filling every byte of <paramref name="destination"/>.</summary>
    /// <remarks>
    /// The 8-byte case is not dead code, it is the eligibility argument. Its callers hand widths of
    /// 1, 2 or 4 -- <c>IndexPType</c> tops out at <c>u32</c>, and the varbin path refuses a heap
    /// above <c>int.MaxValue</c> before it picks a type -- but this writes into buffers from
    /// <c>CanonicalArena.AllocateUninitialized</c>, whose contract is that every byte is provably
    /// written. A <c>default</c> arm writing four bytes into an eight-byte slot would put pooled
    /// bytes from another file into a column the day a caller passes <c>u64</c>.
    /// <para>
    /// The width switch is inside the loop and it does not matter: the index arrays are short --
    /// one entry a patch, one a dictionary entry -- so what would be a per-row switch elsewhere is
    /// a per-entry one here.
    /// </para>
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

    /// <summary>Writes indices at <paramref name="ptype"/>'s width as a raw primitive node, in a rental the blob hands back.</summary>
    private static int WriteIndexArray(
        Workspace blob, EncodingDictionary encodings, ReadOnlySpan<int> values, PType ptype)
    {
        int width = ptype.ByteWidth();
        int length = values.Length * width;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)Add(blob, new PendingBuffer(bytes, length, Exponent(width), rented: true));

        // Every byte of the slice, which is what makes the rental's leftovers safe to leave.
        WriteIndices(values, width, bytes.AsSpan(0, length));
        return Node(blob, encodings, "vortex.primitive"u8, default, [], indices);
    }

    private static ReadOnlySpan<byte> RunEndBytes(Workspace blob, PType endsPType, ulong runCount)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        RunEndMetadata value = new RunEndMetadata(endsPType, runCount, 0);
        RunEndMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    private static ReadOnlySpan<byte> DictBytes(Workspace blob, uint valuesLength, PType codesPType)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        DictMetadata value = new DictMetadata(valuesLength, codesPType, false, null);
        DictMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <remarks>
    /// <paramref name="compress"/> says whether the children that are columns in their own right -
    /// a list's elements, a fixed-size list's elements, a struct's fields, an extension's storage -
    /// may be compressed. Validity bitmaps and a list's offsets and sizes never are: they are index
    /// machinery, and they are small.
    /// </remarks>
    private static int WriteNode(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        EncodingDictionary encodings,
        bool compress,
        ChunkStats stats = default)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);

        // Exhaustive by construction, and this is the site where it matters most. Every kind is
        // named and the `_` arm throws: a `default` arm that fell through to one of the encodings
        // would write a new kind as the wrong array and produce a coherent file, which the
        // byte-exact size tests cannot catch because the bytes agree with themselves. The analyzer
        // that demands every named kind is an error here, so a missing one fails the build.
        return node.Kind switch
        {
            CanonicalKind.Null => Node(blob, encodings, "vortex.null"u8, default, [], []),
            CanonicalKind.Bool => WriteBool(blob, arena, node, encodings),
            CanonicalKind.Primitive => WritePrimitive(blob, arena, node, encodings),
            CanonicalKind.Decimal => WriteDecimal(blob, arena, node, encodings),
            CanonicalKind.VarBinView => WriteVarBinView(blob, arena, node, encodings),

            // A map node is a ListView wearing the map dtype, so the kind alone does not say which
            // id to write. Emitting `vortex.listview` under a map schema produces a file this
            // reader refuses, correctly: the encoding yields a List dtype where a Map was asked
            // for.
            CanonicalKind.ListView => node.DType.Kind == DTypeKind.Map
                ? WriteMap(blob, arena, node, encodings, compress, stats)
                : WriteListView(blob, arena, node, encodings, compress, stats),

            CanonicalKind.FixedSizeList =>
                WriteFixedSizeList(blob, arena, node, encodings, compress, stats),

            // The dtype decides, as it does for a map above: a Struct wearing a variant dtype is a
            // variant column in this library's canonical form, and writing it as `vortex.struct`
            // would produce a file whose array says "two fields" and whose schema says "variant" --
            // which every reader, this one included, refuses.
            CanonicalKind.Struct => node.DType.Kind == DTypeKind.Variant
                ? WriteParquetVariant(blob, arena, node, encodings, compress, stats)
                : WriteStruct(blob, arena, node, encodings, compress, stats),

            CanonicalKind.Extension =>
                WriteExtension(blob, arena, node, encodings, compress, stats),
            // Materialized above, before the compressor (see WriteCompressed). Reaching it here
            // would mean a constant arrived by a path that skips `Choose`, and the bytes would not
            // be the ones the corpus was written with.
            CanonicalKind.Constant => throw new UnreachableException(
                "A constant node reached WriteNode; it is materialized in WriteCompressed."),

            // A batch delivered encoded carries these, and the writer decodes them at its door; one
            // met here is decoded the same way, so the file holds the canonical array whatever path
            // the node took.
            CanonicalKind.Dictionary or CanonicalKind.RunEnd => WriteNode(
                blob, arena, arena.MaterializeEncoded(nodeIndex), encodings, compress, stats),
            _ => throw new UnreachableException($"CanonicalKind {(byte)node.Kind} is not defined."),
        };
    }


    /// <summary>
    /// Expands a constant, dictionary or run-end node back to its materialized form; anything else
    /// unchanged.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The node about to be compressed and written.</param>
    /// <returns>A node index the rest of the writer already knows how to handle.</returns>
    /// <remarks>
    /// Expanded rather than emitted as `vortex.constant`. Writing the constant encoding on the wire
    /// would make the file smaller, but that is a change of size, while the constant form exists to
    /// spare the memory a reader holds; folding the two together is how a regression hides behind
    /// an improvement.
    /// <para>
    /// So the element is expanded back to the form the writer already emits, and the file does not
    /// depend on how the read stored the column: the canonicalizer keeps the element and the writer
    /// tiles it to the buffer a primitive would have arrived in.
    /// </para>
    /// <para>
    /// The expansion itself is the arena's, not a second copy here. Tiling the element at this end
    /// means choosing a buffer alignment, and the element's own length is not one: it is a power of
    /// two for a primitive and anything at all for a string, so the writer would refuse its own
    /// file. One expansion, in one place, keeps the reader's form and the writer's from drifting
    /// apart.
    /// </para>
    /// </remarks>
    internal static int Materialize(CanonicalArena arena, int nodeIndex) =>
        arena.GetNode(nodeIndex).Kind == CanonicalKind.Constant
            ? arena.MaterializeConstant(nodeIndex)
            : arena.Decoded(nodeIndex);

    /// <remarks>Writes a child that is a column in its own right, compressing it when asked.</remarks>
    private static int WriteChild(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        EncodingDictionary encodings,
        bool compress,
        ChunkStats stats = default) =>
        compress
            ? WriteCompressed(blob, arena, nodeIndex, encodings, stats)
            : WriteNode(blob, arena, nodeIndex, encodings, compress: false, stats);

    private static int WriteBool(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings)
    {
        int bits = Buffer(blob, node.Bits, alignmentExponent: 0);
        Span<int> children = stackalloc int[1];
        int count = Validity(blob, arena, node, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)bits;

        // The bit offset rides in the metadata, so a bitmap sliced off a byte boundary is written
        // without re-shifting it.
        return Node(
            blob, encodings, "vortex.bool"u8, BoolBytes(blob, (uint)node.BitOffset),
            children[..count], bufferIndices);
    }

    private static int WritePrimitive(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings)
    {
        int width = node.PType.ByteWidth();
        int values = Buffer(blob, node.Values, Exponent(width));

        Span<int> children = stackalloc int[1];
        int count = Validity(blob, arena, node, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)values;
        return Node(blob, encodings, "vortex.primitive"u8, default, children[..count], bufferIndices);
    }

    private static int WriteDecimal(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings)
    {
        int width = DecimalStorage.ByteWidth(node.Storage);
        int values = Buffer(blob, node.Values, Exponent(width));

        Span<int> children = stackalloc int[1];
        int count = Validity(blob, arena, node, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)values;
        return Node(
            blob, encodings, "vortex.decimal"u8, DecimalBytes(blob, node.Storage),
            children[..count], bufferIndices);
    }

    /// <remarks>
    /// Chooses between the two serializations of a binary column, which is a size decision and not
    /// a compression one.
    ///
    /// A canonical VarBinView costs 16 bytes of view per row whatever the values are; `vortex.varbin`
    /// costs one offset per row plus a contiguous heap, and inlines nothing. Four-byte offsets beat
    /// sixteen-byte views by twelve bytes a row, and inlining can save at most the twelve bytes a
    /// value of that length would have occupied in the heap - so varbin is never worse than
    /// varbinview and is usually much better. The reference reaches the same conclusion: every
    /// plain binary and utf8 column in the corpus is written `vortex.varbin`.
    ///
    /// What it costs here is a copy: the view form can hand the existing data buffers straight to the
    /// writer, and this has to gather the values into one heap. That is paid once at write time for
    /// a saving every reader keeps.
    /// </remarks>
    private static int WriteVarBinView(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings)
    {
        int dataCount = node.DataBufferCount;
        long viewForm = ((long)node.Length * ViewSize) + DataBufferBytes(node, dataCount);
        if (TryWriteVarBin(blob, arena, node, encodings, viewForm, out int varbin))
        {
            return varbin;
        }

        // Data buffers first, views last: the reader reaches for views at index `dataBufferCount`.
        Span<ushort> stack = stackalloc ushort[16];
        using Scratch<ushort> indices = new Scratch<ushort>(dataCount + 1, stack);
        for (int i = 0; i < dataCount; i++)
        {
            indices.Span[i] = (ushort)Buffer(blob, node.GetDataBuffer(i), alignmentExponent: 0);
        }

        indices.Span[dataCount] = (ushort)Buffer(blob, node.Views, Exponent(ViewSize));

        Span<int> children = stackalloc int[1];
        int count = Validity(blob, arena, node, encodings, children);
        return Node(blob, encodings, "vortex.varbinview"u8, default, children[..count], indices.Span);
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
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        long viewForm, out int result)
    {
        result = 0;
        int rows = node.Length;
        ValidityReader mask = ValidityReader.Of(arena, node.Validity);
        ViewValues values = new ViewValues(node);

        long heapBytes = 0;
        for (int i = 0; i < rows; i++)
        {
            if (mask.IsValid(i))
            {
                heapBytes += values.At(i).Length;
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

        // Rented, and written once without being cleared first. `heapBytes` is the sum of the valid
        // values' lengths, so the loop below fills exactly that many bytes of the heap, which goes
        // into the blob as a rental queued before the loop so that the blob's `finally` hands it
        // back. The offsets are copied into the arena at their own width and go back at once.
        int heapLength = (int)heapBytes;
        byte[] heap = ArrayPool<byte>.Shared.Rent(heapLength);
        int heapBuffer = Add(blob, new PendingBuffer(heap, heapLength, 0, rented: true));
        int width = offsetsPType.ByteWidth();
        VortexBuffer offsetBuffer = arena.AllocateUninitialized(
            (rows + 1) * width, width, out Span<byte> offsetBytes);
        int[] offsets = ArrayPool<int>.Shared.Rent(rows + 1);
        try
        {
            int written = 0;
            for (int i = 0; i < rows; i++)
            {
                offsets[i] = written;
                if (!mask.IsValid(i))
                {
                    // A null row is zero-length: offsets stay monotone and the reader never looks
                    // at the bytes, because validity already told it not to.
                    continue;
                }

                ReadOnlySpan<byte> value = values.At(i);
                value.CopyTo(heap.AsSpan(written));
                written += value.Length;
            }

            offsets[rows] = written;
            WriteIndices(offsets.AsSpan(0, rows + 1), width, offsetBytes);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(offsets);
        }

        Span<int> children = stackalloc int[2];
        children[0] = WriteIndexBuffer(
            blob, arena, node.DType.Arena, offsetBuffer, offsetsPType, rows + 1, encodings);
        int count = 1 + Validity(blob, arena, node, encodings, children[1..]);

        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)heapBuffer;
        result = Node(
            blob, encodings, "vortex.varbin"u8, VarBinBytes(blob, offsetsPType),
            children[..count], indices);
        return true;
    }

    private static ReadOnlySpan<byte> VarBinBytes(Workspace blob, PType offsetsPType)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        VarBinMetadata value = new VarBinMetadata(offsetsPType);
        VarBinMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <summary>Writes <c>vortex.map</c>: empty metadata, no buffers, one <c>vortex.listview</c> child.</summary>
    /// <remarks>
    /// The inner listview carries the validity, which is where <c>MapDecoder</c>
    /// reads it from, so the pair is symmetric rather than merely compatible. Upstream requires the
    /// entries child to use the listview encoding specifically, so this wrapper is the only shape a
    /// conformant map can take.
    /// </remarks>
    private static int WriteMap(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default)
    {
        int entries = WriteListView(blob, arena, node, encodings, compress, stats);
        Span<int> children = stackalloc int[1];
        children[0] = entries;
        return Node(blob, encodings, "vortex.map"u8, default, children, []);
    }

    /// <summary>
    /// What a test installs to see every list chunk's elements beside the summary the chooser is
    /// handed for them: the arena, the elements node, and that summary, which is absent when the
    /// chooser has to walk the elements itself. It flows with the async write, as
    /// <c>ColumnCompressor.Differential</c> does, so two tests writing at once never see each
    /// other's chunks.
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
    /// The elements are a column with statistics of their own: the ingest summarized them into the
    /// list's blocks, so the chooser reads them as it reads a struct field's — when the blocks name
    /// exactly the elements this chunk holds, which the cursor checks — and walks them itself
    /// otherwise.
    /// </remarks>
    private static int WriteListView(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default)
    {
        ulong elementsLength = (ulong)arena.GetNode(node.ElementsIndex).Length;

        // Children in the reader's order: elements, offsets, sizes, then validity.
        Span<int> children = stackalloc int[4];
        children[0] = WriteChild(
            blob, arena, node.ElementsIndex, encodings, compress,
            ElementsOf(arena, node.ElementsIndex, compress, stats));
        children[1] = compress
            ? WriteIndexBuffer(
                blob, arena, node.DType.Arena, node.Offsets, node.OffsetPType, node.Length, encodings)
            : WritePrimitiveBuffer(blob, encodings, node.Offsets, node.OffsetPType);
        children[2] = compress
            ? WriteIndexBuffer(
                blob, arena, node.DType.Arena, node.Sizes, node.SizePType, node.Length, encodings)
            : WritePrimitiveBuffer(blob, encodings, node.Sizes, node.SizePType);

        int count = 3 + Validity(blob, arena, node, encodings, children[3..]);
        return Node(
            blob, encodings, "vortex.listview"u8,
            ListViewBytes(blob, elementsLength, node.OffsetPType, node.SizePType),
            children[..count], []);
    }

    private static int WriteFixedSizeList(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default)
    {
        Span<int> children = stackalloc int[2];
        children[0] = WriteChild(
            blob, arena, node.ElementsIndex, encodings, compress,
            ElementsOf(arena, node.ElementsIndex, compress, stats));
        int count = 1 + Validity(blob, arena, node, encodings, children[1..]);
        return Node(blob, encodings, "vortex.fixed_size_list"u8, default, children[..count], []);
    }

    /// <summary>
    /// Writes a variant column as <c>vortex.parquet.variant</c>: the unshredded metadata and value.
    /// </summary>
    /// <remarks>
    /// One spelling out, two in. Both `vortex.variant` and `vortex.parquet.variant` decode to
    /// `Struct{metadata, value}`, and the information that distinguished them -- whether the value
    /// arrived as a typed scalar or as bytes -- is gone by then, because the canonical form is the
    /// bytes. So everything goes out as the parquet spelling, which is the one that stores exactly
    /// what this form holds. A file round-trips to a different encoding and the same values, which
    /// is what `vortex.varbin` already does (it reads as a VarBinView and writes as one).
    /// </remarks>
    private static int WriteParquetVariant(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default)
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
        int validityCount = Validity(blob, arena, node, encodings, validity);
        if (validityCount == 1)
        {
            children[0] = validity[0];
        }

        children[validityCount] = WriteChild(
            blob, arena, arena.GetNode(node.Index).GetFieldIndex(0), encodings, compress,
            stats.Field(0));
        children[validityCount + 1] = WriteChild(
            blob, arena, arena.GetNode(node.Index).GetFieldIndex(1), encodings, compress,
            stats.Field(1));

        return Node(
            blob, encodings, "vortex.parquet.variant"u8, ParquetVariantBytes(blob, valueNullable),
            children[..(validityCount + 2)], []);
    }

    private static ReadOnlySpan<byte> ParquetVariantBytes(Workspace blob, bool valueNullable)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        ParquetVariantMetadata value = new ParquetVariantMetadata(
            hasValue: true, hasTypedValue: false, valueNullable: valueNullable);
        ParquetVariantMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    private static int WriteStruct(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default)
    {
        // A struct puts its validity first, unlike every other canonical kind; upstream's own
        // child-slot mapping does the same.
        int fields = node.FieldCount;
        Span<int> stack = stackalloc int[16];
        using Scratch<int> children = new Scratch<int>(fields + 1, stack);
        Span<int> validity = stackalloc int[1];
        int validityCount = Validity(blob, arena, node, encodings, validity);

        for (int i = 0; i < fields; i++)
        {
            children.Span[validityCount + i] = WriteChild(
                blob, arena, arena.GetNode(node.Index).GetFieldIndex(i), encodings, compress,
                stats.Field(i));
        }

        if (validityCount == 1)
        {
            children.Span[0] = validity[0];
        }

        return Node(
            blob, encodings, "vortex.struct"u8, default, children.Span[..(fields + validityCount)], []);
    }

    private static int WriteExtension(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default)
    {
        if (node.Kind != CanonicalKind.Extension)
        {
            throw new NotSupportedException($"A writer cannot serialize a {node.Kind} array.");
        }

        Span<int> children = stackalloc int[1];
        children[0] = WriteChild(blob, arena, node.StorageIndex, encodings, compress, stats.Field(0));
        return Node(blob, encodings, "vortex.ext"u8, default, children, []);
    }

    /// <summary>Writes a bare <c>vortex.primitive</c> node over an existing buffer.</summary>
    private static int WritePrimitiveBuffer(
        Workspace blob, EncodingDictionary encodings, VortexBuffer values, PType ptype)
    {
        int index = Buffer(blob, values, Exponent(ptype.ByteWidth()));
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)index;
        return Node(blob, encodings, "vortex.primitive"u8, default, [], indices);
    }

    /// <summary>
    /// Emits the validity child, when one has to exist.
    /// </summary>
    /// <returns><c>1</c> when a child was written, <c>0</c> when the reader can derive the validity.</returns>
    private static int Validity(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        Span<int> destination)
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
                // Not omittable: without the child the reader derives AllValid and every null row
                // comes back as a zero that claims to be present. The bitmap is a run of zeros,
                // which the blob writes as it writes padding, so no array ever holds it.
                int bytes = CanonicalSupport.BitmapByteCount(node.Length);
                Span<ushort> indices = stackalloc ushort[1];
                indices[0] = (ushort)Add(blob, PendingBuffer.Zeros(Math.Max(bytes, 1)));
                destination[0] = Node(blob, encodings, "vortex.bool"u8, BoolBytes(blob, 0), [], indices);
                return 1;
            }

            default:
                // Never compressed: a validity bitmap is index machinery, and it is small.
                destination[0] = WriteNode(
                    blob, arena, node.Validity.CanonicalNodeIndex, encodings, compress: false);
                return 1;
        }
    }

    private static int Node(
        Workspace blob,
        EncodingDictionary encodings,
        ReadOnlySpan<byte> idUtf8,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<int> children,
        ReadOnlySpan<ushort> bufferIndices) =>
        ArrayWriter.WriteNode(
            blob.Builder, encodings.Intern(idUtf8), metadata, children, bufferIndices, statsOffset: 0);

    /// <summary>Queues a buffer for the blob and returns its index among the blob's buffers.</summary>
    private static int Add(Workspace blob, PendingBuffer pending)
    {
        blob.Buffers.Add(pending);
        return blob.Buffers.Count - 1;
    }

    /// <summary>
    /// Records a buffer the arena already owns, as a view: the blob copies it once, not twice.
    /// </summary>
    private static int Buffer(Workspace blob, VortexBuffer buffer, int alignmentExponent) =>
        Add(blob, new PendingBuffer(buffer, alignmentExponent));

    private static ReadOnlySpan<byte> BoolBytes(Workspace blob, uint bitOffset)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        BoolMetadata value = new BoolMetadata(bitOffset);
        BoolMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    private static ReadOnlySpan<byte> DecimalBytes(Workspace blob, DecimalStorageType storage)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        DecimalMetadata value = new DecimalMetadata(storage);
        DecimalMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    private static ReadOnlySpan<byte> ListViewBytes(
        Workspace blob, ulong elementsLength, PType offsets, PType sizes)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        ListViewMetadata value = new ListViewMetadata(elementsLength, offsets, sizes);
        ListViewMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
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
    /// The blob is rented rather than allocated: there is one per column per chunk, and none of
    /// them survives the <c>WriteAsync</c> that consumes it, so none of them needs to be allocated.
    /// </para>
    /// <para>
    /// The length is separate from the array, for the same reason
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

    /// <summary>One buffer waiting to be placed in the blob: bytes, a view onto them, or zeros.</summary>
    /// <remarks>
    /// A view wherever the bytes already exist. A canonical column's values, views, data buffers
    /// and validity bits are all in the arena, correctly laid out, so copying them into a fresh
    /// <c>byte[]</c> only to copy that again into the blob would serve nothing: the blob is
    /// assembled before <c>Write</c> returns, while the arena that owns the bytes is still alive by
    /// construction.
    /// <para>
    /// The bytes form stays for the buffers a scheme actually produces — a packed block, an FSST
    /// symbol table, a zstd frame — which have no home but their own array. A run of zeros has no
    /// home at all: the blob clears it in place, as it clears padding.
    /// </para>
    /// <para>
    /// The length is separate from the array because of <see cref="Rented"/>: an array from
    /// <c>ArrayPool</c> is at least as long as asked for and usually longer, so <c>Bytes.Length</c>
    /// stops being the number of bytes that belong in the file the moment one of these is pooled.
    /// </para>
    /// </remarks>
    internal readonly struct PendingBuffer
    {
        private readonly byte[]? _bytes;
        private readonly VortexBuffer _view;
        private readonly bool _zeros;

        internal PendingBuffer(byte[] bytes, int alignmentExponent)
            : this(bytes, bytes.Length, alignmentExponent, rented: false)
        {
        }

        internal PendingBuffer(byte[] bytes, int length, int alignmentExponent, bool rented)
            : this(bytes, offset: 0, length, alignmentExponent, rented)
        {
        }

        /// <summary>
        /// <paramref name="length"/> bytes of <paramref name="bytes"/> from <paramref name="offset"/>:
        /// several buffers cut from one rental, of which exactly one says it is rented, so that the
        /// array goes back to the pool once, after the blob has taken them all.
        /// </summary>
        internal PendingBuffer(byte[] bytes, int offset, int length, int alignmentExponent, bool rented)
        {
            _bytes = bytes;
            Offset = offset;
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

        private PendingBuffer(int length)
        {
            _zeros = true;
            Length = length;
        }

        /// <summary><paramref name="length"/> zero bytes, byte-aligned.</summary>
        internal static PendingBuffer Zeros(int length) => new PendingBuffer(length);

        /// <summary>The pooled array to hand back, or <see langword="null"/> for a view or zeros.</summary>
        internal byte[]? Bytes => _bytes;

        /// <summary>Where in <see cref="Bytes"/> the buffer starts.</summary>
        internal int Offset { get; }

        /// <summary>Bytes that belong in the blob.</summary>
        internal int Length { get; }

        internal int AlignmentExponent { get; }

        /// <summary>Whether <c>Write</c> must hand <see cref="Bytes"/> back to the pool.</summary>
        internal bool Rented { get; }

        /// <summary>Writes the buffer into <paramref name="destination"/>, exactly <see cref="Length"/> bytes of it.</summary>
        internal void CopyTo(Span<byte> destination)
        {
            if (_zeros)
            {
                destination.Clear();
            }
            else if (_bytes is not null)
            {
                _bytes.AsSpan(Offset, Length).CopyTo(destination);
            }
            else
            {
                _view.Span[..Length].CopyTo(destination);
            }
        }
    }

    /// <summary>
    /// What a blob is assembled in, kept from one blob to the next by the writer that owns it: the
    /// FlatBuffer builder, the buffers waiting to be placed and their specs, the writer a node's
    /// metadata is encoded in before the builder copies it, and the store its scalars live in.
    /// </summary>
    /// <remarks>
    /// A blob is written synchronously from its first node to its last byte, and a writer writes
    /// one blob at a time, so a workspace per writer is never in two blobs at once. Each blob
    /// clears the builder before it starts and empties the buffer list in its own <c>finally</c>,
    /// so one that throws leaves the next nothing to trip on.
    /// </remarks>
    internal sealed class Workspace : IDisposable
    {
        private readonly List<PendingBuffer> _buffers = [];

        /// <summary>Two values at most, a base and a step: the store's own floor already holds them.</summary>
        private readonly ScalarStore _scalars = new ScalarStore(initialCapacity: 0);

        private BufferSpec[] _specs = [];
        private ProtoWriter _metadata;
        private ZstandardEncoder? _zstd;
        private Workspace? _measure;
        private EncodingDictionary? _measureEncodings;

        /// <summary>The builder, cleared by each blob as it starts.</summary>
        internal FlatBufferBuilder Builder { get; } = new FlatBufferBuilder();

        /// <summary>
        /// The encoder every zstd trial of the file compresses in, taken from the process's with
        /// the first and given back with the workspace: its native context is a megabyte, made once
        /// for many files rather than once per trial or per file.
        /// </summary>
        internal ZstandardEncoder Zstd => _zstd ??= ZstdEncoders.Rent();

        /// <summary>
        /// The rows a zstd frame holds the values of, the writer's block; 0 for one frame a column.
        /// A read of some rows then decompresses the frames of their blocks and not the column.
        /// </summary>
        internal int FrameRows { get; init; }

        /// <summary>The threads a column's zstd frames may be compressed on, the writer's degree; one by default.</summary>
        internal int Lanes { get; init; } = 1;

        private ZstdFrames? _frames;

        /// <summary>The frames' fan-out over <see cref="Lanes"/> threads, rented by the first column that uses it.</summary>
        internal ZstdFrames Frames => _frames ??= ZstdFrames.Rent(Lanes);

        /// <summary>The zstd trials whose frames were compressed across threads.</summary>
        internal int ColumnsAcross => _frames?.Columns ?? 0;

        /// <summary>The buffers the blob being written has queued, in order.</summary>
        internal List<PendingBuffer> Buffers => _buffers;

        /// <summary>The metadata writer, emptied for one node.</summary>
        /// <remarks>
        /// What it holds is valid until the next call, which is why each node's metadata is
        /// encoded as the argument of the <c>Node</c> call that copies it, after its children:
        /// a child encoded between the two would overwrite it.
        /// </remarks>
        internal ref ProtoWriter Metadata()
        {
            _metadata.Clear();
            return ref _metadata;
        }

        /// <summary>The scalar store, emptied for one node's values.</summary>
        internal ScalarStore Scalars()
        {
            _scalars.Clear();
            return _scalars;
        }

        /// <summary>Room for <paramref name="count"/> buffer specs.</summary>
        internal Span<BufferSpec> Specs(int count)
        {
            if (_specs.Length < count)
            {
                _specs = new BufferSpec[Math.Max(count, _specs.Length * 2)];
            }

            return _specs.AsSpan(0, count);
        }

        /// <summary>
        /// A second workspace, and an encoding table of its own, for a node written only to be
        /// measured: made at the first such write, which only size first asks for.
        /// </summary>
        /// <param name="target">The edition the measured node is written under.</param>
        internal (Workspace Blob, EncodingDictionary Encodings) Measure(VortexEdition target)
        {
            _measure ??= new Workspace { FrameRows = FrameRows, Lanes = Lanes };
            if (_measureEncodings is null || _measureEncodings.Target != target)
            {
                _measureEncodings = new EncodingDictionary(ComponentKind.Array, target);
            }

            return (_measure, _measureEncodings);
        }

        /// <summary>Drops what a blob queued without writing it: its rentals go back, its builder is cleared.</summary>
        internal void Discard()
        {
            foreach (PendingBuffer pending in _buffers)
            {
                if (pending.Rented && pending.Bytes is byte[] rented)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            _buffers.Clear();
            Builder.Clear();
        }

        /// <summary>Hands the builder's and the metadata writer's rentals back, and the zstd encoder and fan-out to the process's.</summary>
        public void Dispose()
        {
            Builder.Dispose();
            _metadata.Dispose();
            if (_zstd is { } zstd)
            {
                _zstd = null;
                ZstdEncoders.Return(zstd);
            }

            if (_frames is { } frames)
            {
                _frames = null;
                ZstdFrames.Return(frames);
            }

            _measure?.Dispose();
            _measure = null;
        }
    }
}
