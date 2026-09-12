// vortex.alp - vortex-alp-0.86.1/src/alp/array.rs `deserialize` and src/alp/decompress.rs.
//
// "Adaptive Lossless floating-Point": a float whose decimal representation is short is stored as
// the integer `round(value * 10^e / 10^f)`, which then bit-packs like any other small integer.
// Decoding undoes the scaling with two table lookups (AlpTables) and is exact by construction for
// every value the encoder accepted. Values it could NOT represent are carried verbatim in the patch
// set, which is why an ALP array is lossless despite the rounding.
//
// Child layout, from `deserialize`:
//     no patches                -> [encoded]
//     patches, no chunk offsets -> [encoded, patch_indices, patch_values]
//     patches, chunk offsets    -> [encoded, patch_indices, patch_values, patch_chunk_offsets]
//
// There is NO validity child: `decompress_unchunked_core` takes the validity off the encoded child,
// so an ALP array's nullability rides on the integers.
//
// The exponents are file-supplied indices into the scaling tables, and upstream only checks that
// they fit in a u8 -- an out-of-range exponent indexes past the end of a static slice there. Here
// they are bounded by the table length before use, because that is exactly the class I check that
// keeps the lookup in memory (docs/08-semantics.md §5).
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.alp</c> into the float array it encodes.</summary>
public sealed class AlpDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.alp";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly AlpDecoder Instance = new AlpDecoder();

    private AlpDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.alp"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Alp;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        AlpMetadata metadata = AlpMetadata.Read(node.Metadata);

        // `DType::Primitive(F32 | F64, n)`, and nothing else, is accepted upstream.
        if (dtype.Kind != DTypeKind.Primitive ||
            dtype.PType is not (PType.F32 or PType.F64))
        {
            CompressedThrow.Format($"{Id} decodes f32 or f64; this node's dtype is {dtype}.");
        }

        bool isSingle = dtype.PType == PType.F32;
        PType encodedPType = isSingle ? PType.I32 : PType.I64;
        int width = isSingle ? sizeof(float) : sizeof(double);

        int exponentE = CheckExponent(
            metadata.ExponentE, isSingle ? AlpTables.If10Single.Length : AlpTables.If10Double.Length, "exp_e");
        int exponentF = CheckExponent(
            metadata.ExponentF, isSingle ? AlpTables.F10Single.Length : AlpTables.F10Double.Length, "exp_f");

        int expectedChildren = !metadata.HasPatches
            ? 1
            : metadata.Patches.HasChunkOffsets ? 4 : 3;
        ArrayDecodeContext.RequireChildCount(node.ChildCount, expectedChildren, Id);

        DType encodedType = context.Types.Primitive(encodedPType, dtype.Nullability);
        int encodedIndex = context.DecodeChild(in node, 0, encodedType, length);
        CanonicalNode encoded = CanonicalSupport.RequirePrimitiveChild(
            context, encodedIndex, encodedPType, length, Id + " encoded");

        int total = ArrayDecodeContext.CheckedMultiply(length, width, Id + " values");
        VortexBuffer output = CanonicalSupport.Allocate(context, total, width, out Span<byte> destination);
        if (length != 0)
        {
            ReadOnlySpan<byte> source = encoded.Values.Span;
            if (isSingle)
            {
                AlpTables.DecodeSingle(
                    MemoryMarshal.Cast<byte, int>(source)[..length],
                    MemoryMarshal.Cast<byte, float>(destination),
                    exponentE,
                    exponentF);
            }
            else
            {
                AlpTables.DecodeDouble(
                    MemoryMarshal.Cast<byte, long>(source)[..length],
                    MemoryMarshal.Cast<byte, double>(destination),
                    exponentE,
                    exponentF);
            }
        }

        if (metadata.HasPatches)
        {
            ApplyPatches(context, in node, dtype, length, in metadata, width, destination);
        }

        return context.Canonical.AddPrimitive(dtype, length, encoded.Validity, dtype.PType, output);
    }

    /// <summary>
    /// Overwrites the rows the encoder could not represent with the values it stored verbatim.
    /// </summary>
    /// <remarks>
    /// Applied AFTER the whole array is decoded, not per chunk. Upstream has a chunked path that
    /// decodes and patches one <c>PATCH_CHUNK_SIZE</c> window at a time, which is a cache
    /// optimization for a sliced read; over a whole array the two produce the same bytes, because a
    /// patch overwrites its row outright rather than combining with what the decode wrote there.
    /// </remarks>
    private static void ApplyPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        in AlpMetadata metadata,
        int width,
        Span<byte> destination)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(patchesMetadata.Length, $"{Id} patch count");

        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = context.DecodeChild(in node, 1, indicesType, patchCount);

        // The patch values are FLOATS at the array's own dtype - the unencodable originals, not
        // encoded integers.
        int valuesIndex = context.DecodeChild(in node, 2, dtype, patchCount);

        if (patchesMetadata.HasChunkOffsets)
        {
            // Validated and then deliberately unused: the per-chunk index offsets only accelerate
            // a sliced patch lookup, and nothing here slices.
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(
                patchesMetadata.ChunkOffsetsLength, $"{Id} patch chunk_offsets_len");
            DType chunkOffsetsType = context.Types.Primitive(
                patchesMetadata.ChunkOffsetsPType, Nullability.NonNullable);
            int chunkOffsets = context.DecodeChild(in node, 3, chunkOffsetsType, chunkOffsetsLength);
            CompressedValues.RequireIndexChild(
                context, chunkOffsets, patchesMetadata.ChunkOffsetsPType, chunkOffsetsLength, Id,
                "patch_chunk_offsets");
        }

        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id);

        CanonicalNode values = context.Canonical.GetNode(valuesIndex);
        if (values.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "patch_values", values.Kind, "a Primitive");
        }

        if (values.PType != dtype.PType)
        {
            CompressedThrow.Format(
                $"{Id}'s patch values decoded as {values.PType.Name()}; " +
                $"{dtype.PType.Name()} was required.");
        }

        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{Id} patch values must not contain nulls.");
        }

        ReadOnlySpan<byte> source = values.Values.Span;
        for (int i = 0; i < patches.Count; i++)
        {
            int position = patches.GetPosition(i);
            source.Slice(i * width, width).CopyTo(destination.Slice(position * width, width));
        }
    }

    private static int CheckExponent(uint value, int tableLength, string name)
    {
        if (value >= (uint)tableLength)
        {
            CompressedThrow.Format(
                $"{Id} declares {name} = {value}; the scaling table has {tableLength} entries.");
        }

        return (int)value;
    }
}
