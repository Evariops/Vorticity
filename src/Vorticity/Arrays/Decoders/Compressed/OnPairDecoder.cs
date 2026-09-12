// vortex.onpair - vortex-onpair-0.86.1/src/array.rs `deserialize`, src/canonical.rs
// `canonicalize_onpair`, and onpair-0.2.1 (`try_decode_into`, `CompactDictionary::validate_safety`).
//
// FSST's competitor at write time, and its structural twin at read time. Where FSST has 255 symbols
// of at most 8 bytes addressed by one byte, OnPair has up to 65536 TOKENS of at most 16 bytes
// addressed by a u16 -- and no escape, because a conformant dictionary contains all 256 single-byte
// tokens and can therefore encode any byte string. Decoding is a concatenation:
//
//     for each code: append dict_bytes[dict_offsets[code] .. dict_offsets[code + 1]]
//
// and, exactly as in FSST, the ROW BOUNDARIES ARE ON THE DECODED SIDE: `codes_offsets` bounds the
// code stream, `uncompressed_lengths` cuts the decoded heap.
//
// Children, from `deserialize`:
//     [dict_offsets, codes, codes_offsets, uncompressed_lengths, validity?]
//
// with their lengths carried in the metadata rather than on the wire -- `dict_size + 1`, `codes_len`
// and `len + 1` respectively -- because slot children do not persist their own.
//
// One rule is deliberately relaxed. `validate_safety` requires the dictionary blob to be READ-PADDED
// to MAX_TOKEN_SIZE past the last token's start, because its decoder copies a fixed 16 bytes per
// token and advances by the real length. This one copies the real length, so it needs the logical
// bound -- the last offset within the blob -- and not the padding. A padded file satisfies both; an
// unpadded one is readable here and not there, which is the safe direction to differ in.
using System;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.onpair</c> into a canonical varbin view.</summary>
public sealed class OnPairDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.onpair";

    /// <summary><c>MAX_TOKEN_SIZE</c>: the longest dictionary token, in bytes.</summary>
    private const int MaxTokenSize = 16;

    /// <summary><c>MAX_NUM_TOKENS</c>: a code is a <c>u16</c>, so the dictionary holds at most 2^16.</summary>
    private const int MaxTokenCount = 1 << 16;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly OnPairDecoder Instance = new OnPairDecoder();

    private OnPairDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.onpair"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.OnPair;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        CanonicalSupport.RequireBinaryLike(dtype, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 4, 5, Id);

        OnPairMetadata metadata = OnPairMetadata.Read(node.Metadata);

        // Child 0: the dictionary's offsets, dict_size + 1 of them.
        int tokenCount = CheckedTokenCount(metadata.DictionarySize);
        CanonicalNode dictOffsets = DecodePart(
            context, in node, 0, "dict_offsets", metadata.DictionaryOffsetsPType, tokenCount + 1);

        VortexBuffer dictionary = node.GetBuffer(0);
        ValidateDictionary(dictOffsets, metadata.DictionaryOffsetsPType, tokenCount, dictionary);

        // Child 1: the code stream, whose length the metadata carries.
        int codesLength = ArrayDecodeContext.CheckedLength(metadata.CodesLength, Id + " codes_len");
        CanonicalNode codes = DecodePart(
            context, in node, 1, "codes", metadata.CodesPType, codesLength);

        // Child 2: the per-row code boundaries, len + 1 of them. Only the first and last are read:
        // a whole-array decode walks the codes in order and never needs the interior.
        int offsetCount = ArrayDecodeContext.CheckedLength((ulong)length + 1, Id + " codes_offsets");
        CanonicalNode codesOffsets = DecodePart(
            context, in node, 2, "codes_offsets", metadata.CodesOffsetsPType, offsetCount);

        // Child 3: the decoded length of each row, zero for a null one.
        CanonicalNode uncompressedLengths = DecodePart(
            context, in node, 3, "uncompressed_lengths", metadata.UncompressedLengthsPType, length);

        Validity validity = context.DecodeValidity(in node, 4, dtype.Nullability, length);

        (int codeStart, int codeEnd) = CodeWindow(
            codesOffsets, metadata.CodesOffsetsPType, length, codesLength);

        int total = TotalDecodedLength(uncompressedLengths, metadata.UncompressedLengthsPType, length);
        VortexBuffer heap = CanonicalSupport.Allocate(context, total, 1, out Span<byte> destination);

        int written = DecodeCodes(
            codes.Values.Span, metadata.CodesPType, codeStart, codeEnd,
            dictOffsets.Values.Span, metadata.DictionaryOffsetsPType, tokenCount,
            dictionary.Span, destination);
        if (written != total)
        {
            CompressedThrow.Format(
                $"{Id} decoded {written} bytes; its uncompressed lengths sum to {total}.");
        }

        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, Id + " views");
        VortexBuffer views = CanonicalSupport.Allocate(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        BuildViews(
            uncompressedLengths.Values.Span, metadata.UncompressedLengthsPType, destination, writable,
            length, dtype.Kind == DTypeKind.Utf8);

        if (total == 0)
        {
            return context.Canonical.AddVarBinView(dtype, length, validity, views, default);
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = heap;
        return context.Canonical.AddVarBinView(dtype, length, validity, views, single);
    }

    /// <summary>Concatenates the tokens the codes name, in order.</summary>
    private static int DecodeCodes(
        ReadOnlySpan<byte> codes,
        PType codesPType,
        int codeStart,
        int codeEnd,
        ReadOnlySpan<byte> dictOffsets,
        PType offsetsPType,
        int tokenCount,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination)
    {
        int written = 0;
        for (int i = codeStart; i < codeEnd; i++)
        {
            ulong code = CompressedValues.ReadUnsigned(codes, codesPType, i);
            if (code >= (ulong)tokenCount)
            {
                CompressedThrow.Format(
                    $"{Id} code {code} names a token the {tokenCount}-entry dictionary does not hold.");
            }

            int start = (int)CanonicalSupport.ReadInteger(dictOffsets, offsetsPType, (int)code);
            int end = (int)CanonicalSupport.ReadInteger(dictOffsets, offsetsPType, (int)code + 1);
            int size = end - start;
            if (written + size > destination.Length)
            {
                CompressedThrow.Format(
                    $"{Id}: the code stream decodes to more than the {destination.Length} bytes " +
                    "its uncompressed lengths account for.");
            }

            dictionary.Slice(start, size).CopyTo(destination.Slice(written, size));
            written += size;
        }

        return written;
    }

    /// <summary>
    /// <c>validate_safety</c>: the offsets must start at zero, be strictly increasing (no empty
    /// token), describe tokens of at most <see cref="MaxTokenSize"/> bytes, and end inside the blob.
    /// </summary>
    private static void ValidateDictionary(
        CanonicalNode dictOffsets, PType ptype, int tokenCount, VortexBuffer dictionary)
    {
        ReadOnlySpan<byte> offsets = dictOffsets.Values.Span;
        long previous = CanonicalSupport.ReadInteger(offsets, ptype, 0);
        if (previous != 0)
        {
            CompressedThrow.Format($"{Id}'s dictionary offsets start at {previous}, not 0.");
        }

        for (int i = 1; i <= tokenCount; i++)
        {
            long current = CanonicalSupport.ReadInteger(offsets, ptype, i);
            long size = current - previous;
            if (size <= 0)
            {
                CompressedThrow.Format(
                    $"{Id} dictionary token {i - 1} spans [{previous}, {current}); tokens are " +
                    "non-empty and their offsets increase.");
            }

            if (size > MaxTokenSize)
            {
                CompressedThrow.Format(
                    $"{Id} dictionary token {i - 1} is {size} bytes; the maximum is {MaxTokenSize}.");
            }

            previous = current;
        }

        // The logical end, not the reference's read-padded one: see the header note.
        if (previous > dictionary.Length)
        {
            CompressedThrow.Format(
                $"{Id}'s dictionary offsets end at {previous}, past its {dictionary.Length}-byte blob.");
        }
    }

    /// <summary>
    /// The half-open range of codes this array owns, <c>codes_offsets[0]..codes_offsets[length]</c>.
    /// </summary>
    private static (int Start, int End) CodeWindow(
        CanonicalNode codesOffsets, PType ptype, int length, int codesLength)
    {
        ReadOnlySpan<byte> offsets = codesOffsets.Values.Span;
        long start = CanonicalSupport.ReadInteger(offsets, ptype, 0);
        long end = CanonicalSupport.ReadInteger(offsets, ptype, length);

        if (start < 0 || end < start || end > codesLength)
        {
            CompressedThrow.Format(
                $"{Id} codes span [{start}, {end}) of a {codesLength}-code stream.");
        }

        return ((int)start, (int)end);
    }

    private static int TotalDecodedLength(CanonicalNode lengths, PType ptype, int length)
    {
        ReadOnlySpan<byte> raw = lengths.Values.Span;
        long total = 0;
        for (int i = 0; i < length; i++)
        {
            long value = CanonicalSupport.ReadInteger(raw, ptype, i);
            if (value < 0)
            {
                CompressedThrow.Format($"{Id} row {i} declares a negative uncompressed length.");
            }

            total += value;
            if (total > int.MaxValue)
            {
                CompressedThrow.Format($"{Id} uncompressed lengths sum past {int.MaxValue} bytes.");
            }
        }

        return (int)total;
    }

    private static void BuildViews(
        ReadOnlySpan<byte> lengths,
        PType ptype,
        ReadOnlySpan<byte> heap,
        Span<byte> views,
        int length,
        bool requireUtf8)
    {
        int offset = 0;
        for (int i = 0; i < length; i++)
        {
            int size = (int)CanonicalSupport.ReadInteger(lengths, ptype, i);
            ReadOnlySpan<byte> value = heap.Slice(offset, size);

            if (requireUtf8 && !Utf8.IsValid(value))
            {
                throw new VortexFormatException($"Row {i} of a Utf8 array is not valid UTF-8.");
            }

            Span<byte> view = views.Slice(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                CanonicalSupport.WriteInlineView(view, value);
            }
            else
            {
                CanonicalSupport.WriteReferenceView(view, size, value, bufferIndex: 0, offset: offset);
            }

            offset += size;
        }
    }

    /// <summary>Decodes one non-nullable integer child and checks its shape.</summary>
    private static CanonicalNode DecodePart(
        ArrayDecodeContext context,
        in ArrayNode node,
        int childIndex,
        string name,
        PType ptype,
        int length)
    {
        CanonicalSupport.RequireIntegerPType(ptype, $"{Id} {name}");
        DType childType = context.Types.Primitive(ptype, Nullability.NonNullable);
        int index = context.DecodeChild(in node, childIndex, childType, length);
        return CanonicalSupport.RequirePrimitiveChild(context, index, ptype, length, $"{Id} {name}");
    }

    private static int CheckedTokenCount(uint dictionarySize)
    {
        if (dictionarySize == 0)
        {
            CompressedThrow.Format($"{Id} declares an empty dictionary.");
        }

        if (dictionarySize > MaxTokenCount)
        {
            CompressedThrow.Format(
                $"{Id} declares {dictionarySize} dictionary tokens; a code is a u16, so at most " +
                $"{MaxTokenCount} are addressable.");
        }

        return (int)dictionarySize;
    }
}
