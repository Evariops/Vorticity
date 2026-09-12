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
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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

    /// <summary>
    /// The largest decoded heap a selective decode will keep on the stack instead of renting.
    /// </summary>
    /// <remarks>
    /// Sized so that a take of the usual few dozen rows of short strings fits: 12 bytes is the
    /// longest value a view inlines, so anything at or under this bound can still turn out to need
    /// no buffer at all. Above it the arena is cheaper than the stack.
    /// </remarks>
    private const int InlineScratchBytes = 512;

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
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Decodes only the wanted rows, by concatenating only their codes.
    /// </summary>
    /// <remarks>
    /// Structurally identical to <c>FsstDecoder.DecodeSelected</c>, for the reason the header gives:
    /// these two encodings are twins at read time. `codes_offsets[i]..[i+1]` bounds row i's codes
    /// exactly, so a row is addressable without walking the rows before it - the reference simply
    /// does not decode that way, and docs/90 classified the encoding from the reference's strategy
    /// rather than from the format.
    ///
    /// `codes_offsets` is not itself pushed down: row i needs offsets i AND i+1, so its wanted set
    /// is the union of `wanted` and `wanted + 1`, which costs more to build than the one integer
    /// decode it would save. `uncompressed_lengths` IS pushed down, because one per wanted row is
    /// all the views need.
    ///
    /// The dictionary offset table is built ONCE and shared across the wanted rows rather than per
    /// row. It is also the part of this decode that a take does not shrink: it is sized by the
    /// dictionary, not by the selection.
    /// </remarks>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
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

        int produced = selective ? wanted.Length : length;

        // Child 3: the decoded length of each row, zero for a null one.
        //
        // NOT pushed down, and that is measured rather than assumed. Selecting it costs 192 bytes
        // per split - a canonical node and its buffer - against the 1024 integers it saves
        // decoding, and the scattered take allocated 4 536 bytes MORE with the push-down than
        // without it. The allocation ratchet caught that; the ceiling stayed where it was.
        CanonicalNode uncompressedLengths = DecodePart(
            context, in node, 3, "uncompressed_lengths", metadata.UncompressedLengthsPType, length);

        Validity validity = context.DecodeValidity(in node, 4, dtype.Nullability, length);
        if (selective)
        {
            validity = Compute.CanonicalFilter.FilterValidity(context.Canonical, validity, wanted);
        }

        int total = TotalDecodedLength(
            uncompressedLengths, metadata.UncompressedLengthsPType, length, wanted, selective,
            out int longestRow);

        // A HEAP NOBODY POINTS INTO IS NOT WORTH RENTING, and on a take that is the common case: a
        // value of 12 bytes or fewer lives inside its own view, so a selection of short rows leaves
        // the heap unread. Renting it anyway costs a block from the pool's smallest size class,
        // which retains 8 - so 64 splits in one batch allocate a fresh owner object for nearly
        // every one of them. That was measured, not guessed: the allocation ratchet went red by
        // 4 536 bytes on a scattered take, which is 64 splits x one 71-byte NativeSegmentOwner.
        // The selective path makes FEWER rents than the fallback it replaces; it simply moved them
        // all into the one bucket that cannot serve them.
        // The bound on the LONGEST ROW is what makes the stack safe, not the bound on the total:
        // a view over a value longer than MaxInlineViewLength holds a POINTER into the heap, and a
        // pointer into a stack frame that is about to return is a use-after-free that no test over
        // short strings would ever catch.
        bool inlineOnly = selective
            && total <= InlineScratchBytes
            && longestRow <= CanonicalSupport.MaxInlineViewLength;
        Span<byte> scratch = inlineOnly ? stackalloc byte[InlineScratchBytes].Slice(0, total) : default;
        VortexBuffer heap = VortexBuffer.Empty;
        Span<byte> destination = scratch;
        if (!inlineOnly)
        {
            heap = CanonicalSupport.Allocate(context, total, 1, out destination);
        }

        if (selective)
        {
            DecodeRows(
                codes.Values.Span, metadata.CodesPType, codesOffsets.Values.Span,
                metadata.CodesOffsetsPType, codesLength,
                dictOffsets.Values.Span, metadata.DictionaryOffsetsPType, tokenCount,
                dictionary.Span, uncompressedLengths.Values.Span,
                metadata.UncompressedLengthsPType, wanted, destination);

        }
        else
        {
            (int codeStart, int codeEnd) = CodeWindow(
                codesOffsets, metadata.CodesOffsetsPType, length, codesLength);

            int written = DecodeCodes(
                codes.Values.Span, metadata.CodesPType, codeStart, codeEnd,
                dictOffsets.Values.Span, metadata.DictionaryOffsetsPType, tokenCount,
                dictionary.Span, destination);
            if (written != total)
            {
                CompressedThrow.Format(
                    $"{Id} decoded {written} bytes; its uncompressed lengths sum to {total}.");
            }
        }

        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            produced, CanonicalSupport.ViewSize, Id + " views");
        VortexBuffer views = CanonicalSupport.Allocate(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        bool referenced = BuildViews(
            uncompressedLengths.Values.Span, metadata.UncompressedLengthsPType, destination, writable,
            produced, dtype.Kind == DTypeKind.Utf8, wanted, selective);

        // The heap is attached only if a view actually points into it. A value of 12 bytes or fewer
        // lives inside its own view, so a selection of short rows references nothing - and handing
        // the node a buffer nobody reads costs a buffer slot per split for no reader. The whole-
        // array path almost always has at least one long row and so almost always attaches it;
        // a take of a few short ones almost never does.
        if (total == 0 || !referenced)
        {
            // Nothing points into the heap, so the node carries no buffers - and when the decode
            // ran on the stack there is no buffer to carry.
            return context.Canonical.AddVarBinView(dtype, produced, validity, views, default);
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = heap;
        return context.Canonical.AddVarBinView(dtype, produced, validity, views, single);
    }

    /// <summary>
    /// Concatenates one wanted row at a time, each from its own window of the code stream.
    /// </summary>
    /// <remarks>
    /// The dictionary offset table is built once here and reused across the rows, which is the
    /// whole reason this is not a loop over <see cref="DecodeCodes"/>: that method rents, fills and
    /// returns the table per call, and a take would pay for it per row.
    ///
    /// Each row concatenates into the REMAINING heap rather than a slice of exactly its own length.
    /// The 16-byte store is only legal while sixteen writable bytes remain, so an exactly-sized
    /// destination would push every token of every row onto the exact-copy path. The declared
    /// length is still enforced against what the row actually wrote.
    /// </remarks>
    private static void DecodeRows(
        ReadOnlySpan<byte> codes,
        PType codesPType,
        ReadOnlySpan<byte> codesOffsets,
        PType codesOffsetsPType,
        int codesLength,
        ReadOnlySpan<byte> dictOffsets,
        PType offsetsPType,
        int tokenCount,
        ReadOnlySpan<byte> dictionary,
        ReadOnlySpan<byte> lengths,
        PType lengthsPType,
        ReadOnlySpan<int> wanted,
        Span<byte> destination)
    {
        int[] rented = ArrayPool<int>.Shared.Rent(tokenCount + 1);
        try
        {
            Span<int> offsets = rented.AsSpan(0, tokenCount + 1);
            for (int t = 0; t <= tokenCount; t++)
            {
                offsets[t] = (int)CanonicalSupport.ReadInteger(dictOffsets, offsetsPType, t);
            }

            int written = 0;
            for (int k = 0; k < wanted.Length; k++)
            {
                int row = wanted[k];
                long start = CanonicalSupport.ReadInteger(codesOffsets, codesOffsetsPType, row);
                long end = CanonicalSupport.ReadInteger(codesOffsets, codesOffsetsPType, row + 1);
                if (start < 0 || end < start || end > codesLength)
                {
                    CompressedThrow.Format(
                        $"{Id} row {row} spans codes [{start}, {end}) of a {codesLength}-code stream.");
                }

                int expected = (int)CanonicalSupport.ReadInteger(lengths, lengthsPType, row);
                int got = Concatenate(
                    codes, codesPType, (int)start, (int)end, offsets, tokenCount, dictionary,
                    destination.Slice(written));
                if (got != expected)
                {
                    CompressedThrow.Format(
                        $"{Id} row {row} decoded to {got} bytes; it declares {expected}.");
                }

                written += got;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
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
        // The token offsets are read ONCE into an int table rather than twice per code through
        // CanonicalSupport.ReadInteger's physical-type switch. There are at most 65536 tokens and
        // usually far more codes than that, so this trades a bounded pass for two switches and two
        // bounds checks on every code. ValidateDictionary has already proved every offset lies
        // inside the blob and never decreases, so the table needs no checking of its own.
        int[] rented = ArrayPool<int>.Shared.Rent(tokenCount + 1);
        try
        {
            Span<int> offsets = rented.AsSpan(0, tokenCount + 1);
            for (int t = 0; t <= tokenCount; t++)
            {
                offsets[t] = (int)CanonicalSupport.ReadInteger(dictOffsets, offsetsPType, t);
            }

            return Concatenate(
                codes, codesPType, codeStart, codeEnd, offsets, tokenCount, dictionary, destination);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>The concatenation itself, with the code width resolved once.</summary>
    private static int Concatenate(
        ReadOnlySpan<byte> codes,
        PType codesPType,
        int codeStart,
        int codeEnd,
        ReadOnlySpan<int> offsets,
        int tokenCount,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination)
    {
        int written = 0;

        // Past this point a token no longer has sixteen writable bytes behind it, so the wide
        // store is unsafe and the exact copy takes over.
        int wideLimit = destination.Length - MaxTokenSize;
        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte source = ref MemoryMarshal.GetReference(dictionary);

        for (int i = codeStart; i < codeEnd; i++)
        {
            ulong code = CompressedValues.ReadUnsigned(codes, codesPType, i);
            if (code >= (ulong)tokenCount)
            {
                CompressedThrow.Format(
                    $"{Id} code {code} names a token the {tokenCount}-entry dictionary does not hold.");
            }

            int start = offsets[(int)code];
            int size = offsets[(int)code + 1] - start;
            if (written + size > destination.Length)
            {
                CompressedThrow.Format(
                    $"{Id}: the code stream decodes to more than the {destination.Length} bytes " +
                    "its uncompressed lengths account for.");
            }

            if (written <= wideLimit && start <= dictionary.Length - MaxTokenSize)
            {
                // ONE 16-BYTE STORE PER TOKEN, then advance by the token's REAL length - the same
                // trick FSST's decoder uses, and legal for the same reason: the bytes past the
                // token are garbage the next store overwrites. A token is at most 16 bytes, so one
                // Vector128 move replaces a variable-length Span.CopyTo call.
                Vector128.StoreUnsafe(
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)start)),
                    ref Unsafe.Add(ref output, (uint)written));
                written += size;
                continue;
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

    /// <summary>
    /// The decoded heap's size: the sum over every row, or over the selected rows only.
    /// </summary>
    /// <remarks>
    /// Summed in <see cref="long"/> and capped, because the lengths are file-supplied: a row count
    /// of 8192 each declaring <c>u64::MAX</c> would otherwise wrap into a small allocation that the
    /// decode then overruns.
    /// </remarks>
    /// <param name="lengths">The uncompressed-lengths child.</param>
    /// <param name="ptype">Its physical type.</param>
    /// <param name="length">The array's full row count.</param>
    /// <param name="wanted">The selection, meaningful only when <paramref name="selective"/>.</param>
    /// <param name="selective">Whether to sum the selection rather than every row.</param>
    /// <param name="longestRow">The longest single row counted, which decides stack safety.</param>
    /// <returns>The decoded heap's size in bytes.</returns>
    private static int TotalDecodedLength(
        CanonicalNode lengths, PType ptype, int length, ReadOnlySpan<int> wanted, bool selective,
        out int longestRow)
    {
        ReadOnlySpan<byte> raw = lengths.Values.Span;
        int count = selective ? wanted.Length : length;
        long total = 0;
        long longest = 0;
        for (int i = 0; i < count; i++)
        {
            int row = selective ? wanted[i] : i;
            long value = CanonicalSupport.ReadInteger(raw, ptype, row);
            if (value < 0)
            {
                CompressedThrow.Format($"{Id} row {row} declares a negative uncompressed length.");
            }

            total += value;
            if (total > int.MaxValue)
            {
                CompressedThrow.Format($"{Id} uncompressed lengths sum past {int.MaxValue} bytes.");
            }

            longest = Math.Max(longest, value);
        }

        longestRow = (int)Math.Min(longest, int.MaxValue);
        return (int)total;
    }

    /// <summary>
    /// Cuts the decoded heap into per-row views, advancing by each row's uncompressed length.
    /// </summary>
    /// <remarks>
    /// The heap holds the produced rows back to back in selection order, so the OFFSET walks the
    /// output while the LENGTH is read at the row's own index. Null rows are not special-cased:
    /// upstream stores a zero length for them, so they consume nothing and get an empty view, which
    /// is what the zeroed allocation already holds.
    /// </remarks>
    /// <returns><c>true</c> if any view references the heap rather than inlining its value.</returns>
    private static bool BuildViews(
        ReadOnlySpan<byte> lengths,
        PType ptype,
        ReadOnlySpan<byte> heap,
        Span<byte> views,
        int length,
        bool requireUtf8,
        ReadOnlySpan<int> wanted,
        bool selective)
    {
        bool referenced = false;
        int offset = 0;
        for (int i = 0; i < length; i++)
        {
            int size = (int)CanonicalSupport.ReadInteger(
                lengths, ptype, selective ? wanted[i] : i);
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
                referenced = true;
            }

            offset += size;
        }

        return referenced;
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
