using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.alprd</c> into the float array it encodes: the encoding for values that use
/// their full precision and so have no short decimal form. Their bit pattern is cut in two at a
/// width the encoder chose, the high part coming from a tiny dictionary of recurring patterns and
/// the low part stored as it is, so decoding a row is one dictionary lookup, one shift and one or.
/// </summary>
/// <remarks>
/// <para>
/// The combined word is reinterpreted as the float, not converted, so every bit pattern survives,
/// NaN payloads and negative zero included; that is what makes the encoding lossless where scaling
/// would have to carry a patch for every value.
/// </para>
/// <para>
/// The children are the left parts and the right parts, followed by the patch indices and patch
/// values when the metadata declares patches. A patch here replaces a left part rather than a whole
/// float, so it takes effect before the combine; this decoder achieves that by recombining the
/// patched row from the right part it already holds, instead of mutating an intermediate buffer.
/// </para>
/// <para>
/// A code past the end of the dictionary is a format error on both the patched and the unpatched
/// path: no conformant writer emits one, and for a reader of untrusted input decoding it to
/// something plausible is worse than refusing the file.
/// </para>
/// </remarks>
internal sealed class AlpRdDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.alprd";

    /// <summary>The left-parts dictionary holds at most this many patterns.</summary>
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
        return Core(context, in node, dtype, length, wanted: default, selective: false, start: 0, count: length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount >= 2 && context.ChildDecodesRange(in node, 0) && context.ChildDecodesRange(in node, 1);
    }

    /// <summary>
    /// The two children's range, combined, with the patches of the range applied: the patch set is
    /// read whole for each range, as ALP reads its own, since a patch index is a position in the
    /// node's row space.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start, count);
    }

    /// <summary>
    /// ALP-RD is pointwise, so a take reaches straight through it to the two children underneath.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One output row comes from one input row, which is the only property a selective decode
    /// needs: the rows nobody asked for are never combined and, where the children can honour a
    /// selection, never decoded. The selection reaches both children unchanged, since they live in
    /// this node's row space. Without it, a take of a handful of rows costs a whole scan of the
    /// node.
    /// </para>
    /// <para>
    /// Patches need care: a patch replaces a left part, so the patched row is recombined with the
    /// right part it already has, which after a selective decode sits at the selected index rather
    /// than at the one the file names.
    /// </para>
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
        bool ranged = !selective && (start != 0 || count != length);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        if (dtype.Kind != DTypeKind.Primitive || dtype.PType is not (PType.F32 or PType.F64))
        {
            CompressedThrow.Format($"{Id} decodes f32 or f64; this node's dtype is {dtype}.");
        }

        // Bounded before it is read, so the dictionary lands on the stack whatever the file says.
        // A message carrying more entries than the encoding allows is refused rather than served
        // from a heap allocation sized by a file-supplied count.
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

        // The combine shifts a left part by this width in a value of `bits` bits; at or past that
        // width the shift has no meaningful result, so the declared width is bounded here.
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

        // The children live in this node's row space -- one row each per row here -- so a selection
        // or a range reaches them unchanged, and whether they can honour it positionally is their
        // business.
        int produced = selective ? wanted.Length : count;

        DType leftType = context.Types.Primitive(leftPType, dtype.Nullability);
        int leftIndex = selective
            ? context.DecodeChildSelected(in node, 0, leftType, length, wanted)
            : ranged
                ? context.DecodeChildRange(in node, 0, leftType, length, start, count)
                : context.DecodeChild(in node, 0, leftType, length);
        CanonicalNode left = CanonicalSupport.RequirePrimitiveChild(
            context, leftIndex, leftPType, produced, Id + " left_parts");

        PType rightPType = isSingle ? PType.U32 : PType.U64;
        DType rightType = context.Types.Primitive(rightPType, Nullability.NonNullable);
        int rightIndex = selective
            ? context.DecodeChildSelected(in node, 1, rightType, length, wanted)
            : ranged
                ? context.DecodeChildRange(in node, 1, rightType, length, start, count)
                : context.DecodeChild(in node, 1, rightType, length);
        CanonicalNode right = CanonicalSupport.RequirePrimitiveChild(
            context, rightIndex, rightPType, produced, Id + " right_parts");

        int total = ArrayDecodeContext.CheckedMultiply(produced, width, Id + " values");

        // Uninitialized: `Combine` casts the destination to exactly `produced` elements of `width`
        // bytes -- which is `total` -- and assigns every one of them. `ApplyLeftPartPatches` only
        // ever overwrites rows the combine already wrote. The one path that stops short is
        // `ThrowCode`, and it throws: the buffer is never reachable from a decode that failed.
        VortexBuffer output = CanonicalSupport.AllocateUninitialized(
            context, total, width, out Span<byte> destination);

        Combine(
            left.Values.Span, leftPType, right.Values.Span, destination, produced,
            dictionary[..dictionaryLength], (int)metadata.RightBitWidth, isSingle, wanted, start);

        if (metadata.HasPatches)
        {
            ApplyLeftPartPatches(
                context, in node, length, in metadata, leftPType, right.Values.Span, destination,
                (int)metadata.RightBitWidth, isSingle, wanted, selective, start, produced);
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
        ReadOnlySpan<int> wanted,
        int start)
    {
        // The physical type of the left parts and the output width are properties of the node, so
        // they are resolved once here rather than asked about on every row; the loop body is then a
        // gather from a handful of dictionary entries, a shift and an or.
        switch (leftPType)
        {
            case PType.U8:
                CombineCodes<byte>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted, start);
                break;
            case PType.U16:
                CombineCodes<ushort>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted, start);
                break;
            case PType.U32:
                CombineCodes<uint>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted, start);
                break;
            default:
                CombineCodes<ulong>(left, right, destination, length, dictionary, rightBitWidth, isSingle, wanted, start);
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
        ReadOnlySpan<int> wanted,
        int start)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> codes = MemoryMarshal.Cast<byte, TCode>(left)[..length];

        // The dictionary is pre-shifted once: it holds a handful of entries while the loop below
        // runs once per row, so the shift belongs here and not in the body. A code out of range
        // still reaches `ThrowCode`, since the table is only ever indexed after that test.
        //
        // Each branch keeps its own table rather than sharing a wide one. C# masks a shift count by
        // the operand's width, so shifting in a narrow type and shifting in a wide one before
        // narrowing give different values once the declared right bit width reaches the narrow
        // width -- and that width comes from the file. Each branch therefore pre-shifts at exactly
        // the width its body works in.
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
                    ThrowCode(Row(i, wanted, start), code, dictionary.Length);
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
                ThrowCode(Row(i, wanted, start), code, dictionary.Length);
            }

            output[i] = unchecked(wideShifted[(int)code] | wide[i]);
        }
    }

    /// <summary>
    /// The row a combine index names in the file, which after a selective or a ranged decode is not
    /// the index.
    /// </summary>
    /// <remarks>
    /// Only ever called on the way to a throw, so the indirection costs nothing and buys a message
    /// that points at the row the reader can go and look at.
    /// </remarks>
    private static int Row(int index, ReadOnlySpan<int> wanted, int start) =>
        wanted.IsEmpty ? start + index : wanted[index];

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
    /// Decoding the left parts into a scratch buffer, patching that and only then combining would
    /// give the same bytes, because a patch replaces the left part outright and the right part is
    /// untouched either way; recombining the patched row from the right part it already has saves
    /// the scratch buffer without changing a bit.
    /// </para>
    /// <para>
    /// The patch set is decoded whole even for a selective decode: patch indices are positions in
    /// the file's row space, so knowing which of them the selection touches means having them all.
    /// They are the rows the dictionary could not hold, a small minority or the encoder would have
    /// chosen otherwise, and the two ascending lists are then walked together once.
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
        bool selective,
        int start,
        int produced)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(patchesMetadata.Length, Id, "patch count");

        // Anything short of the whole array reads the whole patch set, so it is decoded once for
        // every visit of the node -- every window of a scan, every batch of a take -- as ALP's is.
        bool whole = !selective && start == 0 && produced == length;
        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = whole
            ? context.DecodeChild(in node, 2, indicesType, patchCount)
            : context.DecodeWholeChild(in node, 2, indicesType, patchCount);

        // Patch values are left parts at the left child's own physical type, always non-nullable.
        // They are raw high bits, not dictionary codes, which is why they are not bounds-checked
        // against the dictionary.
        DType valuesType = context.Types.Primitive(leftPType, Nullability.NonNullable);
        int valuesIndex = whole
            ? context.DecodeChild(in node, 3, valuesType, patchCount)
            : context.DecodeWholeChild(in node, 3, valuesType, patchCount);

        // No reader carries patch chunk offsets for this encoding, so a node declaring them
        // describes a shape nothing can honour.
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

        // One loop serves both paths: a second copy of this walk would be a second per-row dispatch
        // on the left parts' physical type. The branch below runs once per patch -- the rows the
        // dictionary could not hold, a minority by construction -- so the dense path pays one
        // predictable test for each of them.
        //
        // Both lists ascend, so one walk finds the intersection. `at` is where the patched row
        // landed in the selection, which is also where its right part is: the right child was
        // decoded selectively, so it holds the wanted rows in the same order. A range holds the
        // patches of its own rows, rebased to its first, and the walk starts at the first of them.
        ReadOnlySpan<byte> source = values.Values.Span;
        int at = 0;
        int first = 0;
        if (!selective && start != 0)
        {
            first = Patches.Find(in patches, start, 0);
            first = first < 0 ? ~first : first;
        }

        for (int i = first; i < patches.Count; i++)
        {
            int position = patches.GetPosition(i);
            int target;

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
            else
            {
                if (position >= start + produced)
                {
                    return;
                }

                target = position - start;
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
