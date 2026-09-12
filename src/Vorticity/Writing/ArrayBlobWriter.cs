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
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Numerics;

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
    /// <returns>The blob.</returns>
    /// <exception cref="NotSupportedException">The canonical form has no writer.</exception>
    internal static byte[] Write(CanonicalArena arena, int nodeIndex, EncodingDictionary encodings)
    {
        List<PendingBuffer> buffers = [];
        using FlatBufferBuilder builder = new FlatBufferBuilder();

        int root = WriteNode(builder, arena, nodeIndex, buffers, encodings);

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
                (uint)pending.Bytes.Length);

            offset = aligned + pending.Bytes.Length;
        }

        int table = ArrayWriter.Write(builder, root, specs);
        ReadOnlySpan<byte> flatBuffer = builder.Finish(table);

        // The FlatBuffer is 8-byte aligned within the blob; the padding before it is recorded
        // nowhere because the reader finds it from the end.
        long flatStart = Align(offset, 8);
        long total = flatStart + flatBuffer.Length + sizeof(uint);

        byte[] blob = new byte[checked((int)total)];
        long cursor = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            cursor += specs[i].Padding;
            buffers[i].Bytes.CopyTo(blob.AsSpan((int)cursor));
            cursor += buffers[i].Bytes.Length;
        }

        flatBuffer.CopyTo(blob.AsSpan((int)flatStart));
        BinaryPrimitives.WriteUInt32LittleEndian(
            blob.AsSpan((int)(total - sizeof(uint))), (uint)flatBuffer.Length);

        return blob;
    }

    private static int WriteNode(
        FlatBufferBuilder builder,
        CanonicalArena arena,
        int nodeIndex,
        List<PendingBuffer> buffers,
        EncodingDictionary encodings)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        switch (node.Kind)
        {
            case CanonicalKind.Null:
                return Node(builder, encodings, "vortex.null"u8, default, [], []);

            case CanonicalKind.Bool:
                return WriteBool(builder, arena, node, buffers, encodings);

            case CanonicalKind.Primitive:
                return WritePrimitive(builder, arena, node, buffers, encodings);

            case CanonicalKind.Decimal:
                return WriteDecimal(builder, arena, node, buffers, encodings);

            case CanonicalKind.VarBinView:
                return WriteVarBinView(builder, arena, node, buffers, encodings);

            case CanonicalKind.ListView:
                return WriteListView(builder, arena, node, buffers, encodings);

            case CanonicalKind.FixedSizeList:
                return WriteFixedSizeList(builder, arena, node, buffers, encodings);

            case CanonicalKind.Struct:
                return WriteStruct(builder, arena, node, buffers, encodings);

            default:
                return WriteExtension(builder, arena, node, buffers, encodings);
        }
    }

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

    private static int WriteVarBinView(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        // Data buffers first, views LAST: the reader reaches for views at index `dataBufferCount`.
        int dataCount = node.DataBufferCount;
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

    private static int WriteListView(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        CanonicalNode elements = arena.GetNode(node.ElementsIndex);
        byte[] metadata = ListViewBytes(
            (ulong)elements.Length, node.OffsetPType, node.SizePType);

        // Children in the reader's order: elements, offsets, sizes, then validity.
        Span<int> children = stackalloc int[4];
        children[0] = WriteNode(builder, arena, node.ElementsIndex, buffers, encodings);
        children[1] = WritePrimitiveBuffer(
            builder, buffers, encodings, node.Offsets, node.OffsetPType);
        children[2] = WritePrimitiveBuffer(builder, buffers, encodings, node.Sizes, node.SizePType);

        int count = 3 + Validity(builder, arena, node, buffers, encodings, children[3..]);
        return Node(builder, encodings, "vortex.listview"u8, metadata, children[..count], []);
    }

    private static int WriteFixedSizeList(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        Span<int> children = stackalloc int[2];
        children[0] = WriteNode(builder, arena, node.ElementsIndex, buffers, encodings);
        int count = 1 + Validity(builder, arena, node, buffers, encodings, children[1..]);
        return Node(
            builder, encodings, "vortex.fixed_size_list"u8, default, children[..count], []);
    }

    private static int WriteStruct(
        FlatBufferBuilder builder, CanonicalArena arena, CanonicalNode node,
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        // A struct puts its validity FIRST, unlike every other canonical kind
        // (vortex-array's slot_to_child: `nullable.then_some(0)`).
        int fields = node.FieldCount;
        int[] children = new int[fields + 1];
        Span<int> validity = stackalloc int[1];
        int validityCount = Validity(builder, arena, node, buffers, encodings, validity);

        for (int i = 0; i < fields; i++)
        {
            children[validityCount + i] =
                WriteNode(builder, arena, arena.GetNode(node.Index).GetFieldIndex(i), buffers, encodings);
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
        List<PendingBuffer> buffers, EncodingDictionary encodings)
    {
        if (node.Kind != CanonicalKind.Extension)
        {
            throw new NotSupportedException($"A writer cannot serialize a {node.Kind} array.");
        }

        Span<int> children = stackalloc int[1];
        children[0] = WriteNode(builder, arena, node.StorageIndex, buffers, encodings);
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
                int bytes = CanonicalSupport.BitmapByteCount(node.Length);
                byte[] zeros = new byte[Math.Max(bytes, 1)];
                destination[0] = BitmapNode(builder, buffers, encodings, zeros);
                return 1;
            }

            default:
                destination[0] = WriteNode(
                    builder, arena, node.Validity.CanonicalNodeIndex, buffers, encodings);
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

    private static int Buffer(List<PendingBuffer> buffers, VortexBuffer buffer, int alignmentExponent)
    {
        buffers.Add(new PendingBuffer(buffer.Span.ToArray(), alignmentExponent));
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

    private readonly struct PendingBuffer
    {
        internal PendingBuffer(byte[] bytes, int alignmentExponent)
        {
            Bytes = bytes;
            AlignmentExponent = alignmentExponent;
        }

        internal byte[] Bytes { get; }

        internal int AlignmentExponent { get; }
    }
}
