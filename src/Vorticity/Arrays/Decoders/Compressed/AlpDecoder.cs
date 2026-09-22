using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.alp</c> into the float array it encodes: a float whose decimal form is short
/// is stored as a scaled integer, and the values the encoder could not represent that way are
/// carried verbatim as patches, which is what makes the encoding lossless despite the rounding.
/// </summary>
/// <remarks>
/// The children are the encoded integers, then, when the metadata declares patches, the patch
/// indices and the patch values, and last the per-chunk index offsets when those are declared too.
/// There is no validity child: the array's nullability rides on its encoded integers.
/// </remarks>
internal sealed class AlpDecoder : ArrayDecoder
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
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start: 0, count: length);
    }

    /// <summary>ALP is pointwise, so a range is the encoded integers' range, scaled.</summary>
    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount >= 1 && context.ChildDecodesRange(in node, 0);
    }

    /// <summary>
    /// The encoded integers' range, scaled, with the patches of the range applied: the patch set is
    /// read whole, decoded once for every range of the node, and its first patch in the range found
    /// by a binary search.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start, count);
    }

    /// <summary>
    /// ALP is pointwise, so a take reaches straight through it to the integers underneath.
    /// </summary>
    /// <remarks>
    /// One output row comes from one input row, scaled, with the exceptions carried as patches, so
    /// a take never needs a row it was not asked for. It reaches the bit-packing underneath, which
    /// is where the saving is.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true, start: 0, count: wanted.Length);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective, int start, int count)
    {
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
        bool whole = !selective && start == 0 && count == length;
        int encodedIndex = selective
            ? context.DecodeChildSelected(in node, 0, encodedType, length, wanted)
            : whole
                ? context.DecodeChild(in node, 0, encodedType, length)
                : context.DecodeChildRange(in node, 0, encodedType, length, start, count);
        int produced = count;
        CanonicalNode encoded = CanonicalSupport.RequirePrimitiveChild(
            context, encodedIndex, encodedPType, produced, Id + " encoded");

        int total = ArrayDecodeContext.CheckedMultiply(produced, width, Id + " values");
        // Uninitialized: both DecodeSingle and DecodeDouble write all `produced` values, and
        // `produced == 0` means `total == 0`, so there is no uncovered case. Patches only overwrite.
        VortexBuffer output = CanonicalSupport.AllocateUninitialized(
            context, total, width, out Span<byte> destination);
        if (produced != 0)
        {
            ReadOnlySpan<byte> source = encoded.Values.Span;
            if (isSingle)
            {
                AlpTables.DecodeSingle(
                    MemoryMarshal.Cast<byte, int>(source)[..produced],
                    MemoryMarshal.Cast<byte, float>(destination),
                    exponentE,
                    exponentF);
            }
            else
            {
                AlpTables.DecodeDouble(
                    MemoryMarshal.Cast<byte, long>(source)[..produced],
                    MemoryMarshal.Cast<byte, double>(destination),
                    exponentE,
                    exponentF);
            }
        }

        if (metadata.HasPatches)
        {
            ApplyPatches(context, in node, dtype, length, in metadata, width, destination, wanted, selective, start, count);
        }

        return context.Canonical.AddPrimitive(dtype, produced, encoded.Validity, dtype.PType, output);
    }

    /// <summary>
    /// Overwrites the rows the encoder could not represent with the values it stored verbatim.
    /// </summary>
    /// <remarks>
    /// A patch overwrites its row outright rather than combining with what the decode wrote there,
    /// so the patches are applied after the scaling, over the whole array, the selection or the
    /// range alike. Anything short of the whole array reads the whole patch set, decoded once for
    /// every visit of the node.
    /// </remarks>
    private static void ApplyPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        in AlpMetadata metadata,
        int width,
        Span<byte> destination,
        ReadOnlySpan<int> wanted,
        bool selective,
        int start,
        int count)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(patchesMetadata.Length, $"{Id} patch count");
        bool whole = !selective && start == 0 && count == length;

        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = whole
            ? context.DecodeChild(in node, 1, indicesType, patchCount)
            : context.DecodeWholeChild(in node, 1, indicesType, patchCount);

        // The patch values are floats at the array's own dtype: the originals the encoder could not
        // represent, not encoded integers.
        int valuesIndex = whole
            ? context.DecodeChild(in node, 2, dtype, patchCount)
            : context.DecodeWholeChild(in node, 2, dtype, patchCount);

        if (patchesMetadata.HasChunkOffsets)
        {
            // Validated and then deliberately unused: the first patch of a range is found by a
            // binary search over the indices, which the per-chunk offsets would only shorten.
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(
                patchesMetadata.ChunkOffsetsLength, $"{Id} patch chunk_offsets_len");
            DType chunkOffsetsType = context.Types.Primitive(
                patchesMetadata.ChunkOffsetsPType, Nullability.NonNullable);
            int chunkOffsets = whole
                ? context.DecodeChild(in node, 3, chunkOffsetsType, chunkOffsetsLength)
                : context.DecodeWholeChild(in node, 3, chunkOffsetsType, chunkOffsetsLength);
            CompressedValues.RequireIndexChild(
                context, chunkOffsets, patchesMetadata.ChunkOffsetsPType, chunkOffsetsLength, Id,
                "patch_chunk_offsets");
        }

        bool walked = context.IsNodeChecked(in node);
        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        // The patch values are read as a span below, so a constant child is expanded into one
        // rather than refused.
        CanonicalNode values = context.Canonical.GetNode(
            CanonicalSupport.ExpandIfConstant(context, valuesIndex));
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
        if (selective)
        {
            Patches.ApplySelected(in patches, source, width, wanted, destination);
            return;
        }

        if (whole)
        {
            patches.ApplyAll(source, width, destination);
            return;
        }

        Patches.ApplyRange(in patches, source, width, start, count, destination);
    }

    /// <summary>
    /// Bounds a file-supplied exponent by the length of the table it indexes; nothing else keeps
    /// the lookup in memory.
    /// </summary>
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
