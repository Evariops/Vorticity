using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>fastlanes.bitpacked</c>, patches included, into a primitive array.</summary>
/// <remarks>
/// The metadata, never the child count, decides what the children are: without patches, only an
/// optional validity child; with patches, the patch indices and patch values first, then the
/// per-chunk offsets when the metadata declares them, then the optional validity child. Three
/// children on their own are ambiguous between patches with validity and patches with chunk
/// offsets, and only the declared chunk-offsets type tells the two apart.
///
/// The packed buffer must hold exactly one whole block for every block the rows span; that check is
/// what lets the unpack kernel run without a per-element bounds check.
/// </remarks>
internal sealed class BitPackedDecoder : ArrayDecoder
{
    private const string Id = "fastlanes.bitpacked";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly BitPackedDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "fastlanes.bitpacked"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FastLanesBitPacked;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// Unpacks only the wanted rows, one at a time, without materializing a single block.
    /// </summary>
    /// <remarks>
    /// The inverse transposition gives positional access to a packed row, so a wanted row is read
    /// without unpacking the block around it. Switching to a whole-block unpack once enough of a
    /// block is wanted would need a crossover density this code does not know; per element is never
    /// the worse choice here, and when every row of a split is wanted the scan does not push the
    /// take down at all.
    ///
    /// Patches are still decoded in full. There are few of them by construction, and they arrive as
    /// their own child arrays, so selecting among them would mean pushing a second, differently
    /// based selection into those children, for a fraction of a fraction.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        Packing packing = Packing.Of(in node, dtype, length);
        PType ptype = packing.PType;
        int width = ptype.ByteWidth();
        int count = wanted.Length;
        int total = ArrayDecodeContext.CheckedMultiply(count, width, "BitPacked values");

        VortexBuffer output = VortexBuffer.Empty;
        Span<byte> destination = default;
        if (total != 0)
        {
            // Uninitialized: the gather writes every wanted value, and clears the span itself at
            // bit width 0. Patches only overwrite.
            output = CompressedValues.AllocateUninitialized(context, total, width, Id, out destination);
            Gather(packing.Packed.Span, packing.BitWidth, packing.Offset, width, wanted, destination);
        }

        if (packing.Metadata.HasPatches)
        {
            // Applied against the whole row space and then narrowed, which is the cheap direction:
            // a patch set is small, and the alternative is a search per wanted row.
            int patchCount = ArrayDecodeContext.CheckedLength(packing.Metadata.Patches.Length, $"{Id} patch count");

            // Every selective read of the node reads the whole patch set: decoded once for all of them,
            // as a range's is, when there are enough patches for that to pay.
            ReadOnlySpan<byte> values = ReadPatches(
                context, in node, dtype, length, packing.Metadata, retained: patchCount >= Patches.RetainedFrom, out Patches patches);
            Patches.ApplySelected(in patches, values, width, wanted, destination);
        }

        Validity validity = Compute.CanonicalFilter.FilterValidity(
            context.Canonical,
            context.DecodeValidity(in node, packing.ValidityChild, dtype.Nullability, length),
            wanted);

        return context.Canonical.AddPrimitive(dtype, count, validity, ptype, output);
    }

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Answers a comparison, a range or a set on the packed values themselves, a block of 1 024 rows at a time,
    /// without writing a value out: the integers the predicate selects are a range or a set of the column's values,
    /// which the kernel tests as it unpacks (<see cref="FastLanes.InRange{T}"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Packed values lie from 0 to <c>2^w - 1</c> for a width <c>w</c> under the element's: the predicate's range is cut
    /// to them first, which settles every packed row when it holds none of them or all, whatever the literal's sign or
    /// size. A patch holds a value of any width; each is judged on its own value and set over its row's bit.
    /// </para>
    /// <para>
    /// A set is tested by its members while they are few, by a bit for each value of the width up to 16 bits, and
    /// declined past both, where an equality a member would cost more than decoding.
    /// </para>
    /// </remarks>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (predicate.Integers is null && predicate.Members is null)
        {
            return false;
        }

        Packing packing = Packing.Of(in node, dtype, length);
        bool answered = packing.PType.ByteWidth() switch
        {
            1 => Answer<byte>(in packing, length, predicate, ref bits),
            2 => Answer<ushort>(in packing, length, predicate, ref bits),
            4 => Answer<uint>(in packing, length, predicate, ref bits),
            _ => Answer<ulong>(in packing, length, predicate, ref bits),
        };

        if (!answered)
        {
            return false;
        }

        if (packing.Metadata.HasPatches)
        {
            JudgePatches(context, in node, dtype, length, in packing, predicate, ref bits);
        }

        bits.ApplyValidity(ValidityMask.From(
            context, context.DecodeValidity(in node, packing.ValidityChild, dtype.Nullability, length)));
        return true;
    }

    /// <summary>
    /// The packed rows' answer: settled for every row when the packed values' range decides it, else the kernel's,
    /// over the blocks the rows touch. False for a set the kernels do not take.
    /// </summary>
    private static bool Answer<T>(in Packing packing, int length, Compute.EncodedPredicate predicate, ref Compute.PredicateBits bits)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int bitWidth = packing.BitWidth;
        int elementBits = Unsafe.SizeOf<T>() * 8;
        (Int128 least, Int128 greatest) = bitWidth == elementBits ? Domain(packing.PType) : (Int128.Zero, (Int128.One << bitWidth) - 1);
        ReadOnlySpan<T> packed = MemoryMarshal.Cast<byte, T>(packing.Packed.Span);
        if (predicate.Integers is { } integers)
        {
            Compute.IntegerRange range = integers.Within(least, greatest);
            if (range.IsEmpty || (range.Low == least && range.High == greatest))
            {
                bits.Fill(range.IsEmpty == range.Negated);
                return true;
            }

            Blocks(packed, bitWidth, packing.Offset, length, Kernel.Range, T.CreateTruncating(range.Low),
                T.CreateTruncating(range.High - range.Low), default, default, bits.Truth);
            bits.Trim();
            if (range.Negated)
            {
                bits.Invert();
            }

            return true;
        }

        // The members the packed values can equal, as those values' bits.
        ReadOnlySpan<Int128> members = predicate.Members!;
        int first = LowerBound(members, least);
        int end = LowerBound(members, greatest + 1);
        int count = end - first;
        if (count == 0)
        {
            bits.Fill(false);
            return true;
        }

        if (count <= FewMembers)
        {
            Span<T> kept = stackalloc T[FewMembers];
            for (int m = 0; m < count; m++)
            {
                kept[m] = T.CreateTruncating(members[first + m]);
            }

            Blocks(packed, bitWidth, packing.Offset, length, Kernel.Members, default, default, kept[..count], default, bits.Truth);
            bits.Trim();
            return true;
        }

        if (bitWidth > SetWidth)
        {
            return false;
        }

        ulong[] set = ArrayPool<ulong>.Shared.Rent(Math.Max(1, (1 << bitWidth) >> 6));
        try
        {
            Span<ulong> held = set.AsSpan(0, Math.Max(1, (1 << bitWidth) >> 6));
            held.Clear();
            for (int m = first; m < end; m++)
            {
                int value = int.CreateTruncating(T.CreateTruncating(members[m]));
                held[value >> 6] |= 1UL << (value & 63);
            }

            Blocks(packed, bitWidth, packing.Offset, length, Kernel.Set, default, default, default, held, bits.Truth);
            bits.Trim();
            return true;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(set);
        }
    }

    /// <summary>The members a set tests by an equality each, a vector of rows at a time.</summary>
    private const int FewMembers = 16;

    /// <summary>The widest packing a set of more members tests by a bit for each value: 8 KiB of bits.</summary>
    private const int SetWidth = 16;

    /// <summary>What a run of blocks is asked.</summary>
    private enum Kernel : byte
    {
        Range,
        Members,
        Set,
    }

    /// <summary>
    /// The kernel's answer over rows <c>[offset, offset + length)</c> of the packed blocks, into
    /// <paramref name="truth"/> from its first bit: whole blocks straight into it, a last partial block through a block
    /// of scratch, and the blocks of a slice that starts inside one through scratch moved down to its first row.
    /// </summary>
    [SkipLocalsInit]
    private static void Blocks<T>(
        ReadOnlySpan<T> packed, int bitWidth, int offset, int length, Kernel kernel, T low, T span,
        ReadOnlySpan<T> members, ReadOnlySpan<ulong> set, Span<ulong> truth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int wordsPerBlock = FastLanes.BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();
        int firstBlock = offset / FastLanes.BlockSize;
        int within = offset - (firstBlock * FastLanes.BlockSize);
        if (within == 0)
        {
            int whole = length / FastLanes.BlockSize;
            if (whole > 0)
            {
                Run(packed.Slice(firstBlock * wordsPerBlock, whole * wordsPerBlock), bitWidth, kernel, low, span, members, set,
                    truth[..(whole * FastLanes.BlockWords)], whole);
            }

            int rest = length - (whole * FastLanes.BlockSize);
            if (rest > 0)
            {
                Span<ulong> last = stackalloc ulong[FastLanes.BlockWords];
                Run(packed.Slice((firstBlock + whole) * wordsPerBlock, wordsPerBlock), bitWidth, kernel, low, span, members, set, last, 1);
                last[..Compute.PredicateBits.WordsOf(rest)].CopyTo(truth[(whole * FastLanes.BlockWords)..]);
            }

            return;
        }

        int blocks = ((within + length - 1) / FastLanes.BlockSize) + 1;
        ulong[] rented = ArrayPool<ulong>.Shared.Rent(blocks * FastLanes.BlockWords);
        try
        {
            Span<ulong> answers = rented.AsSpan(0, blocks * FastLanes.BlockWords);
            Run(packed.Slice(firstBlock * wordsPerBlock, blocks * wordsPerBlock), bitWidth, kernel, low, span, members, set, answers, blocks);
            BitmapKernels.CopyRange(MemoryMarshal.AsBytes(answers), within, MemoryMarshal.AsBytes(truth), 0, length);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    private static void Run<T>(
        ReadOnlySpan<T> packed, int bitWidth, Kernel kernel, T low, T span, ReadOnlySpan<T> members, ReadOnlySpan<ulong> set,
        Span<ulong> words, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        switch (kernel)
        {
            case Kernel.Range:
                FastLanes.InRange(packed, bitWidth, low, span, words, blocks);
                break;
            case Kernel.Members:
                FastLanes.AnyOf(packed, bitWidth, members, words, blocks);
                break;
            default:
                FastLanes.InSet(packed, bitWidth, set, words, blocks);
                break;
        }
    }

    /// <summary>Sets each patched row's bit by the predicate on its own value, which the packed bits do not hold.</summary>
    private static void JudgePatches(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, in Packing packing,
        Compute.EncodedPredicate predicate, ref Compute.PredicateBits bits)
    {
        ReadOnlySpan<byte> values = ReadPatches(context, in node, dtype, length, packing.Metadata, retained: false, out Patches patches);
        int width = packing.PType.ByteWidth();
        bool signed = packing.PType.IsSignedInteger();
        for (int i = 0; i < patches.Count; i++)
        {
            Int128 value = Read(values.Slice(i * width, width), signed);
            bool selected = predicate.Integers is { } range
                ? range.Holds(value)
                : Array.BinarySearch(predicate.Members!, value) >= 0;
            bits.Set(patches.GetPosition(i), selected);
        }
    }

    /// <summary>A little-endian integer of one to eight bytes, sign-extended when <paramref name="signed"/>.</summary>
    private static Int128 Read(ReadOnlySpan<byte> bytes, bool signed) => bytes.Length switch
    {
        1 => signed ? (sbyte)bytes[0] : bytes[0],
        2 => signed ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        4 => signed ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        _ => signed ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes),
    };

    /// <summary>The least and the greatest value of an integer type.</summary>
    private static (Int128 Least, Int128 Greatest) Domain(PType ptype) => ptype switch
    {
        PType.I8 => (sbyte.MinValue, sbyte.MaxValue),
        PType.I16 => (short.MinValue, short.MaxValue),
        PType.I32 => (int.MinValue, int.MaxValue),
        PType.I64 => (long.MinValue, long.MaxValue),
        PType.U8 => (0, byte.MaxValue),
        PType.U16 => (0, ushort.MaxValue),
        PType.U32 => (0, uint.MaxValue),
        _ => (0, ulong.MaxValue),
    };

    /// <summary>The first of the ascending <paramref name="values"/> at or past <paramref name="value"/>.</summary>
    private static int LowerBound(ReadOnlySpan<Int128> values, Int128 value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>A node's packing, checked against its rows and its type before a byte of it is read.</summary>
    private readonly struct Packing
    {
        private Packing(BitPackedMetadata metadata, PType ptype, VortexBuffer packed, int validityChild)
        {
            Metadata = metadata;
            PType = ptype;
            Packed = packed;
            ValidityChild = validityChild;
        }

        internal BitPackedMetadata Metadata { get; }

        internal PType PType { get; }

        internal VortexBuffer Packed { get; }

        /// <summary>Where the validity child sits, when there is one: after the patches' children.</summary>
        internal int ValidityChild { get; }

        internal int BitWidth => (int)Metadata.BitWidth;

        /// <summary>The row of the first block that is the node's first; <see cref="BitPackedMetadata.Read"/> keeps it under 1 024.</summary>
        internal int Offset => (int)Metadata.Offset;

        /// <exception cref="VortexFormatException">The width exceeds the type's, or the buffer is not whole blocks for the rows.</exception>
        internal static Packing Of(in ArrayNode node, DType dtype, int length)
        {
            ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
            BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
            PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);
            int elementBits = ptype.ByteWidth() * 8;

            // The metadata is checked before the packed buffer is touched.
            if (metadata.BitWidth > (uint)elementBits)
            {
                CompressedThrow.Format(
                    $"{Id} bit width {metadata.BitWidth} exceeds the {elementBits} bits of " +
                    $"{ptype.Name()}.");
            }

            int bitWidth = (int)metadata.BitWidth;
            int offset = (int)metadata.Offset;
            VortexBuffer packed = node.GetBuffer(0);
            long blocks = ((long)length + offset + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
            long expected = blocks * FastLanes.BlockByteLength(bitWidth);
            if (packed.Length != expected)
            {
                CompressedThrow.Format(
                    $"{Id} needs exactly {expected} packed bytes for {length} rows at offset " +
                    $"{offset} and bit width {bitWidth}; the buffer holds {packed.Length}.");
            }

            int validityChild = metadata.HasPatches
                ? (metadata.Patches.HasChunkOffsets ? 3 : 2)
                : 0;
            ArrayDecodeContext.RequireChildCount(node.ChildCount, validityChild, validityChild + 1, Id);
            return new Packing(metadata, ptype, packed, validityChild);
        }
    }

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, 0, length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
        int validityChildIndex = metadata.HasPatches
            ? (metadata.Patches.HasChunkOffsets ? 3 : 2)
            : 0;
        return context.ValidityDecodesRange(in node, validityChildIndex);
    }

    /// <summary>
    /// Unpacks the blocks the range touches and no other, the partial first and last ones through
    /// a scratch block; the patches are decoded whole, as they are small, and applied where they
    /// fall inside the range.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count);
    }

    /// <summary>
    /// Rows <c>[start, start + count)</c> with <paramref name="reference"/> added to every value,
    /// wrapping, in the pass that unpacks them: what a frame of reference asks of its encoded
    /// child, which otherwise unpacks into one buffer for the reference to be added into another.
    /// </summary>
    internal static int DecodeAdding(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count, ulong reference) =>
        Core(context, in node, dtype, length, start, count, reference);

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count,
        ulong reference = 0)
    {
        Packing packing = Packing.Of(in node, dtype, length);
        PType ptype = packing.PType;
        int validityChildIndex = packing.ValidityChild;
        int width = ptype.ByteWidth();
        int total = ArrayDecodeContext.CheckedMultiply(count, width, "BitPacked values");

        VortexBuffer output = VortexBuffer.Empty;
        Span<byte> destination = default;
        if (total != 0)
        {
            // Uninitialized: Unpack writes all `total` bytes, including at bit width 0 where it
            // clears the span itself rather than inheriting a cleared one. Patches only overwrite.
            output = CompressedValues.AllocateUninitialized(context, total, width, Id, out destination);
            Unpack(packing.Packed.Span, packing.BitWidth, packing.Offset + start, count, width, destination, reference);
        }

        // Patches are decoded and applied before the validity child only because the validity child
        // sits after them; the order of the two operations is otherwise independent.
        if (packing.Metadata.HasPatches)
        {
            ApplyPatches(context, in node, dtype, length, packing.Metadata, width, destination, start, count, reference);
        }

        bool whole = start == 0 && count == length;
        Validity validity = whole
            ? context.DecodeValidity(in node, validityChildIndex, dtype.Nullability, length)
            : context.DecodeValidityRange(in node, validityChildIndex, dtype.Nullability, length, start, count);

        return context.Canonical.AddPrimitive(dtype, count, validity, ptype, output);
    }

    /// <summary>Extracts the wanted rows one at a time.</summary>
    private static void Gather(
        ReadOnlySpan<byte> packed, int bitWidth, int offset, int width, ReadOnlySpan<int> wanted,
        Span<byte> destination)
    {
        switch (width)
        {
            case 1:
                Gather<byte>(packed, bitWidth, offset, wanted, destination);
                break;
            case 2:
                Gather<ushort>(packed, bitWidth, offset, wanted, destination);
                break;
            case 4:
                Gather<uint>(packed, bitWidth, offset, wanted, destination);
                break;
            default:
                Gather<ulong>(packed, bitWidth, offset, wanted, destination);
                break;
        }
    }

    private static void Gather<T>(
        ReadOnlySpan<byte> packedBytes, int bitWidth, int offset, ReadOnlySpan<int> wanted,
        Span<byte> destinationBytes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        Span<T> destination = MemoryMarshal.Cast<byte, T>(destinationBytes);

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        ReadOnlySpan<T> packed = MemoryMarshal.Cast<byte, T>(packedBytes);
        int elementsPerBlock = FastLanes.BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = FastLanes.BlockSize / elementBits;
        ReadOnlySpan<int> rowOf = FastLanes.PackedRowTable(elementBits);
        ReadOnlySpan<int> laneOf = FastLanes.PackedLaneTable(elementBits);

        if (bitWidth == elementBits)
        {
            // The copy-through case the bulk kernel branches out too: the packed word at
            // (row, lane) IS the value, with no shift and no mask.
            for (int i = 0; i < wanted.Length; i++)
            {
                int encoded = wanted[i] + offset;
                int block = encoded / FastLanes.BlockSize;
                int within = encoded - (block * FastLanes.BlockSize);
                destination[i] = packed[(block * elementsPerBlock) + (lanes * rowOf[within]) + laneOf[within]];
            }

            return;
        }

        FastLanes.GatherRows(packed, bitWidth, offset, wanted, destination);
    }

    /// <summary>Unpacks <paramref name="length"/> values from row <paramref name="offset"/>, <paramref name="reference"/> added to each, wrapping.</summary>
    private static void Unpack(
        ReadOnlySpan<byte> packed, int bitWidth, int offset, int length, int width, Span<byte> destination, ulong reference)
    {
        switch (width)
        {
            case 1:
                Unpack<byte>(packed, bitWidth, offset, length, destination, (byte)reference);
                break;
            case 2:
                Unpack<ushort>(packed, bitWidth, offset, length, destination, (ushort)reference);
                break;
            case 4:
                Unpack<uint>(packed, bitWidth, offset, length, destination, (uint)reference);
                break;
            default:
                Unpack<ulong>(packed, bitWidth, offset, length, destination, reference);
                break;
        }
    }

    private static void Unpack<T>(
        ReadOnlySpan<byte> packedBytes, int bitWidth, int offset, int length, Span<byte> destinationBytes, T reference)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        Span<T> destination = MemoryMarshal.Cast<byte, T>(destinationBytes);

        // A zero bit width packs nothing at all; every value is zero and the buffer is empty: the
        // reference itself, under a frame of reference.
        if (bitWidth == 0)
        {
            destination.Fill(reference);
            return;
        }

        // MemoryMarshal.Cast rather than VortexBuffer.Cast: upstream reinterprets the packed bytes
        // through a raw pointer and so needs natural alignment, but a .NET span read does not, and
        // rejecting an under-aligned buffer would refuse a file whose values are perfectly readable.
        ReadOnlySpan<T> packed = MemoryMarshal.Cast<byte, T>(packedBytes);
        int elementsPerBlock = FastLanes.BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();

        // The window decides which blocks are touched, and there are at most three kinds: a partial
        // first, a contiguous run of whole ones, and a partial last. The whole run is handed to the
        // kernel in one call so the per-row shape table, which depends only on the bit width and so
        // is the same for every block of the node, is built once rather than once per block.
        int end = offset + length;
        int firstBlock = offset / FastLanes.BlockSize;
        int lastBlock = (end - 1) / FastLanes.BlockSize;

        bool headPartial = offset % FastLanes.BlockSize != 0;
        bool tailPartial = end % FastLanes.BlockSize != 0;

        int firstWhole = headPartial ? firstBlock + 1 : firstBlock;
        int lastWhole = tailPartial ? lastBlock - 1 : lastBlock;

        if (headPartial || tailPartial)
        {
            T[] scratchArray = ArrayPool<T>.Shared.Rent(FastLanes.BlockSize);
            try
            {
                Span<T> scratch = scratchArray.AsSpan(0, FastLanes.BlockSize);
                if (headPartial)
                {
                    CopyPartial(packed, bitWidth, elementsPerBlock, firstBlock, offset, end, scratch, destination, reference);
                }

                // A window partial at both ends of one and the same block was already copied whole
                // by the head: its clamp is [offset, end), which is all of it.
                if (tailPartial && !(headPartial && lastBlock == firstBlock))
                {
                    CopyPartial(packed, bitWidth, elementsPerBlock, lastBlock, offset, end, scratch, destination, reference);
                }
            }
            finally
            {
                ArrayPool<T>.Shared.Return(scratchArray);
            }
        }

        int wholeBlocks = lastWhole - firstWhole + 1;
        if (wholeBlocks <= 0)
        {
            return;
        }

        ReadOnlySpan<T> words = packed.Slice(firstWhole * elementsPerBlock, wholeBlocks * elementsPerBlock);
        Span<T> run = destination.Slice((firstWhole * FastLanes.BlockSize) - offset, wholeBlocks * FastLanes.BlockSize);
        if (reference == T.Zero)
        {
            FastLanes.UnpackBlocks(words, bitWidth, run, wholeBlocks);
            return;
        }

        // The reference is added to each group of blocks while the group is in the first-level
        // cache, just unpacked: a second pass over the whole run would read it back from wherever
        // the run has gone by then, and a frame of reference's own add read it into a buffer of its own.
        int group = Math.Max(1, AddGroupBytes / (FastLanes.BlockSize * Unsafe.SizeOf<T>()));
        for (int block = 0; block < wholeBlocks; block += group)
        {
            int blocks = Math.Min(group, wholeBlocks - block);
            Span<T> values = run.Slice(block * FastLanes.BlockSize, blocks * FastLanes.BlockSize);
            FastLanes.UnpackBlocks(words.Slice(block * elementsPerBlock, blocks * elementsPerBlock), bitWidth, values, blocks);
            AddInPlace(values, reference);
        }
    }

    /// <summary>The bytes of blocks a reference is added to at a time: half the first-level cache.</summary>
    private const int AddGroupBytes = 16 * 1024;

    /// <summary><paramref name="reference"/> added to every one of <paramref name="values"/>, wrapping.</summary>
    private static void AddInPlace<T>(Span<T> values, T reference)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(values);
        IntegerKernels.AddWrapping(bytes, bytes, Unsafe.SizeOf<T>(), ulong.CreateTruncating(reference));
    }

    /// <summary>Unpacks one block into scratch and copies out only the part the window wants.</summary>
    private static void CopyPartial<T>(
        ReadOnlySpan<T> packed, int bitWidth, int elementsPerBlock, int block, int offset, int end,
        Span<T> scratch, Span<T> destination, T reference)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int encodedStart = block * FastLanes.BlockSize;
        int from = Math.Max(encodedStart, offset);
        int to = Math.Min(encodedStart + FastLanes.BlockSize, end);
        if (to <= from)
        {
            return;
        }

        FastLanes.UnpackBlock(packed.Slice(block * elementsPerBlock, elementsPerBlock), bitWidth, scratch);
        Span<T> copied = destination.Slice(from - offset, to - from);
        scratch.Slice(from - encodedStart, to - from).CopyTo(copied);
        if (reference != T.Zero)
        {
            AddInPlace(copied, reference);
        }
    }

    /// <summary>
    /// The node's patch set, validated, and its values: the indices, the values and the per-chunk offsets decoded once
    /// for every read of the node when <paramref name="retained"/>, for this read alone otherwise.
    /// </summary>
    /// <remarks>
    /// The validation is the full decode's whatever reads the patches - a malformed patch set must be refused whether
    /// or not a take or a range happens to skip the row it corrupts. The per-chunk offsets are read, validated and then
    /// deliberately unused: a range's first patch is found by a binary search over the indices, which they would only
    /// shorten.
    /// </remarks>
    private static ReadOnlySpan<byte> ReadPatches(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, BitPackedMetadata metadata, bool retained,
        out Patches patches)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(patchesMetadata.Length, $"{Id} patch count");
        DType indicesType = context.Types.Primitive(patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = retained
            ? context.DecodeWholeChild(in node, 0, indicesType, patchCount)
            : context.DecodeChild(in node, 0, indicesType, patchCount);
        int valuesIndex = retained
            ? context.DecodeWholeChild(in node, 1, dtype, patchCount)
            : context.DecodeChild(in node, 1, dtype, patchCount);

        if (patchesMetadata.HasChunkOffsets)
        {
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(
                patchesMetadata.ChunkOffsetsLength, $"{Id} patch chunk_offsets_len");
            DType chunkOffsetsType = context.Types.Primitive(patchesMetadata.ChunkOffsetsPType, Nullability.NonNullable);
            int chunkOffsets = retained
                ? context.DecodeWholeChild(in node, 2, chunkOffsetsType, chunkOffsetsLength)
                : context.DecodeChild(in node, 2, chunkOffsetsType, chunkOffsetsLength);
            CompressedValues.RequireIndexChild(
                context, chunkOffsets, patchesMetadata.ChunkOffsetsPType, chunkOffsetsLength, Id, "patch_chunk_offsets");
        }

        bool walked = context.IsNodeChecked(in node);
        patches = Patches.Create(context, in patchesMetadata, length, indicesIndex, valuesIndex, Id, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        // A span is read, so a constant child is expanded into one rather than refused.
        CanonicalNode values = context.Canonical.GetNode(CanonicalSupport.ExpandIfConstant(context, valuesIndex));
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

        return values.Values.Span;
    }

    private static void ApplyPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        BitPackedMetadata metadata,
        int width,
        Span<byte> destination,
        int start,
        int count,
        ulong reference)
    {
        // A range reads the whole patch set, decoded once for every range of the node.
        bool whole = start == 0 && count == length;
        ReadOnlySpan<byte> source = ReadPatches(context, in node, dtype, length, metadata, retained: !whole, out Patches patches);
        byte[]? added = null;
        if (reference != 0)
        {
            // A patch is an encoded value as much as a packed one: under a frame of reference, its
            // value is the patch plus the reference. Patches are few, so they are added on a copy.
            added = ArrayPool<byte>.Shared.Rent(source.Length);
            IntegerKernels.AddWrapping(source, added.AsSpan(0, source.Length), width, reference);
            source = added.AsSpan(0, source.Length);
        }

        try
        {
            if (whole)
            {
                patches.ApplyAll(source, width, destination);
                return;
            }

            Patches.ApplyRange(in patches, source, width, start, count, destination);
        }
        finally
        {
            if (added is not null)
            {
                ArrayPool<byte>.Shared.Return(added);
            }
        }
    }
}
