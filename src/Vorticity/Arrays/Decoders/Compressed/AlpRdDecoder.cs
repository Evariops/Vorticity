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
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
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
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// ALP-RD is pointwise, so a take reaches straight through it to the two children underneath.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE OUTPUT PER INPUT, which is the only property a selective decode needs: row i is
    /// `(dictionary[left[i]] &lt;&lt; right_bit_width) | right[i]`, so the rows nobody asked for
    /// need never be combined and, where the children can do it, never be decoded. The selection is
    /// pushed into both children unchanged - they live in this node's row space - exactly as
    /// `vortex.alp` and `fastlanes.for` do.
    /// </para>
    /// <para>
    /// THE MEASUREMENT THAT ASKED FOR IT (v2 R17, after R23): on the 1M-row axis a take of 64 rows
    /// cost 1 056 µs and a full scan of that same file cost 1 041 µs. The take WAS the scan - the
    /// fallback decoded the whole node once and gathered 64 rows out of it - so the wanted rows were
    /// 0.006% of the work done for them.
    /// </para>
    /// <para>
    /// Patches are the one place this differs from `vortex.alp`, and not in the walk: a patch here
    /// replaces a LEFT part, so the patched row is recombined with the right part it already has,
    /// which after a selective decode sits at the SELECTED index rather than the file's.
    /// </para>
    /// </remarks>
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

        // The children live in THIS node's row space -- one row each per row here -- so a selection
        // reaches them unchanged, and whether they can honour it positionally is their business.
        int produced = selective ? wanted.Length : length;

        DType leftType = context.Types.Primitive(leftPType, dtype.Nullability);
        int leftIndex = selective
            ? context.DecodeChildSelected(in node, 0, leftType, length, wanted)
            : context.DecodeChild(in node, 0, leftType, length);
        CanonicalNode left = CanonicalSupport.RequirePrimitiveChild(
            context, leftIndex, leftPType, produced, Id + " left_parts");

        PType rightPType = isSingle ? PType.U32 : PType.U64;
        DType rightType = context.Types.Primitive(rightPType, Nullability.NonNullable);
        int rightIndex = selective
            ? context.DecodeChildSelected(in node, 1, rightType, length, wanted)
            : context.DecodeChild(in node, 1, rightType, length);
        CanonicalNode right = CanonicalSupport.RequirePrimitiveChild(
            context, rightIndex, rightPType, produced, Id + " right_parts");

        int total = ArrayDecodeContext.CheckedMultiply(produced, width, Id + " values");

        // UNINITIALIZED: `Combine` casts the destination to exactly `produced` elements of `width`
        // bytes -- which is `total` -- and assigns every one of them. `ApplyLeftPartPatches` only
        // ever overwrites rows the combine already wrote. The one path that stops short is
        // `ThrowCode`, and it throws: the buffer is never reachable from a decode that failed.
        VortexBuffer output = CanonicalSupport.AllocateUninitialized(
            context, total, width, out Span<byte> destination);

        Combine(
            left.Values.Span, leftPType, right.Values.Span, destination, produced,
            dictionary[..dictionaryLength], (int)metadata.RightBitWidth, isSingle, wanted);

        if (metadata.HasPatches)
        {
            ApplyLeftPartPatches(
                context, in node, length, in metadata, leftPType, right.Values.Span, destination,
                (int)metadata.RightBitWidth, isSingle, wanted, selective);
        }

        return context.Canonical.AddPrimitive(dtype, produced, left.Validity, dtype.PType, output);
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
        bool isSingle,
        ReadOnlySpan<int> wanted)
    {
        // The physical type of the left parts and the output width are properties of the NODE, and
        // this loop was asking about both on every row: `ReadUnsigned`'s switch to fetch the code,
        // then `Write`'s branch on `isSingle` wrapping two bounds-checked little-endian accesses.
        // Resolved once, the body is a gather from an eight-entry dictionary, a shift and an or.
        switch (leftPType)
        {
            case PType.U8:
                CombineCodes<byte>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted);
                break;
            case PType.U16:
                CombineCodes<ushort>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted);
                break;
            case PType.U32:
                CombineCodes<uint>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted);
                break;
            default:
                CombineCodes<ulong>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted);
                break;
        }
    }

    private static void CombineCodes<TCode>(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right,
        Span<byte> destination,
        int length,
        ReadOnlySpan<uint> dictionary,
        int rightBitWidth,
        bool isSingle,
        ReadOnlySpan<int> wanted)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> codes = MemoryMarshal.Cast<byte, TCode>(left)[..length];

        // THE DICTIONARY IS PRE-SHIFTED, ONCE, which is what upstream stores in the first place
        // (`alp-0.0.4/src/alp_rd/mod.rs:647-675`, `alp_rd_combine_codes_inplace`: its table is
        // already in the high bits). The dictionary holds at most eight entries and the loop below
        // runs a million times, so the shift belongs here and not in the body. A code out of range
        // still reaches `ThrowCode` unchanged: the table is only ever indexed after that test.
        //
        // ONE TABLE PER BRANCH, NOT ONE SHARED `ulong` TABLE, and that is not tidiness. C# masks a
        // shift count by the operand's width -- `& 31` for `uint`, `& 63` for `ulong` -- so a file
        // declaring `right_bit_width >= 32` on an f32 column makes `d << r` and
        // `(uint)((ulong)d << r)` two DIFFERENT values. The bit width comes from the file, so that
        // is not a hypothetical; each branch pre-shifts at exactly the width its body used to.
        if (isSingle)
        {
            Span<uint> shifted = stackalloc uint[dictionary.Length];
            for (int i = 0; i < dictionary.Length; i++)
            {
                shifted[i] = unchecked(dictionary[i] << rightBitWidth);
            }

            ReadOnlySpan<uint> low = MemoryMarshal.Cast<byte, uint>(right)[..length];
            Span<uint> target = MemoryMarshal.Cast<byte, uint>(destination)[..length];
            for (int i = 0; i < length; i++)
            {
                uint code = Widen(codes[i]);
                if (code >= (uint)dictionary.Length)
                {
                    ThrowCode(Row(i, wanted), code, dictionary.Length);
                }

                target[i] = unchecked(shifted[(int)code] | low[i]);
            }

            return;
        }

        Span<ulong> wideShifted = stackalloc ulong[dictionary.Length];
        for (int i = 0; i < dictionary.Length; i++)
        {
            wideShifted[i] = unchecked((ulong)dictionary[i] << rightBitWidth);
        }

        ReadOnlySpan<ulong> wide = MemoryMarshal.Cast<byte, ulong>(right)[..length];
        Span<ulong> output = MemoryMarshal.Cast<byte, ulong>(destination)[..length];
        for (int i = 0; i < length; i++)
        {
            uint code = Widen(codes[i]);
            if (code >= (uint)dictionary.Length)
            {
                ThrowCode(Row(i, wanted), code, dictionary.Length);
            }

            output[i] = unchecked(wideShifted[(int)code] | wide[i]);
        }
    }

    /// <summary>
    /// The row a combine index names in the FILE, which after a selective decode is not the index.
    /// </summary>
    /// <remarks>
    /// Only ever called on the way to a throw, so the indirection costs nothing and buys a message
    /// that points at the row the reader can go and look at.
    /// </remarks>
    private static int Row(int index, ReadOnlySpan<int> wanted) =>
        wanted.IsEmpty ? index : wanted[index];

    /// <summary>Widens one left-parts code, saturating so an over-large one is refused.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Widen<TCode>(TCode code)
        where TCode : unmanaged
    {
        if (typeof(TCode) == typeof(byte))
        {
            return Unsafe.As<TCode, byte>(ref code);
        }

        if (typeof(TCode) == typeof(ushort))
        {
            return Unsafe.As<TCode, ushort>(ref code);
        }

        if (typeof(TCode) == typeof(uint))
        {
            return Unsafe.As<TCode, uint>(ref code);
        }

        ulong value = Unsafe.As<TCode, ulong>(ref code);
        return value > uint.MaxValue ? uint.MaxValue : (uint)value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowCode(int row, uint code, int dictionaryLength) =>
        CompressedThrow.Format(
            $"{Id} row {row} names left-parts dictionary entry {code}, but the dictionary " +
            $"holds {dictionaryLength}.");

    /// <summary>
    /// Replaces the left part of the rows whose high bits were not in the dictionary, then
    /// recombines those rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream dictionary-decodes into a scratch buffer, patches it, and only then combines. The
    /// two agree byte for byte, because a patch replaces the left part outright and the right part
    /// is untouched either way -- so recombining the patched row from the right part it already has
    /// saves the scratch buffer without changing a bit.
    /// </para>
    /// <para>
    /// THE PATCH SET IS DECODED WHOLE EVEN FOR A SELECTIVE DECODE, and deliberately: patch indices
    /// are positions in the FILE's row space, so finding which of them the selection touches means
    /// having them all. They are the rows the dictionary could not hold - a small minority by
    /// construction, or the encoder would have chosen something else - and the two sorted lists are
    /// then walked together once, exactly as <c>Patches.ApplySelected</c> does for `vortex.alp`.
    /// </para>
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
        bool isSingle,
        ReadOnlySpan<int> wanted,
        bool selective)
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

        // Upstream leaves chunk offsets unhandled here and passes None unconditionally, marking
        // the gap in its own source, so a descriptor that declares them describes a shape no
        // reader implements.
        if (patchesMetadata.HasChunkOffsets)
        {
            CompressedThrow.Format(
                $"{Id} declares patch chunk offsets, which the encoding does not carry.");
        }

        bool walked = context.IsNodeChecked(in node);
        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        CanonicalNode values = CanonicalSupport.RequirePrimitiveChild(
            context, valuesIndex, leftPType, patchCount, Id + " patch_values");
        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{Id} patch values must not contain nulls.");
        }

        // ONE LOOP FOR BOTH PATHS, and the F3 ratchet is why: it counts CALL SITES of
        // `ReadUnsigned` per file, so a second copy of this walk reads as a second per-row dispatch
        // whatever the loop around it says. Splitting the two would have meant raising a ceiling to
        // pass, which is the one thing a ratchet may never be asked to do. The branch below is per
        // PATCH -- the rows the dictionary could not hold, a minority by construction -- and the
        // dense path pays exactly one predictable test for each of them.
        //
        // Both lists ascend, so one walk finds the intersection. `at` is where the patched row
        // landed in the SELECTION, which is also where its right part is: the right child was
        // decoded selectively, so it holds `wanted.Length` rows in the same order.
        ReadOnlySpan<byte> source = values.Values.Span;
        int at = 0;
        for (int i = 0; i < patches.Count; i++)
        {
            int position = patches.GetPosition(i);
            int target = position;

            if (selective)
            {
                while (at < wanted.Length && wanted[at] < position)
                {
                    at++;
                }

                if (at == wanted.Length)
                {
                    return;
                }

                if (wanted[at] != position)
                {
                    continue;
                }

                target = at;
            }

            ulong high = CompressedValues.ReadUnsigned(source, leftPType, i);
            Write(destination, target, high, right, rightBitWidth, isSingle);
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
