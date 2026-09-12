// vortex.alprd - vortex-alp-0.86.1/src/alp_rd/array.rs `deserialize` and alp-0.0.4/src/alp_rd/mod.rs
// (`alp_rd_decode` and the three combine kernels).
//
// ALP's other half, for "real doubles" -- values that use their full precision and so have no short
// decimal form. Instead of scaling, it CUTS THE BIT PATTERN IN TWO at a width the encoder chose:
// the high bits (the "left part") come from a tiny dictionary of at most 8 recurring patterns, and
// the low bits (the "right part") are stored as they are and bit-pack. Decoding is one dictionary
// lookup, one shift and one OR:
//
//     bits = (dictionary[code] << right_bit_width) | right_part
//
// and the result is REINTERPRETED as the float -- not converted. Every bit pattern is a valid
// float, NaN payloads and -0.0 included, which is what makes the encoding lossless where classic
// ALP would have to fall back to patches for every value.
//
// Child layout, from `deserialize`:
//     no patches -> [left_parts, right_parts]
//     patches    -> [left_parts, right_parts, patch_indices, patch_values]
//
// Patches here are NOT the float values, as they are in vortex.alp: they are replacement LEFT
// parts, for rows whose high bits were not in the dictionary. So a patch is applied before the
// combine, not after it -- which this decoder does by recombining the patched row from its stored
// right part rather than by mutating an intermediate buffer.
//
// One deliberate divergence. Upstream's unpatched fast path masks the code with MAX_DICT_SIZE - 1
// and reads a zero-filled table, so a code past the dictionary decodes to garbage rather than
// panicking; its patched path indexes the dictionary unmasked and panics. Neither is producible by
// a conformant writer, and both are worse than an error for a reader of untrusted input, so an
// out-of-range code is a format error here on both paths.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.alprd</c> into the float array it encodes.</summary>
public sealed class AlpRdDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.alprd";

    /// <summary>
    /// <c>MAX_DICT_SIZE</c>: the left-parts dictionary holds at most this many patterns
    /// (alp-0.0.4/src/alp_rd/mod.rs).
    /// </summary>
    private const int MaxDictionarySize = 8;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly AlpRdDecoder Instance = new AlpRdDecoder();

    private AlpRdDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.alprd"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.AlpRd;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        if (dtype.Kind != DTypeKind.Primitive || dtype.PType is not (PType.F32 or PType.F64))
        {
            CompressedThrow.Format($"{Id} decodes f32 or f64; this node's dtype is {dtype}.");
        }

        // Bounded before it is read, so the dictionary lands on the stack whatever the file says.
        // Upstream would tolerate a message carrying more entries than dict_len uses; sizing a heap
        // allocation from a file-supplied count to accept a file no writer produces is the worse
        // trade (docs/03-architecture.md §4 invariant 1).
        int dictionaryEntries = AlpRdMetadata.CountDictionaryEntries(node.Metadata);
        if (dictionaryEntries > MaxDictionarySize)
        {
            CompressedThrow.Format(
                $"{Id} carries {dictionaryEntries} left-parts dictionary entries; the encoding " +
                $"allows at most {MaxDictionarySize}.");
        }

        Span<uint> storage = stackalloc uint[MaxDictionarySize];
        Span<uint> dictionary = storage[..dictionaryEntries];
        AlpRdMetadata metadata = AlpRdMetadata.Read(node.Metadata, dictionary);

        bool isSingle = dtype.PType == PType.F32;
        int width = isSingle ? sizeof(float) : sizeof(double);
        int bits = width * 8;

        // `left << right_bit_width` in a type of `bits` bits. At or past the width the shift is
        // undefined in C and wraps in release Rust, so it is bounded rather than reproduced.
        if (metadata.RightBitWidth >= (uint)bits)
        {
            CompressedThrow.Format(
                $"{Id} declares right_bit_width = {metadata.RightBitWidth} for a {bits}-bit float.");
        }

        int dictionaryLength = CheckDictionaryLength(metadata);
        PType leftPType = metadata.LeftPartsPType;
        if (!leftPType.IsUnsignedInteger())
        {
            CompressedThrow.Format(
                $"{Id}'s left_parts_ptype is {leftPType.Name()}; the codes must be unsigned.");
        }

        int expectedChildren = metadata.HasPatches ? 4 : 2;
        ArrayDecodeContext.RequireChildCount(node.ChildCount, expectedChildren, Id);

        DType leftType = context.Types.Primitive(leftPType, dtype.Nullability);
        int leftIndex = context.DecodeChild(in node, 0, leftType, length);
        CanonicalNode left = CanonicalSupport.RequirePrimitiveChild(
            context, leftIndex, leftPType, length, Id + " left_parts");

        PType rightPType = isSingle ? PType.U32 : PType.U64;
        DType rightType = context.Types.Primitive(rightPType, Nullability.NonNullable);
        int rightIndex = context.DecodeChild(in node, 1, rightType, length);
        CanonicalNode right = CanonicalSupport.RequirePrimitiveChild(
            context, rightIndex, rightPType, length, Id + " right_parts");

        int total = ArrayDecodeContext.CheckedMultiply(length, width, Id + " values");
        VortexBuffer output = CanonicalSupport.Allocate(context, total, width, out Span<byte> destination);

        Combine(
            left.Values.Span, leftPType, right.Values.Span, destination, length,
            dictionary[..dictionaryLength], (int)metadata.RightBitWidth, isSingle);

        if (metadata.HasPatches)
        {
            ApplyLeftPartPatches(
                context, in node, length, in metadata, leftPType, right.Values.Span, destination,
                (int)metadata.RightBitWidth, isSingle);
        }

        return context.Canonical.AddPrimitive(dtype, length, left.Validity, dtype.PType, output);
    }

    /// <summary>
    /// <c>destination[i] = (dictionary[code[i]] &lt;&lt; rightBitWidth) | right[i]</c>, written as
    /// the float's own bit pattern.
    /// </summary>
    private static void Combine(
        ReadOnlySpan<byte> left,
        PType leftPType,
        ReadOnlySpan<byte> right,
        Span<byte> destination,
        int length,
        ReadOnlySpan<uint> dictionary,
        int rightBitWidth,
        bool isSingle)
    {
        for (int i = 0; i < length; i++)
        {
            ulong code = CompressedValues.ReadUnsigned(left, leftPType, i);
            if (code >= (ulong)dictionary.Length)
            {
                CompressedThrow.Format(
                    $"{Id} row {i} names left-parts dictionary entry {code}, but the dictionary " +
                    $"holds {dictionary.Length}.");
            }

            ulong high = dictionary[(int)code];
            Write(destination, i, high, right, rightBitWidth, isSingle);
        }
    }

    /// <summary>
    /// Replaces the left part of the rows whose high bits were not in the dictionary, then
    /// recombines those rows.
    /// </summary>
    /// <remarks>
    /// Upstream dictionary-decodes into a scratch buffer, patches it, and only then combines. The
    /// two agree byte for byte, because a patch replaces the left part outright and the right part
    /// is untouched either way -- so recombining the patched row from the right part it already has
    /// saves the scratch buffer without changing a bit.
    /// </remarks>
    private static void ApplyLeftPartPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        int length,
        in AlpRdMetadata metadata,
        PType leftPType,
        ReadOnlySpan<byte> right,
        Span<byte> destination,
        int rightBitWidth,
        bool isSingle)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(patchesMetadata.Length, $"{Id} patch count");

        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = context.DecodeChild(in node, 2, indicesType, patchCount);

        // Patch values are LEFT PARTS at the left child's own physical type, always non-nullable -
        // `left_parts_dtype.as_nonnullable()` upstream. They are raw high bits, not dictionary
        // codes, which is why they are not bounds-checked against the dictionary.
        DType valuesType = context.Types.Primitive(leftPType, Nullability.NonNullable);
        int valuesIndex = context.DecodeChild(in node, 3, valuesType, patchCount);

        // `// TODO(0ax1): handle chunk offsets` - upstream passes None unconditionally, so a
        // descriptor that declares them describes a shape no reader implements.
        if (patchesMetadata.HasChunkOffsets)
        {
            CompressedThrow.Format(
                $"{Id} declares patch chunk offsets, which the encoding does not carry.");
        }

        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id);

        CanonicalNode values = CanonicalSupport.RequirePrimitiveChild(
            context, valuesIndex, leftPType, patchCount, Id + " patch_values");
        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{Id} patch values must not contain nulls.");
        }

        ReadOnlySpan<byte> source = values.Values.Span;
        for (int i = 0; i < patches.Count; i++)
        {
            int position = patches.GetPosition(i);
            ulong high = CompressedValues.ReadUnsigned(source, leftPType, i);
            Write(destination, position, high, right, rightBitWidth, isSingle);
        }
    }

    private static void Write(
        Span<byte> destination,
        int index,
        ulong high,
        ReadOnlySpan<byte> right,
        int rightBitWidth,
        bool isSingle)
    {
        if (isSingle)
        {
            uint low = BinaryPrimitives.ReadUInt32LittleEndian(right.Slice(index * 4, 4));
            uint word = unchecked(((uint)high << rightBitWidth) | low);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(index * 4, 4), word);
        }
        else
        {
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(right.Slice(index * 8, 8));
            ulong word = unchecked((high << rightBitWidth) | low);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(index * 8, 8), word);
        }
    }

    /// <summary>
    /// The used prefix of the dictionary: <c>dict_len</c> entries out of however many the message
    /// carried, capped at <see cref="MaxDictionarySize"/>.
    /// </summary>
    private static int CheckDictionaryLength(in AlpRdMetadata metadata)
    {
        // `dict_len <= entries` is already enforced by the codec; `entries <= MaxDictionarySize` by
        // the caller. What is left is the case the kernels would divide by, or index with, nothing.
        uint declared = metadata.DictionaryLength;
        if (declared == 0)
        {
            CompressedThrow.Format($"{Id} declares an empty left-parts dictionary.");
        }

        return (int)declared;
    }
}
