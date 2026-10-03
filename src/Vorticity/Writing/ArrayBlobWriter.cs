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
using Vorticity.Zstd;

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
        Cascade cascade = default) =>
        WriteCompressed(blob, arena, nodeIndex, encodings, out _, stats, cascade);

    /// <summary>The same, saying which scheme the node was written with.</summary>
    private static int WriteCompressed(
        Workspace blob,
        CanonicalArena arena,
        int nodeIndex,
        EncodingDictionary encodings,
        out ColumnScheme written,
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
        written = plan.Scheme;
        int node = WriteChosen(blob, arena, nodeIndex, in plan, encodings, stats, out long dictionaryLayer);
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
        return node;
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
    // plus entries as built -- which is what plan memory holds it to; for FSST and OnPair, whose
    // row tables and codes then take schemes of their own, the bytes their plan priced; -1 for
    // every other plan, whose buffers are their own measure.
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
            dictionaryLayer = plan.PredictedBytes;
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

        if (plan.Scheme == ColumnScheme.DecimalByteParts)
        {
            return WriteDecimalParts(blob, arena, nodeIndex, plan.PartsPType, encodings);
        }

        if (plan.Scheme == ColumnScheme.Constant)
        {
            return WriteConstant(blob, arena, nodeIndex, plan.ConstantRow, encodings);
        }

        if (plan.Scheme == ColumnScheme.DateTimeParts)
        {
            return WriteDateTimeParts(blob, arena, nodeIndex, plan.DateTimeParts!, encodings);
        }

        if (plan.Scheme == ColumnScheme.Sparse)
        {
            return WriteSparse(blob, arena, nodeIndex, encodings);
        }

        if (plan.Scheme == ColumnScheme.Pco)
        {
            return WritePco(blob, arena, nodeIndex, plan.Pco!, encodings);
        }

        if (plan.Scheme == ColumnScheme.OnPair)
        {
            dictionaryLayer = plan.PredictedBytes;
            return WriteOnPair(blob, arena, nodeIndex, plan.OnPair!, encodings);
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
        //
        // Width zero still walks: a column packed at zero bits is a constant with exceptions, and
        // the exceptions are exactly what the loop has to find. Only an empty column has nothing to
        // look at. The packed bytes are empty either way -- `BlockByteLength(0)` is 0 -- and the
        // pack itself is skipped block by block.
        int exceptions = patchIndices.Length;
        if (length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> values = node.Values.Span;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int found = ptype.ByteWidth() switch
        {
            1 => Pack<byte>(values, in mask, in plan, length, destination, patchIndices, patchValues),
            2 => Pack<ushort>(values, in mask, in plan, length, destination, patchIndices, patchValues),
            4 => Pack<uint>(values, in mask, in plan, length, destination, patchIndices, patchValues),
            _ => Pack<ulong>(values, in mask, in plan, length, destination, patchIndices, patchValues),
        };

        if (found != exceptions)
        {
            // The count came from the histogram and the gather from the transform; they read the
            // same rows under the same map, so they agree or one of the two is wrong -- and a
            // short array of patches would otherwise put row 0 back into the file in silence.
            throw new InvalidOperationException(
                $"The width histogram counted {exceptions} values above {plan.BitWidth} bits and the " +
                $"pack found {found}.");
        }
    }

    /// <summary>
    /// The pack at the element's own width: each block transformed into the buffer
    /// <c>PackBlock</c> reads, its nulls cleared, its patches found, and packed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the element's own type throughout, which is the whole point. The transforms are the same
    /// numbers there: <see cref="BitPackPlan.Encode"/> computes in 64 bits and masks to the element
    /// width, and an element's own subtraction wraps at that width, its zigzag's shifts drop the
    /// same bits. So there is no block of 64-bit words to widen into, compare in and narrow back
    /// out of one value at a time: a u8 column's transform is 64 values a 512-bit vector, not one a
    /// row, and its patches are found 64 at a compare.
    /// </para>
    /// <para>
    /// A null row packs as zero, which always fits, so a null is never a patch: its value is not
    /// read back, and a stable zero compresses better than whatever the slot held.
    /// </para>
    /// </remarks>
    /// <returns>The patches found.</returns>
    private static int Pack<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, in BitPackPlan plan, int length,
        Span<byte> destination, Span<int> patchIndices, Span<ulong> patchValues)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes)[..length];
        int bitWidth = plan.BitWidth;
        int blockBytes = FastLanes.BlockByteLength(bitWidth);
        int blocks = (length + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        bool frame = plan.Transform == BitPackTransform.Frame;
        T reference = T.CreateTruncating(plan.Reference);

        // An exception is a transformed value above the largest the width holds; at the element's
        // own width nothing can be one, and the chooser then counted none.
        bool patching = patchIndices.Length > 0;
        T largest = patching ? T.CreateTruncating((1UL << bitWidth) - 1) : T.AllBitsSet;

        // One block of the element's own type, rented once for the column.
        T[] rented = ArrayPool<T>.Shared.Rent(FastLanes.BlockSize);
        int found = 0;
        try
        {
            Span<T> block = rented.AsSpan(0, FastLanes.BlockSize);
            for (int b = 0; b < blocks; b++)
            {
                int start = b * FastLanes.BlockSize;
                int count = Math.Min(FastLanes.BlockSize, length - start);

                // Only the tail of the last block needs clearing: every other block is written
                // whole by the transform.
                if (count < FastLanes.BlockSize)
                {
                    block[count..].Clear();
                }

                if (mask.AllInvalid)
                {
                    block[..count].Clear();
                }
                else
                {
                    Transform(values.Slice(start, count), frame, reference, block);
                    if (!mask.AllValid)
                    {
                        ClearNulls(block[..count], in mask, start);
                    }
                }

                if (patching)
                {
                    found = FindPatches<T>(block[..count], largest, start, patchIndices, patchValues, found);
                }

                if (blockBytes > 0)
                {
                    FastLanes.PackBlock<T>(
                        block, bitWidth, MemoryMarshal.Cast<byte, T>(destination.Slice(b * blockBytes, blockBytes)));
                }
            }
        }
        finally
        {
            ArrayPool<T>.Shared.Return(rented);
        }

        return found;
    }

    /// <summary>
    /// One block's rows under the transform, in the element's own arithmetic: a vector of the
    /// widest width the machine accelerates at a time, then the tail.
    /// </summary>
    private static void Transform<T>(ReadOnlySpan<T> source, bool frame, T reference, Span<T> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int count = source.Length;
        int shift = (Unsafe.SizeOf<T>() * 8) - 1;
        ref T from = ref MemoryMarshal.GetReference(source);
        ref T into = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            int lanes = Vector512<T>.Count;
            Vector512<T> origin = Vector512.Create(reference);
            for (; i <= count - lanes; i += lanes)
            {
                Vector512<T> value = Vector512.LoadUnsafe(ref from, (nuint)i);
                (frame ? value - origin : (value << 1) ^ (Vector512<T>.Zero - (value >>> shift)))
                    .StoreUnsafe(ref into, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            int lanes = Vector128<T>.Count;
            Vector128<T> origin = Vector128.Create(reference);
            for (; i <= count - lanes; i += lanes)
            {
                Vector128<T> value = Vector128.LoadUnsafe(ref from, (nuint)i);
                (frame ? value - origin : (value << 1) ^ (Vector128<T>.Zero - (value >>> shift)))
                    .StoreUnsafe(ref into, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            T value = Unsafe.Add(ref from, i);
            Unsafe.Add(ref into, i) = frame
                ? unchecked(value - reference)
                : unchecked((value << 1) ^ (T.Zero - (value >> shift)));
        }
    }

    /// <summary>Zeroes the transformed value of every null row of a block, a validity word at a time.</summary>
    /// <remarks>
    /// Transforming the garbage and clearing it costs less than a test per row in front of the
    /// transform. A word of no null, which is most of them, costs a test; a word of nothing but
    /// nulls a clear; a mixed one, with 512-bit vectors, an and per vector of its rows with a mask
    /// of their bits, and without them an and per row.
    /// </remarks>
    private static void ClearNulls<T>(Span<T> block, in ValidityMask mask, int start)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<byte> bits = mask.Bits;
        int offset = mask.BitOffset + start;
        ref T value = ref MemoryMarshal.GetReference(block);
        bool wide = WordBytes.IsAccelerated;
        WordBytes spread = wide ? WordBytes.Create() : default;
        for (int i = 0; i < block.Length; i += 64)
        {
            int take = Math.Min(64, block.Length - i);
            ulong word = BitWords.Load(bits, offset + i) | ~BitWords.Mask(take);
            if (word == ulong.MaxValue)
            {
                continue;
            }

            if ((word & BitWords.Mask(take)) == 0)
            {
                block.Slice(i, take).Clear();
                continue;
            }

            if (wide && take == 64)
            {
                ClearLanes(ref Unsafe.Add(ref value, i), word, in spread);
                continue;
            }

            for (int k = 0; k < take; k++)
            {
                Unsafe.Add(ref value, i + k) &= T.CreateTruncating(0UL - ((word >> k) & 1));
            }
        }
    }

    /// <summary>Sixty-four rows anded with a mask of their validity bits, a 512-bit vector at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearLanes<T>(ref T rows, ulong word, in WordBytes spread)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int lanes = Vector512<T>.Count;
        for (int group = 0; group * lanes < 64; group++)
        {
            nuint at = (nuint)(group * lanes);
            (Vector512.LoadUnsafe(ref rows, at) & spread.Lanes<T>(word, group)).StoreUnsafe(ref rows, at);
        }
    }

    /// <summary>
    /// The exceptions of one transformed block, found a vector at a time: a compare against the
    /// largest value the width holds, and a scalar walk only of the vectors that hold one.
    /// </summary>
    /// <remarks>
    /// Exceptions are rare by construction -- the chooser priced every one -- so the common step is
    /// one compare and a test. A found array that would overflow is left to the caller's count
    /// check, which names both counts.
    /// </remarks>
    /// <returns>The patches found so far, this block's included.</returns>
    private static int FindPatches<T>(
        ReadOnlySpan<T> block, T largest, int start, Span<int> indices, Span<ulong> values, int found)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T first = ref MemoryMarshal.GetReference(block);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            int lanes = Vector512<T>.Count;
            Vector512<T> bound = Vector512.Create(largest);
            for (; i <= block.Length - lanes; i += lanes)
            {
                if (Vector512.GreaterThanAny(Vector512.LoadUnsafe(ref first, (nuint)i), bound))
                {
                    found = CollectPatches(block.Slice(i, lanes), largest, start + i, indices, values, found);
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            int lanes = Vector128<T>.Count;
            Vector128<T> bound = Vector128.Create(largest);
            for (; i <= block.Length - lanes; i += lanes)
            {
                if (Vector128.GreaterThanAny(Vector128.LoadUnsafe(ref first, (nuint)i), bound))
                {
                    found = CollectPatches(block.Slice(i, lanes), largest, start + i, indices, values, found);
                }
            }
        }

        return CollectPatches(block[i..], largest, start + i, indices, values, found);
    }

    /// <summary>The scalar walk: every value above the largest the width holds, in order.</summary>
    private static int CollectPatches<T>(
        ReadOnlySpan<T> block, T largest, int start, Span<int> indices, Span<ulong> values, int found)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] <= largest)
            {
                continue;
            }

            if (found < indices.Length)
            {
                indices[found] = start + i;
                values[found] = ulong.CreateTruncating(block[i]);
            }

            found++;
        }

        return found;
    }

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
        int rows = node.Length;
        VortexBuffer lengths;
        VortexBuffer rowOffsets;
        try
        {
            lengthsPType = FsstPlan.IndexPType(FsstPlan.MaxOf(plan.Lengths));
            offsetsPType = FsstPlan.IndexPType(plan.CodeLength);
            lengths = IndexValues(arena, plan.Lengths, lengthsPType);
            rowOffsets = IndexValues(arena, plan.Offsets, offsetsPType);
        }
        finally
        {
            plan.Release();
        }

        // The row tables are columns like any other, as the reference writes them: the lengths of
        // values of one width are a constant, the offsets a frame of reference over their climb.
        int uncompressed = WriteIndexBuffer(blob, arena, node.DType.Arena, lengths, lengthsPType, rows, encodings);
        int offsets = WriteIndexBuffer(blob, arena, node.DType.Arena, rowOffsets, offsetsPType, rows + 1, encodings);

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

    /// <summary>
    /// Writes <c>vortex.decimal_byte_parts</c>: the unscaled values as a signed primitive child,
    /// which carries the decimal's validity and takes whatever scheme its integers want.
    /// </summary>
    private static int WriteDecimalParts(
        Workspace blob, CanonicalArena arena, int nodeIndex, PType parts, EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        VortexBuffer values = DecimalParts.Narrow(arena, in node, parts);
        int integers = arena.AddPrimitive(
            node.DType.Arena.Primitive(parts, node.DType.Nullability), node.Length, node.Validity, parts, values);
        Span<int> children = stackalloc int[1];
        children[0] = WriteCompressed(blob, arena, integers, encodings);
        return Node(blob, encodings, "vortex.decimal_byte_parts"u8, DecimalPartsBytes(blob, parts), children, []);
    }

    /// <summary>
    /// Writes <c>vortex.onpair</c>: the dictionary as the one buffer, padded for its readers; then
    /// its offsets, the code stream, each row's code offset and decoded length, each a column the
    /// integer schemes take -- the codes bit-pack to their twelve bits -- and the validity.
    /// </summary>
    /// <summary>
    /// Writes <c>vortex.pco</c>: every chunk's metadata, then every page, each a buffer cut from the
    /// plan's one rental, the node's validity the only child -- the valid rows' values alone are
    /// in the stream.
    /// </summary>
    private static int WritePco(Workspace blob, CanonicalArena arena, int nodeIndex, PcoPlan plan, EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int buffers = plan.ChunkCount + plan.PageCount;

        // The buffers are cut from one rental whose ownership moves here, as zstd's frames are: only
        // the last says it is rented, so the array goes back once, after the blob has taken all.
        byte[] data = plan.TakeData();
        Span<ushort> stack = stackalloc ushort[64];
        using Scratch<ushort> scratch = new Scratch<ushort>(buffers, stack);
        Span<ushort> indices = scratch.Span;
        for (int i = 0; i < buffers; i++)
        {
            (int start, int length) = plan.BufferAt(i);
            indices[i] = (ushort)Add(blob, new PendingBuffer(data, start, length, 0, rented: i == buffers - 1));
        }

        if (buffers == 0)
        {
            ArrayPool<byte>.Shared.Return(data);
        }

        Span<int> children = stackalloc int[1];
        int childCount = Validity(blob, arena, node, encodings, children);
        int written = Node(blob, encodings, "vortex.pco"u8, PcoBytes(blob, plan), children[..childCount], indices);
        plan.Release();
        return written;
    }

    /// <summary>
    /// The pco node's metadata, upstream's <c>PcoMetadata</c>: pco's header, then per chunk the value
    /// count of each of its pages.
    /// </summary>
    private static ReadOnlySpan<byte> PcoBytes(Workspace blob, PcoPlan plan)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        writer.WriteBytes(1, PcoPlan.Header);
        ReadOnlySpan<int> pages = plan.PageValues;
        int page = 0;
        foreach (int count in plan.ChunkPages)
        {
            ProtoWriter.MessageScope chunk = writer.BeginMessage(2);
            for (int i = 0; i < count; i++, page++)
            {
                ProtoWriter.MessageScope info = writer.BeginMessage(1);
                writer.WriteUInt32(1, (uint)pages[page]);
                info.End();
            }

            chunk.End();
        }

        return writer.WrittenSpan;
    }

    private static int WriteOnPair(
        Workspace blob, CanonicalArena arena, int nodeIndex, OnPairPlan plan, EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        DTypeArena types = node.DType.Arena;
        Span<int> children = stackalloc int[5];
        int dictionaryBuffer;
        PType dictionaryOffsetsPType;
        PType codeOffsetsPType;
        PType lengthsPType;
        int tokens = plan.Tokens;
        int codeCount = plan.CodeCount;
        int rows = plan.Rows;
        int codesNode;
        VortexBuffer dictionaryOffsets;
        VortexBuffer codeOffsets;
        VortexBuffer lengths;
        try
        {
            byte[] dictionary = ArrayPool<byte>.Shared.Rent(plan.DictionaryBytes);
            plan.Dictionary.CopyTo(dictionary);
            dictionaryBuffer = Add(blob, new PendingBuffer(dictionary, plan.DictionaryBytes, Exponent(8), rented: true));
            dictionaryOffsetsPType = FsstPlan.IndexPType(plan.DictionaryOffsets[tokens]);
            codeOffsetsPType = FsstPlan.IndexPType(codeCount);
            lengthsPType = FsstPlan.IndexPType(FsstPlan.MaxOf(plan.Lengths));
            dictionaryOffsets = IndexValues(arena, plan.DictionaryOffsets, dictionaryOffsetsPType);
            codeOffsets = IndexValues(arena, plan.CodeOffsets, codeOffsetsPType);
            lengths = IndexValues(arena, plan.Lengths, lengthsPType);
            VortexBuffer codes = arena.AllocateUninitialized(codeCount * sizeof(ushort), sizeof(ushort), out Span<byte> into);
            MemoryMarshal.AsBytes(plan.Codes).CopyTo(into);
            codesNode = arena.AddPrimitive(types.Primitive(PType.U16, Nullability.NonNullable), codeCount, Arrays.Validity.NonNullable, PType.U16, codes);
        }
        finally
        {
            plan.Release();
        }

        children[0] = WriteIndexBuffer(blob, arena, types, dictionaryOffsets, dictionaryOffsetsPType, tokens + 1, encodings);
        children[1] = WriteCompressed(blob, arena, codesNode, encodings);
        children[2] = WriteIndexBuffer(blob, arena, types, codeOffsets, codeOffsetsPType, rows + 1, encodings);
        children[3] = WriteIndexBuffer(blob, arena, types, lengths, lengthsPType, rows, encodings);
        int count = 4 + Validity(blob, arena, node, encodings, children[4..]);
        Span<ushort> buffers = stackalloc ushort[1];
        buffers[0] = (ushort)dictionaryBuffer;
        return Node(
            blob, encodings, "vortex.onpair"u8,
            OnPairBytes(blob, lengthsPType, (uint)tokens, (ulong)codeCount, dictionaryOffsetsPType, codeOffsetsPType),
            children[..count], buffers);
    }

    private static ReadOnlySpan<byte> OnPairBytes(
        Workspace blob, PType lengths, uint tokens, ulong codes, PType dictionaryOffsets, PType codeOffsets)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        OnPairMetadata value = new OnPairMetadata(lengths, tokens, codes, dictionaryOffsets, PType.U16, codeOffsets);
        OnPairMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <summary>Index values at the unsigned width that holds them, every element written.</summary>
    private static VortexBuffer IndexValues(CanonicalArena arena, ReadOnlySpan<int> values, PType ptype)
    {
        int width = ptype.ByteWidth();
        VortexBuffer buffer = arena.AllocateUninitialized(values.Length * width, width, out Span<byte> into);
        IntegerNarrowing.Truncate(values, width switch { 1 => PType.I8, 2 => PType.I16, _ => PType.I32 }, into);
        return buffer;
    }

    /// <summary>
    /// Writes <c>vortex.sparse</c> with a null fill: the valid rows' positions, then their values,
    /// then the null as the node's one buffer; no validity, which the fill and the values spell
    /// between them.
    /// </summary>
    private static int WriteSparse(Workspace blob, CanonicalArena arena, int nodeIndex, EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int valid = BitmapKernels.CountSet(mask.Bits, mask.BitOffset, rows);
        PType indicesPType = FsstPlan.IndexPType(rows - 1);
        int width = indicesPType.ByteWidth();
        VortexBuffer indices = arena.AllocateUninitialized(valid * width, width, out Span<byte> into);
        int[] positions = ArrayPool<int>.Shared.Rent(Math.Max(valid, 1));
        int values;
        try
        {
            // The positions a word at a time, each set bit one valid row.
            ReadOnlySpan<byte> bits = mask.Bits;
            int offset = mask.BitOffset;
            int found = 0;
            for (int word = 0; word < rows; word += 64)
            {
                ulong set = BitWords.Load(bits, offset + word);
                if (rows - word < 64)
                {
                    set &= (1UL << (rows - word)) - 1;
                }

                while (set != 0)
                {
                    positions[found++] = word + BitOperations.TrailingZeroCount(set);
                    set &= set - 1;
                }
            }

            for (int i = 0; i < valid; i++)
            {
                switch (width)
                {
                    case 1:
                        into[i] = (byte)positions[i];
                        break;
                    case 2:
                        BinaryPrimitives.WriteUInt16LittleEndian(into[(i * 2)..], (ushort)positions[i]);
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32LittleEndian(into[(i * 4)..], (uint)positions[i]);
                        break;
                }
            }

            values = CanonicalFilter.Apply(arena, nodeIndex, positions.AsSpan(0, valid));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(positions);
        }

        Span<int> children = stackalloc int[2];
        children[0] = WriteIndexBuffer(blob, arena, node.DType.Arena, indices, indicesPType, valid, encodings);
        children[1] = WriteCompressed(blob, arena, values, encodings);
        Span<ushort> fill = stackalloc ushort[1];
        fill[0] = (ushort)ScalarBuffer(blob, blob.Scalars().Null());
        return Node(blob, encodings, "vortex.sparse"u8, SparseBytes(blob, valid, indicesPType), children, fill);
    }

    private static ReadOnlySpan<byte> SparseBytes(Workspace blob, int patches, PType indicesPType)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        SparseMetadata value = new SparseMetadata(PatchesMetadata.Create((ulong)patches, 0, indicesPType));
        SparseMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
    }

    /// <summary>
    /// Queues a bare protobuf <c>ScalarValue</c> as a buffer -- a few bytes, or the one string --
    /// rented and handed back once the blob has copied it, and returns its index.
    /// </summary>
    private static int ScalarBuffer(Workspace blob, ScalarValue value)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        ScalarProtobuf.WriteValue(ref writer, value);
        ReadOnlySpan<byte> scalar = writer.WrittenSpan;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(scalar.Length);
        scalar.CopyTo(bytes);
        return Add(blob, new PendingBuffer(bytes, scalar.Length, alignmentExponent: 0, rented: true));
    }

    /// <summary>
    /// Writes <c>vortex.constant</c>: the value at <paramref name="row"/>, or a null when it is
    /// negative, as the node's one buffer -- a bare protobuf <c>ScalarValue</c>, whose dtype is the
    /// node's -- and no metadata, which is how the reference writes it.
    /// </summary>
    private static int WriteConstant(
        Workspace blob, CanonicalArena arena, int nodeIndex, int row, EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        ScalarStore store = blob.Scalars();
        ScalarValue value = row < 0 ? store.Null() : ConstantValue(store, in node, row);
        Span<ushort> indices = stackalloc ushort[1];
        indices[0] = (ushort)ScalarBuffer(blob, value);
        return Node(blob, encodings, "vortex.constant"u8, default, [], indices);
    }

    /// <summary>
    /// A row's value as the reference spells the scalar: a signed integer as an int64, an unsigned
    /// one as a uint64, a half by its bits, a decimal by its little-endian storage, a string or a
    /// blob by its bytes.
    /// </summary>
    private static ScalarValue ConstantValue(ScalarStore store, in CanonicalNode node, int row)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Constant:
                return Scalar(store, node.DType, node.ConstantElement);
            case CanonicalKind.Bool:
            {
                int bit = node.BitOffset + row;
                return store.Bool(((node.Bits.Span[bit >> 3] >> (bit & 7)) & 1) != 0);
            }

            case CanonicalKind.Primitive:
            {
                int width = node.PType.ByteWidth();
                return Scalar(store, node.DType, node.Values.Span.Slice(row * width, width));
            }

            case CanonicalKind.Decimal:
            {
                int width = DecimalStorage.ByteWidth(node.Storage);
                return store.Bytes(node.Values.Span.Slice(row * width, width));
            }

            case CanonicalKind.VarBinView:
                return Scalar(store, node.DType, BlockStatsPass.Value(node, node.Views.Span, row));
            default:
                throw new InvalidOperationException($"A {node.Kind} node has no scalar of one row; only its nulls are constant.");
        }
    }

    /// <summary>One element of a primitive, text or binary dtype, from its canonical bytes.</summary>
    private static ScalarValue Scalar(ScalarStore store, DType dtype, ReadOnlySpan<byte> element)
    {
        if (dtype.Kind == DTypeKind.Utf8)
        {
            return store.String(element);
        }

        if (dtype.Kind == DTypeKind.Binary)
        {
            return store.Bytes(element);
        }

        PType ptype = dtype.PType;
        if (ptype == PType.F16)
        {
            return store.F16FromBits(BinaryPrimitives.ReadUInt16LittleEndian(element));
        }

        if (ptype == PType.F32)
        {
            return store.F32(BinaryPrimitives.ReadSingleLittleEndian(element));
        }

        if (ptype == PType.F64)
        {
            return store.F64(BinaryPrimitives.ReadDoubleLittleEndian(element));
        }

        ulong bits = ptype.ByteWidth() switch
        {
            1 => element[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(element),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(element),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(element),
        };
        return ptype.IsSignedInteger() ? store.Int64(unchecked((long)SignExtend(bits, ptype))) : store.UInt64(bits);
    }

    private static ReadOnlySpan<byte> DecimalPartsBytes(Workspace blob, PType parts)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        DecimalBytePartsMetadata value = new DecimalBytePartsMetadata(parts);
        DecimalBytePartsMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
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
    /// <remarks>
    /// The width is resolved once, not once an index: a varbin's offsets are one a row, where the
    /// other index arrays are one a patch or a dictionary entry. The host is little-endian, which
    /// the writer checks once at start-up, so the indices are written as the typed words they are.
    /// </remarks>
    private static void WriteIndices(ReadOnlySpan<int> values, int width, Span<byte> destination)
    {
        switch (width)
        {
            case 1:
            {
                Span<byte> into = destination[..values.Length];
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = (byte)values[i];
                }

                break;
            }

            case 2:
            {
                Span<ushort> into = MemoryMarshal.Cast<byte, ushort>(destination)[..values.Length];
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = (ushort)values[i];
                }

                break;
            }

            case 8:
            {
                Span<ulong> into = MemoryMarshal.Cast<byte, ulong>(destination)[..values.Length];
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = (uint)values[i];
                }

                break;
            }

            default:
                MemoryMarshal.AsBytes(values).CopyTo(destination);
                break;
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

    /// <remarks>
    /// A decimal a <c>vortex.decimal_byte_parts</c> decoded is stored at its integers' width, which
    /// may be narrower than its precision requires; a <c>vortex.decimal</c> declaring that width is
    /// one every reader refuses, so such a node is widened to the storage its precision names.
    /// </remarks>
    private static int WriteDecimal(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings)
    {
        DecimalStorageType storage = node.Storage;
        VortexBuffer buffer = node.Values;
        DecimalStorageType required = DecimalStorage.ForPrecision(node.Precision);
        if (DecimalStorage.ByteWidth(storage) < DecimalStorage.ByteWidth(required))
        {
            buffer = DecimalParts.Widen(arena, in node, required);
            storage = required;
        }

        int width = DecimalStorage.ByteWidth(storage);
        int values = Buffer(blob, buffer, Exponent(width));

        Span<int> children = stackalloc int[1];
        int count = Validity(blob, arena, node, encodings, children);

        Span<ushort> bufferIndices = stackalloc ushort[1];
        bufferIndices[0] = (ushort)values;
        return Node(
            blob, encodings, "vortex.decimal"u8, DecimalBytes(blob, storage),
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

        // The sizes are read from the views, a null row's as zero: a null row is zero-length, so
        // offsets stay monotone and the reader never looks at the bytes, because validity already
        // told it not to. The values are then gathered by `ViewHeap`, each view's start in the
        // heap being the row's offset.
        int[] lengths = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        int[]? offsets = null;
        VortexBuffer offsetBuffer;
        PType offsetsPType;
        int heapBuffer;
        try
        {
            long heapBytes = ViewHeap.Lengths(node, mask, lengths.AsSpan(0, rows));
            if (heapBytes > int.MaxValue - ViewHeap.Slack)
            {
                return false;
            }

            offsetsPType = FsstPlan.IndexPType(heapBytes);
            long varbinForm = (((long)rows + 1) * offsetsPType.ByteWidth()) + heapBytes;
            if (varbinForm >= viewForm)
            {
                return false;
            }

            // Rented, and written once without being cleared first, with the slack the gather
            // writes past its last value. `heapBytes` is the sum of the valid values' lengths, so
            // the gather fills exactly that many bytes of the heap, which goes into the blob as a
            // rental queued before the gather so that the blob's `finally` hands it back. The
            // offsets are copied into the arena at their own width and go back at once.
            int heapLength = (int)heapBytes;
            byte[] heap = ArrayPool<byte>.Shared.Rent(heapLength + ViewHeap.Slack);
            heapBuffer = Add(blob, new PendingBuffer(heap, heapLength, 0, rented: true));
            int width = offsetsPType.ByteWidth();
            offsetBuffer = arena.AllocateUninitialized((rows + 1) * width, width, out Span<byte> offsetBytes);
            offsets = ArrayPool<int>.Shared.Rent(rows + 1);
            ViewHeap.Gather(node, lengths.AsSpan(0, rows), heap, offsets.AsSpan(0, rows));
            offsets[rows] = heapLength;
            WriteIndices(offsets.AsSpan(0, rows + 1), width, offsetBytes);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lengths);
            if (offsets is not null)
            {
                ArrayPool<int>.Shared.Return(offsets);
            }
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
        int entries = WriteListView(blob, arena, node, encodings, compress, stats, asList: false);
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
    /// <para>
    /// A list view whose every list starts where the one before it ends -- what a builder and a
    /// decoded <c>vortex.list</c> hold -- is written as <c>vortex.list</c>: one offset a row and the
    /// end, where a list view carries an offset and a size a row, as the reference writes it. Only
    /// the entries of a map stay a list view, which is the one shape upstream reads under a map.
    /// </para>
    private static int WriteListView(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats = default, bool asList = true)
    {
        if (asList && Contiguous(in node))
        {
            return WriteList(blob, arena, node, encodings, compress, stats);
        }

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

    /// <summary>
    /// Whether each list starts where the one before it ends, from a first offset at or above zero,
    /// with its offsets and sizes of one type: the width is resolved once a chunk, never a row.
    /// </summary>
    private static bool Contiguous(in CanonicalNode node) =>
        node.OffsetPType == node.SizePType && node.OffsetPType switch
        {
            PType.U8 => Contiguous<byte>(in node),
            PType.U16 => Contiguous<ushort>(in node),
            PType.U32 => Contiguous<uint>(in node),
            PType.U64 => Contiguous<ulong>(in node),
            PType.I8 => Contiguous<sbyte>(in node),
            PType.I16 => Contiguous<short>(in node),
            PType.I32 => Contiguous<int>(in node),
            PType.I64 => Contiguous<long>(in node),
            PType.F16 or PType.F32 or PType.F64 => false,
            _ => false,
        };

    private static bool Contiguous<T>(in CanonicalNode node)
        where T : unmanaged, IBinaryInteger<T>
    {
        int rows = node.Length;
        ReadOnlySpan<T> offsets = MemoryMarshal.Cast<byte, T>(node.Offsets.Span)[..rows];
        ReadOnlySpan<T> sizes = MemoryMarshal.Cast<byte, T>(node.Sizes.Span)[..rows];
        T next = rows == 0 ? T.Zero : offsets[0];
        if (T.IsNegative(next))
        {
            return false;
        }

        for (int i = 0; i < rows; i++)
        {
            // A size never shrinks the end, so an end below its offset has wrapped past the type.
            T end = next + sizes[i];
            if (offsets[i] != next || end < next)
            {
                return false;
            }

            next = end;
        }

        return true;
    }

    /// <summary>The rows' offsets and the last end, in the offsets' own type: the <c>n + 1</c> offsets of a list.</summary>
    private static VortexBuffer ListOffsets(CanonicalArena arena, in CanonicalNode node) => node.OffsetPType switch
    {
        PType.U8 => ListOffsets<byte>(arena, in node),
        PType.U16 => ListOffsets<ushort>(arena, in node),
        PType.U32 => ListOffsets<uint>(arena, in node),
        PType.U64 => ListOffsets<ulong>(arena, in node),
        PType.I8 => ListOffsets<sbyte>(arena, in node),
        PType.I16 => ListOffsets<short>(arena, in node),
        PType.I32 => ListOffsets<int>(arena, in node),
        PType.I64 => ListOffsets<long>(arena, in node),
        PType.F16 or PType.F32 or PType.F64 => throw new UnreachableException("A list's offsets are integers; Contiguous refused this node."),
        _ => throw new UnreachableException($"PType {(byte)node.OffsetPType} is not defined."),
    };

    private static VortexBuffer ListOffsets<T>(CanonicalArena arena, in CanonicalNode node)
        where T : unmanaged, IBinaryInteger<T>
    {
        int rows = node.Length;
        ReadOnlySpan<T> offsets = MemoryMarshal.Cast<byte, T>(node.Offsets.Span)[..rows];
        ReadOnlySpan<T> sizes = MemoryMarshal.Cast<byte, T>(node.Sizes.Span)[..rows];

        // Every element written below: the rows' offsets, then the end.
        VortexBuffer buffer = arena.AllocateUninitialized((rows + 1) * Unsafe.SizeOf<T>(), Unsafe.SizeOf<T>(), out Span<byte> bytes);
        Span<T> into = MemoryMarshal.Cast<byte, T>(bytes);
        offsets.CopyTo(into);
        into[rows] = rows == 0 ? T.Zero : offsets[rows - 1] + sizes[rows - 1];
        return buffer;
    }

    /// <summary>Writes a contiguous list view as <c>vortex.list</c>: elements, the rows' offsets and the last end, then validity.</summary>
    private static int WriteList(
        Workspace blob, CanonicalArena arena, CanonicalNode node, EncodingDictionary encodings,
        bool compress, ChunkStats stats)
    {
        int rows = node.Length;
        PType ptype = node.OffsetPType;
        VortexBuffer offsets = ListOffsets(arena, in node);

        // Children in the reader's order: elements, offsets, then validity.
        Span<int> children = stackalloc int[3];
        children[0] = WriteChild(
            blob, arena, node.ElementsIndex, encodings, compress,
            ElementsOf(arena, node.ElementsIndex, compress, stats));
        children[1] = compress
            ? WriteIndexBuffer(blob, arena, node.DType.Arena, offsets, ptype, rows + 1, encodings)
            : WritePrimitiveBuffer(blob, encodings, offsets, ptype);
        int count = 2 + Validity(blob, arena, node, encodings, children[2..]);
        return Node(
            blob, encodings, "vortex.list"u8,
            ListBytes(blob, (ulong)arena.GetNode(node.ElementsIndex).Length, ptype),
            children[..count], []);
    }

    private static ReadOnlySpan<byte> ListBytes(Workspace blob, ulong elementsLength, PType offsets)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        ListMetadata value = new ListMetadata(elementsLength, offsets);
        ListMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
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
        long units = compress ? UnitsPerSecond(node.DType) : 0;
        if (units > 0)
        {
            // A timestamp's storage prices its instants split into days, seconds and subseconds, and
            // a split it chose is the column's node, carrying the extension's dtype in place of it.
            children[0] = WriteCompressed(
                blob, arena, node.StorageIndex, encodings, out ColumnScheme scheme, stats.Field(0), Cascade.TimestampStorage(units));
            if (scheme == ColumnScheme.DateTimeParts)
            {
                return children[0];
            }
        }
        else
        {
            children[0] = WriteChild(blob, arena, node.StorageIndex, encodings, compress, stats.Field(0));
        }

        return Node(blob, encodings, "vortex.ext"u8, default, children, []);
    }

    /// <summary>The units a second holds for a timestamp dtype, zero for any other extension.</summary>
    private static long UnitsPerSecond(DType dtype)
    {
        ReadOnlySpan<byte> id = dtype.ExtensionIdUtf8;
        if (ExtensionDTypeRegistry.Resolve(id) != ExtensionKind.Timestamp)
        {
            return 0;
        }

        return ExtensionDTypeRegistry.ReadTimestamp(dtype.ExtensionMetadata, dtype.StorageType).Unit switch
        {
            VortexTimeUnit.Nanoseconds => 1_000_000_000,
            VortexTimeUnit.Microseconds => 1_000_000,
            VortexTimeUnit.Milliseconds => 1_000,
            VortexTimeUnit.Seconds => 1,
            VortexTimeUnit.Days => 0,
            _ => 0,
        };
    }

    /// <summary>
    /// Writes <c>vortex.datetimeparts</c> in place of a timestamp's extension node: the days, which
    /// carry the timestamp's validity, then the seconds and the subseconds, each at the narrowest
    /// type holding it and each taking the scheme its integers want.
    /// </summary>
    private static int WriteDateTimeParts(
        Workspace blob, CanonicalArena arena, int nodeIndex, DateTimePartsPlan parts, EncodingDictionary encodings)
    {
        CanonicalNode storage = arena.GetNode(nodeIndex);
        DTypeArena types = storage.DType.Arena;
        int rows = parts.Rows;
        int days;
        int seconds;
        int subseconds;
        try
        {
            // A part of one value -- the subseconds of instants to the second, the seconds of dates
            // -- is a constant node, which its chooser writes as one without walking a row.
            DType daysType = types.Primitive(parts.DaysPType, storage.DType.Nullability);
            days = parts.OneDay is long day && ValidityMask.From(arena, storage.Validity).AllValid
                ? ConstantPart(arena, daysType, rows, storage.Validity, day)
                : arena.AddPrimitive(daysType, rows, storage.Validity, parts.DaysPType, Part(arena, parts.Days, parts.DaysPType));
            DType secondsType = types.Primitive(parts.SecondsPType, Nullability.NonNullable);
            seconds = parts.OneSecond is long second
                ? ConstantPart(arena, secondsType, rows, Arrays.Validity.NonNullable, second)
                : arena.AddPrimitive(secondsType, rows, Arrays.Validity.NonNullable, parts.SecondsPType, Part(arena, parts.Seconds, parts.SecondsPType));
            DType subsecondsType = types.Primitive(parts.SubsecondsPType, Nullability.NonNullable);
            subseconds = parts.OneSubsecond is long sub
                ? ConstantPart(arena, subsecondsType, rows, Arrays.Validity.NonNullable, sub)
                : arena.AddPrimitive(subsecondsType, rows, Arrays.Validity.NonNullable, parts.SubsecondsPType, Part(arena, parts.Subseconds, parts.SubsecondsPType));
        }
        finally
        {
            parts.Release();
        }

        Span<int> children = stackalloc int[3];
        children[0] = WriteCompressed(blob, arena, days, encodings);
        children[1] = WriteCompressed(blob, arena, seconds, encodings);
        children[2] = WriteCompressed(blob, arena, subseconds, encodings);
        return Node(
            blob, encodings, "vortex.datetimeparts"u8,
            DateTimePartsBytes(blob, parts.DaysPType, parts.SecondsPType, parts.SubsecondsPType), children, []);
    }

    /// <summary>A part holding one value on every row, at its type.</summary>
    private static int ConstantPart(CanonicalArena arena, DType dtype, int rows, Validity validity, long value)
    {
        Span<byte> element = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(element, value);
        return arena.AddConstant(dtype, rows, validity, element[..dtype.PType.ByteWidth()]);
    }

    /// <summary>One part at its type, every element written.</summary>
    private static VortexBuffer Part(CanonicalArena arena, ReadOnlySpan<int> values, PType ptype)
    {
        int width = ptype.ByteWidth();
        VortexBuffer buffer = arena.AllocateUninitialized(values.Length * width, width, out Span<byte> into);
        IntegerNarrowing.Truncate(values, ptype, into);
        return buffer;
    }

    private static ReadOnlySpan<byte> DateTimePartsBytes(Workspace blob, PType days, PType seconds, PType subseconds)
    {
        ref ProtoWriter writer = ref blob.Metadata();
        DateTimePartsMetadata value = new DateTimePartsMetadata(days, seconds, subseconds);
        DateTimePartsMetadata.Write(ref writer, in value);
        return writer.WrittenSpan;
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
        private ZstdCompressor? _zstd;
        private Workspace? _measure;
        private EncodingDictionary? _measureEncodings;

        /// <summary>The builder, cleared by each blob as it starts.</summary>
        internal FlatBufferBuilder Builder { get; } = new FlatBufferBuilder();

        /// <summary>
        /// The compressor every zstd trial of the file compresses in, taken from the process's
        /// with the first and given back with the workspace: its tables are two megabytes, made
        /// once for many files rather than once per trial or per file.
        /// </summary>
        internal ZstdCompressor Zstd => _zstd ??= ZstdEncoders.Rent();

        /// <summary>
        /// The rows a zstd frame holds the values of, the writer's block; 0 for one frame a column.
        /// A read of some rows then decompresses the frames of their blocks and not the column.
        /// </summary>
        internal int FrameRows { get; init; }

        /// <summary>The writer's threads, which a column's zstd frames may be compressed on; none for one thread.</summary>
        internal WorkFan? Fan { get; init; }

        /// <summary>
        /// Whether every array the workspace writes is priced by its bytes alone, a column's
        /// children -- an ALP's integers, a dictionary's codes, a run's ends -- as well as the
        /// column, as the size-first profile asks and the reference's compact compressor does.
        /// </summary>
        internal bool SizeFirst { get; init; }

        private ZstdFrames? _frames;

        /// <summary>The frame table of a column compressed on <see cref="Fan"/>, rented by the first column that uses it.</summary>
        internal ZstdFrames Frames => _frames ??= ZstdFrames.Rent();

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
            // Never size first: what it measures is the plan every other profile writes.
            _measure ??= new Workspace { FrameRows = FrameRows, Fan = Fan };
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

        /// <summary>Hands the builder's and the metadata writer's rentals back, and the zstd compressor and frame table to the process's.</summary>
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
