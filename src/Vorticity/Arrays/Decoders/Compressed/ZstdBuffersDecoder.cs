// vortex.zstd_buffers - vortex-zstd-0.86.1/src/zstd_buffers.rs.
//
// NOT A VALUE CODEC. `vortex.zstd` compresses a column's values; this compresses the BUFFERS of
// another array, one zstd frame each, and stores that array's encoding id and metadata so it can be
// rebuilt afterwards. Decoding is therefore: decompress every buffer, then decode the INNER array
// with those buffers substituted for its own.
//
// WHICH SOUNDED ARCHITECTURAL AND IS NOT, for one reason: `inner_metadata` is a `bytes` field inside
// this node's metadata, so it is already a sub-span of the arena's FlatBuffer copy. A synthesised
// node can point at it with an offset and a length exactly as a parsed node does - no new storage,
// no arena extension, and the pinned tree makes the offset stable. The children are this node's
// children unchanged; only the buffers are new.
//
// A DRAFT EDITION. `zstd2026.02.0` carries no read-forever guarantee, so a file using this is not
// promised to stay readable by its own edition's rules. That is a reason to watch the encoding, not
// a reason to refuse it.
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vorticity.Buffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.zstd_buffers</c>: an inner array whose buffers were zstd-compressed.</summary>
public sealed class ZstdBuffersDecoder : ArrayDecoder
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

        ReadOnlySpan<byte> tree = node.Arena.TreeSpan;
        ReadOnlySpan<byte> metadata = node.Metadata;

        ReadOnlySpan<byte> innerId = default;
        ReadOnlySpan<byte> innerMetadata = default;

        // Plain lists on a deliberately cold path: this runs once per node, and the counts are the
        // buffer count of one array. A stackalloc here fought the ref-struct scoping rules for no
        // measurable gain.
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
        // have (contract §8.4) and is released with the batch.
        int firstBuffer = -1;
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

            if (size != 0
                && (!ZstandardDecoder.TryDecompress(node.GetBuffer(i).Span, writable, out int written)
                    || written != (int)size))
            {
                CompressedThrow.Format($"{Id}'s buffer {i} did not decompress to its declared {size} bytes.");
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

        return context.DecodeRoot(new ArrayNode(node.Arena, synthetic), dtype, length);
    }

    /// <summary>The byte offset of <paramref name="inner"/> within <paramref name="outer"/>.</summary>
    private static int OffsetIn(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        (int)Unsafe.ByteOffset(
            ref MemoryMarshal.GetReference(outer), ref MemoryMarshal.GetReference(inner));

    /// <summary>Reads a <c>repeated</c> varint field, packed or not.</summary>
    /// <remarks>
    /// PACKED IS THE DEFAULT IN PROTO3, so the common shape is one length-delimited blob of varints
    /// rather than one tag per element. Treating it as a bare varint leaves the reader mid-blob and
    /// the next tag reads as field 0 - which is exactly the error this produced before it was fixed,
    /// and a good one to get, since field 0 is reserved and can only mean a misaligned reader.
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
