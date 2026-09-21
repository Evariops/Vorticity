using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vorticity.Buffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.zstd_buffers</c>: an inner array whose buffers were zstd-compressed. This is
/// not a value codec. It wraps another array, holding one frame per buffer of it along with that
/// array's encoding id and metadata, so decoding means decompressing every buffer and then
/// decoding the inner array with those buffers in place of its own.
/// </summary>
/// <remarks>
/// The inner metadata is a length-delimited field inside this node's own metadata, hence already a
/// sub-span of the arena's copy, so the synthesized node addresses it by offset and length exactly
/// as a parsed node does: no new storage and no arena extension, and the children are this node's
/// children unchanged. The encoding belongs to a draft edition, which carries no promise that such
/// a file stays readable; that is a reason to watch it, not to refuse it.
/// </remarks>
internal sealed class ZstdBuffersDecoder : ArrayDecoder
{
    private const string Id = "vortex.zstd_buffers";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ZstdBuffersDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.zstd_buffers"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.ZstdBuffers;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The inner node is this node with its buffers restored, so it inherits the grant.
        bool keep = context.KeepsEncoding;

        ReadOnlySpan<byte> tree = node.Arena.TreeSpan;
        ReadOnlySpan<byte> metadata = node.Metadata;

        ReadOnlySpan<byte> innerId = default;
        ReadOnlySpan<byte> innerMetadata = default;

        // Plain lists on a deliberately cold path: this runs once per node, and the counts are the
        // buffer count of one array.
        List<long> sizes = [];
        List<int> alignments = [];

        ProtoReader reader = new ProtoReader(metadata);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    innerId = reader.ReadLengthDelimited();
                    break;
                case 2:
                    innerMetadata = reader.ReadLengthDelimited();
                    break;
                case 3:
                    ReadRepeated(ref reader, wire, sizes);
                    break;
                case 4:
                    ReadRepeatedInt(ref reader, wire, alignments);
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        int buffers = node.BufferCount;
        if (sizes.Count != buffers || alignments.Count != buffers)
        {
            CompressedThrow.Format(
                $"{Id} has {buffers} compressed buffers but {sizes.Count} sizes and " +
                $"{alignments.Count} alignments.");
        }

        if (innerId.IsEmpty)
        {
            CompressedThrow.Format($"{Id} names no inner encoding.");
        }

        ArrayEncodingId inner = EncodingRegistry.ResolveArray(innerId);
        if (inner == ArrayEncodingId.ZstdBuffers)
        {
            // Refused rather than recursed: a file that nests this in itself would decompress
            // forever, and no writer has a reason to produce one.
            CompressedThrow.Format($"{Id} cannot wrap itself.");
        }

        // Decompressed into the canonical arena, which is the only writable memory a decoder may
        // have and is released with the batch.
        //
        // One decoder reused across every buffer of the node: the one-shot form builds and tears
        // down a native decompression context per call.
        int firstBuffer = -1;
        using ZstandardDecoder reused = new ZstandardDecoder();
        for (int i = 0; i < buffers; i++)
        {
            long size = sizes[i];
            if (size < 0 || size > context.Options.MaxDecompressedSize)
            {
                CompressedThrow.Format(
                    $"{Id}'s buffer {i} declares {size} bytes, above the " +
                    $"{context.Options.MaxDecompressedSize}-byte decompression ceiling.");
            }

            int alignment = alignments[i] <= 0 ? 1 : alignments[i];
            VortexBuffer destination = context.Canonical.Allocate(
                (int)size, alignment, out Span<byte> writable);

            if (size != 0)
            {
                reused.Reset();
                System.Buffers.OperationStatus status =
                    reused.Decompress(node.GetBuffer(i).Span, writable, out _, out int written);
                if (status != System.Buffers.OperationStatus.Done || written != (int)size)
                {
                    CompressedThrow.Format($"{Id}'s buffer {i} did not decompress to its declared {size} bytes.");
                }
            }

            node.Arena.AddGlobalBuffer(destination);
            int slot = node.Arena.AddNodeBufferIndex(node.Arena.GlobalBufferCount - 1);
            if (i == 0)
            {
                firstBuffer = slot;
            }
        }

        // The inner metadata is a sub-span of the tree, so the synthetic record addresses it the way
        // a parsed one does. Computed rather than assumed: an offset taken from anywhere else would
        // be a silent misread the moment the layout of the message changed.
        int metadataOffset = innerMetadata.IsEmpty ? 0 : OffsetIn(tree, innerMetadata);
        int synthetic = node.Arena.ReserveNodes(1);
        node.Arena.SetRecord(
            synthetic,
            new ArrayNodeRecord(
                node.EncodingSpecIndex,
                inner,
                metadataOffset,
                innerMetadata.Length,
                node.ChildCount == 0 ? -1 : node.Arena.RecordRef(node.Index).FirstChild,
                node.ChildCount,
                firstBuffer,
                buffers,
                -1));

        return context.DecodeRoot(new ArrayNode(node.Arena, synthetic), dtype, length, keep);
    }

    /// <summary>The byte offset of <paramref name="inner"/> within <paramref name="outer"/>.</summary>
    private static int OffsetIn(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        (int)Unsafe.ByteOffset(
            ref MemoryMarshal.GetReference(outer), ref MemoryMarshal.GetReference(inner));

    /// <summary>Reads a <c>repeated</c> varint field, packed or not.</summary>
    /// <remarks>
    /// Packing is the default in PROTO3, so the common shape is one length-delimited blob of
    /// varints rather than one tag per element. Treating such a blob as a bare varint leaves the
    /// reader mid-blob and the next tag reads as field 0, which is reserved and can only mean a
    /// misaligned reader.
    /// </remarks>
    private static void ReadRepeated(ref ProtoReader reader, ProtoWireType wire, List<long> into)
    {
        if (wire != ProtoWireType.LengthDelimited)
        {
            into.Add((long)reader.ReadVarint());
            return;
        }

        ProtoReader packed = reader.ReadMessage();
        while (packed.TryReadVarint(out ulong value))
        {
            into.Add((long)value);
        }
    }

    private static void ReadRepeatedInt(ref ProtoReader reader, ProtoWireType wire, List<int> into)
    {
        if (wire != ProtoWireType.LengthDelimited)
        {
            into.Add((int)reader.ReadVarint());
            return;
        }

        ProtoReader packed = reader.ReadMessage();
        while (packed.TryReadVarint(out ulong value))
        {
            into.Add((int)value);
        }
    }
}
